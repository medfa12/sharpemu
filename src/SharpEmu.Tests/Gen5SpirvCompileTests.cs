// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// End-to-end shader pipeline: real RDNA2 machine words -> instruction decoder
/// -> scalar evaluator -> SPIR-V compiler -> structural SPIR-V validation.
/// This is the same path game shaders take at runtime.
/// </summary>
public sealed class Gen5SpirvCompileTests
{
    private const ulong ShaderAddress = 0x30000;
    private const uint ExecutionModelFragment = 4;

    // The production fullscreen barycentric pixel shader:
    // v_interp_p1/p2 attribute interpolation + color export + s_endpgm.
    private static readonly uint[] FullscreenBarycentricPs =
    [
        0xD52F0000, 0x00000200,
        0xD52F0001, 0x00000602,
        0xF8001C0F, 0x00000100,
        0xBF810000,
    ];

    private static (Gen5ShaderState State, Gen5ShaderEvaluation Evaluation, CpuContext Ctx) DecodeAndEvaluate(uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, words);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, 0, new Dictionary<uint, uint>(), 0, out var state, out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx, state, out var evaluation, out var evalError), evalError);
        return (state, evaluation, ctx);
    }

    [Fact]
    public void PixelShader_CompilesToStructurallyValidSpirv()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(FullscreenBarycentricPs);

        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out var error);
        Assert.True(ok, $"TryCompilePixelShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        SpirvModuleAssert.AssertShaderModule(module, ExecutionModelFragment);
    }

    [Fact]
    public void PixelShader_HasNoResourceBindings()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(FullscreenBarycentricPs);

        Assert.True(Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out _));

        // The fullscreen PS only interpolates and exports — no buffers, images,
        // or vertex inputs (AttributeCount is a vertex-stage concept).
        Assert.Empty(shader.GlobalMemoryBindings);
        Assert.Empty(shader.ImageBindings);
        Assert.Equal(0u, shader.AttributeCount);
    }

    [Theory]
    [InlineData((int)Gen5PixelOutputKind.Float)]
    [InlineData((int)Gen5PixelOutputKind.Uint)]
    [InlineData((int)Gen5PixelOutputKind.Sint)]
    public void PixelShader_CompilesForAllOutputKinds(int outputKindValue)
    {
        var outputKind = (Gen5PixelOutputKind)outputKindValue;
        var (state, evaluation, _) = DecodeAndEvaluate(FullscreenBarycentricPs);

        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, outputKind, out var shader, out var error);
        Assert.True(ok, $"outputKind={outputKind}: {error}");
        SpirvModuleAssert.Parse(shader.Spirv);
    }
}
