// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// VOP3P packed-f16 arithmetic: decode of the 0xCC prefix and its
/// op_sel/op_sel_hi/neg_lo/neg_hi/clamp modifiers, SPIR-V emission of the
/// packed family (add/mul/min/max/fma), and the single-rounding (round-to-odd)
/// path v_pk_fma_f16 uses so a fused f16 FMA is not double-rounded. This math is
/// heavy in Astro Bot's fp16 HDR passes, where absent packed-f16 ops produced
/// zero/black color.
/// </summary>
public sealed class Gen5Vop3pPackedF16Tests
{
    private const ulong ShaderAddress = 0x30000;
    private const uint ExecutionModelGLCompute = 5;
    private const ushort OpDecorate = 71;
    private const uint DecorationNoContraction = 42;
    private const uint ComputePgmRsrc2 = 0x213;
    private const uint ComputeUserData = 0x240;

    // Packed f16 (VOP3P) arithmetic covering all five opcodes and the src2
    // neg_lo/neg_hi modifier path on the fused multiply-add. The constants pin
    // the double-rounding regression: fma(0x4100, 0x7522, 0x04EA) must round
    // once to 0x7A6B (an f32 multiply-add then pack yields 0x7A6A).
    private static readonly uint[] PackedF16Program =
    [
        0x7E0002FF, 0x41004100, // v_mov_b32 v0, 0x41004100 (2.5 packed)
        0x7E0202FF, 0x75227522, // v_mov_b32 v1, 0x75227522 (21024 packed)
        0x7E0402FF, 0x04EA04EA, // v_mov_b32 v2, 0x04EA04EA (~7.496e-5 packed)
        0xCC0E4003, 0x1C0A0300, // v_pk_fma_f16 v3, v0, v1, v2
        0xCC0F4004, 0x18020500, // v_pk_add_f16 v4, v0, v2
        0xCC104005, 0x18020300, // v_pk_mul_f16 v5, v0, v1
        0xCC114006, 0x18020300, // v_pk_min_f16 v6, v0, v1
        0xCC124007, 0x18020300, // v_pk_max_f16 v7, v0, v1
        0xCC0E4408, 0x9C0A0300, // v_pk_fma_f16 v8, v0, v1, neg_lo:[0,0,1] neg_hi:[0,0,1] v2
        0xBF810000,             // s_endpgm
    ];

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        Assert.True(Gen5ShaderTranslator.TryCreateState(
            new CpuContext(memory, Generation.Gen5),
            ShaderAddress,
            shaderHeaderAddress: 0,
            shaderRegisters: new Dictionary<uint, uint> { [ComputePgmRsrc2] = 0 },
            userDataBaseRegister: ComputeUserData,
            out var state,
            out var error), error);
        return state.Program;
    }

    private static (Gen5ShaderState State, Gen5ShaderEvaluation Evaluation) DecodeAndEvaluate(uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            ShaderAddress,
            0,
            new Dictionary<uint, uint> { [ComputePgmRsrc2] = 0 },
            ComputeUserData,
            out var state,
            out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx, state, out var evaluation, out var evalError), evalError);
        return (state, evaluation);
    }

    [Theory]
    [InlineData(0x0Eu, "VPkFmaF16")]
    [InlineData(0x0Fu, "VPkAddF16")]
    [InlineData(0x10u, "VPkMulF16")]
    [InlineData(0x11u, "VPkMinF16")]
    [InlineData(0x12u, "VPkMaxF16")]
    public void Decode_PackedF16Opcode_IsRoutedToVop3pNotSmem(uint opcode, string expected)
    {
        // Word0 = 0xCC000000 | (opcode << 16) | dst. The 0xCC prefix collides
        // with SMEM under a plain top-6-bit switch, so this pins that packed
        // ops decode as VOP3P and get their real opcode name.
        var word0 = 0xCC000000u | (opcode << 16) | 0x03u;
        var program = Decode(word0, 0x18020300, 0xBF810000);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Vop3p, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
    }

    [Fact]
    public void Decode_PackedFma_DefaultModifiers_ParsesOpSelAndLanes()
    {
        // v_pk_fma_f16 v3, v0, v1, v2 in its default packed encoding: op_sel = 0
        // (low lane reads the low halves) and op_sel_hi = all ones (high lane
        // reads the high halves), no negate, no clamp.
        var program = Decode(0xCC0E4003, 0x1C0A0300, 0xBF810000);

        var instruction = program.Instructions[0];
        var control = Assert.IsType<Gen5Vop3pControl>(instruction.Control);
        Assert.Equal(0u, control.OpSelMask);
        Assert.Equal(0x7u, control.OpSelHiMask); // op_sel_hi bit2 comes from word0[14]
        Assert.Equal(0u, control.NegLoMask);
        Assert.Equal(0u, control.NegHiMask);
        Assert.False(control.Clamp);
        Assert.Equal(3u, Assert.Single(instruction.Destinations).Value);
    }

    [Fact]
    public void Decode_PackedFma_NegAddend_ParsesNegLoAndNegHiOnSrc2()
    {
        // v_pk_fma_f16 v8, v0, v1, neg_lo:[0,0,1] neg_hi:[0,0,1] v2: the src2
        // (bit 2) negate applies to both lanes. Getting neg_lo/neg_hi wrong is a
        // value bug, so pin them explicitly.
        var program = Decode(0xCC0E4408, 0x9C0A0300, 0xBF810000);

        var instruction = program.Instructions[0];
        var control = Assert.IsType<Gen5Vop3pControl>(instruction.Control);
        Assert.Equal(0x4u, control.NegLoMask); // src2 low negated
        Assert.Equal(0x4u, control.NegHiMask); // src2 high negated
        Assert.Equal(8u, Assert.Single(instruction.Destinations).Value);
    }

    [Fact]
    public void Compile_PackedF16Program_ProducesStructurallyValidSpirv()
    {
        var (state, evaluation) = DecodeAndEvaluate(PackedF16Program);

        var ok = Gen5SpirvTranslator.TryCompileComputeShader(
            state, evaluation, 64, 1, 1, out var shader, out var error);
        Assert.True(ok, $"TryCompileComputeShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        SpirvModuleAssert.AssertShaderModule(module, ExecutionModelGLCompute);
    }

    [Fact]
    public void Compile_PackedFma_EmitsNoContractionForSingleRounding()
    {
        // The fused FMA rebuilds an exact residual with a 2Sum chain; every op in
        // it must carry NoContraction or a driver folds the sequence back into a
        // double-rounded f32 multiply-add. Its presence proves the round-to-odd
        // single-rounding path was emitted (add/mul/min/max never need it).
        var (state, evaluation) = DecodeAndEvaluate(PackedF16Program);
        Assert.True(Gen5SpirvTranslator.TryCompileComputeShader(
            state, evaluation, 64, 1, 1, out var shader, out var error), error);

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpDecorate &&
                i.Words.Length >= 3 &&
                i.Words[2] == DecorationNoContraction);
    }

    [Fact]
    public void Compile_PackedFma_WithClamp_FailsLoudly()
    {
        // Clamp (word0[15]) is out of the first slice's scope; it must fail
        // emission with a clear message rather than silently ignore saturation.
        var clampFma = new uint[]
        {
            0x7E0002FF, 0x41004100,
            0x7E0202FF, 0x75227522,
            0x7E0402FF, 0x04EA04EA,
            0xCC0E8003, 0x1C0A0300, // v_pk_fma_f16 v3, v0, v1, v2 clamp (bit15 set)
            0xBF810000,
        };
        var (state, evaluation) = DecodeAndEvaluate(clampFma);

        var ok = Gen5SpirvTranslator.TryCompileComputeShader(
            state, evaluation, 64, 1, 1, out _, out var error);
        Assert.False(ok);
        Assert.Contains("clamp", error);
    }

    // ---- Single-rounding correctness of the round-to-odd FMA algorithm ----
    // These mirror the exact f32 sequence EmitPackedF16FusedMultiplyAdd emits
    // (Knuth 2Sum + parity nudge to round-to-odd, then RNE narrow to f16) and
    // check it against a true fused f16 FMA computed in double precision, which
    // rounds exactly once. .NET float/double ops are strict IEEE (no implicit
    // FMA contraction), so the mirror matches the NoContraction-decorated SPIR-V.

    private static float HalfBitsToFloat(ushort bits) =>
        (float)BitConverter.UInt16BitsToHalf(bits);

    private static ushort FloatToHalfBits(float value) =>
        BitConverter.HalfToUInt16Bits((Half)value);

    // True fused f16 FMA: widen to double (exact), multiply-add in double (the
    // product needs 22 bits and the sum stays well inside 52), round once to f16.
    private static ushort FusedReference(ushort a, ushort b, ushort c)
    {
        double result = (double)HalfBitsToFloat(a) * HalfBitsToFloat(b) + HalfBitsToFloat(c);
        return BitConverter.HalfToUInt16Bits((Half)result);
    }

    // Naive f32 multiply-add then pack: rounds twice.
    private static ushort NaiveDoubleRounded(ushort a, ushort b, ushort c) =>
        FloatToHalfBits(HalfBitsToFloat(a) * HalfBitsToFloat(b) + HalfBitsToFloat(c));

    // Mirror of the emitted round-to-odd sequence, then RNE narrow.
    private static ushort FusedRoundToOdd(ushort a, ushort b, ushort c)
    {
        var product = HalfBitsToFloat(a) * HalfBitsToFloat(b);
        var addend = HalfBitsToFloat(c);
        var sum = product + addend;

        var productPart = sum - addend;
        var addendPart = sum - productPart;
        var productError = product - productPart;
        var addendError = addend - addendPart;
        var residual = productError + addendError;

        var sumBits = BitConverter.SingleToUInt32Bits(sum);
        var residualBits = BitConverter.SingleToUInt32Bits(residual);
        var inexact = residual != 0f;
        var evenSignificand = (sumBits & 1) == 0;
        if (inexact && evenSignificand)
        {
            var towardZero = ((sumBits ^ residualBits) & 0x8000_0000u) != 0;
            sumBits = towardZero ? sumBits - 1 : sumBits + 1;
        }

        return FloatToHalfBits(BitConverter.UInt32BitsToSingle(sumBits));
    }

    [Fact]
    public void FusedFma_PinnedMidpoint_RoundsOnceUp_WhereF32DoubleRoundsDown()
    {
        // fma(2.5, 21024, 7.496e-5): the exact product 52560 sits on an f16 tie
        // (0x7A6A/0x7A6B). The tiny positive addend nudges it above the midpoint,
        // so a single fused rounding lands on 0x7A6B; an f32 multiply-add collapses
        // the addend and ties-to-even to 0x7A6A.
        const ushort A = 0x4100, B = 0x7522, C = 0x04EA;

        Assert.Equal((ushort)0x7A6B, FusedReference(A, B, C));
        Assert.Equal((ushort)0x7A6B, FusedRoundToOdd(A, B, C));
        Assert.Equal((ushort)0x7A6A, NaiveDoubleRounded(A, B, C)); // the bug the path fixes
    }

    [Fact]
    public void FusedFma_PinnedMidpoint_NegatedAddend_RoundsOnceDown()
    {
        // The same product with the addend negated straddles the midpoint from
        // below, pinning the opposite rounding direction to 0x7A6A.
        const ushort A = 0x4100, B = 0x7522, NegC = 0x84EA; // -7.496e-5

        Assert.Equal((ushort)0x7A6A, FusedReference(A, B, NegC));
        Assert.Equal((ushort)0x7A6A, FusedRoundToOdd(A, B, NegC));
    }

    [Fact]
    public void FusedRoundToOdd_MatchesTrueFusedFma_AcrossOperandSweep()
    {
        // A directed sweep over normal, subnormal and signed operands: the
        // round-to-odd mirror must equal a true single-rounded fused FMA on every
        // case (double-rounding via a plain f32 multiply-add would diverge on the
        // midpoint families above).
        ushort[] samples =
        [
            0x0000, 0x8000, 0x0001, 0x8001, 0x03FF, 0x3C00, 0xBC00,
            0x4100, 0x7522, 0x04EA, 0x84EA, 0x5000, 0x6000, 0x7BFF,
            0x1234, 0x9ABC, 0x2AAA, 0x3555,
        ];

        foreach (var a in samples)
        {
            foreach (var b in samples)
            {
                foreach (var c in samples)
                {
                    Assert.Equal(FusedReference(a, b, c), FusedRoundToOdd(a, b, c));
                }
            }
        }
    }
}
