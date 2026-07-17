// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// PT_TLS Variant II template registration and per-thread seeding. The main
/// scenario mirrors Astro Bot's eboot: filesz 0x40 of -1 sentinels, memsz
/// 0xA0, align 8, so fs:[-0x74] must initialize to -1 on every thread.
/// </summary>
public sealed class GuestTlsImageTests : IDisposable
{
    private const ulong Ps5MainImageBase = 0x0000_0008_0000_0000UL;

    public GuestTlsImageTests()
    {
        GuestTlsImage.Reset();
    }

    public void Dispose()
    {
        GuestTlsImage.Reset();
    }

    [Theory]
    [InlineData(0UL, 0x20UL, 0x10UL, 0UL, 0x20UL)]
    [InlineData(0x20UL, 0x18UL, 0x20UL, 8UL, 0x38UL)]
    [InlineData(0UL, 0xA0UL, 8UL, 0UL, 0xA0UL)]
    [InlineData(0UL, 0xA1UL, 8UL, 0UL, 0xA8UL)]
    public void CalculateStaticOffset_SatisfiesSizeAndCongruence(
        ulong previousOffset, ulong size, ulong alignment, ulong bias, ulong expected)
    {
        var offset = GuestTlsImage.CalculateStaticOffset(previousOffset, size, alignment, bias);

        Assert.Equal(expected, offset);
        Assert.True(offset - previousOffset >= size);
        Assert.Equal(bias, unchecked(0UL - offset) & (alignment - 1));
    }

    [Fact]
    public void Set_AstroShapedTemplate_ComputesAlignedBlockSize()
    {
        var initImage = new byte[0x40];
        Array.Fill(initImage, (byte)0xFF);

        GuestTlsImage.Set(initImage, memorySize: 0xA0, alignment: 8);

        Assert.True(GuestTlsImage.HasImage);
        Assert.Equal(0xA0UL, GuestTlsImage.BlockSize);
        Assert.Equal(0xA0UL, GuestTlsImage.MemorySize);
        Assert.Equal(8UL, GuestTlsImage.Alignment);
        Assert.Equal(initImage, GuestTlsImage.InitImage);
    }

    [Fact]
    public void Set_RejectsInvalidTemplates()
    {
        Assert.Throws<InvalidDataException>(() => GuestTlsImage.Set([], 0x10, alignment: 3));
        Assert.Throws<InvalidDataException>(() => GuestTlsImage.Set(new byte[0x20], memorySize: 0x10, alignment: 8));
    }

    [Fact]
    public void TrySeedThreadBlock_WritesImageThenZeros_AndInitializesAstroSlotToMinusOne()
    {
        var memory = new VirtualMemory();
        var garbage = new byte[0x2000];
        Array.Fill(garbage, (byte)0xAB);
        memory.Map(0x10000, 0x2000, fileOffset: 0, garbage, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        const ulong fsBase = 0x11000;

        var initImage = new byte[0x40];
        Array.Fill(initImage, (byte)0xFF);
        GuestTlsImage.Set(initImage, memorySize: 0xA0, alignment: 8);

        Assert.True(GuestTlsImage.TrySeedThreadBlock(memory, fsBase, mappedPrefixSize: 0x1000));

        var block = new byte[0xA0];
        Assert.True(memory.TryRead(fsBase - 0xA0, block));
        Assert.All(block[..0x40], b => Assert.Equal((byte)0xFF, b));
        Assert.All(block[0x40..], b => Assert.Equal((byte)0, b));

        var slot = new byte[4];
        Assert.True(memory.TryRead(fsBase - 0x74, slot));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(slot));

        // Bytes below the block stay untouched.
        var below = new byte[1];
        Assert.True(memory.TryRead(fsBase - 0xA1, below));
        Assert.Equal((byte)0xAB, below[0]);
    }

    [Fact]
    public void TrySeedThreadBlock_WithoutTemplate_IsNoOp()
    {
        var memory = new VirtualMemory();
        Assert.True(GuestTlsImage.TrySeedThreadBlock(memory, 0x11000, mappedPrefixSize: 0x1000));
    }

