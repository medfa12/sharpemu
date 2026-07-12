// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

public sealed class Gen5ShaderMetadataReaderTests
{
    private const ulong ShaderHeader = 0x1000;
    private const ulong UserData = 0x2000;
    private const ulong DirectOffsets = 0x3000;
    private const ulong ResourceTableBase = 0x4000;

    /// <summary>Sparse byte-addressable guest memory backed by a dictionary.</summary>
    private sealed class SparseMemory : ICpuMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = new();

        public void WriteUInt16(ulong address, ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            Write(address, buffer);
        }

        public void WriteUInt64(ulong address, ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            Write(address, buffer);
        }

        private void Write(ulong address, ReadOnlySpan<byte> source)
        {
            for (var i = 0; i < source.Length; i++)
            {
                _bytes[address + (ulong)i] = source[i];
            }
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                if (!_bytes.TryGetValue(virtualAddress + (ulong)i, out var value))
                {
                    return false;
                }

                destination[i] = value;
            }

            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            Write(virtualAddress, source);
            return true;
        }
    }

    private static SparseMemory BuildBaseLayout(
        ushort directResourceCount = 0,
        ushort[]? resourceCounts = null)
    {
        var memory = new SparseMemory();
        memory.WriteUInt64(ShaderHeader + 0x08, UserData);
        memory.WriteUInt64(UserData, directResourceCount == 0 ? 0UL : DirectOffsets);

        resourceCounts ??= new ushort[4];
        for (var resourceClass = 0; resourceClass < 4; resourceClass++)
        {
            var offset = resourceCounts[resourceClass] == 0
                ? 0UL
                : ResourceTableBase + (ulong)(resourceClass * 0x100);
            memory.WriteUInt64(UserData + 0x08 + (ulong)(resourceClass * 8), offset);
        }

        memory.WriteUInt16(UserData + 0x28, 0x20); // extended user data size
        memory.WriteUInt16(UserData + 0x2A, 0x10); // shader resource table size
        memory.WriteUInt16(UserData + 0x2C, directResourceCount);
        for (var resourceClass = 0; resourceClass < 4; resourceClass++)
        {
            memory.WriteUInt16(UserData + 0x2E + (ulong)(resourceClass * 2), resourceCounts[resourceClass]);
        }

        return memory;
    }

    private static bool TryRead(SparseMemory memory, out Gen5ShaderMetadata metadata)
        => Gen5ShaderMetadataReader.TryRead(
            new CpuContext(memory, Generation.Gen5),
            ShaderHeader,
            out metadata);

    [Fact]
    public void TryRead_MinimalLayout_YieldsEmptyMetadata()
    {
        var memory = BuildBaseLayout();

        Assert.True(TryRead(memory, out var metadata));
        Assert.Equal(0x20u, metadata.ExtendedUserDataSizeDwords);
        Assert.Equal(0x10u, metadata.ShaderResourceTableSizeDwords);
        Assert.Empty(metadata.DirectResources);
        Assert.Empty(metadata.Resources);
    }

    [Fact]
    public void TryRead_DirectResources_SkipsUnmappedSentinel()
    {
        var memory = BuildBaseLayout(directResourceCount: 3);
        memory.WriteUInt16(DirectOffsets + 0, 0x0004);
        memory.WriteUInt16(DirectOffsets + 2, ushort.MaxValue); // unmapped sentinel
        memory.WriteUInt16(DirectOffsets + 4, 0x0008);

        Assert.True(TryRead(memory, out var metadata));
        Assert.Equal(2, metadata.DirectResources.Count);
        Assert.Equal(0x0004u, metadata.DirectResources[0]);
        Assert.Equal(0x0008u, metadata.DirectResources[2]);
        Assert.False(metadata.DirectResources.ContainsKey(1));
    }

    [Fact]
    public void TryRead_ResourceTables_DecodeOffsetAndSizeFlag()
    {
        var memory = BuildBaseLayout(resourceCounts: [2, 0, 0, 1]);
        // Class 0 (ReadOnlyTexture): slot 0 with size flag, slot 1 skipped sentinel.
        memory.WriteUInt16(ResourceTableBase + 0, 0x8010);
        memory.WriteUInt16(ResourceTableBase + 2, 0x7FFF);
        // Class 3 (ConstantBuffer): slot 0 plain offset.
        memory.WriteUInt16(ResourceTableBase + 0x300, 0x0024);

        Assert.True(TryRead(memory, out var metadata));

        Assert.Equal(2, metadata.Resources.Count);
        var texture = metadata.Resources[0];
        Assert.Equal(Gen5ShaderResourceKind.ReadOnlyTexture, texture.Kind);
        Assert.Equal(0u, texture.Slot);
        Assert.Equal(0x10u, texture.OffsetDwords);
        Assert.True(texture.SizeFlag);

        var constantBuffer = metadata.Resources[1];
        Assert.Equal(Gen5ShaderResourceKind.ConstantBuffer, constantBuffer.Kind);
        Assert.Equal(0x24u, constantBuffer.OffsetDwords);
        Assert.False(constantBuffer.SizeFlag);
    }

    [Fact]
    public void TryRead_NullUserDataPointer_Fails()
    {
        var memory = new SparseMemory();
        memory.WriteUInt64(ShaderHeader + 0x08, 0);

        Assert.False(TryRead(memory, out _));
    }

    [Fact]
    public void TryRead_UnmappedUserData_Fails()
    {
        var memory = new SparseMemory();
        memory.WriteUInt64(ShaderHeader + 0x08, UserData); // user data itself never written

        Assert.False(TryRead(memory, out _));
    }

    [Fact]
    public void TryRead_DirectResourceCountAboveLimit_Fails()
    {
        var memory = BuildBaseLayout();
        memory.WriteUInt16(UserData + 0x2C, 4097); // MaxMetadataEntries is 4096

        Assert.False(TryRead(memory, out _));
    }

    [Fact]
    public void TryRead_ResourceCountAboveLimit_Fails()
    {
        var memory = BuildBaseLayout();
        memory.WriteUInt16(UserData + 0x2E, 4097);

        Assert.False(TryRead(memory, out _));
    }

    [Fact]
    public void TryRead_DirectResourcesWithNullOffsetTable_Fails()
    {
        var memory = BuildBaseLayout(directResourceCount: 1);
        memory.WriteUInt64(UserData, 0); // direct offsets pointer nulled

        Assert.False(TryRead(memory, out _));
    }

    [Fact]
    public void TryRead_ResourceClassWithCountButNullTable_Fails()
    {
        var memory = BuildBaseLayout(resourceCounts: [1, 0, 0, 0]);
        memory.WriteUInt64(UserData + 0x08, 0); // class-0 table pointer nulled
        memory.WriteUInt16(ResourceTableBase, 0x0001);

        Assert.False(TryRead(memory, out _));
    }
}
