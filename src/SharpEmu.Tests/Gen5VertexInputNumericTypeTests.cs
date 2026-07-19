// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// A vertex attribute's SPIR-V component type must match the guest buffer
/// NUMBER_FORMAT: UINT/SINT declare integer inputs so the shader interface
/// agrees with the Vulkan pipeline's integer vertex format, while normalized,
/// scaled, and floating-point formats stay on float inputs. Mismatched numeric
/// classes make the loaded attribute values undefined.
/// </summary>
public sealed class Gen5VertexInputNumericTypeTests
{
    private const ulong ShaderAddress = 0x30000;
    private const ushort OpDecorate = 71;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypePointer = 32;
    private const ushort OpVariable = 59;
    private const uint DecorationLocation = 30;
    private const uint StorageClassInput = 1;

    // Minimal vertex program: end of program. DeclareVertexInputs runs off the
    // evaluation's VertexInputs regardless of the body, so a bare program is
    // enough to observe the declared attribute's component type.
    private static readonly uint[] MinimalVertex = [0xBF810000];

    private static (Gen5ShaderState State, Gen5ShaderEvaluation Evaluation)
        EvaluationWithInput(uint numberFormat)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, MinimalVertex);
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

        var input = new Gen5VertexInputBinding(
            Pc: 0,
            Location: 0,
            ComponentCount: 1,
            DataFormat: 4,
            NumberFormat: numberFormat,
            BaseAddress: 0x1000,
            Stride: 4,
            OffsetBytes: 0,
            Data: new byte[4]);
        return (state, evaluation with { VertexInputs = new[] { input } });
    }

    [Theory]
    [InlineData(4u, OpTypeInt)]   // UINT
    [InlineData(5u, OpTypeInt)]   // SINT
    [InlineData(7u, OpTypeFloat)] // FLOAT
    [InlineData(0u, OpTypeFloat)] // UNORM -> float
    public void VertexAttributeComponentTypeMatchesNumberFormat(
        uint numberFormat,
        ushort expectedTypeOp)
    {
        var (state, evaluation) = EvaluationWithInput(numberFormat);
        Assert.True(Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var shader, out var error), error);

        var module = SpirvModuleAssert.Parse(shader.Spirv);

        // The single Location-decorated Input variable is the vertex attribute;
        // system-value inputs (VertexIndex, ...) are BuiltIn-decorated instead.
        var locationTargets = module.Instructions
            .Where(i => i.Opcode == OpDecorate && i.Words.Length >= 4 &&
                i.Words[2] == DecorationLocation)
            .Select(i => i.Words[1])
            .ToHashSet();
        var variable = Assert.Single(
            module.Instructions,
            i => i.Opcode == OpVariable && i.Words.Length >= 4 &&
                i.Words[3] == StorageClassInput &&
                locationTargets.Contains(i.Words[2]));

        var pointer = Assert.Single(
            module.Instructions,
            i => i.Opcode == OpTypePointer && i.Words[1] == variable.Words[1]);
        var pointeeType = pointer.Words[3];
        var typeDef = Assert.Single(
            module.Instructions,
            i => (i.Opcode == OpTypeInt || i.Opcode == OpTypeFloat) &&
                i.Words[1] == pointeeType);

        Assert.Equal(expectedTypeOp, typeDef.Opcode);
    }
}