    [Fact]
    public void TrySeedThreadBlock_OversizedBlock_SkipsWithoutCorruptingMemory()
    {
        var memory = new VirtualMemory();
        var garbage = new byte[0x3000];
        Array.Fill(garbage, (byte)0xAB);
        memory.Map(0x10000, 0x3000, fileOffset: 0, garbage, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        const ulong fsBase = 0x12000;

        GuestTlsImage.Set(new byte[0x40], memorySize: 0x1800, alignment: 8);
        Assert.Equal(0x1800UL, GuestTlsImage.BlockSize);

        Assert.True(GuestTlsImage.TrySeedThreadBlock(memory, fsBase, mappedPrefixSize: 0x1000));

        var readBack = new byte[0x2000];
        Assert.True(memory.TryRead(0x10000, readBack));
        Assert.All(readBack, b => Assert.Equal((byte)0xAB, b));
    }

    [Fact]
    public void TrySeedThreadBlock_UnmappedRegion_Fails()
    {
        var memory = new VirtualMemory();
        GuestTlsImage.Set(new byte[0x40], memorySize: 0xA0, alignment: 8);

        Assert.False(GuestTlsImage.TrySeedThreadBlock(memory, 0x11000, mappedPrefixSize: 0x1000));
    }

    [Fact]
    public void Load_ElfWithTlsSegment_RegistersInitImage()
    {
        var tdata = new byte[0x40];
        Array.Fill(tdata, (byte)0xFF);
        var load = new Segment(
            Type: (uint)ProgramHeaderType.Load,
            Flags: (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Write),
            FileOffset: 0x1000,
            VirtualAddress: 0,
            FileSize: 0x40,
            MemorySize: 0x2000,
            Alignment: 0x1000,
            Data: tdata);
        var tls = new Segment(
            Type: (uint)ProgramHeaderType.Tls,
            Flags: (uint)ProgramHeaderFlags.Read,
            FileOffset: 0x1000,
            VirtualAddress: 0,
            FileSize: 0x40,
            MemorySize: 0xA0,
            Alignment: 8);
        var elf = BuildElf(abiVersion: 2, entryPoint: 0, load, tls);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(Ps5MainImageBase, image.EntryPoint);
        Assert.True(GuestTlsImage.HasImage);
        Assert.Equal(0xA0UL, GuestTlsImage.BlockSize);
        Assert.Equal(0xA0UL, GuestTlsImage.MemorySize);
        Assert.Equal(8UL, GuestTlsImage.Alignment);
        Assert.Equal(tdata, GuestTlsImage.InitImage);
    }

    [Fact]
    public void Load_ElfWithoutTlsSegment_ClearsPreviousTemplate()
    {
        GuestTlsImage.Set(new byte[0x10], memorySize: 0x20, alignment: 8);
        var elf = BuildElf(
            abiVersion: 2,
            entryPoint: 0,
            new Segment(
                Type: (uint)ProgramHeaderType.Load,
                Flags: (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
                FileOffset: 0x1000,
                VirtualAddress: 0,
                FileSize: 1,
                MemorySize: 0x1000,
                Alignment: 0x1000,
                Data: [0xC3]));

        _ = new SelfLoader().Load(elf, new VirtualMemory());

        Assert.False(GuestTlsImage.HasImage);
    }

    private sealed record Segment(
        uint Type,
        uint Flags,
        ulong FileOffset,
        ulong VirtualAddress,
        ulong FileSize,
        ulong MemorySize,
        ulong Alignment,
        byte[]? Data = null);

    private static byte[] BuildElf(byte abiVersion, ulong entryPoint, params Segment[] segments)
    {
        const int headerSize = 64;
        const int phEntrySize = 56;
        var imageSize = headerSize + (segments.Length * phEntrySize);
        foreach (var s in segments)
        {
            if (s.Data is not null)
            {
                imageSize = Math.Max(imageSize, checked((int)(s.FileOffset + (ulong)s.Data.Length)));
            }
        }

        var image = new byte[imageSize];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2; // ELFCLASS64
        image[5] = 1; // ELFDATA2LSB
        image[8] = abiVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 0xFE10); // e_type
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 0x3E);   // e_machine
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(24), entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), headerSize); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), phEntrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), (ushort)segments.Length);

        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            var o = headerSize + (i * phEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(o + 0), s.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(o + 4), s.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 8), s.FileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 16), s.VirtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 24), s.VirtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 32), s.FileSize);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 40), s.MemorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 48), s.Alignment);
            s.Data?.CopyTo(image.AsSpan(checked((int)s.FileOffset)));
        }

        return image;
    }
}
