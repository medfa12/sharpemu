// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Verifies that the blittable loader structs match the ELF64 on-disk layout
/// (System V gABI), since <c>SelfLoader</c> materializes them by reinterpreting
/// raw image bytes.
/// </summary>
public sealed class ElfStructLayoutTests
{
    [Fact]
    public void ElfHeader_HasElf64Size()
    {
        Assert.Equal(64, Unsafe.SizeOf<ElfHeader>());
    }

    [Fact]
    public void ProgramHeader_HasElf64Size()
    {
        Assert.Equal(56, Unsafe.SizeOf<ProgramHeader>());
    }

    [Fact]
    public void ElfHeader_FieldsMapToSpecOffsets()
    {
        var bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2; // ELFCLASS64
        bytes[5] = 1; // ELFDATA2LSB
        bytes[7] = 9; // ABI (EI_OSABI)
        bytes[8] = 1; // ABI version (EI_ABIVERSION)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 0xFE10); // e_type (ET_SCE_EXEC)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 0x3E);   // e_machine (EM_X86_64)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);      // e_version
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x0000_0000_00A0_1234UL); // e_entry
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0x40UL); // e_phoff
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 0x1000UL); // e_shoff
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 0xCAFE);  // e_flags
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);     // e_ehsize
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);     // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 7);      // e_phnum
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 64);     // e_shentsize
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(60), 3);      // e_shnum
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(62), 2);      // e_shstrndx

        var header = MemoryMarshal.Read<ElfHeader>(bytes);

        Assert.True(header.HasElfMagic);
        Assert.True(header.Is64Bit);
        Assert.True(header.IsLittleEndian);
        Assert.Equal(9, header.Abi);
        Assert.Equal(1, header.AbiVersion);
        Assert.Equal(0xFE10, header.Type);
        Assert.Equal(0x3E, header.Machine);
        Assert.Equal(1u, header.Version);
        Assert.Equal(0x0000_0000_00A0_1234UL, header.EntryPoint);
        Assert.Equal(0x40UL, header.ProgramHeaderOffset);
        Assert.Equal(0x1000UL, header.SectionHeaderOffset);
        Assert.Equal(0xCAFEu, header.Flags);
        Assert.Equal(64, header.HeaderSize);
        Assert.Equal(56, header.ProgramHeaderEntrySize);
        Assert.Equal(7, header.ProgramHeaderCount);
        Assert.Equal(64, header.SectionHeaderEntrySize);
        Assert.Equal(3, header.SectionHeaderCount);
        Assert.Equal(2, header.SectionHeaderStringIndex);
    }

    [Fact]
    public void ElfHeader_RejectsWrongMagic()
    {
        var bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'X';

        var header = MemoryMarshal.Read<ElfHeader>(bytes);

        Assert.False(header.HasElfMagic);
    }

    [Fact]
    public void ProgramHeader_FieldsMapToSpecOffsets()
    {
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0x61000001);  // p_type (SceProcParam)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x5);         // p_flags (R+X)
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x4000UL);    // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0x0000_0000_0040_0000UL); // p_vaddr
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x0000_0000_0040_0000UL); // p_paddr
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0x800UL);    // p_filesz
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 0x1000UL);   // p_memsz
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), 0x4000UL);   // p_align

        var header = MemoryMarshal.Read<ProgramHeader>(bytes);

        Assert.Equal(ProgramHeaderType.SceProcParam, header.HeaderType);
        Assert.Equal(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, header.Flags);
        Assert.Equal(0x4000UL, header.Offset);
        Assert.Equal(0x0000_0000_0040_0000UL, header.VirtualAddress);
        Assert.Equal(0x0000_0000_0040_0000UL, header.PhysicalAddress);
        Assert.Equal(0x800UL, header.FileSize);
        Assert.Equal(0x1000UL, header.MemorySize);
        Assert.Equal(0x4000UL, header.Alignment);
    }
}
