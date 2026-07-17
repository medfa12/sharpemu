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

    private const uint ExportV0Rgba = 0xF8001C0F; // exp mrt0 v0,v0,v0,v0 done vm
    private const uint SEndpgm = 0xBF810000;

    private const ushort OpName = 5, OpConstant = 43, OpStore = 62,
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
