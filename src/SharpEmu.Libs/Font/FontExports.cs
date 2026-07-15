// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Font;

/// <summary>
/// First-cut HLE for libSceFont. This is enough of the initialization surface
/// to let a title's font/text subsystem set up without dereferencing a null
/// library, renderer, or font object: creation and open calls hand back a
/// non-null, zero-filled scratch object; everything else reports success.
///
/// It does NOT render real glyphs yet — text will be blank until the renderer
/// and glyph paths are implemented. The metrics/layout queries return
/// synthetic but self-consistent values derived from the pixel scale the game
/// set, so text layout completes and the frame loop keeps advancing. The
/// out-parameter positions follow the public libSceFont signatures
/// (out-handle is the trailing pointer argument); writes are guarded by
/// TryWriteUInt64 so a wrong guess cannot corrupt memory.
/// </summary>
public static class FontExports
{
    // Opaque font objects are backed by generously sized zeroed scratch so the
    // game's field reads (observed up to +0x370 on a library object) land in
    // mapped, zeroed memory rather than faulting.
    private const int ScratchObjectSize = 0x1000;
    private const int ScratchObjectAlign = 16;

    // Error values from the libSceFont ABI (see shadPS4 font_error.h).
    private const uint FontErrorInvalidParameter = 0x80460002;
    private const uint FontErrorInvalidFontHandle = 0x80460005;
    private const uint FontErrorNoSupportCode = 0x80460041;

    // Pixel scale per font handle, captured from sceFontSetScalePixel /
    // sceFontSetupRenderScalePixel. Handles are the scratch-object guest
    // addresses handed out above. 16px matches the sysfont default a title
    // gets when it never sets a scale.
    private const float DefaultScalePixel = 16.0f;
    private static readonly ConcurrentDictionary<ulong, (float W, float H)> ScalePixels = new();

    private static bool TryHandOutHandle(CpuContext ctx, ulong outPointer, out ulong handle)
    {
        handle = 0;
        if (outPointer == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, ScratchObjectSize, ScratchObjectAlign, out handle))
        {
            return false;
        }

