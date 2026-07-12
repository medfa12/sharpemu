// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// End-to-end loader tests using synthetic plain-ELF images (the non-SELF path)
/// mapped into the managed <see cref="VirtualMemory"/>.
/// </summary>
public sealed class SelfLoaderTests
{
    private const ulong Ps5MainImageBase = 0x0000_0008_0000_0000UL;
    private const ulong Ps4MainImageBase = 0x0000_0000_0040_0000UL;
    private const byte Gen5AbiVersion = 2;
    private const byte Gen4AbiVersion = 1;

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

    private static Segment TextSegment(byte[] code, ulong vaddr = 0, ulong fileOffset = 0x1000)
        => new(
            Type: (uint)ProgramHeaderType.Load,
            Flags: (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
            FileOffset: fileOffset,
            VirtualAddress: vaddr,
            FileSize: (ulong)code.Length,
            MemorySize: 0x2000,
            Alignment: 0x1000,
            Data: code);

    [Fact]
    public void Load_Gen5Elf_MapsSegmentAtPs5ImageBase()
    {
        byte[] code = [0x48, 0x31, 0xC0, 0xC3]; // xor rax,rax; ret
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0x10, TextSegment(code));
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.False(image.IsSelf);
        Assert.Equal(Ps5MainImageBase + 0x10, image.EntryPoint);

        var region = Assert.Single(image.MappedRegions);
        Assert.Equal(Ps5MainImageBase, region.VirtualAddress);
        Assert.Equal(0x2000UL, region.MemorySize);
        Assert.Equal(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, region.Protection);

        var readBack = new byte[code.Length];
        Assert.True(memory.TryRead(Ps5MainImageBase, readBack));
        Assert.Equal(code, readBack);
    }

    [Fact]
    public void Load_Gen4Elf_UsesPs4ImageBase()
    {
        var elf = BuildElf(Gen4AbiVersion, entryPoint: 0x20, TextSegment([0xC3]));
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(Ps4MainImageBase + 0x20, image.EntryPoint);
        Assert.Equal(Ps4MainImageBase, Assert.Single(image.MappedRegions).VirtualAddress);
    }

    [Fact]
    public void Load_SceProcParamHeader_ResolvesProcParamAddress()
    {
        var procParam = new Segment(
            Type: (uint)ProgramHeaderType.SceProcParam,
            Flags: (uint)ProgramHeaderFlags.Read,
            FileOffset: 0x1000,
            VirtualAddress: 0x40,
            FileSize: 0,
            MemorySize: 0,
            Alignment: 8);
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]), procParam);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Equal(Ps5MainImageBase + 0x40, image.ProcParamAddress);
    }

    [Fact]
    public void Load_BssOnlyTail_IsZeroFilled()
    {
        byte[] code = [0xAA, 0xBB];
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment(code));
        var memory = new VirtualMemory();

        new SelfLoader().Load(elf, memory);

        var tail = new byte[0x10];
        Assert.True(memory.TryRead(Ps5MainImageBase + (ulong)code.Length, tail));
        Assert.All(tail, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Load_NoDynamicSegment_YieldsNoImportStubs()
    {
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]));
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        Assert.Empty(image.ImportStubs);
        Assert.Empty(image.ImportedRelocations);
    }

    [Fact]
    public void Load_EmptyImage_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => new SelfLoader().Load(ReadOnlySpan<byte>.Empty, new VirtualMemory()));
    }

    [Fact]
    public void Load_WrongMagic_Throws()
    {
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]));
        elf[0] = 0x00;

        Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void Load_32BitImage_Throws()
    {
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]));
        elf[4] = 1; // ELFCLASS32

        Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void Load_BigEndianImage_Throws()
    {
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]));
        elf[5] = 2; // ELFDATA2MSB

        Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void Load_SegmentFileSizeExceedingMemorySize_Throws()
    {
        var bad = new Segment(
            Type: (uint)ProgramHeaderType.Load,
            Flags: (uint)ProgramHeaderFlags.Read,
            FileOffset: 0x1000,
            VirtualAddress: 0,
            FileSize: 0x100,
            MemorySize: 0x10,
            Alignment: 0x1000,
            Data: new byte[0x100]);
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, bad);

        Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void Load_SegmentExtendingBeyondImage_Throws()
    {
        // Program header claims 0x100 bytes of file data at an offset past EOF.
        var phantom = new Segment(
            Type: (uint)ProgramHeaderType.Load,
            Flags: (uint)ProgramHeaderFlags.Read,
            FileOffset: 0x10_0000,
            VirtualAddress: 0,
            FileSize: 0x100,
            MemorySize: 0x1000,
            Alignment: 0x1000);
        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, phantom);

        Assert.Throws<InvalidDataException>(() => new SelfLoader().Load(elf, new VirtualMemory()));
    }

    [Fact]
    public void Load_ClearsPreviousMappings()
    {
        var memory = new VirtualMemory();
        memory.Map(0x1234_0000, 0x1000, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        var elf = BuildElf(Gen5AbiVersion, entryPoint: 0, TextSegment([0xC3]));
        new SelfLoader().Load(elf, memory);

        Assert.DoesNotContain(memory.SnapshotRegions(), r => r.VirtualAddress == 0x1234_0000);
    }
}
