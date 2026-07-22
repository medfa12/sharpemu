// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5SpirvAtomicTranslationTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x1_0000_1000;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;

    private static readonly uint[] AtomicSpirvOpcodes =
    [
        (ushort)SpirvOp.AtomicExchange,
        (ushort)SpirvOp.AtomicCompareExchange,
        (ushort)SpirvOp.AtomicIIncrement,
        (ushort)SpirvOp.AtomicIDecrement,
        (ushort)SpirvOp.AtomicIAdd,
        (ushort)SpirvOp.AtomicISub,
        (ushort)SpirvOp.AtomicSMin,
        (ushort)SpirvOp.AtomicUMin,
        (ushort)SpirvOp.AtomicSMax,
        (ushort)SpirvOp.AtomicUMax,
        (ushort)SpirvOp.AtomicAnd,
        (ushort)SpirvOp.AtomicOr,
        (ushort)SpirvOp.AtomicXor,
    ];

    [Fact]
    public void BufferAtomics_AllVariantsTranslate()
    {
        var words = new List<uint>();
        foreach (var opcode in new uint[]
                 {
                     0x30, 0x31, 0x32, 0x33, 0x35, 0x36, 0x37,
                     0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
                 })
        {
            words.Add(0xE0004000u | (opcode << 18));
            words.Add(0x80000100u);
        }

        var opcodes = CompileCompute(words.ToArray(), BufferDescriptorRegisters());

        AssertAtomicOpcodes(opcodes);
    }

    [Fact]
    public void DataShareAtomics_AllVariantsTranslate()
    {
        var words = new List<uint>();
        foreach (var opcode in new uint[]
                 {
                     0x00, 0x01, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                     0x09, 0x0A, 0x0B, 0x10, 0x20, 0x21, 0x23, 0x24,
                     0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2D, 0x30,
                 })
        {
            words.Add(0xD8000000u | (opcode << 18));
            words.Add(opcode is 0x10 or 0x30 ? 0x03020100u : 0x03000100u);
        }

        var opcodes = CompileCompute(words.ToArray(), []);

        AssertAtomicOpcodes(opcodes);
    }

    private static void AssertAtomicOpcodes(HashSet<ushort> opcodes)
    {
        foreach (var opcode in AtomicSpirvOpcodes)
        {
            Assert.Contains((ushort)opcode, opcodes);
        }
    }

    private static Dictionary<uint, uint> BufferDescriptorRegisters() => new()
    {
        [0] = unchecked((uint)BufferAddress),
        [1] = (uint)(BufferAddress >> 32),
        [2] = 64,
        [3] = 0,
    };

    private static HashSet<ushort> CompileCompute(
        uint[] programWords,
        Dictionary<uint, uint> userDataSgprs)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [ComputePgmRsrc2Register] = 16u << 1,
        };
        foreach (var (sgpr, value) in userDataSgprs)
        {
            shaderRegisters[ComputeUserDataRegister + sgpr] = value;
        }

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return CollectOpcodes(shader.Spirv);
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }
}
