// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5PackedF16ClampSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;

    [Theory]
    [InlineData(0xCC0EC003u, 0x1C0A0300u, "VPkFmaF16")]
    [InlineData(0xCC0FC003u, 0x18020300u, "VPkAddF16")]
    [InlineData(0xCC10C003u, 0x18020300u, "VPkMulF16")]
    [InlineData(0xCC11C003u, 0x18020300u, "VPkMinF16")]
    [InlineData(0xCC12C003u, 0x18020300u, "VPkMaxF16")]
    public void PackedF16Clamp_EmitsOrderedUnitIntervalClamp(
        uint first,
        uint second,
        string opcode)
    {
        var (instruction, spirv) = Compile(first, second);

        Assert.Equal(opcode, instruction.Opcode);
        Assert.True(Assert.IsType<Gen5Vop3pControl>(instruction.Control).Clamp);
        var opcodes = CollectOpcodes(spirv);
        Assert.Contains((ushort)SpirvOp.FOrdGreaterThan, opcodes);
        Assert.Contains((ushort)SpirvOp.FOrdLessThan, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);
    }

    private static (Gen5ShaderInstruction Instruction, byte[] Spirv) Compile(
        uint first,
        uint second)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, [first, second]);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [ComputePgmRsrc2Register] = 16u << 1,
        };

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
        return (state.Program.Instructions[0], shader.Spirv);
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

    private static void WriteProgram(FakeCpuMemory memory, uint[] words)
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
