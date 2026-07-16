// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Structural validation of the SPIR-V binaries emitted by
/// <see cref="SpirvFixedShaders"/> / <see cref="SpirvModuleBuilder"/>:
/// header layout, instruction word-count integrity, ID bounds, and the
/// invariants Vulkan drivers reject immediately when broken.
/// </summary>
public sealed class SpirvFixedShadersTests
{
    private const uint ExecutionModelVertex = 0;
    private const uint ExecutionModelFragment = 4;

    public static TheoryData<uint> AttributeCounts => new() { 0u, 1u, 4u };

    [Theory]
    [MemberData(nameof(AttributeCounts))]
    public void FullscreenVertex_IsStructurallyValidSpirv(uint attributeCount)
    {
        var module = SpirvModuleAssert.Parse(SpirvFixedShaders.CreateFullscreenVertex(attributeCount));

        var entryPoint = AssertCommonInvariants(module, ExecutionModelVertex, minResultBearingInstructions: 12);

        // vertexIndex + position + one interface entry per attribute
        var interfaceCount = CountEntryPointInterfaces(entryPoint.Words);
        Assert.Equal(2 + attributeCount, interfaceCount);
    }

    [Theory]
    [MemberData(nameof(AttributeCounts))]
    public void PositionPassthroughVertex_IsStructurallyValidSpirv(uint attributeCount)
    {
        var module = SpirvModuleAssert.Parse(
            SpirvFixedShaders.CreatePositionPassthroughVertex(attributeCount, 0));

        // Loads the location-0 position and (for attributeCount > 0) builds one
        // zero vec4, so it always carries at least the position Load.
        var entryPoint = AssertCommonInvariants(
            module,
            ExecutionModelVertex,
            minResultBearingInstructions: 1);

        // positionInput + position + one interface entry per attribute.
        var interfaceCount = CountEntryPointInterfaces(entryPoint.Words);
        Assert.Equal(2 + attributeCount, interfaceCount);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(2u, 2u)]
    [InlineData(4u, 2u)]
    [InlineData(1u, 3u)]
    public void PositionPassthroughVertex_ForwardsCapturedParams(
        uint capturedParamCount,
        uint attributeCount)
    {
        var module = SpirvModuleAssert.Parse(
            SpirvFixedShaders.CreatePositionPassthroughVertex(
                attributeCount,
                capturedParamCount));

        var entryPoint = AssertCommonInvariants(
            module,
            ExecutionModelVertex,
            minResultBearingInstructions: 1);

        // positionInput + position + one input per captured param + one output
        // per attribute (at least as many outputs as captured params).
        var outputCount = Math.Max(attributeCount, capturedParamCount);
        var interfaceCount = CountEntryPointInterfaces(entryPoint.Words);
        Assert.Equal(2 + capturedParamCount + outputCount, interfaceCount);
    }

    [Fact]
    public void CopyFragment_IsStructurallyValidSpirv()
    {
        var module = SpirvModuleAssert.Parse(SpirvFixedShaders.CreateCopyFragment());

        AssertCommonInvariants(module, ExecutionModelFragment, minResultBearingInstructions: 6);
    }

    private static (ushort Opcode, uint[] Words) AssertCommonInvariants(
        SpirvModuleAssert.ParsedModule module,
        uint executionModel,
        int minResultBearingInstructions)
    {
        var entryPoint = SpirvModuleAssert.AssertShaderModule(module, executionModel);

        // Every result ID must stay below the declared bound; every used ID must be non-zero.
        var checkedInstructions = 0;
        foreach (var (opcode, words) in module.Instructions)
        {
            foreach (var word in ResultIds(opcode, words))
            {
                Assert.InRange(word, 1u, module.Bound - 1);
                checkedInstructions++;
            }
        }

        // Guards against the opcode set drifting from what the shaders emit and
        // this check silently degrading to a no-op.
        Assert.True(
            checkedInstructions >= minResultBearingInstructions,
            $"Expected at least {minResultBearingInstructions} result-bearing instructions, checked {checkedInstructions}.");
        return entryPoint;
    }

    private static uint CountEntryPointInterfaces(uint[] entryPointWords)
    {
        // OpEntryPoint: [opcode] [execution model] [function id] [literal name...] [interfaces...]
        var index = 3;
        while (index < entryPointWords.Length)
        {
            var word = entryPointWords[index];
            index++;
            if (((word >> 24) & 0xFF) == 0 ||
                ((word >> 16) & 0xFF) == 0 ||
                ((word >> 8) & 0xFF) == 0 ||
                (word & 0xFF) == 0)
            {
                break; // NUL terminator of the literal name
            }
        }

        return (uint)(entryPointWords.Length - index);
    }

    private static IEnumerable<uint> ResultIds(ushort opcode, uint[] words)
    {
        // Opcodes emitted by the fixed shaders whose result ID is word 2 (type + result),
        // matching SpirvModuleBuilder's SpirvOp values.
        var hasTypeAndResult = (SpirvOp)opcode is SpirvOp.Constant or SpirvOp.Function
            or SpirvOp.Load or SpirvOp.VectorShuffle or SpirvOp.CompositeConstruct
            or SpirvOp.ImageSampleExplicitLod or SpirvOp.ConvertUToF
            or SpirvOp.FSub or SpirvOp.FMul or SpirvOp.ShiftLeftLogical or SpirvOp.BitwiseAnd;
        if (hasTypeAndResult && words.Length > 2)
        {
            yield return words[2];
        }
    }
}
