// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Font;

/// <summary>
/// Synthetic libSceFont implementation used when the platform font modules
/// are unavailable. It exposes a scale-aware monospace face and renders
/// coverage rectangles into guest render surfaces.
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
    private const uint FontErrorInvalidGlyph = 0x80460006;
    private const uint FontErrorAllocationFailed = 0x80460010;
    private const uint FontErrorNoSupportCode = 0x80460041;
    private const uint FontErrorNoSupportSurface = 0x80460050;

    private const float DefaultScalePixel = 16.0f;
    private const float PointsPerInch = 72.0f;
    private const ushort StyleFrameMagic = 0x0F09;
    private readonly record struct FontScale(float W, float H, bool IsPoint, uint DpiX, uint DpiY);
    private static readonly ConcurrentDictionary<ulong, FontScale> FontScales = new();
    private static readonly byte[] CoveragePixel1 = [0xFF];
    private static readonly byte[] CoveragePixel4 = [0xFF, 0xFF, 0xFF, 0xFF];

    private static bool TryHandOutHandle(
        CpuContext ctx,
        ulong outPointer,
        out ulong handle,
        FontScale? inheritedScale = null)
    {
        handle = 0;
        if (outPointer == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, ScratchObjectSize, ScratchObjectAlign, out handle))
        {
            return false;
        }

        if (!ctx.TryWriteUInt64(outPointer, handle))
        {
            return false;
        }

        FontScales[handle] = inheritedScale ?? DefaultFontScale;
        return true;
    }

    private static FontScale DefaultFontScale =>
        new(DefaultScalePixel, DefaultScalePixel, false, 72, 72);

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
        // (fontHandle, templateFont, OrbisFontHandle* out) -> out in RDX,
        // confirmed at the call site (mov rdx,<slot>; xor esi,esi).
        var source = ctx[CpuRegister.Rdi];
        if (!FontScales.TryGetValue(source, out var inherited))
        {
            inherited = DefaultFontScale;
        }

        _ = TryHandOutHandle(ctx, ctx[CpuRegister.Rdx], out _, inherited);
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

    // The game's text engine derives its glyph-blit SOURCE pointer from the
    // render surface's buffer field (surface+0x00), either directly or via the
    // SurfaceImage.address we echo back from sceFontRenderCharGlyphImage*.
    // The real library writes that struct in sceFontRenderSurfaceInit; when
    // this call was a no-op the field stayed null and the game's per-row blit
    // faulted on src = null + penY*widthByte + penX. If a title hands us no
    // backing store at all, a single shared zero-filled arena stands in so
    // every derived pixel pointer reads mapped zeros (blank glyphs, no fault).
    private const int BlankGlyphArenaSize = 0x100000;
    private const uint FallbackSurfaceWidthByte = 0x800;
    private static ulong _blankGlyphArena;
    private static readonly object BlankGlyphArenaGate = new();

    private static ulong GetBlankGlyphArena(CpuContext ctx)
    {
        if (_blankGlyphArena != 0)
        {
            return _blankGlyphArena;
        }

        lock (BlankGlyphArenaGate)
        {
            if (_blankGlyphArena == 0 &&
                KernelMemoryCompatExports.TryAllocateHleData(
                    ctx, BlankGlyphArenaSize, ScratchObjectAlign, out var arena))
            {
                // The allocator hands back zeroed pages, but the arena is the
                // one buffer whose contents the game consumes as pixels, so
                // re-zero explicitly rather than trust that forever.
                for (var offset = 0; offset < BlankGlyphArenaSize; offset += GlyphZeroFill.Length)
                {
                    _ = ctx.Memory.TryWrite(arena + (ulong)offset, GlyphZeroFill);
                }

                _blankGlyphArena = arena;
            }
        }

        return _blankGlyphArena;
    }

    [SysAbiExport(Nid = "gdUCnU0gHdI", ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderSurfaceInit(CpuContext ctx)
    {
        // void sceFontRenderSurfaceInit(OrbisFontRenderSurface* surface,
        //   void* buffer, int bufWidthByte, int pixelSizeByte, int widthPixel,
        //   int heightPixel) -> RDI, RSI, EDX, ECX, R8D, R9D.
        // OrbisFontRenderSurface: buffer @+0x00, widthByte @+0x08,
        // pixelSizeByte/pad/styleFlag/pad @+0x0C, width @+0x10, height @+0x14,
        // scissor x0/y0/x1/y1 @+0x18..0x24 (reset to the full surface here).
        var surface = ctx[CpuRegister.Rdi];
        if (surface == 0)
        {
            return Ok(ctx);
        }

        var buffer = ctx[CpuRegister.Rsi];
        var widthByte = (uint)ctx[CpuRegister.Rdx];
        var pixelSize = (uint)ctx[CpuRegister.Rcx] & 0xFF;
        var width = (uint)Math.Max((int)(uint)ctx[CpuRegister.R8], 0);
        var height = (uint)Math.Max((int)(uint)ctx[CpuRegister.R9], 0);
        if (buffer == 0)
        {
            buffer = GetBlankGlyphArena(ctx);
            if (widthByte == 0)
            {
                widthByte = FallbackSurfaceWidthByte;
            }

            if (pixelSize == 0)
            {
                pixelSize = 1;
            }
        }

        _ = ctx.TryWriteUInt64(surface + 0x00, buffer)
            && ctx.TryWriteUInt32(surface + 0x08, widthByte)
            && ctx.TryWriteUInt32(surface + 0x0C, pixelSize)
            && ctx.TryWriteUInt32(surface + 0x10, width)
            && ctx.TryWriteUInt32(surface + 0x14, height)
            && ctx.TryWriteUInt32(surface + 0x18, 0)
            && ctx.TryWriteUInt32(surface + 0x1C, 0)
            && ctx.TryWriteUInt32(surface + 0x20, width)
            && ctx.TryWriteUInt32(surface + 0x24, height);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "vRxf4d0ulPs", ExportName = "sceFontRenderSurfaceSetScissor",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderSurfaceSetScissor(CpuContext ctx)
    {
        var surface = ctx[CpuRegister.Rdi];
        if (surface == 0 ||
            !ctx.TryReadInt32(surface + 0x10, out var surfaceWidth) ||
            !ctx.TryReadInt32(surface + 0x14, out var surfaceHeight))
        {
            return Ok(ctx);
        }

        var x = (int)ctx[CpuRegister.Rsi];
        var y = (int)ctx[CpuRegister.Rdx];
        var width = (int)ctx[CpuRegister.Rcx];
        var height = (int)ctx[CpuRegister.R8];
        var x0 = ClampLongToRange(x, 0, Math.Max(surfaceWidth, 0));
        var y0 = ClampLongToRange(y, 0, Math.Max(surfaceHeight, 0));
        var x1 = ClampLongToRange((long)x + Math.Max(width, 0), 0, Math.Max(surfaceWidth, 0));
        var y1 = ClampLongToRange((long)y + Math.Max(height, 0), 0, Math.Max(surfaceHeight, 0));
        _ = ctx.TryWriteUInt32(surface + 0x18, (uint)x0)
            && ctx.TryWriteUInt32(surface + 0x1C, (uint)y0)
            && ctx.TryWriteUInt32(surface + 0x20, (uint)Math.Max(x1, x0))
            && ctx.TryWriteUInt32(surface + 0x24, (uint)Math.Max(y1, y0));
        return Ok(ctx);
    }

    private static int ClampLongToRange(long value, int minimum, int maximum) =>
        (int)Math.Clamp(value, minimum, maximum);

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
    public static int FontSetScalePixel(CpuContext ctx) => RecordScale(ctx, isPoint: false);

    [SysAbiExport(Nid = "6vGCkkQJOcI", ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderScalePixel(CpuContext ctx) => RecordScale(ctx, isPoint: false);

    [SysAbiExport(Nid = "sw65+7wXCKE", ExportName = "sceFontSetScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetScalePoint(CpuContext ctx) => RecordScale(ctx, isPoint: true);

    [SysAbiExport(Nid = "nMZid4oDfi4", ExportName = "sceFontSetupRenderScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderScalePoint(CpuContext ctx) => RecordScale(ctx, isPoint: true);

    [SysAbiExport(Nid = "I1acwR7Qp8E", ExportName = "sceFontSetResolutionDpi",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetResolutionDpi(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        var scale = FontScales.GetOrAdd(handle, DefaultFontScale);
        var dpiX = (uint)ctx[CpuRegister.Rsi];
        var dpiY = (uint)ctx[CpuRegister.Rdx];
        FontScales[handle] = scale with
        {
            DpiX = dpiX == 0 ? 72u : dpiX,
            DpiY = dpiY == 0 ? 72u : dpiY,
        };
        return Ok(ctx);
    }

    private static int RecordScale(CpuContext ctx, bool isPoint)
    {
        var handle = ctx[CpuRegister.Rdi];
        var w = ReadXmmFloat(ctx, 0);
        var h = ReadXmmFloat(ctx, 1);
        if (handle == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (!float.IsFinite(w) || !float.IsFinite(h) || w <= 0f || h <= 0f)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var previous = FontScales.GetOrAdd(handle, DefaultFontScale);
        FontScales[handle] = new FontScale(w, h, isPoint, previous.DpiX, previous.DpiY);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "IQtleGLL5pQ", ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderCharGlyphMetrics(CpuContext ctx) => FillGlyphMetrics(ctx);

    [SysAbiExport(Nid = "L97d+3OgMlE", ExportName = "sceFontGetCharGlyphMetrics",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetCharGlyphMetrics(CpuContext ctx) => FillGlyphMetrics(ctx);

    private readonly record struct GlyphMetrics(
        float Width,
        float Height,
        float HorizontalBearingX,
        float HorizontalBearingY,
        float HorizontalAdvance,
        float VerticalBearingX,
        float VerticalBearingY,
        float VerticalAdvance);

    private static int FillGlyphMetrics(CpuContext ctx)
    {
        return FillGlyphMetricsAt(
            ctx, ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }

    private static int FillGlyphMetricsAt(CpuContext ctx, ulong handle, uint code, ulong metrics)
    {
        if (handle == 0)
        {
            ZeroGuestStruct(ctx, metrics, 0x20);
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (!IsSupportedCode(code))
        {
            ZeroGuestStruct(ctx, metrics, 0x20);
            return Error(ctx, FontErrorNoSupportCode);
        }

        if (metrics == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var (scaleW, scaleH) = GetScalePixel(handle);
        var values = BuildGlyphMetrics(code, scaleW, scaleH);
        var ok = WriteGlyphMetrics(ctx, metrics, values);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    private static GlyphMetrics BuildGlyphMetrics(uint code, float scaleW, float scaleH)
    {
        var isSpace = IsWhiteSpace(code);
        var width = isSpace ? 0f : Quantize26Dot6(0.55f * scaleW);
        var height = isSpace ? 0f : Quantize26Dot6(0.72f * scaleH);
        var horizontalBearingX = isSpace ? 0f : Quantize26Dot6(0.04f * scaleW);
        var horizontalAdvance = Quantize26Dot6(0.60f * scaleW);
        var verticalAdvance = Quantize26Dot6(scaleH);
        return new GlyphMetrics(
            width,
            height,
            horizontalBearingX,
            height,
            horizontalAdvance,
            isSpace ? 0f : Quantize26Dot6(width * 0.5f),
            isSpace ? 0f : Quantize26Dot6(0.14f * scaleH),
            verticalAdvance);
    }

    private static bool WriteGlyphMetrics(CpuContext ctx, ulong address, GlyphMetrics metrics) =>
        TryWriteFloat(ctx, address + 0x00, metrics.Width)
        && TryWriteFloat(ctx, address + 0x04, metrics.Height)
        && TryWriteFloat(ctx, address + 0x08, metrics.HorizontalBearingX)
        && TryWriteFloat(ctx, address + 0x0C, metrics.HorizontalBearingY)
        && TryWriteFloat(ctx, address + 0x10, metrics.HorizontalAdvance)
        && TryWriteFloat(ctx, address + 0x14, metrics.VerticalBearingX)
        && TryWriteFloat(ctx, address + 0x18, metrics.VerticalBearingY)
        && TryWriteFloat(ctx, address + 0x1C, metrics.VerticalAdvance);

    private static bool IsSupportedCode(uint code) =>
        code is > 0 and <= 0x10FFFF && code is not (>= 0xD800 and <= 0xDFFF);

    private static bool IsWhiteSpace(uint code) => code is 0x09 or 0x0A or 0x0D or 0x20 or 0xA0 or 0x3000;

    private static float Quantize26Dot6(float value) => MathF.Round(value * 64f) / 64f;

    [SysAbiExport(Nid = "imxVx8lm+KM", ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetHorizontalLayout(CpuContext ctx)
    {
        // (OrbisFontHandle handle, OrbisFontHorizontalLayout* layout).
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
        var ascender = Quantize26Dot6(0.88f * scaleH);
        var descender = Quantize26Dot6(0.30f * scaleH);
        var ok = TryWriteFloat(ctx, layout + 0x00, ascender)
            && TryWriteFloat(ctx, layout + 0x04, ascender + descender)
            && TryWriteFloat(ctx, layout + 0x08, 0f);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "3BrWWFU+4ts", ExportName = "sceFontGetVerticalLayout",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetVerticalLayout(CpuContext ctx)
    {
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

        var (scaleW, _) = GetScalePixel(handle);
        var columnAdvance = Quantize26Dot6(scaleW);
        var ok = TryWriteFloat(ctx, layout + 0x00, Quantize26Dot6(columnAdvance * 0.5f))
            && TryWriteFloat(ctx, layout + 0x04, columnAdvance)
            && TryWriteFloat(ctx, layout + 0x08, 0f);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "OINC0X9HGBY", ExportName = "sceFontGetCharGlyphCode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetCharGlyphCode(CpuContext ctx)
    {
        // The public prototype and output ownership for this export remain
        // unreconstructed. Register the NID and avoid speculative guest writes.
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "lVSR5ftvNag", ExportName = "sceFontCharactersRefersTextCodes",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharactersRefersTextCodes(CpuContext ctx)
    {
        // The public prototype and output ownership for this export remain
        // unreconstructed. Register the NID and avoid speculative guest writes.
        return Ok(ctx);
    }

    // ----- Glyph objects (sceFontGenerateCharGlyph .. sceFontDeleteGlyph) -----
    //
    // The glyph object is opaque to the game but the game's font renderer
    // module reads fields well past shadPS4's 0x20-byte reconstruction (the
    // pre-fix crash was a field read at glyph+0x40C), so each glyph is backed
    // by the same 0x1000-byte zeroed scratch as the other font objects. The
    // known header (shadPS4 font.h OrbisFontGlyphOpaque) is filled in so any
    // magic/form checks pass; everything else reads as zero. Deleted glyphs
    // are recycled so a menu that regenerates its glyph set every frame does
    // not grow guest memory without bound.
    //
    // The glyph object carries no bitmap-pointer field: the blit source base
    // the game consumes is OrbisFontRenderSurface.buffer, wired by
    // sceFontRenderSurfaceInit and backstopped inside
    // sceFontRenderCharGlyphImageHorizontal (the null + 0x1040A memcpy fix).
    // If some title turns out to read a bitmap pointer out of the glyph
    // object itself, point that field at GetBlankGlyphArena here as well.
    private static readonly ConcurrentDictionary<ulong, byte> LiveGlyphs = new();
    private static readonly ConcurrentQueue<ulong> FreeGlyphs = new();
    private static readonly byte[] GlyphZeroFill = new byte[ScratchObjectSize];

    [SysAbiExport(Nid = "C-4Qw5Srlyw", ExportName = "sceFontGenerateCharGlyph",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGenerateCharGlyph(CpuContext ctx)
    {
        // (OrbisFontHandle handle, u32 codepoint,
        //  const OrbisFontGenerateGlyphParams* params, OrbisFontGlyph* outGlyph)
        // -> RDI, ESI, RDX (params may be null), RCX (out glyph pointer).
        var handle = ctx[CpuRegister.Rdi];
        var code = (uint)ctx[CpuRegister.Rsi];
        var genParams = ctx[CpuRegister.Rdx];
        var outGlyph = ctx[CpuRegister.Rcx];

        if (outGlyph == 0 || !ctx.TryWriteUInt64(outGlyph, 0))
        {
            // The real library clears the out pointer before any validation.
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (handle == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (code == 0)
        {
            return Error(ctx, FontErrorNoSupportCode);
        }

        // OrbisFontGenerateGlyphParams: u16 id; u16 res0; u16 formOptions;
        // u8 glyphForm; u8 metricsForm; OrbisFontMem* mem (+0x08).
        uint formsWord = 0;
        ulong glyphMemory = 0;
        if (genParams != 0 && ctx.TryReadUInt32(genParams + 0x04, out formsWord))
        {
            _ = ctx.TryReadUInt64(genParams + 0x08, out glyphMemory);
        }

        if (!TryAcquireGlyphObject(ctx, out var glyph))
        {
            return Error(ctx, FontErrorAllocationFailed);
        }

        // OrbisFontGlyphOpaque header: magic 0x0F03 @+0x00, formOptions
        // @+0x02, glyphForm/metricsForm @+0x04..0x05, emSize @+0x06, baseline
        // @+0x08, heightPx @+0x0A, originX @+0x0C, originY @+0x0E, scaleX
        // @+0x10 (f32), baseScale @+0x14 (f32), OrbisFontMem* @+0x18.
        var (scaleW, scaleH) = GetScalePixel(handle);
        var emSize = ClampToU16(scaleH);
        var baseline = ClampToU16(0.72f * scaleH);
        var ok = ctx.TryWriteUInt32(glyph + 0x00, 0x0F03u | (formsWord << 16))
            && ctx.TryWriteUInt32(glyph + 0x04, (formsWord >> 16) | ((uint)emSize << 16))
            && ctx.TryWriteUInt32(glyph + 0x08, baseline | ((uint)baseline << 16))
            && ctx.TryWriteUInt32(glyph + 0x0C, (uint)baseline << 16)
            && TryWriteFloat(ctx, glyph + 0x10, scaleW)
            && TryWriteFloat(ctx, glyph + 0x14, scaleH)
            && ctx.TryWriteUInt64(glyph + 0x18, glyphMemory)
            && ctx.TryWriteUInt64(outGlyph, glyph);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "8-zmgsxkBek", ExportName = "sceFontGlyphDefineAttribute",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphDefineAttribute(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "kAenWy1Zw5o", ExportName = "sceFontRenderCharGlyphImageHorizontal",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderCharGlyphImageHorizontal(CpuContext ctx) => RenderCharGlyphImage(ctx);

    [SysAbiExport(Nid = "3G4zhgKuxE8", ExportName = "sceFontRenderCharGlyphImage",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderCharGlyphImage(CpuContext ctx) => RenderCharGlyphImage(ctx);

    [SysAbiExport(Nid = "i6UNdSig1uE", ExportName = "sceFontRenderCharGlyphImageVertical",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderCharGlyphImageVertical(CpuContext ctx) => RenderCharGlyphImage(ctx);

    private static int RenderCharGlyphImage(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var code = (uint)ctx[CpuRegister.Rsi];
        var surf = ctx[CpuRegister.Rdx];
        var metrics = ctx[CpuRegister.Rcx];
        var result = ctx[CpuRegister.R8];

        // The real library zeroes both out structs on every failure path so
        // the caller never consumes stale image pointers.
        if (handle == 0)
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidFontHandle);
        }

        if (!IsSupportedCode(code))
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorNoSupportCode);
        }

        if (surf == 0 || metrics == 0 || result == 0)
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidParameter);
        }

        var (scaleW, scaleH) = GetRenderScalePixel(ctx, handle, surf);
        var glyphMetrics = BuildGlyphMetrics(code, scaleW, scaleH);
        if (!WriteGlyphMetrics(ctx, metrics, glyphMetrics))
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (!TryReadRenderSurface(
                ctx,
                surf,
                out var surfBuffer,
                out var surfWidthByte,
                out var pixelSize,
                out var surfaceWidth,
                out var surfaceHeight,
                out var scissorX0,
                out var scissorY0,
                out var scissorX1,
                out var scissorY1))
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (surfBuffer == 0)
        {
            surfBuffer = GetBlankGlyphArena(ctx);
            if (surfWidthByte == 0)
            {
                surfWidthByte = FallbackSurfaceWidthByte;
            }

            if (pixelSize == 0)
            {
                pixelSize = 1;
            }
        }

        if (surfBuffer == 0 || pixelSize is not (1 or 4) ||
            surfWidthByte == 0 || surfaceWidth < 0 || surfaceHeight < 0)
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorNoSupportSurface);
        }

        if (surfBuffer == _blankGlyphArena)
        {
            surfaceHeight = Math.Min(surfaceHeight, (int)(BlankGlyphArenaSize / surfWidthByte));
        }

        var writableWidth = (int)Math.Min((uint)surfaceWidth, surfWidthByte / pixelSize);
        var clipX0 = Math.Clamp((int)Math.Min(scissorX0, int.MaxValue), 0, writableWidth);
        var clipY0 = Math.Clamp((int)Math.Min(scissorY0, int.MaxValue), 0, surfaceHeight);
        var clipX1 = Math.Clamp((int)Math.Min(scissorX1, int.MaxValue), 0, writableWidth);
        var clipY1 = Math.Clamp((int)Math.Min(scissorY1, int.MaxValue), 0, surfaceHeight);

        var x = ReadXmmFloat(ctx, 0);
        var y = ReadXmmFloat(ctx, 1);
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidParameter);
        }

        var glyphWidth = SaturatingCeilingToNonNegativeInt(glyphMetrics.Width);
        var glyphHeight = SaturatingCeilingToNonNegativeInt(glyphMetrics.Height);
        var destinationX = SaturatingAdd(
            SaturatingFloorToInt(x),
            SaturatingFloorToInt(glyphMetrics.HorizontalBearingX));
        var destinationY = SaturatingSubtract(
            SaturatingFloorToInt(y),
            SaturatingCeilingToNonNegativeInt(glyphMetrics.HorizontalBearingY));
        var updateX = Math.Max(destinationX, clipX0);
        var updateY = Math.Max(destinationY, clipY0);
        var updateRight = Math.Min(SaturatingAdd(destinationX, glyphWidth), clipX1);
        var updateBottom = Math.Min(SaturatingAdd(destinationY, glyphHeight), clipY1);
        var updateWidth = Math.Max(0, updateRight - updateX);
        var updateHeight = Math.Max(0, updateBottom - updateY);

        if (!IsWhiteSpace(code) && !WriteCoverageRectangle(
                ctx,
                surfBuffer,
                surfWidthByte,
                pixelSize,
                updateX,
                updateY,
                updateWidth,
                updateHeight))
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorNoSupportSurface);
        }

        var left = MathF.Floor(x + glyphMetrics.HorizontalBearingX);
        var top = MathF.Ceiling(y + glyphMetrics.HorizontalBearingY);
        var right = MathF.Ceiling(x + glyphMetrics.HorizontalBearingX + glyphMetrics.Width);
        var bottom = MathF.Floor(y + glyphMetrics.HorizontalBearingY - glyphMetrics.Height);
        var snappedAdvance = MathF.Ceiling(x + glyphMetrics.HorizontalAdvance) - x;
        var imageWidth = (uint)Math.Max(0f, right - left);
        var imageHeight = (uint)Math.Max(0f, top - bottom);

        // SurfaceImage.address remains the surface base for compatibility with
        // titles that derive their atlas source pointer from this field.
        var ok = ctx.TryWriteUInt64(result + 0x00, 0)
            && ctx.TryWriteUInt64(result + 0x08, surfBuffer)
            && ctx.TryWriteUInt32(result + 0x10, surfWidthByte)
            && ctx.TryWriteUInt32(result + 0x14, pixelSize)
            && ctx.TryWriteUInt32(result + 0x18, (uint)updateX)
            && ctx.TryWriteUInt32(result + 0x1C, (uint)updateY)
            && ctx.TryWriteUInt32(result + 0x20, (uint)updateWidth)
            && ctx.TryWriteUInt32(result + 0x24, (uint)updateHeight)
            && TryWriteFloat(ctx, result + 0x28, left - x)
            && TryWriteFloat(ctx, result + 0x2C, top - y)
            && TryWriteFloat(ctx, result + 0x30, snappedAdvance)
            && TryWriteFloat(ctx, result + 0x34, MathF.Max(snappedAdvance, right - x))
            && ctx.TryWriteUInt32(result + 0x38, imageWidth)
            && ctx.TryWriteUInt32(result + 0x3C, imageHeight);
        if (!ok)
        {
            ClearRenderOutputs(ctx, metrics, result);
            return Error(ctx, FontErrorInvalidParameter);
        }

        return Ok(ctx);
    }

    private static bool TryReadRenderSurface(
        CpuContext ctx,
        ulong surface,
        out ulong buffer,
        out uint widthByte,
        out uint pixelSize,
        out int width,
        out int height,
        out uint scissorX0,
        out uint scissorY0,
        out uint scissorX1,
        out uint scissorY1)
    {
        buffer = 0;
        widthByte = 0;
        width = 0;
        height = 0;
        scissorX0 = 0;
        scissorY0 = 0;
        scissorX1 = 0;
        scissorY1 = 0;
        uint pixelWord = 0;
        var ok = ctx.TryReadUInt64(surface + 0x00, out buffer);
        ok &= ctx.TryReadUInt32(surface + 0x08, out widthByte);
        ok &= ctx.TryReadUInt32(surface + 0x0C, out pixelWord);
        ok &= ctx.TryReadInt32(surface + 0x10, out width);
        ok &= ctx.TryReadInt32(surface + 0x14, out height);
        ok &= ctx.TryReadUInt32(surface + 0x18, out scissorX0);
        ok &= ctx.TryReadUInt32(surface + 0x1C, out scissorY0);
        ok &= ctx.TryReadUInt32(surface + 0x20, out scissorX1);
        ok &= ctx.TryReadUInt32(surface + 0x24, out scissorY1);
        pixelSize = pixelWord & 0xFF;
        return ok;
    }

    private static bool WriteCoverageRectangle(
        CpuContext ctx,
        ulong buffer,
        uint widthByte,
        uint pixelSize,
        int x,
        int y,
        int width,
        int height)
    {
        var pixel = pixelSize == 1 ? CoveragePixel1 : CoveragePixel4;
        for (var row = 0; row < height; row++)
        {
            var rowOffset = checked((ulong)(y + row) * widthByte);
            for (var column = 0; column < width; column++)
            {
                var columnOffset = checked((ulong)(x + column) * pixelSize);
                if (rowOffset > ulong.MaxValue - buffer ||
                    columnOffset > ulong.MaxValue - buffer - rowOffset ||
                    !ctx.Memory.TryWrite(buffer + rowOffset + columnOffset, pixel))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int SaturatingFloorToInt(float value) =>
        value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)MathF.Floor(value);

    private static int SaturatingCeilingToNonNegativeInt(float value) =>
        value <= 0f ? 0 : value >= int.MaxValue ? int.MaxValue : (int)MathF.Ceiling(value);

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    private static int SaturatingSubtract(int left, int right) =>
        left < int.MinValue + right ? int.MinValue : left - right;

    [SysAbiExport(Nid = "LHDoRWVFGqk", ExportName = "sceFontDeleteGlyph",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDeleteGlyph(CpuContext ctx)
    {
        // (const OrbisFontMem* memory, OrbisFontGlyph* glyph) -> RDI, RSI.
        // The real library validates *glyph, frees it, and nulls the slot.
        var glyphSlot = ctx[CpuRegister.Rsi];
        if (glyphSlot == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (!ctx.TryReadUInt64(glyphSlot, out var glyph) || glyph == 0)
        {
            return Error(ctx, FontErrorInvalidGlyph);
        }

        if (LiveGlyphs.TryRemove(glyph, out _))
        {
            FreeGlyphs.Enqueue(glyph);
        }

        _ = ctx.TryWriteUInt64(glyphSlot, 0);
        return Ok(ctx);
    }

    private static bool TryAcquireGlyphObject(CpuContext ctx, out ulong glyph)
    {
        if (FreeGlyphs.TryDequeue(out glyph))
        {
            // Recycled objects are re-zeroed so no stale state from the
            // previous glyph leaks into the guest's field reads.
            _ = ctx.Memory.TryWrite(glyph, GlyphZeroFill);
        }
        else if (!KernelMemoryCompatExports.TryAllocateHleData(
            ctx, ScratchObjectSize, ScratchObjectAlign, out glyph))
        {
            return false;
        }

        LiveGlyphs[glyph] = 1;
        return true;
    }

    private static void ClearRenderOutputs(CpuContext ctx, ulong metrics, ulong result)
    {
        ZeroGuestStruct(ctx, metrics, 0x20);
        ZeroGuestStruct(ctx, result, 0x40);
    }

    private static ushort ClampToU16(float value) =>
        float.IsFinite(value) && value > 0f
            ? (ushort)Math.Min(value, ushort.MaxValue)
            : (ushort)0;

    private static (float W, float H) GetScalePixel(ulong handle)
    {
        var scale = FontScales.TryGetValue(handle, out var stored) ? stored : DefaultFontScale;
        if (!scale.IsPoint)
        {
            return (scale.W, scale.H);
        }

        return (
            scale.W * (scale.DpiX == 0 ? 72u : scale.DpiX) / PointsPerInch,
            scale.H * (scale.DpiY == 0 ? 72u : scale.DpiY) / PointsPerInch);
    }

    private static (float W, float H) GetRenderScalePixel(CpuContext ctx, ulong handle, ulong surface)
    {
        var scale = GetScalePixel(handle);
        if (!ctx.TryReadByte(surface + 0x0E, out var styleFlag) || (styleFlag & 1) == 0 ||
            !ctx.TryReadUInt64(surface + 0x28, out var styleFrame) || styleFrame == 0 ||
            !ctx.TryReadUInt16(styleFrame + 0x00, out var magic) || magic != StyleFrameMagic ||
            !ctx.TryReadByte(styleFrame + 0x02, out var flags) || (flags & 1) == 0 ||
            !ctx.TryReadUInt32(styleFrame + 0x0C, out var unit) ||
            !ctx.TryReadUInt32(styleFrame + 0x14, out var widthBits) ||
            !ctx.TryReadUInt32(styleFrame + 0x18, out var heightBits))
        {
            return scale;
        }

        var width = BitConverter.UInt32BitsToSingle(widthBits);
        var height = BitConverter.UInt32BitsToSingle(heightBits);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
        {
            return scale;
        }

        if (unit == 0)
        {
            return (width, height);
        }

        _ = ctx.TryReadUInt32(styleFrame + 0x04, out var dpiX);
        _ = ctx.TryReadUInt32(styleFrame + 0x08, out var dpiY);
        return (
            width * (dpiX == 0 ? 72u : dpiX) / PointsPerInch,
            height * (dpiY == 0 ? 72u : dpiY) / PointsPerInch);
    }

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

    [SysAbiExport(Nid = "la2AOWnHEAc", ExportName = "sceFontStyleFrameInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameInit(CpuContext ctx)
    {
        var styleFrame = ctx[CpuRegister.Rdi];
        if (styleFrame == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        ZeroGuestStruct(ctx, styleFrame, 0x60);
        var ok = ctx.TryWriteUInt16(styleFrame + 0x00, StyleFrameMagic)
            && ctx.TryWriteUInt32(styleFrame + 0x04, 72)
            && ctx.TryWriteUInt32(styleFrame + 0x08, 72);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "da4rQ4-+p-4", ExportName = "sceFontStyleFrameSetScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameSetScalePixel(CpuContext ctx) =>
        SetStyleFrameScale(ctx, isPoint: false);

    [SysAbiExport(Nid = "O997laxY-Ys", ExportName = "sceFontStyleFrameSetScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameSetScalePoint(CpuContext ctx) =>
        SetStyleFrameScale(ctx, isPoint: true);

    private static int SetStyleFrameScale(CpuContext ctx, bool isPoint)
    {
        var styleFrame = ctx[CpuRegister.Rdi];
        var width = ReadXmmFloat(ctx, 0);
        var height = ReadXmmFloat(ctx, 1);
        if (styleFrame == 0 ||
            !ctx.TryReadUInt16(styleFrame + 0x00, out var magic) || magic != StyleFrameMagic ||
            !float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f ||
            !ctx.TryReadByte(styleFrame + 0x02, out var flags))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var ok = WriteByte(ctx, styleFrame + 0x02, (byte)(flags | 1))
            && ctx.TryWriteUInt32(styleFrame + 0x0C, isPoint ? 1u : 0u)
            && TryWriteFloat(ctx, styleFrame + 0x14, width)
            && TryWriteFloat(ctx, styleFrame + 0x18, height);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "0hr-w30SjiI", ExportName = "sceFontRenderSurfaceSetStyleFrame",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRenderSurfaceSetStyleFrame(CpuContext ctx)
    {
        var surface = ctx[CpuRegister.Rdi];
        var styleFrame = ctx[CpuRegister.Rsi];
        if (surface == 0 || !ctx.TryReadByte(surface + 0x0E, out var styleFlag))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (styleFrame == 0)
        {
            var cleared = WriteByte(ctx, surface + 0x0E, (byte)(styleFlag & ~1))
                && ctx.TryWriteUInt64(surface + 0x28, 0)
                && ctx.TryWriteUInt64(surface + 0x30, 0);
            return cleared ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
        }

        if (!ctx.TryReadUInt16(styleFrame + 0x00, out var magic) || magic != StyleFrameMagic)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var ok = WriteByte(ctx, surface + 0x0E, (byte)(styleFlag | 1))
            && ctx.TryWriteUInt64(surface + 0x28, styleFrame)
            && ctx.TryWriteUInt64(surface + 0x30, 0);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    private static bool WriteByte(CpuContext ctx, ulong address, byte value) =>
        ctx.Memory.TryWrite(address, [value]);

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
    public static int FontCloseFont(CpuContext ctx)
    {
        FontScales.TryRemove(ctx[CpuRegister.Rdi], out _);
        return Ok(ctx);
    }

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

    internal static void ResetScaleForTests() => FontScales.Clear();

    internal static void ResetGlyphStateForTests()
    {
        LiveGlyphs.Clear();
        while (FreeGlyphs.TryDequeue(out _))
        {
        }

        lock (BlankGlyphArenaGate)
        {
            _blankGlyphArena = 0;
        }
    }
}
