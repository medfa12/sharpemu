// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Runs the production fullscreen-shader word blobs (the exact programs
/// Gen5ShaderTranslator pattern-matches at runtime) through the public
/// recognition entry points. This exercises the full RDNA2 decoder on real
/// shader code: SOPP/SOP1/SOP2/VOP1/VOP2/VOP3/EXP/SOPK encodings, literals,
/// and multi-dword instructions.
/// </summary>
public sealed class Gen5FullscreenShaderTests
{
    private const ulong EsAddress = 0x10000;
    private const ulong PsAddress = 0x20000;

    // Mirrors of the translator's embedded reference programs. If the source
    // arrays change these copies must be updated — the tests below assert the
    // decoder still recognizes exactly these words.
    private static readonly uint[] FullscreenBarycentricEs =
    [
        0xBFA00001, 0x7E000000, 0x7E000000, 0x7E000000,
        0x93EBFF03, 0x00080008, 0x8F6A8C6B, 0x8700FF03,
        0x000000FF, 0x887C6A00, 0xBF900009, 0x81EA6BC0,
        0x90FE6AC1, 0xF8000941, 0x00000000, 0x81EA00C0,
        0xBF8CFF0F, 0x90FE6AC1, 0x36040A81, 0x2C060A81,
        0x7E000280, 0x7E0202F2, 0xD7460002, 0x03050302,
        0xD7460003, 0x03050303, 0x7E040B02, 0x7E060B03,
        0xF80008CF, 0x01000302, 0xBF810000,
    ];

    private static readonly uint[] FullscreenBarycentricPs =
    [
        0xD52F0000, 0x00000200,
        0xD52F0001, 0x00000602,
        0xF8001C0F, 0x00000100,
        0xBF810000,
    ];

    private static readonly uint[] Gen5RectListExportEs =
    [
        0xBFA00001, 0x7E000000, 0x7E000000, 0x7E000000,
        0x9380FF03, 0x00080008, 0x8F6A8C00, 0x876BFF03,
        0x000000FF, 0x887C6A6B, 0xBF900009, 0x81EA6BC0,
        0x90FE6AC1, 0x36060A81, 0x2C080A81, 0x7E020280,
        0x7E0402F2, 0xD7460003, 0x03050303, 0xD7460004,
        0x03050304, 0x7E060B03, 0x7E080B04, 0xF80008CF,
        0x02010403, 0x81EA00C0, 0xBF8CFF0F, 0x90FE6AC1,
        0xF8000941, 0x00000000, 0xBF810000,
    ];

    private static CpuContext ContextWith(ulong address, uint[] words)
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(address, words);
        return new CpuContext(memory, Generation.Gen5);
    }

    [Fact]
    public void IsFullscreenExportShader_RecognizesBarycentricProgram()
    {
        var ctx = ContextWith(EsAddress, FullscreenBarycentricEs);

        Assert.True(Gen5ShaderTranslator.IsFullscreenExportShader(ctx, EsAddress));
    }

    [Fact]
    public void IsFullscreenExportShader_RecognizesRectListProgram()
    {
        var ctx = ContextWith(EsAddress, Gen5RectListExportEs);

        Assert.True(Gen5ShaderTranslator.IsFullscreenExportShader(ctx, EsAddress));
    }

    [Fact]
    public void IsFullscreenExportShader_RejectsPerturbedProgram()
    {
        var perturbed = (uint[])FullscreenBarycentricEs.Clone();
        perturbed[20] ^= 0x0000_0001; // flip one operand bit
        var ctx = ContextWith(EsAddress, perturbed);

        Assert.False(Gen5ShaderTranslator.IsFullscreenExportShader(ctx, EsAddress));
    }

    [Fact]
    public void IsFullscreenExportShader_RejectsZeroAddressAndUnmapped()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);

        Assert.False(Gen5ShaderTranslator.IsFullscreenExportShader(ctx, 0));
        Assert.False(Gen5ShaderTranslator.IsFullscreenExportShader(ctx, EsAddress));
    }

    [Fact]
    public void TryTranslate_RecognizesFullscreenBarycentricPair()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(EsAddress, FullscreenBarycentricEs);
        memory.WriteWords(PsAddress, FullscreenBarycentricPs);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryTranslate(
            ctx, EsAddress, PsAddress, psInputEna: 0x2, psInputAddr: 0x2, out var drawKind));
        Assert.Equal(GuestDrawKind.FullscreenBarycentric, drawKind);
    }

    [Fact]
    public void TryTranslate_RejectsWrongPsInputConfiguration()
    {
        var memory = new SparseGuestMemory();
        memory.WriteWords(EsAddress, FullscreenBarycentricEs);
        memory.WriteWords(PsAddress, FullscreenBarycentricPs);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(Gen5ShaderTranslator.TryTranslate(
            ctx, EsAddress, PsAddress, psInputEna: 0x3, psInputAddr: 0x2, out var drawKind));
        Assert.Equal(GuestDrawKind.None, drawKind);
    }

    [Fact]
    public void DescribeWords_RoundTripsAllProgramWords()
    {
        var ctx = ContextWith(EsAddress, FullscreenBarycentricEs);

        var described = Gen5ShaderTranslator.DescribeWords(ctx, EsAddress);

        var expected = string.Join(',', FullscreenBarycentricEs.Select(w => $"{w:X8}"));
        Assert.Equal(expected, described);
    }
}
