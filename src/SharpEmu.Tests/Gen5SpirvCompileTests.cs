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

    // v_interp_p1_f32 v0, v0, attr0.x; v_interp_p1_f32 v1, v0, attr1.x;
    // exp mrt0 done compr vm v0, v1; s_endpgm. Reads two attributes so the
    // interpolant-routing tests can observe both fragment input locations.
    private static readonly uint[] TwoAttributePs =
    [
        0xC8000000,
        0xC8040400,
        0xF8001C0F, 0x00000100,
        0xBF810000,
    ];

    // exp param5 v0, v0, v0, v0; exp pos0 done v0, v0, v0, v0; s_endpgm.
    // A vertex program whose only parameter export is guest location 5.
    private static readonly uint[] Param5ExportVs =
    [
        0xF800025F, 0x00000000,
        0xF80008CF, 0x00000000,
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

    [Fact]
    public void VertexShader_WithSwappedInputControls_DeclaresOneOutputPerAttribute()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(Param5ExportVs);

        // Controls [1, 0]: fragment attribute 0 reads guest parameter 1 and
        // attribute 1 reads guest parameter 0.
        var ok = Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var shader, out var error,
            pixelInputControls: [1u, 0u]);
        Assert.True(ok, $"TryCompileVertexShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        var outputs = CollectVariableLocations(module, StorageClassOutput);
        Assert.Equal([0u, 1u], outputs.Values.Order());
    }

    [Fact]
    public void VertexShader_WithAliasedInputControls_FansExportIntoUniqueLocations()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(Param5ExportVs);

        // Two SPI_PS_INPUT_CNTL entries alias the same guest parameter 5. The
        // single guest export must fan out into two unique host locations.
        var ok = Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var shader, out var error,
            pixelInputControls: [5u, 5u]);
        Assert.True(ok, $"TryCompileVertexShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        var outputs = CollectVariableLocations(module, StorageClassOutput);
        Assert.Equal([0u, 1u], outputs.Values.Order());

        // Both variables receive the zero-init store and the fanned-out
        // export store; a dropped fan-out leaves only the single init store.
        foreach (var variable in outputs.Keys)
        {
            var stores = module.Instructions.Count(
                i => i.Opcode == OpStore && i.Words[1] == variable);
            Assert.True(
                stores >= 2,
                $"output variable {variable} has {stores} stores; expected the zero-init plus the export fan-out");
        }
    }

    [Fact]
    public void VertexShader_WithoutControls_KeepsGuestExportLocations()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(Param5ExportVs);

        var ok = Gen5SpirvTranslator.TryCompileVertexShader(
            state, evaluation, out var shader, out var error);
        Assert.True(ok, $"TryCompileVertexShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        var outputs = CollectVariableLocations(module, StorageClassOutput);
        Assert.Equal([5u], outputs.Values.Order());
    }

    [Fact]
    public void PixelShader_WithAliasedControls_KeepsFragmentLocationsUnique()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(TwoAttributePs);

        // Fragment locations stay keyed by attribute index even when both
        // controls select the same guest parameter; deriving them from the
        // control value would declare location 5 twice and fail validation.
        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out var error,
            pixelInputControls: [5u, 5u]);
        Assert.True(ok, $"TryCompilePixelShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        var inputs = CollectVariableLocations(module, StorageClassInput);
        Assert.Equal([0u, 1u], inputs.Values.Order());
    }

    [Fact]
    public void PixelShader_FlatControlBit_DecoratesInputFlat()
    {
        var (state, evaluation, _) = DecodeAndEvaluate(TwoAttributePs);

        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out var error,
            pixelInputControls: [0x400u, 1u]);
        Assert.True(ok, $"TryCompilePixelShader failed: {error}");

        var module = SpirvModuleAssert.Parse(shader.Spirv);
        var inputs = CollectVariableLocations(module, StorageClassInput);
        var flatTargets = module.Instructions
            .Where(i => i.Opcode == OpDecorate &&
                        i.Words[2] == DecorationFlat)
            .Select(i => i.Words[1])
            .ToHashSet();
        var location0 = Assert.Single(inputs, pair => pair.Value == 0u).Key;
        var location1 = Assert.Single(inputs, pair => pair.Value == 1u).Key;
        Assert.Contains(location0, flatTargets);
        Assert.DoesNotContain(location1, flatTargets);
    }

    private const ushort OpDecorate = 71;
    private const ushort OpVariable = 59;
    private const ushort OpStore = 62;
    private const uint StorageClassInput = 1;
    private const uint StorageClassOutput = 3;
    private const uint DecorationLocation = 30;
    private const uint DecorationFlat = 14;

    // Maps variable id -> Location for every location-decorated variable of
    // the given storage class. Builtins carry no Location and are excluded.
    private static Dictionary<uint, uint> CollectVariableLocations(
        SpirvModuleAssert.ParsedModule module,
        uint storageClass)
    {
        var variables = module.Instructions
            .Where(i => i.Opcode == OpVariable && i.Words[3] == storageClass)
            .Select(i => i.Words[2])
            .ToHashSet();
        var locations = new Dictionary<uint, uint>();
        foreach (var (opcode, words) in module.Instructions)
        {
            if (opcode == OpDecorate &&
                words[2] == DecorationLocation &&
                variables.Contains(words[1]))
            {
                locations.Add(words[1], words[3]);
            }
        }

        return locations;
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