        return ctx.TryWriteUInt64(outPointer, handle);
    }

    [SysAbiExport(Nid = "whrS4oksXc4", ExportName = "sceFontMemoryInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontMemoryInit(CpuContext ctx)
    {
        // sceFontMemoryInit(OrbisFontMemory* memory, void* buffer, uint size).
        // The memory struct is caller-provided; report success.
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "n590hj5Oe-k", ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateLibraryWithEdition(CpuContext ctx)
    {
        // (OrbisFontMemory* memory, const void* edition, uint num, OrbisFontLibrary** outLibrary)
        _ = TryHandOutHandle(ctx, ctx[CpuRegister.Rcx], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "WaSFJoRWXaI", ExportName = "sceFontCreateRendererWithEdition",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateRendererWithEdition(CpuContext ctx)
    {
        // (OrbisFontMemory* memory, const void* edition, uint num, OrbisFontRenderer** outRenderer)
        _ = TryHandOutHandle(ctx, ctx[CpuRegister.Rcx], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "KXUpebrFk1U", ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontOpenFontMemory(CpuContext ctx)
    {
        // (library, void* memory, size, options, OrbisFontHandle* out) -> out in R8,
        // confirmed at the game's call site (mov r8,<slot>; mov [slot],0 first).
        _ = TryHandOutHandle(ctx, ctx[CpuRegister.R8], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "JzCH3SCFnAU", ExportName = "sceFontOpenFontInstance",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontOpenFontInstance(CpuContext ctx)
    {
        // (library, openDetail, OrbisFontHandle* out) -> out in RDX, confirmed at
        // the call site (mov rdx,<slot>; xor esi,esi).
        _ = TryHandOutHandle(ctx, ctx[CpuRegister.Rdx], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "cKYtVmeSTcw", ExportName = "sceFontOpenFontSet",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontOpenFontSet(CpuContext ctx)
    {
        // (library, fontSet, mode, options, OrbisFontHandle* out) -> out in R8,
        // confirmed at the call site (mov r8,<slot>; mov [slot],0 first).
        _ = TryHandOutHandle(ctx, ctx[CpuRegister.R8], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Everything below sets up render state, queries layout, or tears objects
    // down. Reporting success lets initialization proceed; real glyph output
    // is future work.
    [SysAbiExport(Nid = "3OdRkSjOcog", ExportName = "sceFontBindRenderer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontBindRenderer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "1QjhKxrsOB8", ExportName = "sceFontUnbindRenderer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontUnbindRenderer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "oM+XCzVG3oM", ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSelectLibraryFt(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "Xx974EW-QFY", ExportName = "sceFontSelectRendererFt",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSelectRendererFt(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "mz2iTY0MK4A", ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSupportExternalFonts(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "SsRbbCiWoGw", ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSupportSystemFonts(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "gdUCnU0gHdI", ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderSurfaceInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "oaJ1BpN2FQk", ExportName = "sceFontTextSourceInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextSourceInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "eCRMCSk96NU", ExportName = "sceFontTextSourceSetDefaultFont",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextSourceSetDefaultFont(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "OqQKX0h5COw", ExportName = "sceFontTextSourceSetWritingForm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextSourceSetWritingForm(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "fD5rqhEXKYQ", ExportName = "sceFontWritingInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "N1EBMeGhf7E", ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetScalePixel(CpuContext ctx) => RecordScalePixel(ctx);

    [SysAbiExport(Nid = "6vGCkkQJOcI", ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderScalePixel(CpuContext ctx) => RecordScalePixel(ctx);

    private static int RecordScalePixel(CpuContext ctx)
    {
        // (OrbisFontHandle handle, float w, float h) -> handle in RDI, the two
        // floats in XMM0/XMM1. The scale feeds the synthetic glyph metrics.
        var handle = ctx[CpuRegister.Rdi];
        var w = ReadXmmFloat(ctx, 0);
        var h = ReadXmmFloat(ctx, 1);
        if (handle != 0 && float.IsFinite(w) && float.IsFinite(h) && w > 0f && h > 0f)
        {
            ScalePixels[handle] = (w, h);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "IQtleGLL5pQ", ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderCharGlyphMetrics(CpuContext ctx) => FillGlyphMetrics(ctx);

    [SysAbiExport(Nid = "L97d+3OgMlE", ExportName = "sceFontGetCharGlyphMetrics",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetCharGlyphMetrics(CpuContext ctx) => FillGlyphMetrics(ctx);

    // Both metrics queries share one body: titles call the Render variant first
    // and fall back to the plain one on any error, so both must exist and both
    // report the same synthetic metrics.
    //
    // OrbisFontGlyphMetrics is 8 consecutive floats (32 bytes): width, height,
    // Horizontal { bearingX, bearingY, advance }, Vertical { bearingX,
    // bearingY, advance }. The Vertical block is written as zero even on
    // success, matching the real library's horizontal-only fill.
    //
    // Without a real charmap we treat every nonzero codepoint as supported and
    // return metrics derived from the handle's pixel scale. A per-codepoint
    // NO_SUPPORT_GLYPH here would be arbitrary, and a zero advance stalls the
    // caller's pen-advance loop, so printable glyphs always get advance > 0.
    private static int FillGlyphMetrics(CpuContext ctx)
    {
        // (OrbisFontHandle handle, u32 code, OrbisFontGlyphMetrics* metrics)
        var handle = ctx[CpuRegister.Rdi];
        var code = (uint)ctx[CpuRegister.Rsi];
        var metrics = ctx[CpuRegister.Rdx];

        if (handle == 0)
        {
            ZeroGuestStruct(ctx, metrics, 0x20);
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (code == 0)
        {
            ZeroGuestStruct(ctx, metrics, 0x20);
            return Error(ctx, FontErrorNoSupportCode);
        }

        if (metrics == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var (scaleW, scaleH) = GetScalePixel(handle);
        var isSpace = code is 0x20 or 0x09 or 0xA0 or 0x3000;
        var width = isSpace ? 0f : 0.55f * scaleW;
        var height = isSpace ? 0f : 0.72f * scaleH;
        var bearingX = isSpace ? 0f : 0.04f * scaleW;
        var bearingY = height; // glyph top sits bearingY above the baseline, inside the 0.88h ascent
        var advance = code == 0x3000 ? scaleW : (isSpace ? 0.30f * scaleW : 0.60f * scaleW);

        var ok = TryWriteFloat(ctx, metrics + 0x00, width)
            && TryWriteFloat(ctx, metrics + 0x04, height)
            && TryWriteFloat(ctx, metrics + 0x08, bearingX)
            && TryWriteFloat(ctx, metrics + 0x0C, bearingY)
            && TryWriteFloat(ctx, metrics + 0x10, advance)
            && TryWriteFloat(ctx, metrics + 0x14, 0f)
            && TryWriteFloat(ctx, metrics + 0x18, 0f)
            && TryWriteFloat(ctx, metrics + 0x1C, 0f);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "imxVx8lm+KM", ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetHorizontalLayout(CpuContext ctx)
    {
        // (OrbisFontHandle handle, OrbisFontHorizontalLayout* layout).
        // Three floats: baselineOffset (line top to baseline), lineAdvance
        // (full line height), decorationExtent (effect height, none here).
        // Values stay consistent with the glyph metrics above: bearingY
        // (0.72h) <= baselineOffset (0.88h) < lineAdvance (1.18h).
        var handle = ctx[CpuRegister.Rdi];
        var layout = ctx[CpuRegister.Rsi];

        if (handle == 0)
        {
            ZeroGuestStruct(ctx, layout, 0x0C);
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (layout == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var (_, scaleH) = GetScalePixel(handle);
        var ok = TryWriteFloat(ctx, layout + 0x00, 0.88f * scaleH)
            && TryWriteFloat(ctx, layout + 0x04, 1.18f * scaleH)
            && TryWriteFloat(ctx, layout + 0x08, 0f);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    private static (float W, float H) GetScalePixel(ulong handle) =>
        ScalePixels.TryGetValue(handle, out var scale) && scale.W > 0f && scale.H > 0f
            ? scale
            : (DefaultScalePixel, DefaultScalePixel);

    private static float ReadXmmFloat(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var low, out _);
        return BitConverter.UInt32BitsToSingle((uint)low);
    }

    private static bool TryWriteFloat(CpuContext ctx, ulong address, float value) =>
        ctx.TryWriteUInt32(address, BitConverter.SingleToUInt32Bits(value));

    // The real library clears the out struct on every failure path before
    // returning the error, so callers never see stale fields.
    private static void ZeroGuestStruct(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return;
        }

        for (var offset = 0; offset < size; offset += sizeof(uint))
        {
            _ = ctx.TryWriteUInt32(address + (ulong)offset, 0);
        }
    }

    [SysAbiExport(Nid = "TMtqoFQjjbA", ExportName = "sceFontSetEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetEffectSlant(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "v0phZwa4R5o", ExportName = "sceFontSetEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetEffectWeight(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "lz9y9UFO2UU", ExportName = "sceFontSetupRenderEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderEffectSlant(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "XIGorvLusDQ", ExportName = "sceFontSetupRenderEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderEffectWeight(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "CUKn5pX-NVY", ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontAttachDeviceCacheBuffer(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "vzHs3C8lWJk", ExportName = "sceFontCloseFont",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCloseFont(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "exAxkyVLt0s", ExportName = "sceFontDestroyRenderer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyRenderer(CpuContext ctx) => Ok(ctx);

    private static int Ok(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int Error(CpuContext ctx, uint error)
    {
        ctx[CpuRegister.Rax] = error;
        return 0;
    }

    internal static void ResetScaleForTests() => ScalePixels.Clear();
}
