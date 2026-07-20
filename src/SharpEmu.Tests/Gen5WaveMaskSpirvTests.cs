// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

// Regression test for how a VCC/EXEC wave mask consumed as a per-lane predicate
// is lowered to SPIR-V. A wave mask must be tested at the current lane's bit
// (mask & lane_bit) - exactly as the hardware evaluates a VCC/EXEC predicate -
// not with a whole-word "the 64-bit value is non-zero" test.
//
// The two agree for comparison results (only the lane's own bit is ever set), but
// diverge for the bitwise-complement wave-mask idioms (S_NOT / S_ORN2 / S_ANDN2 /
// S_NAND / S_NOR), which set the unused upper 63 bits. A whole-word test then
// reports the lane active even when its bit is clear. Unity's PostProcessing NaN
// killer combines its channels as `anyNaN | ~allFinite` (S_ORN2_B64); under the
// whole-word test every valid pixel read as NaN and was replaced with 0, zeroing
// the whole HDR scene before tone-mapping.
public sealed class Gen5WaveMaskSpirvTests
{
    private const ulong ShaderAddress = 0x30000;

    // v_cmp_eq_f32 vcc, v0, v1; exp mrt0 done compr vm v0, v1; s_endpgm.
    // Writing VCC re-materialises the per-lane _vcc predicate from the wave mask
    // via IsWaveMaskActive, which is where the lane-bit test must appear.
    private static readonly uint[] VccPredicatePs =
    [
        0x7C040300,
        0xF8001C0F, 0x00000100,
        0xBF810000,
    ];

    [Fact]
    public void WaveMaskPredicate_IsTestedAtCurrentLaneBit()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, VccPredicatePs);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            ShaderAddress,
            0,
            new Dictionary<uint, uint> { [0x213] = 0 },
            0x240,
            out var state,
            out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx, state, out var evaluation, out var evalError), evalError);
        Assert.True(Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out var error), error);

        // The lane's bit in single-lane emulation is the 64-bit constant 1, so the
        // predicate must be `(mask & 1) != 0`. The whole-word bug emitted a bare
        // `mask != 0` with no such AND. Require the lane-bit mask to be present.
        Assert.True(
            ContainsLaneBitMaskedWaveTest(shader.Spirv),
            "wave-mask predicate must be tested at the current lane bit "
                + "(mask & lane_bit), not as a whole-word non-zero test");
    }

    // True when the module contains an OpBitwiseAnd whose operand is a 64-bit
    // constant of value 1 - the current-lane bit IsCurrentLaneSet masks the wave
    // mask with before the non-zero test.
    private static bool ContainsLaneBitMaskedWaveTest(byte[] spirv)
    {
        const ushort OpConstant = 43;
        const ushort OpBitwiseAnd = 199;
        var laneBitConstIds = new HashSet<uint>();

        // Pass 1: collect 64-bit OpConstant result-ids whose value is 1
        // (opcode, resultType, resultId, valueLow, valueHigh) = 5 words.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op != OpConstant || wordCount != 5)
            {
                continue;
            }

            var resultId = ReadWord(spirv, offset + 8);
            var low = ReadWord(spirv, offset + 12);
            var high = ReadWord(spirv, offset + 16);
            if (low == 1 && high == 0)
            {
                laneBitConstIds.Add(resultId);
            }
        }

        // Pass 2: an OpBitwiseAnd (opcode, resultType, resultId, op0, op1) that
        // consumes one of those constants.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op != OpBitwiseAnd || wordCount != 5)
            {
                continue;
            }

            var operand0 = ReadWord(spirv, offset + 12);
            var operand1 = ReadWord(spirv, offset + 16);
            if (laneBitConstIds.Contains(operand0) || laneBitConstIds.Contains(operand1))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Opcode, ushort WordCount, int Offset)> EnumerateInstructions(byte[] spirv)
    {
        // 5-word header precedes the instruction stream.
        var offset = 5 * sizeof(uint);
        while (offset + sizeof(uint) <= spirv.Length)
        {
            var word0 = ReadWord(spirv, offset);
            var opcode = (ushort)(word0 & 0xFFFF);
            var wordCount = (ushort)(word0 >> 16);
            if (wordCount == 0)
            {
                yield break;
            }

            yield return (opcode, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        (uint)(spirv[offset]
            | (spirv[offset + 1] << 8)
            | (spirv[offset + 2] << 16)
            | (spirv[offset + 3] << 24));
}
