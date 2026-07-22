// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5FmaMixSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;

    [Fact]
    public void FmaMixF32_TranslatesToFmaAndAbsoluteValue()
    {
        var spirv = Compile([0xCC201103u, 0x9C0A0300u]);

        Assert.True(ContainsExtInst(spirv, 50));
        Assert.True(ContainsExtInst(spirv, 4));
    }

    [Theory]
    [InlineData(0xCC214003u, 0x1C0A0300u)]
    [InlineData(0xCC224003u, 0x1C0A0300u)]
    public void FmaMixHalf_TranslatesWithoutDroppingShader(uint first, uint second)
    {
        var spirv = Compile([first, second]);

        Assert.True(ContainsExtInst(spirv, 50));
    }

    private static bool ContainsExtInst(byte[] spirv, uint instruction)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if ((ushort)word == 12 && wordCount >= 5 && ReadWord(spirv, offset + 16) == instruction)
            {
                return true;
            }

            if (wordCount <= 0)
            {
                return false;
            }

            offset += wordCount * sizeof(uint);
        }

        return false;
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, programWords);
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
        return shader.Spirv;
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
