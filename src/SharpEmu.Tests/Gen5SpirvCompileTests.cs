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

    private const uint ExportV0Rgba = 0xF8001C0F; // exp mrt0 v0,v0,v0,v0 done vm
    private const uint SEndpgm = 0xBF810000;

    private const ushort OpName = 5, OpConstant = 43,
        OpAccessChain = 65, OpLogicalOr = 166, OpLogicalAnd = 167,
        OpSelect = 169, OpULessThan = 176, OpFOrdLessThan = 184;

    private static SpirvModuleAssert.ParsedModule CompilePixelShader(params uint[] words)
    {
        var (state, evaluation, _) = DecodeAndEvaluate(words);
        var ok = Gen5SpirvTranslator.TryCompilePixelShader(
            state, evaluation, Gen5PixelOutputKind.Float, out var shader, out var error);
        Assert.True(ok, $"TryCompilePixelShader failed: {error}");
        return SpirvModuleAssert.Parse(shader.Spirv);
    }

    private static string DebugName(uint[] w)
    {
        var bytes = new List<byte>();
        for (var word = 2; word < w.Length; word++)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(w[word] >> shift);
                if (value == 0)
                {
                    return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    // How many times each SGPR index is stored, keyed off the OpAccessChain
    // constant index into the OpName-tagged "sgpr" array.
    private static Dictionary<uint, int> ScalarRegisterStoreCounts(
        SpirvModuleAssert.ParsedModule module)
    {
        uint sgpr = 0;
        var constants = new Dictionary<uint, uint>(); // 32-bit constant id -> value
        var pointers = new Dictionary<uint, uint>();  // pointer id -> sgpr index
        var counts = new Dictionary<uint, int>();
        foreach (var (opcode, w) in module.Instructions)
        {
            switch (opcode)
            {
                case OpName when DebugName(w) == "sgpr":
                    sgpr = w[1];
                    break;
                case OpConstant when w.Length == 4:
                    constants[w[2]] = w[3];
                    break;
                case OpAccessChain when w.Length == 5 && sgpr != 0 && w[3] == sgpr:
                    pointers[w[2]] = constants[w[4]];
                    break;
                case OpStore when pointers.TryGetValue(w[1], out var register):
                    counts[register] = counts.GetValueOrDefault(register) + 1;
                    break;
            }
        }

        return counts;
    }

    private static uint SingleResultOf(
        SpirvModuleAssert.ParsedModule module,
        ushort opcode)
    {
        var (_, words) = Assert.Single(module.Instructions, i => i.Opcode == opcode);
        return words[2];
    }

    [Fact]
    public void PixelShader_ReadFirstLane_StoresScalarBroadcastFromGuestActiveLane()
    {
        // v_readfirstlane_b32 s5, v0: the value must land in the SGPR file
        // (never a VGPR) and come from the first guest-EXEC-active lane via a
        // ballot + broadcast, not a per-lane move.
        const ushort OpGroupNonUniformBroadcast = 337;
        const ushort OpGroupNonUniformBallot = 339;
        var module = CompilePixelShader(0x7E0A0500, ExportV0Rgba, 0x00000000, SEndpgm);

        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpGroupNonUniformBallot);
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpGroupNonUniformBroadcast);
        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(1, stores.GetValueOrDefault(5u));
    }

    [Fact]
    public void PixelShader_ExecInactiveLaneAtExit_IsKilledNotZeroExported()
    {
        // A lane whose EXEC bit is still clear when the guest program exits is
        // a killed fragment; returning normally would store the zero-seeded
        // outputs and paint the attachment black.
        const ushort OpKill = 252;
        var module = CompilePixelShader(FullscreenBarycentricPs);

        Assert.Contains(module.Instructions, i => i.Opcode == OpKill);
    }

    [Fact]
    public void PixelShader_VectorCompare_BallotIsMaskedWithExec()
    {
        // v_cmp_lt_f32 vcc, v1, v2: the VCC ballot must be EXEC & condition;
        // a raw-condition ballot leaks disabled-lane results into later
        // saveexec/branch sequences.
        var module = CompilePixelShader(0x7C020501, ExportV0Rgba, 0x00000000, SEndpgm);

        var compare = SingleResultOf(module, OpFOrdLessThan);
        var masked = Assert.Single(
            module.Instructions,
            i => i.Opcode == OpLogicalAnd && (i.Words[3] == compare || i.Words[4] == compare));
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpSelect && i.Words[3] == masked.Words[2]);
        Assert.DoesNotContain(
            module.Instructions,
            i => i.Opcode == OpSelect && i.Words[3] == compare);

        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(1, stores.GetValueOrDefault(106u));
        Assert.Equal(1, stores.GetValueOrDefault(107u));
    }

    [Fact]
    public void PixelShader_VCmpSdwaScalarDestination_TargetsSdstPairNotVcc()
    {
        // v_cmp_lt_f32_sdwa s[38:39], v0, v1 (SD=1, sdst=38): the mask lands
        // in the encoded sdst pair and VCC stays untouched.
        var module = CompilePixelShader(0x7C0202F9, 0x0606A600, ExportV0Rgba, 0x00000000, SEndpgm);

        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(1, stores.GetValueOrDefault(38u));
        Assert.Equal(1, stores.GetValueOrDefault(39u));
        Assert.Equal(0, stores.GetValueOrDefault(106u));
        Assert.Equal(0, stores.GetValueOrDefault(107u));
    }

    [Fact]
    public void PixelShader_VCmpxSdwa_WritesExecOnlyAndIgnoresSdst()
    {
        // v_cmpx_lt_f32_sdwa with SD=1 sdst=38 encoded: gfx10 VCmpx writes
        // EXEC only; the sdst bits must not clobber an SGPR pair or VCC. The
        // prologue seeds s126 once from the evaluator's initial EXEC value
        // (the zero s127 half is skipped), so the compare accounts for the
        // second s126 store and the only s127 store.
        var module = CompilePixelShader(0x7C2202F9, 0x0606A600, ExportV0Rgba, 0x00000000, SEndpgm);

        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(0, stores.GetValueOrDefault(38u));
        Assert.Equal(0, stores.GetValueOrDefault(39u));
        Assert.Equal(0, stores.GetValueOrDefault(106u));
        Assert.Equal(0, stores.GetValueOrDefault(107u));
        Assert.Equal(2, stores.GetValueOrDefault(126u));
        Assert.Equal(1, stores.GetValueOrDefault(127u));
    }

    [Fact]
    public void PixelShader_AddCarryOut_ScalarDestinationReceivesExecMaskedLaneMask()
    {
        // v_add_co_u32 v0, s[40:41], v1, v2 (VOP3B): the carry-out is a
        // per-lane ballot like VCC, stored to both halves of the sdst pair
        // and masked with EXEC, not a scalar 0/1.
        var module = CompilePixelShader(0xD70F2800, 0x00020501, ExportV0Rgba, 0x00000000, SEndpgm);

        var carry = SingleResultOf(module, OpULessThan);
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpLogicalAnd && (i.Words[3] == carry || i.Words[4] == carry));

        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(1, stores.GetValueOrDefault(40u));
        Assert.Equal(1, stores.GetValueOrDefault(41u));
        Assert.Equal(0, stores.GetValueOrDefault(106u));
    }

    [Fact]
    public void PixelShader_AddWithCarry_VccBallotIsMaskedWithExec()
    {
        // v_add_co_ci_u32 v0, vcc, v1, v2, vcc: the carry-out ballot
        // (LogicalOr of the two overflow tests) must be ANDed with EXEC
        // before it lands in VCC.
        var module = CompilePixelShader(0x50000501, ExportV0Rgba, 0x00000000, SEndpgm);

        var carry = SingleResultOf(module, OpLogicalOr);
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpLogicalAnd && (i.Words[3] == carry || i.Words[4] == carry));

        var stores = ScalarRegisterStoreCounts(module);
        Assert.Equal(1, stores.GetValueOrDefault(106u));
        Assert.Equal(1, stores.GetValueOrDefault(107u));
    }
}
