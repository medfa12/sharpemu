// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Covers the flattened NGG pass-through vertex variant: when
/// <c>instanceIdFromVertexIndex</c> is set the vertex prologue seeds VGPR8 from
/// the VertexIndex built-in instead of InstanceIndex, so an instance-indexed
/// geometry fetch is driven by the vertex index over the expanded
/// 1-instance x N-vertex draw. The default (unset) path must stay byte-identical
/// to the historical vertex compile.
/// </summary>
public sealed class Gen5VertexInstanceRemapTests
{
    private const ulong ShaderAddress = 0x30000;
    private const uint ExecutionModelVertex = 0;
    private const ushort OpDecorate = 71;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationLocation = 30;
    private const uint BuiltInVertexIndex = 42;
    private const uint BuiltInInstanceIndex = 43;

    // Minimal export/vertex program: end of program. The prologue that seeds the
    // VertexIndex/InstanceIndex system-value VGPRs is emitted for every vertex
    // stage regardless of the body, which is exactly what these tests exercise.
    private static readonly uint[] MinimalVertex = [0xBF810000];

    private static (Gen5ShaderState State, Gen5ShaderEvaluation Evaluation) DecodeAndEvaluate()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, MinimalVertex);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx,
            ShaderAddress,
            0,
            new Dictionary<uint, uint> { [0x213] = 0 }, // COMPUTE_PGM_RSRC2, USER_SGPR=0
            0x240,
            out var state,
            out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx, state, out var evaluation, out var evalError), evalError);
        return (state, evaluation);
    }

    private static byte[] Compile(bool instanceIdFromVertexIndex)
    {
        var (state, evaluation) = DecodeAndEvaluate();
        var ok = Gen5SpirvTranslator.TryCompileVertexShader(
            state,
            evaluation,
            out var shader,
            out var error,
            instanceIdFromVertexIndex: instanceIdFromVertexIndex);
        Assert.True(ok, $"TryCompileVertexShader failed: {error}");
        return shader.Spirv;
    }

    private static uint[] LocationDecorations(SpirvModuleAssert.ParsedModule module) =>
        module.Instructions
            .Where(i => i.Opcode == OpDecorate && i.Words.Length >= 4 &&
                i.Words[2] == DecorationLocation)
            .Select(i => i.Words[3])
            .ToArray();

    [Fact]
    public void RequiredOutputLocations_PadMissingVertexOutputs()
    {
        // The minimal vertex program exports no parameters, so on its own it
        // declares no location-decorated outputs.
        var (state, evaluation) = DecodeAndEvaluate();
        Assert.True(Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var bare, out var bareError), bareError);
        Assert.Empty(LocationDecorations(SpirvModuleAssert.Parse(bare.Spirv)));

        // Pairing it with a pixel shader that interpolates Locations 0 and 2
        // must pad the vertex output interface with exactly those locations so
        // vkCreateGraphicsPipelines accepts the pair.
        Assert.True(Gen5SpirvTranslator.TryCompileVertexShader(
            state,
            evaluation,
            out var padded,
            out var paddedError,
            requiredVertexOutputLocations: new uint[] { 0, 2 }), paddedError);

        var module = SpirvModuleAssert.Parse(padded.Spirv);
        SpirvModuleAssert.AssertShaderModule(module, ExecutionModelVertex);
        Assert.Equal(new uint[] { 0, 2 }, LocationDecorations(module).Order().ToArray());
    }

    private static bool HasBuiltIn(SpirvModuleAssert.ParsedModule module, uint builtIn) =>
        module.Instructions.Any(i =>
            i.Opcode == OpDecorate &&
            i.Words.Length >= 4 &&
            i.Words[2] == DecorationBuiltIn &&
            i.Words[3] == builtIn);

    [Fact]
    public void DefaultVertex_IsByteIdenticalToExplicitFalse()
    {
        var (state, evaluation) = DecodeAndEvaluate();
        Assert.True(Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var implicitShader, out var implicitError), implicitError);

        var explicitFalse = Compile(instanceIdFromVertexIndex: false);

        // The remap flag defaults to false, so ordinary (non-flattened) vertex
        // shaders must compile to exactly the same SPIR-V as before it existed.
        Assert.Equal(implicitShader.Spirv, explicitFalse);
    }

    [Fact]
    public void RemapVariant_ProducesDifferentSpirvThanDefault()
    {
        var normal = Compile(instanceIdFromVertexIndex: false);
        var remapped = Compile(instanceIdFromVertexIndex: true);

        // The variants must differ so the separate shader-cache entry is
        // justified: the prologue seeds VGPR8 from a different built-in.
        Assert.NotEqual(normal, remapped);
    }

    [Fact]
    public void BothVariants_AreStructurallyValidVertexModules()
    {
        foreach (var flag in new[] { false, true })
        {
            var module = SpirvModuleAssert.Parse(Compile(instanceIdFromVertexIndex: flag));
            SpirvModuleAssert.AssertShaderModule(module, ExecutionModelVertex);
        }
    }

    [Fact]
    public void BothVariants_DeclareVertexAndInstanceIndexBuiltIns()
    {
        // The InstanceIndex input stays declared even in the remapped variant
        // (an unreferenced Input built-in is legal SPIR-V), so both modules keep
        // both system-value inputs in their interface.
        foreach (var flag in new[] { false, true })
        {
            var module = SpirvModuleAssert.Parse(Compile(instanceIdFromVertexIndex: flag));
            Assert.True(HasBuiltIn(module, BuiltInVertexIndex), "VertexIndex built-in missing.");
            Assert.True(HasBuiltIn(module, BuiltInInstanceIndex), "InstanceIndex built-in missing.");
        }
    }
}
