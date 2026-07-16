// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Covers the position-capture compute variant of a pass-through NGG export
/// shader: with an <see cref="NggComputeCapture"/> config the compute compile
/// declares gl_GlobalInvocationID, seeds the vertex-index VGPR from it, and
/// redirects the POS0 (exp target 12) export into a storage buffer instead of
/// dropping every export. With no config the compute compile is byte-identical
/// to the historical compute path.
/// </summary>
public sealed class Gen5NggComputeCaptureTests
{
    private const ulong ShaderAddress = 0x30000;
    private const uint ExecutionModelGLCompute = 5;
    private const ushort OpDecorate = 71;
    private const ushort OpStore = 62;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationBinding = 33;
    private const uint BuiltInGlobalInvocationId = 28;

    // A minimal pass-through export body: export a clip-space vec4 to POS0
    // (exp target 12, EN=0xF, DONE) from v0..v3, then end the program.
    //   word0 = 0xF8000000 (EXP) | (12<<4) | 0xF | (1<<11 DONE) = 0xF80008CF
    //   word1 = source VGPRs v0,v1,v2,v3 packed one per byte
    private static readonly uint[] Pos0ExportEs =
    [
        0xF80008CF,
        0x03020100,
        0xBF810000,
    ];

    private static (Gen5ShaderState State, Gen5ShaderEvaluation Evaluation) DecodeAndEvaluate()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(ShaderAddress, Pos0ExportEs);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryCreateState(
            ctx, ShaderAddress, 0, new Dictionary<uint, uint>(), 0, out var state, out var stateError), stateError);
        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx, state, out var evaluation, out var evalError), evalError);
        return (state, evaluation);
    }

    private static byte[] Compile(NggComputeCapture? capture)
    {
        var (state, evaluation) = DecodeAndEvaluate();
        var ok = Gen5SpirvTranslator.TryCompileComputeShader(
            state,
            evaluation,
            64,
            1,
            1,
            capture,
            out var shader,
            out var error);
        Assert.True(ok, $"TryCompileComputeShader failed: {error}");
        return shader.Spirv;
    }

    private static bool HasDecoration(
        SpirvModuleAssert.ParsedModule module,
        uint decoration,
        uint value) =>
        module.Instructions.Any(i =>
            i.Opcode == OpDecorate &&
            i.Words.Length >= 4 &&
            i.Words[2] == decoration &&
            i.Words[3] == value);

    [Fact]
    public void NoCapture_IsByteIdenticalToLegacyOverload()
    {
        // The capture config defaults to absent, so ordinary compute compiles
        // must stay byte-for-byte identical to the historical 7-argument entry.
        var (state, evaluation) = DecodeAndEvaluate();
        Assert.True(Gen5SpirvTranslator.TryCompileComputeShader(
            state, evaluation, 64, 1, 1, out var legacy, out var legacyError), legacyError);

        Assert.Equal(legacy.Spirv, Compile(capture: null));
    }

    [Fact]
    public void CaptureVariant_ProducesDifferentSpirvThanNoCapture()
    {
        var plain = Compile(capture: null);
        var captured = Compile(new NggComputeCapture(0, 4, 0, 0));

        // The capture route adds an input built-in, a VGPR seed, and a buffer
        // store, so it must diverge from the drop-every-export compute path.
        Assert.NotEqual(plain, captured);
    }

    [Fact]
    public void CaptureVariant_IsStructurallyValidComputeModule()
    {
        var module = SpirvModuleAssert.Parse(Compile(new NggComputeCapture(0, 4, 0, 0)));
        SpirvModuleAssert.AssertShaderModule(module, ExecutionModelGLCompute);
    }

    [Fact]
    public void CaptureVariant_DeclaresGlobalInvocationIdInput()
    {
        // The output vertex index is gl_GlobalInvocationID.x; the no-capture
        // path never declares that built-in.
        var captured = SpirvModuleAssert.Parse(Compile(new NggComputeCapture(0, 4, 0, 0)));
        var plain = SpirvModuleAssert.Parse(Compile(capture: null));

        Assert.True(
            HasDecoration(captured, DecorationBuiltIn, BuiltInGlobalInvocationId),
            "GlobalInvocationId built-in missing from capture variant.");
        Assert.False(
            HasDecoration(plain, DecorationBuiltIn, BuiltInGlobalInvocationId),
            "GlobalInvocationId built-in leaked into the no-capture variant.");
    }

    [Fact]
    public void CaptureVariant_RedirectsPos0IntoAStorageBuffer()
    {
        // The POS0 export is dropped in the no-capture compute path (no exports
        // land anywhere), so that module declares no descriptor-bound buffer.
        // The capture variant appends the output storage buffer at binding 0 and
        // stores into it.
        var captured = SpirvModuleAssert.Parse(Compile(new NggComputeCapture(0, 4, 0, 0)));
        var plain = SpirvModuleAssert.Parse(Compile(capture: null));

        Assert.True(
            HasDecoration(captured, DecorationBinding, 0),
            "Capture variant must declare the output storage buffer at binding 0.");
        Assert.False(
            HasDecoration(plain, DecorationBinding, 0),
            "No-capture variant must not declare any descriptor binding.");
        Assert.Contains(captured.Instructions, i => i.Opcode == OpStore);
    }
}
