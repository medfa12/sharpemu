// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderAtomicDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;

    public static TheoryData<uint, string, uint> BufferAtomicCases => new()
    {
        { 0x30, "BufferAtomicSwap", 1 },
        { 0x31, "BufferAtomicCmpswap", 2 },
        { 0x32, "BufferAtomicAdd", 1 },
        { 0x33, "BufferAtomicSub", 1 },
        { 0x35, "BufferAtomicSmin", 1 },
        { 0x36, "BufferAtomicUmin", 1 },
        { 0x37, "BufferAtomicSmax", 1 },
        { 0x38, "BufferAtomicUmax", 1 },
        { 0x39, "BufferAtomicAnd", 1 },
        { 0x3A, "BufferAtomicOr", 1 },
        { 0x3B, "BufferAtomicXor", 1 },
        { 0x3C, "BufferAtomicInc", 1 },
        { 0x3D, "BufferAtomicDec", 1 },
    };

    public static TheoryData<uint, string, bool, int> DataShareAtomicCases => new()
    {
        { 0x00, "DsAddU32", false, 2 },
        { 0x01, "DsSubU32", false, 2 },
        { 0x03, "DsIncU32", false, 2 },
        { 0x04, "DsDecU32", false, 2 },
        { 0x05, "DsMinI32", false, 2 },
        { 0x06, "DsMaxI32", false, 2 },
        { 0x07, "DsMinU32", false, 2 },
        { 0x08, "DsMaxU32", false, 2 },
        { 0x09, "DsAndB32", false, 2 },
        { 0x0A, "DsOrB32", false, 2 },
        { 0x0B, "DsXorB32", false, 2 },
        { 0x10, "DsCmpstB32", false, 3 },
        { 0x20, "DsAddRtnU32", true, 2 },
        { 0x21, "DsSubRtnU32", true, 2 },
        { 0x23, "DsIncRtnU32", true, 2 },
        { 0x24, "DsDecRtnU32", true, 2 },
        { 0x25, "DsMinRtnI32", true, 2 },
        { 0x26, "DsMaxRtnI32", true, 2 },
        { 0x27, "DsMinRtnU32", true, 2 },
        { 0x28, "DsMaxRtnU32", true, 2 },
        { 0x29, "DsAndRtnB32", true, 2 },
        { 0x2A, "DsOrRtnB32", true, 2 },
        { 0x2B, "DsXorRtnB32", true, 2 },
        { 0x2D, "DsWrxchgRtnB32", true, 2 },
        { 0x30, "DsCmpstRtnB32", true, 3 },
    };

    [Theory]
    [MemberData(nameof(BufferAtomicCases))]
    public void BufferAtomic_DecodesDataWidth(uint opcode, string name, uint dwordCount)
    {
        var instruction = DecodeSingle(
            0xE0004000u | (opcode << 18),
            0x80000100u);

        Assert.Equal(name, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(dwordCount, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.True(control.Glc);
        Assert.Equal((int)dwordCount, instruction.Destinations.Count);
    }

    [Theory]
    [MemberData(nameof(DataShareAtomicCases))]
    public void DataShareAtomic_DecodesOperands(
        uint opcode,
        string name,
        bool returnsValue,
        int sourceCount)
    {
        var compareSwap = opcode == 0x10 || opcode == 0x30;
        var extra = compareSwap ? 0x03020100u : 0x03000100u;
        var instruction = DecodeSingle(0xD8000000u | (opcode << 18), extra);

        Assert.Equal(name, instruction.Opcode);
        Assert.Equal(sourceCount, instruction.Sources.Count);
        Assert.Equal(returnsValue ? 1 : 0, instruction.Destinations.Count);
        if (compareSwap)
        {
            Assert.Equal(Gen5Operand.Vector(1), instruction.Sources[1]);
            Assert.Equal(Gen5Operand.Vector(2), instruction.Sources[2]);
        }
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [ComputePgmRsrc2Register] = 0 },
                ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        return state.Program.Instructions[0];
    }

    internal static void WriteProgram(FakeCpuMemory memory, uint[] words)
    {
        var address = ShaderAddress;
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        foreach (var word in words.Append(0xBF810000u))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, word);
            Assert.True(memory.TryWrite(address, buffer));
            address += sizeof(uint);
        }
    }
}
