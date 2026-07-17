// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Exercises sceAgcCreateInterpolantMapping: pixel-input semantics are matched
/// against geometry-output semantics by semantic id and encoded into
/// SPI_PS_INPUT_CNTL register writes.
/// </summary>
public sealed class AgcInterpolantMappingTests
{
    private const ulong RegistersAddress = 0x1_0000_0100;
    private const ulong GeometryShaderAddress = 0x1_0000_0400;
    private const ulong PixelShaderAddress = 0x1_0000_0600;
    private const ulong OutputSemanticsAddress = 0x1_0000_0800;
    private const ulong InputSemanticsAddress = 0x1_0000_0900;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const uint SpiPsInputCntl0 = 0x191;

    [Fact]
    public void MatchesSemanticIdsAndCustomFlags()
    {
        var memory = CreateMemory(
            geometrySemantics:
            [
                0x0000_0000u,
                0x0000_0101u,
                0x0000_0202u,
                0x0000_0303u,
            ],
            pixelSemantics:
            [
                0x0000_0000u,
                0x0000_0002u,
                0x4100_0003u,
            ]);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        // Matched inputs carry the geometry register index in bits 0..4.
        AssertRegister(memory, 0, 0x000u);
        AssertRegister(memory, 1, 0x002u);
        // Flat-shaded input: matched index 3 plus the flat bit 0x400.
        AssertRegister(memory, 2, 0x423u);
        // Registers past the pixel-input count keep the identity mapping.
        AssertRegister(memory, 3, 0x003u);
        AssertRegister(memory, 31, 0x01Fu);
    }

    [Fact]
    public void PreservesF16Mode()
    {
        var memory = CreateMemory(
            geometrySemantics: [0x0010_0705u],
            pixelSemantics: [0x0010_0005u]);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertRegister(memory, 0, 0x0118_0007u);
    }

    [Fact]
    public void UnmatchedInputGetsDefaultValueBit()
    {
        var memory = CreateMemory(
            geometrySemantics: [0x0000_0007u],
            pixelSemantics: [0x0000_0001u]);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        // Semantic 0x01 has no geometry match: default-value bit, no index.
        AssertRegister(memory, 0, 0x020u);
        AssertRegister(memory, 1, 0x001u);
    }

    [Fact]
    public void NullPixelShaderWritesIdentityMapping()
    {
        var memory = CreateMemory(
            geometrySemantics: [0x0000_0000u],
            pixelSemantics: [0x0000_0000u]);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistersAddress;
        ctx[CpuRegister.Rsi] = GeometryShaderAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AgcExports.CreateInterpolantMapping(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        for (uint index = 0; index < 32; index++)
        {
            AssertRegister(memory, index, index);
        }
    }

    [Fact]
    public void NullRegistersAddressIsRejected()
    {
        var memory = CreateMemory(
            geometrySemantics: [0x0000_0000u],
            pixelSemantics: [0x0000_0000u]);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = GeometryShaderAddress;
        ctx[CpuRegister.Rdx] = PixelShaderAddress;

        var result = AgcExports.CreateInterpolantMapping(ctx);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
    }

    [Fact]
    public void MasksPackedOutputSemanticsCountToUInt16()
    {
        // num_output_semantics is a uint16; the enclosing dword carries other
        // packed header fields in its high half that must not inflate the
        // geometry search range past the single real entry.
        var memory = CreateMemory(
            geometrySemantics: [0x0000_0305u],
            pixelSemantics: [0x0000_0005u, 0x0000_0006u]);
        memory.WriteUInt32(
            GeometryShaderAddress + ShaderNumOutputSemanticsOffset,
            0xABCD_0001u);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertRegister(memory, 0, 0x003u);
        // Semantic 0x06 finds no match inside the masked count of one.
        AssertRegister(memory, 1, 0x020u);
    }

    private static SparseGuestMemory CreateMemory(
        IReadOnlyList<uint> geometrySemantics,
        IReadOnlyList<uint> pixelSemantics)
    {
        var memory = new SparseGuestMemory();
        memory.WriteUInt64(
            GeometryShaderAddress + ShaderOutputSemanticsOffset,
            OutputSemanticsAddress);
        memory.WriteUInt32(
            GeometryShaderAddress + ShaderNumOutputSemanticsOffset,
            (uint)geometrySemantics.Count);
        memory.WriteUInt64(
            PixelShaderAddress + ShaderInputSemanticsOffset,
            InputSemanticsAddress);
        memory.WriteUInt32(
            PixelShaderAddress + ShaderNumInputSemanticsOffset,
            (uint)pixelSemantics.Count);

        for (var index = 0; index < geometrySemantics.Count; index++)
        {
            memory.WriteUInt32(
                OutputSemanticsAddress + (ulong)(index * sizeof(uint)),
                geometrySemantics[index]);
        }

        for (var index = 0; index < pixelSemantics.Count; index++)
        {
            memory.WriteUInt32(
                InputSemanticsAddress + (ulong)(index * sizeof(uint)),
                pixelSemantics[index]);
        }

        return memory;
    }

    private static int Invoke(SparseGuestMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistersAddress;
        ctx[CpuRegister.Rsi] = GeometryShaderAddress;
        ctx[CpuRegister.Rdx] = PixelShaderAddress;
        return AgcExports.CreateInterpolantMapping(ctx);
    }

    private static void AssertRegister(
        SparseGuestMemory memory,
        uint index,
        uint expectedValue)
    {
        var address = RegistersAddress + (index * 8);
        Assert.Equal(SpiPsInputCntl0 + index, ReadUInt32(memory, address));
        Assert.Equal(expectedValue, ReadUInt32(memory, address + sizeof(uint)));
    }

    private static uint ReadUInt32(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
