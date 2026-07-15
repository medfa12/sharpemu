// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// RDNA2 instruction-decode tests for <see cref="Gen5ShaderTranslator"/> via
/// its public TryCreateState entry, using hand-encoded machine words.
/// </summary>
public sealed class Gen5ShaderTranslatorTests
{
    private const ulong ShaderAddress = 0x8000;
    private const uint SEndpgm = 0xBF81_0000;

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint>(),
            userDataBaseRegister: 0,
            out var state,
            out var error);
        Assert.True(ok, $"TryCreateState failed: {error}");
        return state.Program;
    }

    private static string DecodeError(params uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint>(),
            userDataBaseRegister: 0,
            out _,
            out var error);
        Assert.False(ok);
        return error;
    }

    [Fact]
    public void Decode_SMovB32WithLiteral_YieldsLiteralOperand()
    {
        // s_mov_b32 s0, 0xDEADBEEF: SOP1 op 0x03, sdst 0, ssrc0 0xFF (literal follows)
        var program = Decode(0xBE80_03FF, 0xDEAD_BEEF, SEndpgm);

        Assert.Equal(2, program.Instructions.Count);
        var mov = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sop1, mov.Encoding);
        Assert.Equal("SMovB32", mov.Opcode);
        Assert.Equal(2, mov.Words.Count);
        var source = Assert.Single(mov.Sources);
        Assert.Equal(Gen5OperandKind.LiteralConstant, source.Kind);
        Assert.Equal(0xDEAD_BEEFu, source.Value);
        Assert.Equal(Gen5Operand.Scalar(0), Assert.Single(mov.Destinations));
        Assert.Equal(8u, program.Instructions[1].Pc); // opcode + literal dword = 8 bytes
    }

    [Fact]
    public void Decode_SAddU32_YieldsScalarOperands()
    {
        // s_add_u32 s2, s0, s1: SOP2 op 0x00, sdst 2, ssrc1 1, ssrc0 0
        var program = Decode(0x8002_0100, SEndpgm);

        var add = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sop2, add.Encoding);
        Assert.Equal("SAddU32", add.Opcode);
        Assert.Equal(Gen5Operand.Scalar(0), add.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(1), add.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(2), Assert.Single(add.Destinations));
    }

    [Fact]
    public void Decode_SMovkI32_YieldsImmediateOperand()
    {
        // s_movk_i32 s3, 0x8000: SOPK op 0x60, sdst 3, simm16 0x8000
        var program = Decode(0xB003_8000, SEndpgm);

        var movk = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sopk, movk.Encoding);
        Assert.Equal("SMovkI32", movk.Opcode);
        var immediate = Assert.Single(movk.Sources);
        Assert.Equal(Gen5OperandKind.EncodedConstant, immediate.Kind);
        Assert.Equal(0x8000u, immediate.Value);
        Assert.Equal(Gen5Operand.Scalar(3), Assert.Single(movk.Destinations));
    }

    [Fact]
    public void Decode_SBranch_IsRecognizedAsSopp()
    {
        // s_branch +1: SOPP op 0x02, simm16 1
        var program = Decode(0xBF82_0001, SEndpgm, SEndpgm);

        var branch = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sopp, branch.Encoding);
        Assert.Equal("SBranch", branch.Opcode);
    }

    [Fact]
    public void Decode_StopsAtFirstSEndpgm()
    {
        // Garbage after s_endpgm must never be decoded.
        var program = Decode(SEndpgm, 0xFFFF_FFFF);

        var end = Assert.Single(program.Instructions);
        Assert.Equal("SEndpgm", end.Opcode);
    }

    [Fact]
    public void Decode_UnknownSop1Opcode_Fails()
    {
        // SOP1 op 0x50 is not in the decode table.
        var error = DecodeError(0xBE80_5000, SEndpgm);

        Assert.Contains("unknown-sop1", error);
    }

    [Fact]
    public void Decode_UnmappedProgram_FailsWithReadError()
    {
        var error = DecodeError(0xBE80_03FF); // literal + rest of program missing

        Assert.Contains("read-failed", error);
    }

    [Fact]
    public void Decode_ZeroAddress_Fails()
    {
        var memory = new SparseGuestMemory();
        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            shaderAddress: 0,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint>(),
            userDataBaseRegister: 0,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Equal("missing", error);
    }

    [Fact]
    public void TryCreateState_PopulatesUserDataFromShaderRegisters()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint> { [0x40] = 111, [0x41] = 222 };

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0x40,
            out var state,
            out _));

        Assert.Equal(111u, state.UserData[0]);
        Assert.Equal(222u, state.UserData[1]);
        Assert.Equal(16, state.UserData.Count); // MinimumUserDataDwords without metadata
    }

    [Fact]
    public void Decode_EndToEnd_ScalarProgramEvaluates()
    {
        // Full pipeline: machine words -> decoder -> scalar evaluator.
        // s_mov_b32 s0, 0x11110000; s_movk_i32 s1, 0x2222; s_add_u32 s2, s0, s1; s_endpgm
        var memory = new SparseGuestMemory();
        memory.WriteWords(
            ShaderAddress,
            0xBE80_03FF, 0x1111_0000,
            0xB001_2222,
            0x8002_0100,
            SEndpgm);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, 0, new Dictionary<uint, uint>(), 0, out var state, out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out var evalError), evalError);

        Assert.Equal(0x1111_0000u, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x2222u, evaluation.ScalarRegisters[1]);
        Assert.Equal(0x1111_2222u, evaluation.ScalarRegisters[2]);
    }
}
