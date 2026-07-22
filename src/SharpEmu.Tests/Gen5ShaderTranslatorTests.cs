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

    [Theory]
    [InlineData(0xBF97_0001u, "SCbranchCdbgsys")]
    [InlineData(0xBF98_0001u, "SCbranchCdbguser")]
    [InlineData(0xBF99_0001u, "SCbranchCdbgsysOrUser")]
    [InlineData(0xBF9A_0001u, "SCbranchCdbgsysAndUser")]
    public void Decode_ConditionalDebugBranch_DecodesAsSoppBranch(uint word, string expected)
    {
        // GFX10 SOPP 0x17-0x1A branch on the COND_DBG_SYS/USER mode flags a
        // debugger sets; retail hardware always falls through.
        var program = Decode(word, 0x7E000280, SEndpgm);

        var branch = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sopp, branch.Encoding);
        Assert.Equal(expected, branch.Opcode);
    }

    [Theory]
    [InlineData(0xBF9B_0000u)] // s_endpgm_saved
    [InlineData(0xBF9E_0000u)] // s_endpgm_ordered_ps_done
    public void Decode_EndpgmVariant_TerminatesProgram(uint word)
    {
        var program = Decode(word);

        var end = Assert.Single(program.Instructions);
        Assert.Equal("SEndpgm", end.Opcode);
    }

    [Fact]
    public void Decode_SWaitcntVscnt_IsRecognizedAsSopk()
    {
        // s_waitcnt_vscnt null, 0: SOPK op 0x17 with NULL (125) in SDST.
        var program = Decode(0xBBFD_0000, SEndpgm);

        var wait = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sopk, wait.Encoding);
        Assert.Equal("SWaitcntVscnt", wait.Opcode);
    }

    [Fact]
    public void Decode_SmemNullSoffset_IsConstantZero()
    {
        // s_buffer_load_dword s106, s[28:31], null offset:0x74. GFX10 operand
        // 125 is architectural NULL; treating it as mutable s125 can offset
        // every constant-buffer read in a shader.
        var program = Decode(0xF420_1A8E, 0xFA00_0074, SEndpgm);

        var load = program.Instructions[0];
        Assert.Equal("SBufferLoadDword", load.Opcode);
        Assert.Equal(Gen5Operand.Scalar(28), load.Sources[0]);
        Assert.Equal(
            new Gen5Operand(Gen5OperandKind.EncodedConstant, 125),
            load.Sources[1]);
        var control = Assert.IsType<Gen5ScalarMemoryControl>(load.Control);
        Assert.Equal(116, control.ImmediateOffsetBytes);
        Assert.Null(control.DynamicOffsetRegister);
    }

    [Theory]
    [InlineData(0x0Fu, "ImageAtomicSwap")]
    [InlineData(0x11u, "ImageAtomicAdd")]
    [InlineData(0x12u, "ImageAtomicSub")]
    [InlineData(0x14u, "ImageAtomicSmin")]
    [InlineData(0x15u, "ImageAtomicUmin")]
    [InlineData(0x16u, "ImageAtomicSmax")]
    [InlineData(0x17u, "ImageAtomicUmax")]
    [InlineData(0x18u, "ImageAtomicAnd")]
    [InlineData(0x19u, "ImageAtomicOr")]
    [InlineData(0x1Au, "ImageAtomicXor")]
    [InlineData(0x1Bu, "ImageAtomicInc")]
    public void Decode_ImageAtomic_DecodesAsMimgWithVectorDestination(uint op, string expected)
    {
        // image_atomic_* v2, v[0:1], s[8:15] dmask:0x1 dim:2D glc:
        // MIMG op in bits 24:18, vdata v2, srsrc s[8:15].
        var word0 = 0xF000_2108u | (op << 18);
        var program = Decode(word0, 0x0002_0200, SEndpgm);

        var atomic = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Mimg, atomic.Encoding);
        Assert.Equal(expected, atomic.Opcode);
        Assert.Equal(Gen5Operand.Vector(2), Assert.Single(atomic.Destinations));
        var control = Assert.IsType<Gen5ImageControl>(atomic.Control);
        Assert.Equal(8u, control.ScalarResource);
        Assert.True(control.Glc);
    }

    [Fact]
    public void Decode_SAbsI32_DecodesWithScalarOperands()
    {
        // s_abs_i32 s13, s24 (SOP1 0x34): the exact word Astro Bot's
        // title-time compute shaders hit.
        var program = Decode(0xBE8D3418, SEndpgm);

        var abs = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sop1, abs.Encoding);
        Assert.Equal("SAbsI32", abs.Opcode);
        Assert.Equal(Gen5Operand.Scalar(13), Assert.Single(abs.Destinations));
        Assert.Equal(Gen5Operand.Scalar(24), Assert.Single(abs.Sources));
    }

    [Fact]
    public void Decode_VXnorB32_DecodesAsVop2()
    {
        // v_xnor_b32 v2, v0, v1 (VOP2 0x1E, new on GFX10); Astro Bot's
        // particle-emitter compute shader 0x555F4F500 uses it.
        var program = Decode(0x3C040300, SEndpgm);

        var xnor = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Vop2, xnor.Encoding);
        Assert.Equal("VXnorB32", xnor.Opcode);
        Assert.Equal(Gen5Operand.Vector(2), Assert.Single(xnor.Destinations));
    }

    [Fact]
    public void Decode_VMbcntHiU32B32_DecodesAsVop3()
    {
        // v_mbcnt_hi_u32_b32 v2, v0, v1 through the VOP3 encoding (0x366).
        var program = Decode(0xD766_0002, 0x0002_0300, SEndpgm);

        var mbcnt = program.Instructions[0];
        Assert.Equal("VMbcntHiU32B32", mbcnt.Opcode);
        Assert.Equal(Gen5Operand.Vector(2), Assert.Single(mbcnt.Destinations));
    }

    [Fact]
    public void Decode_DsReadB64_YieldsPairedDestinations()
    {
        // ds_read_b64 v[4:5], v1 offset:16 - word 0xD9D80010 from Astro Bot's
        // title compute shader 0x500698B00.
        var program = Decode(0xD9D80010, 0x04000001, SEndpgm);

        var read = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Ds, read.Encoding);
        Assert.Equal("DsReadB64", read.Opcode);
        Assert.Equal(2, read.Destinations.Count);
        Assert.Equal(Gen5Operand.Vector(4), read.Destinations[0]);
        Assert.Equal(Gen5Operand.Vector(5), read.Destinations[1]);
        var control = Assert.IsType<Gen5DataShareControl>(read.Control);
        Assert.Equal(0x10u, control.Offset0);
    }

    [Fact]
    public void Decode_DsWriteB96_ConsumesThreeDataRegisters()
    {
        // ds_write_b96 v1, v[8:10] offset0:0x10 offset1:0x05 - word
        // 0xDB780510 from Astro Bot's title compute shader 0x5006E7A00 family.
        var program = Decode(0xDB780510, 0x00000801, SEndpgm);

        var write = program.Instructions[0];
        Assert.Equal("DsWriteB96", write.Opcode);
        Assert.Equal(4, write.Sources.Count);
        Assert.Equal(Gen5Operand.Vector(1), write.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(8), write.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(10), write.Sources[3]);
        Assert.Empty(write.Destinations);
    }

    [Fact]
    public void Decode_DsWriteB128_ConsumesFourDataRegisters()
    {
        // ds_write_b128 v0, v[4:7] - word 0xDB7C0000 from Astro Bot's title
        // compute shaders 0x5006F9600/0x5006FA200.
        var program = Decode(0xDB7C0000, 0x00000400, SEndpgm);

        var write = program.Instructions[0];
        Assert.Equal("DsWriteB128", write.Opcode);
        Assert.Equal(5, write.Sources.Count);
        Assert.Equal(Gen5Operand.Vector(0), write.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(4), write.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(7), write.Sources[4]);
    }

    [Fact]
    public void Decode_DsWriteAddtidB32_HasNoAddressOperand()
    {
        // ds_write_addtid_b32 v3 offset1:0x07 - word 0xDAC00700 from Astro
        // Bot's emitter-side compute shader 0x555F41F00; the LDS address is
        // offset + 4 * thread id, so only the data register is consumed.
        var program = Decode(0xDAC00700, 0x00000300, SEndpgm);

        var write = program.Instructions[0];
        Assert.Equal("DsWriteAddtidB32", write.Opcode);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(write.Sources));
        Assert.Empty(write.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(write.Control);
        Assert.Equal(0x07u, control.Offset1);
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
    public void Decode_SFf1I32B64_YieldsSingleScalarDestination()
    {
        // s_ff1_i32_b64 s2, exec: SOP1 op 0x14, sdst 2, ssrc0 0x7E.
        var program = Decode(0xBE82_147E, SEndpgm);

        var ff1 = program.Instructions[0];
        Assert.Equal("SFF1I32B64", ff1.Opcode);
        Assert.Equal(Gen5Operand.Scalar(2), Assert.Single(ff1.Destinations));
        Assert.Equal(Gen5Operand.Scalar(126), Assert.Single(ff1.Sources));
    }

    [Fact]
    public void Decode_SAndSaveexecB32_IsRecognized()
    {
        // s_and_saveexec_b32 s3, s2: GFX10 wave32 SOP1 op 0x3C.
        var program = Decode(0xBE83_3C02, SEndpgm);

        var saveexec = program.Instructions[0];
        Assert.Equal("SAndSaveexecB32", saveexec.Opcode);
        Assert.Equal(Gen5Operand.Scalar(3), Assert.Single(saveexec.Destinations));
        Assert.Equal(Gen5Operand.Scalar(2), Assert.Single(saveexec.Sources));
    }

    [Fact]
    public void Decode_VReadlaneB32_TargetsScalarDestination()
    {
        // v_readlane_b32 s4, v0, 0: GFX10 VOP3 op 0x360 with the SDST in the
        // VDST field (bits 7:0), src0 v0, src1 inline constant zero.
        var program = Decode(0xD760_0004, 0x0001_0100, SEndpgm);

        var readlane = program.Instructions[0];
        Assert.Equal("VReadlaneB32", readlane.Opcode);
        Assert.Equal(Gen5Operand.Scalar(4), Assert.Single(readlane.Destinations));
        Assert.Equal(Gen5Operand.Vector(0), readlane.Sources[0]);
    }

    [Fact]
    public void Decode_VWritelaneB32_KeepsVectorDestination()
    {
        // v_writelane_b32 v5, s2, 1: GFX10 VOP3 op 0x361.
        var program = Decode(0xD761_0005, 0x0001_0202, SEndpgm);

        var writelane = program.Instructions[0];
        Assert.Equal("VWritelaneB32", writelane.Opcode);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(writelane.Destinations));
        Assert.Equal(Gen5Operand.Scalar(2), writelane.Sources[0]);
    }

    [Fact]
    public void Decode_DsPermuteB32_YieldsLanePermuteOperands()
    {
        // ds_permute_b32 v3, v0, v1: DS op 0x3E, addr v0, data0 v1, vdst v3.
        var program = Decode(0xD8F8_0000, 0x0300_0100, SEndpgm);

        var permute = program.Instructions[0];
        Assert.Equal("DsPermuteB32", permute.Opcode);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(permute.Destinations));
        Assert.Equal(Gen5Operand.Vector(0), permute.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(1), permute.Sources[1]);
    }

    [Fact]
    public void Decode_Vop3bAddc_CarriesExplicitScalarDestination()
    {
        // v_add_co_ci_u32_e64 v6, s6, v0, v1, vcc: GFX10 VOP3B op 0x128.
        var program = Decode(0xD528_0606, 0x01AA_0300, SEndpgm);

        var addc = program.Instructions[0];
        Assert.Equal("VAddcU32", addc.Opcode);
        Assert.Equal(Gen5Operand.Vector(6), Assert.Single(addc.Destinations));
        Assert.Equal(3, addc.Sources.Count);
        Assert.Equal(Gen5Operand.Scalar(106), addc.Sources[2]);
        var control = Assert.IsType<Gen5Vop3Control>(addc.Control);
        Assert.Equal(6u, control.ScalarDestination);
    }

    [Fact]
    public void Decode_BufferLoadUbyte_IsSingleDwordLoad()
    {
        // buffer_load_ubyte v1, v0, s[4:7], 0: MUBUF op 0x08.
        var program = Decode(0xE020_0000, 0x8001_0100, SEndpgm);

        var load = program.Instructions[0];
        Assert.Equal("BufferLoadUbyte", load.Opcode);
        Assert.Equal(Gen5Operand.Vector(1), Assert.Single(load.Destinations));
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
    public void Decode_SWqmB32_YieldsSop1Operands()
    {
        // s_wqm_b32 exec_lo, exec_lo: SOP1 op 0x09, sdst 126, ssrc0 126
        // (exact word from the Astro Bot boot stream).
        var program = Decode(0xBEFE_097E, SEndpgm);

        var wqm = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sop1, wqm.Encoding);
        Assert.Equal("SWqmB32", wqm.Opcode);
        Assert.Equal(Gen5Operand.Scalar(126), Assert.Single(wqm.Sources));
        Assert.Equal(Gen5Operand.Scalar(126), Assert.Single(wqm.Destinations));
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
    public void TryCreateState_MissingRsrc2_FallsBackToMetadataHeuristic()
    {
        // A draw recorded before the stage's PGM_RSRC2 is first programmed must
        // still translate: without metadata the heuristic seeds the minimum
        // 16-dword user-data window instead of failing.
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint>(),
            userDataBaseRegister: PsUserData,
            out var state,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(16, state.UserData.Count);
    }

    [Fact]
    public void TryCreateState_EsMissingRsrc2_SeedsUserDataFromRegisters()
    {
        // Regression shape from Astro Bot's first submission: the ES stage's
        // user-data registers are programmed but SPI_SHADER_PGM_RSRC2_ES (0xCB)
        // has not been written yet. The fallback window must still pick up the
        // user data that is present.
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, SEndpgm);
        var registers = new Dictionary<uint, uint>
        {
            [EsUserData] = 0xAAA,
            [EsUserData + 3] = 0xBBB,
        };

        var ok = Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            registers,
            userDataBaseRegister: EsUserData,
            out var state,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(16, state.UserData.Count);
        Assert.Equal(0xAAAu, state.UserData[0]);
        Assert.Equal(0xBBBu, state.UserData[3]);
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
    public void Decode_PackedNormMbcntAndU64Compare_DecodesByName()
    {
        // v_cvt_pknorm_u16_f32 v0, v0, v1; v_cvt_pknorm_i16_f32 v2, v0, v1;
        // v_mbcnt_lo_u32_b32 v1, -1, 0; s_cmp_lg_u64 s[0:1], s[2:3]; s_endpgm
        var program = Decode(
            0xD769_0000, 0x0002_0300,
            0xD768_0002, 0x0002_0300,
            0xD765_0001, 0x0001_00C1,
            0xBF13_0200,
            SEndpgm);

        Assert.Equal(
            [
                "VCvtPknormU16F32",
                "VCvtPknormI16F32",
                "VMbcntLoU32B32",
                "SCmpLgU64",
                "SEndpgm",
            ],
            program.Instructions.Select(instruction => instruction.Opcode));
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
