// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceAgcGetRegisterDefaults2 hands the guest a blob with per-group pointer
/// tables for context (78), shader (29), and uconfig (20) register groups.
/// The guest indexes those tables directly; any missing group leaves a null
/// pointer that degrades into invalid indirect register writes and placeholder
/// render-target state, so the primary table must cover the full Kyty-derived
/// set. These tests pin the group counts, the null-free pointer tables, and
/// spot-check hash/offset/value triples against the Kyty tables.
/// </summary>
public sealed class AgcRegisterDefaultsTests
{
    private const uint RegisterDefaultsVersion = 8;
    private const int CxGroupCount = 78;
    private const int ShGroupCount = 29;
    private const int UcGroupCount = 20;
    private const int PrimaryGroupCount = 127;
    private const int HeaderCxTableOffset = 0x00;
    private const int HeaderShTableOffset = 0x08;
    private const int HeaderUcTableOffset = 0x10;
    private const int HeaderTypesOffset = 0x30;
    private const int HeaderGroupCountOffset = 0x38;
    private const int TypeEntrySize = 3 * sizeof(uint);
    private const uint CxSpace = 0;
    private const uint ShSpace = 1;
    private const uint UcSpace = 2;

    private static CpuContext NewContext() =>
        new(new AllocatingGuestMemory(), Generation.Gen5);

    private static ulong GetDefaults(CpuContext ctx, bool internalDefaults)
    {
        ctx[CpuRegister.Rdi] = RegisterDefaultsVersion;
        var result = internalDefaults
            ? AgcExports.GetRegisterDefaults2Internal(ctx)
            : AgcExports.GetRegisterDefaults2(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        return ctx[CpuRegister.Rax];
    }

    private static ulong ReadPointer(CpuContext ctx, ulong address)
    {
        Assert.True(ctx.TryReadUInt64(address, out var value));
        return value;
    }

    private static uint ReadWord(CpuContext ctx, ulong address)
    {
        Assert.True(ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private static ulong ReadGroupPointer(CpuContext ctx, ulong blob, uint space, uint index)
    {
        var tableHeaderOffset = space switch
        {
            CxSpace => HeaderCxTableOffset,
            ShSpace => HeaderShTableOffset,
            _ => HeaderUcTableOffset,
        };
        var table = ReadPointer(ctx, blob + (ulong)tableHeaderOffset);
        return ReadPointer(ctx, table + (index * sizeof(ulong)));
    }

    private static (uint Offset, uint Value) ReadGroupRegister(
        CpuContext ctx,
        ulong blob,
        uint space,
        uint index,
        uint registerIndex)
    {
        var block = ReadGroupPointer(ctx, blob, space, index);
        Assert.NotEqual(0UL, block);
        var pair = block + (registerIndex * 2UL * sizeof(uint));
        return (ReadWord(ctx, pair), ReadWord(ctx, pair + sizeof(uint)));
    }

    private static uint FindGroupTypeHash(CpuContext ctx, ulong blob, uint space, uint index)
    {
        var types = ReadPointer(ctx, blob + HeaderTypesOffset);
        var count = ReadWord(ctx, blob + HeaderGroupCountOffset);
        var key = (index * 4) + space;
        for (uint entry = 0; entry < count; entry++)
        {
            var entryAddress = types + (entry * TypeEntrySize);
            if (ReadWord(ctx, entryAddress + sizeof(uint)) == key)
            {
                return ReadWord(ctx, entryAddress);
            }
        }

        Assert.Fail($"no type entry for space={space} index={index}");
        return 0;
    }

    [Fact]
    public void PrimaryDefaults_CoverEveryGroupSlot()
    {
        var ctx = NewContext();
        var blob = GetDefaults(ctx, internalDefaults: false);
        Assert.NotEqual(0UL, blob);
        Assert.Equal((uint)PrimaryGroupCount, ReadWord(ctx, blob + HeaderGroupCountOffset));

        for (uint index = 0; index < CxGroupCount; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, CxSpace, index));
        }

        for (uint index = 0; index < ShGroupCount; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, ShSpace, index));
        }

        for (uint index = 0; index < UcGroupCount; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, UcSpace, index));
        }
    }

    [Theory]
    [InlineData(CxSpace, 4u, 0u, 0x9E5AD592u, 0x08Eu, 0x0000000Fu)] // CB_TARGET_MASK
    [InlineData(CxSpace, 21u, 0u, 0x09AFDDAFu, 0x206u, 0x0000043Fu)] // PA_CL_VTE_CNTL
    [InlineData(CxSpace, 40u, 0u, 0xA00D0C8Du, 0x205u, 0x00000240u)] // PA_SU_SC_MODE_CNTL
    [InlineData(CxSpace, 52u, 0u, 0xC5831803u, 0x2D4u, 0x88101000u)] // VGT_TESS_DISTRIBUTION
    [InlineData(CxSpace, 64u, 0u, 0x67096014u, 0x010u, 0x80000000u)] // DB_Z_INFO
    [InlineData(CxSpace, 64u, 1u, 0x67096014u, 0x011u, 0x20000000u)] // DB_STENCIL_INFO
    [InlineData(ShSpace, 19u, 0u, 0x50685F29u, 0x800002FFu, 0u)] // SH_NOP
    [InlineData(ShSpace, 28u, 0u, 0xBDF02A4Cu, 0x00Cu, 0u)] // SPI_SHADER_USER_DATA_PS_0
    [InlineData(UcSpace, 10u, 0u, 0xF6D8A76Eu, 0x382u, 0x40000040u)] // TEXTURE_GRADIENT_FACTORS
    [InlineData(UcSpace, 19u, 0u, 0x036AC8A6u, 0x25Cu, 0u)] // GE_USER_VGPR1
    public void PrimaryDefaults_MatchKytyTriples(
        uint space,
        uint index,
        uint registerIndex,
        uint expectedTypeHash,
        uint expectedOffset,
        uint expectedValue)
    {
        var ctx = NewContext();
        var blob = GetDefaults(ctx, internalDefaults: false);
        Assert.NotEqual(0UL, blob);

        Assert.Equal(expectedTypeHash, FindGroupTypeHash(ctx, blob, space, index));
        var (offset, value) = ReadGroupRegister(ctx, blob, space, index, registerIndex);
        Assert.Equal(expectedOffset, offset);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void InternalDefaults_CoverEveryGroupSlot()
    {
        var ctx = NewContext();
        var blob = GetDefaults(ctx, internalDefaults: true);
        Assert.NotEqual(0UL, blob);
        Assert.Equal(22u, ReadWord(ctx, blob + HeaderGroupCountOffset));

        for (uint index = 0; index < 4; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, CxSpace, index));
        }

        for (uint index = 0; index < 15; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, ShSpace, index));
        }

        for (uint index = 0; index < 3; index++)
        {
            Assert.NotEqual(0UL, ReadGroupPointer(ctx, blob, UcSpace, index));
        }
    }

    /// <summary>
    /// Sparse guest memory that also satisfies the virtual-range allocator so
    /// KernelMemoryCompatExports.TryAllocateHleData (which backs the register
    /// defaults blob) works under test.
    /// </summary>
    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly Dictionary<ulong, byte> _bytes = new();

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
            for (var i = 0; i < source.Length; i++)
            {
                _bytes[virtualAddress + (ulong)i] = source[i];
            }

            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
            => desiredAddress;

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            var effectiveAlignment = Math.Max(alignment, 1UL);
            actualAddress = (desiredAddress + effectiveAlignment - 1) & ~(effectiveAlignment - 1);
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }
    }
}
