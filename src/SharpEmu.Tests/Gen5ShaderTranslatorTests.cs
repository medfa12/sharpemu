// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
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
    private const uint PsUserData = 0x0C;
    private const uint PsPgmRsrc2 = 0x0B;
    private const uint VsUserData = 0x4C;
    private const uint VsPgmRsrc2 = 0x4B;
    private const uint EsUserData = 0xCC;
    private const uint EsPgmRsrc2 = 0xCB;
    private const uint ComputeUserData = 0x240;
    private const uint ComputePgmRsrc2 = 0x213;

    private static Dictionary<uint, uint> ComputeRegisters(uint userSgprCount = 0) => new()
    {
        [ComputePgmRsrc2] = userSgprCount << 1,
    };

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
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
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
            out _,
            out var error);
        Assert.False(ok);
        return error;
    }

    [Theory]
    [InlineData("ImageLoad", true)]
    [InlineData("ImageLoadMip", true)]
    [InlineData("ImageStore", true)]
    [InlineData("ImageStoreMip", true)]
    [InlineData("ImageAtomicAdd", true)]
    [InlineData("ImageSample", false)]
    [InlineData("ImageSampleLzO", false)]
    [InlineData("ImageGetResinfo", false)]
    public void IsStorageImageOperation_ClassifiesMimgOpcodes(string opcode, bool expected)
    {
        // ImageLoad reads through a storage image binding (OpImageRead); the
        // same descriptor can also be stored to, so both must share one
        // storage-class declaration.
        Assert.Equal(expected, Gen5ShaderTranslator.IsStorageImageOperation(opcode));
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
    public void Decode_VReadfirstlaneB32_TargetsScalarDestination()
    {
        // v_readfirstlane_b32 s5, v3: VOP1 op 0x02, dst field 5, src0 v3.
        // The VOP1 destination field names an SGPR for this opcode; decoding
        // it as a VGPR leaves the scalar register stale and clobbers v5.
        var program = Decode(0x7E0A_0503, SEndpgm);

        var lane = program.Instructions[0];
        Assert.Equal("VReadfirstlaneB32", lane.Opcode);
        Assert.Equal(Gen5Operand.Scalar(5), Assert.Single(lane.Destinations));
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(lane.Sources));
    }

    [Fact]
    public void Decode_VMovB32_KeepsVectorDestination()
    {
        // v_mov_b32 v5, v3: VOP1 op 0x01 destinations stay vector registers.
        var program = Decode(0x7E0A_0303, SEndpgm);

        var mov = program.Instructions[0];
        Assert.Equal("VMovB32", mov.Opcode);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(mov.Destinations));
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
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
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
        var registers = new Dictionary<uint, uint>
        {
            [VsPgmRsrc2] = 2u << 1, // USER_SGPR=2
            [VsUserData] = 111,
            [VsUserData + 1] = 222,
        };

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: VsUserData,
            out var state,
            out _));

        Assert.Equal(111u, state.UserData[0]);
        Assert.Equal(222u, state.UserData[1]);
        // The seed window is the hardware USER_SGPR count from RSRC2, not a
        // metadata heuristic.
        Assert.Equal(2, state.UserData.Count);
    }

    [Fact]
    public void TryCreateState_PsSixthUserSgprBit_WidensRegisterWindow()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint>
        {
            // USER_SGPR=3 plus the GFX10 USER_SGPR_MSB bit => 35 registers.
            [PsPgmRsrc2] = (3u << 1) | (1u << 27),
        };
        for (var index = 0u; index < 35; index++)
        {
            registers[PsUserData + index] = 0x100 + index;
        }

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: PsUserData,
            out var state,
            out var error), error);

        Assert.Equal(35, state.UserData.Count);
        Assert.Equal(0x100u, state.UserData[0]);
        Assert.Equal(0x100u + 34, state.UserData[34]);
    }

    [Fact]
    public void TryCreateState_EsIgnoresSixthUserSgprBit()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint>
        {
            [EsPgmRsrc2] = (7u << 1) | (1u << 27), // ES has no USER_SGPR_MSB
        };

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: EsUserData,
            out var state,
            out var error), error);

        Assert.Equal(7, state.UserData.Count);
    }

    [Fact]
    public void TryCreateState_MissingRsrc2_FailsWithUserSgprCountError()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint>(),
            userDataBaseRegister: PsUserData,
            out _,
            out var error);

        Assert.False(ok);
        Assert.StartsWith("missing-user-sgpr-count", error);
    }

    [Fact]
    public void TryCreateState_UnknownUserDataBase_Fails()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint> { [0x3F] = 2u << 1 };

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: 0x40,
            out _,
            out var error);

        Assert.False(ok);
        Assert.StartsWith("missing-user-sgpr-count", error);
    }

    [Fact]
    public void TryCreateState_CapturesProgramResource1()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint>
        {
            [PsPgmRsrc2] = 1u << 1,
            [PsPgmRsrc2 - 1] = 0xDEAD_0001,
        };

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: PsUserData,
            out var state,
            out var error), error);

        Assert.Equal(0xDEAD_0001u, state.ProgramResource1);
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
            ctx, ShaderAddress, 0, ComputeRegisters(), ComputeUserData, out var state, out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out var evalError), evalError);

        Assert.Equal(0x1111_0000u, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x2222u, evaluation.ScalarRegisters[1]);
        Assert.Equal(0x1111_2222u, evaluation.ScalarRegisters[2]);
    }

    [Fact]
    public void Decode_VCmpSdwa_ExplicitScalarDestination_RoutesSdst()
    {
        // v_cmp_lt_f32_sdwa s[38:39], v0, v1 with dword selects. VOPC uses
        // the SDWAB layout: sdst at bits 8-14, its valid bit at 15.
        var program = Decode(0x7C0202F9, 0x0606A600, SEndpgm);

        var compare = program.Instructions[0];
        Assert.Equal("VCmpLtF32", compare.Opcode);
        var control = Assert.IsType<Gen5SdwaControl>(compare.Control);
        Assert.Equal(38u, control.ScalarDestination);
        Assert.Equal(6u, control.DestinationSelect); // forced no-op dword select
        Assert.Equal(6u, control.Source0Select);
        Assert.Equal(6u, control.Source1Select);
        Assert.Equal(0u, control.OutputModifier); // sdst bits, not omod/clamp
        Assert.False(control.Clamp);
    }

    [Fact]
    public void Decode_VCmpSdwa_NoScalarDestination_DefaultsToVcc()
    {
        // Same compare with SD=0: the mask goes to VCC (s106).
        var program = Decode(0x7C0202F9, 0x0606_0000, SEndpgm);

        var control = Assert.IsType<Gen5SdwaControl>(program.Instructions[0].Control);
        Assert.Equal(106u, control.ScalarDestination);
    }

    [Fact]
    public void Decode_Vop2Sdwa_HasNoScalarDestination()
    {
        // v_add_f32_sdwa v0, v0, v1 uses the plain SDWA word, whose bits 8-15
        // are dst_sel/dst_u/clamp/omod rather than an sdst.
        var program = Decode(0x0600_02F9, 0x0606_0600, SEndpgm);

        var add = program.Instructions[0];
        Assert.Equal("VAddF32", add.Opcode);
        var control = Assert.IsType<Gen5SdwaControl>(add.Control);
        Assert.Null(control.ScalarDestination);
        Assert.Equal(6u, control.DestinationSelect);
    }

    private const ulong HeaderAddress = 0x9000;
    private const uint ShaderSizeOffset = 0x44;
    private const uint SNop = 0xBF80_0000;

    [Fact]
    public void Decode_ProgramLongerThan4096Instructions_ReachesEndProgram()
    {
        // Old decoder capped at 4096 instructions; 4097 must now decode fully.
        var words = new uint[4097];
        Array.Fill(words, SNop);
        words[^1] = SEndpgm;

        var program = Decode(words);

        Assert.Equal(4097, program.Instructions.Count);
        Assert.Equal("SEndpgm", program.Instructions[^1].Opcode);
    }

    [Fact]
    public void Decode_HeaderDeclaredSize_BoundsDecode()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SNop, SEndpgm);
        memory.WriteUInt32(HeaderAddress + ShaderSizeOffset, 8); // exact byte length

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            HeaderAddress,
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
            out var state,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(2, state.Program.Instructions.Count);
    }

    [Theory]
    [InlineData(0u)] // zero
    [InlineData(6u)] // not dword-aligned
    [InlineData(1024u * 1024u + 4u)] // above the 1MB ceiling
    public void Decode_InvalidHeaderSize_Fails(uint declaredSize)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        memory.WriteUInt32(HeaderAddress + ShaderSizeOffset, declaredSize);

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            HeaderAddress,
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
            out _,
            out var error);

        Assert.False(ok);
        Assert.StartsWith("invalid-shader-size", error);
    }

    [Fact]
    public void Decode_DeclaredSizeTruncatingProgram_Fails()
    {
        // SEndpgm sits at byte 8, but the header only declares 8 bytes.
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SNop, SNop, SEndpgm);
        memory.WriteUInt32(HeaderAddress + ShaderSizeOffset, 8);

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            HeaderAddress,
            shaderRegisters: ComputeRegisters(),
            userDataBaseRegister: ComputeUserData,
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("unterminated", error);
        Assert.Contains("size=0x8", error);
    }

    [Fact]
    public void Decode_CacheIsKeyedByAddressAndSize()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SNop, SEndpgm);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var registers = ComputeRegisters();

        // Headerless decode succeeds and is cached under (address, 0).
        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, 0, registers, ComputeUserData, out _, out var error), error);

        // A header declaring 4 bytes truncates before SEndpgm; an address-only
        // cache would wrongly return the cached full decode here.
        memory.WriteUInt32(HeaderAddress + ShaderSizeOffset, 4);
        Assert.False(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, HeaderAddress, registers, ComputeUserData, out _, out error));
        Assert.Contains("unterminated", error);

        // The full declared size still decodes independently.
        memory.WriteUInt32(HeaderAddress + ShaderSizeOffset, 8);
        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, HeaderAddress, registers, ComputeUserData, out var state, out error), error);
        Assert.Equal(2, state.Program.Instructions.Count);
    }
}
