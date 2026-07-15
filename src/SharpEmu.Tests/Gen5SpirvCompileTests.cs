// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
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

    [Theory]
    [InlineData((int)Gen5PixelOutputKind.Float)]
    [InlineData((int)Gen5PixelOutputKind.Uint)]
    [InlineData((int)Gen5PixelOutputKind.Sint)]
    public void PixelShader_PartialExportMask_PadsDisabledChannelsWithMatchingType(int outputKindValue)
    {
        // Real game shaders export only some channels (e.g. R,G) and leave the
        // rest disabled. Disabled channels must be padded with a zero of the
        // output's component type; padding a v4float with uint zeros produces an
        // OpCompositeConstruct the driver rejects (NVVM compilation failed).
        var outputKind = (Gen5PixelOutputKind)outputKindValue;
        var words = (uint[])FullscreenBarycentricPs.Clone();
        var exportIndex = Array.IndexOf(words, 0xF8001C0Fu);
        Assert.True(exportIndex >= 0, "expected an EXP instruction with EN=0xF");
        words[exportIndex] = 0xF8001C03u; // EN = 0x3 -> channels 2 and 3 disabled

        var (state, evaluation, _) = DecodeAndEvaluate(words);
        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, outputKind, out var shader, out var error);
        Assert.True(ok, $"outputKind={outputKind}: {error}");

        AssertVectorCompositeConstituentsMatchComponentType(shader.Spirv);
    }

    // The disabled-channel padding is emitted as an OpConstant, so a mismatched
    // pad shows up as an integer-typed constant fed into a float-vector
    // OpCompositeConstruct -- exactly what the driver rejects. Constants have an
    // unambiguous [result-type, result-id, literal] layout, so keying off them
    // avoids the no-result opcodes (OpStore etc.) that would corrupt a general
    // id->type map.
    private static void AssertVectorCompositeConstituentsMatchComponentType(byte[] spirv)
    {
        const ushort OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23,
            OpConstant = 43, OpConstantComposite = 44, OpCompositeConstruct = 80;
        var module = SpirvModuleAssert.Parse(spirv);

        var scalarKind = new Dictionary<uint, string>();   // type id -> "int"/"float"
        var vectorComponent = new Dictionary<uint, uint>(); // vector type id -> component type id
        var constantType = new Dictionary<uint, uint>();    // constant id -> its type id

        foreach (var (opcode, w) in module.Instructions)
        {
            switch (opcode)
            {
                case OpTypeInt: scalarKind[w[1]] = "int"; break;
                case OpTypeFloat: scalarKind[w[1]] = "float"; break;
                case OpTypeVector: vectorComponent[w[1]] = w[2]; break;
                case OpConstant:
                case OpConstantComposite: constantType[w[2]] = w[1]; break;
            }
        }

        foreach (var (opcode, w) in module.Instructions)
        {
            if (opcode != OpCompositeConstruct ||
                !vectorComponent.TryGetValue(w[1], out var componentType) ||
                scalarKind.GetValueOrDefault(componentType) != "float")
            {
                continue;
            }

            for (var operand = 3; operand < w.Length; operand++)
            {
                if (constantType.TryGetValue(w[operand], out var type) &&
                    scalarKind.TryGetValue(type, out var kind))
                {
                    Assert.True(
                        kind == "float",
                        $"OpCompositeConstruct into a float vector has a {kind} constant constituent (id {w[operand]}).");
                }
            }
        }
    }
}
