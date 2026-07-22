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
    private const uint FontErrorInvalidLibrary = 0x80460004;
    private const uint FontErrorInvalidRenderer = 0x80460007;
    private const uint FontErrorInvalidString = 0x80460009;
    private const uint FontErrorUnsetParameter = 0x80460058;

    private const float DefaultScalePixel = 16.0f;
    private const float PointsPerInch = 72.0f;
    private const ushort StyleFrameMagic = 0x0F09;
    private readonly record struct FontScale(float W, float H, bool IsPoint, uint DpiX, uint DpiY);
    private static readonly ConcurrentDictionary<ulong, FontScale> FontScales = new();
    private static readonly byte[] CoveragePixel1 = [0xFF];
    private static readonly byte[] CoveragePixel4 = [0xFF, 0xFF, 0xFF, 0xFF];
    private enum FontObjectKind : byte
    {
        Library,
        Renderer,
        String,
        GraphicsDevice,
        GraphicsService,
    }

    private readonly record struct FontStringState(uint WritingForm, uint TerminateCode, ulong TerminateOrder);
    private static readonly ConcurrentDictionary<ulong, FontObjectKind> FontObjects = new();
    private static readonly ConcurrentDictionary<ulong, ulong> FontParents = new();
    private static readonly ConcurrentDictionary<ulong, FontStringState> FontStrings = new();

    private static bool TryCreateFontObject(
        CpuContext ctx,
        ulong outPointer,
        FontObjectKind kind,
        out ulong handle)
    {
        if (!TryHandOutHandle(ctx, outPointer, out handle))
        {
            return false;
        }

        FontObjects[handle] = kind;
        return true;
    }

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

    [SysAbiExport(Nid = "nWrfPI4Okmg", ExportName = "sceFontCreateLibrary",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateLibrary(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdx];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (!TryCreateFontObject(ctx, output, FontObjectKind.Library, out var library))
        {
            return Error(ctx, FontErrorAllocationFailed);
        }

        _ = ctx.TryWriteUInt16(library, 0x0F01);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "u5fZd3KZcs0", ExportName = "sceFontCreateRenderer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateRenderer(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdx];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (!TryCreateFontObject(ctx, output, FontObjectKind.Renderer, out var renderer))
        {
            return Error(ctx, FontErrorAllocationFailed);
        }

        _ = ctx.TryWriteUInt16(renderer, 0x0F07);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "FXP359ygujs", ExportName = "sceFontDestroyLibrary",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyLibrary(CpuContext ctx) =>
        DestroyFontObject(ctx, FontObjectKind.Library, FontErrorInvalidLibrary);

    [SysAbiExport(Nid = "SSCaczu2aMQ", ExportName = "sceFontDestroyString",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyString(CpuContext ctx) =>
        DestroyFontObject(ctx, FontObjectKind.String, FontErrorInvalidString);

    private static int DestroyFontObject(CpuContext ctx, FontObjectKind expected, uint invalidError)
    {
        var slot = ctx[CpuRegister.Rdi];
        if (slot == 0 || !ctx.TryReadUInt64(slot, out var handle))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        _ = ctx.TryWriteUInt64(slot, 0);
        if (handle == 0)
        {
            return Error(ctx, invalidError);
        }

        if (FontObjects.TryGetValue(handle, out var kind) && kind != expected)
        {
            return Error(ctx, invalidError);
        }

        FontObjects.TryRemove(handle, out _);
        FontParents.TryRemove(handle, out _);
        FontStrings.TryRemove(handle, out _);
        FontScales.TryRemove(handle, out _);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "RvXyHMUiLhE", ExportName = "sceFontOpenFontFile",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontOpenFontFile(CpuContext ctx)
    {
        var library = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.R8];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (library == 0)
        {
            return Error(ctx, FontErrorInvalidLibrary);
        }

        if (!TryHandOutHandle(ctx, output, out var font))
        {
            return Error(ctx, FontErrorAllocationFailed);
        }

        FontParents[font] = library;
        _ = ctx.TryWriteUInt16(font, 0x0F02);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "MO24vDhmS4E", ExportName = "sceFontCreateString",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateString(CpuContext ctx)
    {
        var textSource = ctx[CpuRegister.Rsi];
        var output = ctx[CpuRegister.Rcx];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0) || textSource == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (!TryCreateFontObject(ctx, output, FontObjectKind.String, out var fontString))
        {
            return Error(ctx, FontErrorAllocationFailed);
        }

        var writingForm = 0x10u;
        if (ctx.TryReadUInt64(textSource, out var systemUse))
        {
            var candidate = (uint)(systemUse >> 32);
            if (candidate is >= 0x10 and <= 0x12)
            {
                writingForm = candidate;
            }
        }

        FontStrings[fontString] = new FontStringState(writingForm, 0, 0);
        _ = ctx.TryWriteUInt16(fontString, 0x0F05);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "h6hIgxXEiEc", ExportName = "sceFontMemoryTerm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontMemoryTerm(CpuContext ctx)
    {
        var memory = ctx[CpuRegister.Rdi];
        if (memory == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        ZeroGuestStruct(ctx, memory, 0x40);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "6DFUkCwQLa8", ExportName = "sceFontCharacterGetBidiLevel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterGetBidiLevel(CpuContext ctx)
    {
        var character = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (output != 0)
        {
            _ = ctx.TryWriteUInt32(output, 0);
        }

        if (character == 0 || output == 0 || !ctx.TryReadUInt64(character + 0x38, out var flags) ||
            !ctx.TryWriteUInt32(output, (uint)((flags >> 24) & 0xFF)))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "zN3+nuA0SFQ", ExportName = "sceFontCharacterGetTextFontCode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterGetTextFontCode(CpuContext ctx)
    {
        var character = ctx[CpuRegister.Rdi];
        var fontOutput = ctx[CpuRegister.Rsi];
        var codeOutput = ctx[CpuRegister.Rdx];
        if (fontOutput != 0)
        {
            _ = ctx.TryWriteUInt64(fontOutput, 0);
        }
        if (codeOutput != 0)
        {
            _ = ctx.TryWriteUInt32(codeOutput, 0);
        }

        if (character == 0 || fontOutput == 0 || codeOutput == 0 ||
            !ctx.TryReadUInt64(character + 0x18, out var font) ||
            !ctx.TryReadUInt32(character + 0x28, out var code) ||
            !ctx.TryWriteUInt64(fontOutput, font) || !ctx.TryWriteUInt32(codeOutput, code))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "mxgmMj-Mq-o", ExportName = "sceFontCharacterGetTextOrder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterGetTextOrder(CpuContext ctx)
    {
        var character = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        if (character == 0 || !ctx.TryReadUInt64(character + 0x10, out var order) ||
            !ctx.TryWriteUInt64(output, order))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "-P6X35Rq2-E", ExportName = "sceFontCharacterLooksFormatCharacters",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterLooksFormatCharacters(CpuContext ctx)
    {
        var character = ctx[CpuRegister.Rdi];
        uint result = 0;
        if (character != 0 && ctx.TryReadUInt64(character + 0x38, out var flags) &&
            (flags & (1UL << 42)) != 0)
        {
            _ = ctx.TryReadUInt32(character + 0x28, out result);
        }

        return ReturnValue(ctx, result);
    }

    [SysAbiExport(Nid = "SaRlqtqaCew", ExportName = "sceFontCharacterLooksWhiteSpace",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterLooksWhiteSpace(CpuContext ctx)
    {
        var character = ctx[CpuRegister.Rdi];
        uint result = 0;
        if (character != 0 && ctx.TryReadUInt64(character + 0x38, out var flags) &&
            ((flags >> 8) & 0xFF) == 0x0E)
        {
            _ = ctx.TryReadUInt32(character + 0x28, out result);
        }

        return ReturnValue(ctx, result);
    }

    [SysAbiExport(Nid = "6Gqlv5KdTbU", ExportName = "sceFontCharacterRefersTextBack",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterRefersTextBack(CpuContext ctx) => ReferTextCharacter(ctx, 0x00);

    [SysAbiExport(Nid = "BkjBP+YC19w", ExportName = "sceFontCharacterRefersTextNext",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterRefersTextNext(CpuContext ctx) => ReferTextCharacter(ctx, 0x08);

    private static int ReferTextCharacter(CpuContext ctx, ulong linkOffset)
    {
        var current = ctx[CpuRegister.Rdi];
        for (var count = 0; current != 0 && count < 4096; count++)
        {
            if (!ctx.TryReadUInt64(current + linkOffset, out current) || current == 0)
            {
                return ReturnValue(ctx, 0);
            }

            if (ctx.TryReadByte(current + 0x33, out var synthetic) && synthetic == 0 &&
                ctx.TryReadByte(current + 0x31, out var clusterIndex) && clusterIndex == 0)
            {
                return ReturnValue(ctx, current);
            }
        }

        return ReturnValue(ctx, 0);
    }

    [SysAbiExport(Nid = "ObkDGDBsVtw", ExportName = "sceFontStringGetTerminateCode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStringGetTerminateCode(CpuContext ctx) =>
        ReturnValue(ctx, FontStrings.TryGetValue(ctx[CpuRegister.Rdi], out var state) ? state.TerminateCode : 0);

    [SysAbiExport(Nid = "+B-xlbiWDJ4", ExportName = "sceFontStringGetTerminateOrder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStringGetTerminateOrder(CpuContext ctx) =>
        ReturnValue(ctx, FontStrings.TryGetValue(ctx[CpuRegister.Rdi], out var state) ? state.TerminateOrder : 0);

    [SysAbiExport(Nid = "o1vIEHeb6tw", ExportName = "sceFontStringGetWritingForm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStringGetWritingForm(CpuContext ctx) =>
        ReturnValue(ctx, FontStrings.TryGetValue(ctx[CpuRegister.Rdi], out var state) ? state.WritingForm : 0);

    [SysAbiExport(Nid = "Avv7OApgCJk", ExportName = "sceFontStringRefersTextCharacters",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStringRefersTextCharacters(CpuContext ctx) => EmptyCharacterRange(ctx, CpuRegister.Rsi);

    [SysAbiExport(Nid = "hq5LffQjz-s", ExportName = "sceFontStringRefersRenderCharacters",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStringRefersRenderCharacters(CpuContext ctx) => EmptyCharacterRange(ctx, CpuRegister.Rcx);

    private static int EmptyCharacterRange(CpuContext ctx, CpuRegister countRegister)
    {
        var count = ctx[countRegister];
        if (count != 0)
        {
            _ = ctx.TryWriteUInt32(count, 0);
        }
        return ReturnValue(ctx, 0);
    }

    [SysAbiExport(Nid = "VRFd3diReec", ExportName = "sceFontTextSourceRewind",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextSourceRewind(CpuContext ctx)
    {
        var source = ctx[CpuRegister.Rdi];
        if (source == 0 || !ctx.TryReadUInt64(source, out var systemUse) ||
            (ushort)systemUse != 0x0F04)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        for (var index = 0UL; index < 5; index++)
        {
            if (!ctx.TryReadUInt64(source + 0x38 + index * 8, out var value))
            {
                return Error(ctx, FontErrorInvalidParameter);
            }
            var destination = index switch
            {
                0 => source + 0x08,
                1 => source + 0x10,
                2 => source + 0x20,
                3 => source + 0x28,
                _ => source + 0x30,
            };
            _ = ctx.TryWriteUInt64(destination, value);
        }
        _ = ctx.TryReadUInt64(source + 0x38, out var start);
        _ = ctx.TryWriteUInt64(source + 0x18, start);
        return Ok(ctx);
    }

    private static int ReturnValue(CpuContext ctx, ulong value)
    {
        ctx[CpuRegister.Rax] = value;
        return 0;
    }

    [SysAbiExport(Nid = "CkVmLoCNN-8", ExportName = "sceFontGetScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetScalePixel(CpuContext ctx) => GetFontScale(ctx, point: false);

    [SysAbiExport(Nid = "GoF2bhB7LYk", ExportName = "sceFontGetScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetScalePoint(CpuContext ctx) => GetFontScale(ctx, point: true);

    [SysAbiExport(Nid = "EY38A01lq2k", ExportName = "sceFontGetRenderScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderScalePixel(CpuContext ctx) => GetFontScale(ctx, point: false);

    [SysAbiExport(Nid = "FEafYUcxEGo", ExportName = "sceFontGetRenderScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderScalePoint(CpuContext ctx) => GetFontScale(ctx, point: true);

    private static int GetFontScale(CpuContext ctx, bool point)
    {
        var handle = ctx[CpuRegister.Rdi];
        var widthOutput = ctx[CpuRegister.Rsi];
        var heightOutput = ctx[CpuRegister.Rdx];
        if (widthOutput != 0)
        {
            _ = TryWriteFloat(ctx, widthOutput, 0f);
        }
        if (heightOutput != 0)
        {
            _ = TryWriteFloat(ctx, heightOutput, 0f);
        }
        if (handle == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }
        if (widthOutput == 0 && heightOutput == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }

        var stored = FontScales.TryGetValue(handle, out var value) ? value : DefaultFontScale;
        var pixel = GetScalePixel(handle);
        var width = point ? pixel.W * PointsPerInch / (stored.DpiX == 0 ? 72u : stored.DpiX) : pixel.W;
        var height = point ? pixel.H * PointsPerInch / (stored.DpiY == 0 ? 72u : stored.DpiY) : pixel.H;
        var ok = (widthOutput == 0 || TryWriteFloat(ctx, widthOutput, width)) &&
            (heightOutput == 0 || TryWriteFloat(ctx, heightOutput, height));
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "ynSqYL8VpoA", ExportName = "sceFontGetEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetEffectSlant(CpuContext ctx) => GetEffectSlant(ctx);

    [SysAbiExport(Nid = "Gqa5Pp7y4MU", ExportName = "sceFontGetRenderEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderEffectSlant(CpuContext ctx) => GetEffectSlant(ctx);

    private static int GetEffectSlant(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        if (output != 0)
        {
            _ = TryWriteFloat(ctx, output, 0f);
        }
        return ctx[CpuRegister.Rdi] == 0
            ? Error(ctx, FontErrorInvalidFontHandle)
            : output == 0 ? Error(ctx, FontErrorInvalidParameter) : Ok(ctx);
    }

    [SysAbiExport(Nid = "d7dDgRY+Bzw", ExportName = "sceFontGetEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetEffectWeight(CpuContext ctx) => GetEffectWeight(ctx);

    [SysAbiExport(Nid = "woOjHrkjIYg", ExportName = "sceFontGetRenderEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderEffectWeight(CpuContext ctx) => GetEffectWeight(ctx);

    private static int GetEffectWeight(CpuContext ctx)
    {
        var x = ctx[CpuRegister.Rsi];
        var y = ctx[CpuRegister.Rdx];
        var mode = ctx[CpuRegister.Rcx];
        var ok = (x == 0 || TryWriteFloat(ctx, x, 1f)) &&
            (y == 0 || TryWriteFloat(ctx, y, 1f)) &&
            (mode == 0 || ctx.TryWriteUInt32(mode, 0));
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }
        if (x == 0 && y == 0 && mode == 0 || !ok)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "sDuhHGNhHvE", ExportName = "sceFontGetKerning",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetKerning(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rcx];
        ZeroGuestStruct(ctx, output, 0x10);
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }
        return output == 0 ? Error(ctx, FontErrorInvalidParameter) : Ok(ctx);
    }

    [SysAbiExport(Nid = "LzmHDnlcwfQ", ExportName = "sceFontGetLibrary",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetLibrary(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (output == 0 || !ctx.TryWriteUInt64(output, 0))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if (handle == 0)
        {
            return Error(ctx, FontErrorInvalidFontHandle);
        }
        if (FontParents.TryGetValue(handle, out var library))
        {
            _ = ctx.TryWriteUInt64(output, library);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "PXlA0M8ax40", ExportName = "sceFontGlyphGetGlyphForm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphGetGlyphForm(CpuContext ctx) => GetGlyphHeaderByte(ctx, 0x04);

    [SysAbiExport(Nid = "XUfSWpLhrUw", ExportName = "sceFontGlyphGetMetricsForm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphGetMetricsForm(CpuContext ctx) => GetGlyphHeaderByte(ctx, 0x05);

    private static int GetGlyphHeaderByte(CpuContext ctx, ulong offset)
    {
        var glyph = ctx[CpuRegister.Rdi];
        if (glyph == 0 || !ctx.TryReadUInt16(glyph, out var magic) || magic != 0x0F03 ||
            !ctx.TryReadByte(glyph + offset, out var value))
        {
            return Error(ctx, FontErrorInvalidGlyph);
        }
        return ReturnValue(ctx, value);
    }

    [SysAbiExport(Nid = "lNnUqa1zA-M", ExportName = "sceFontGlyphGetScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphGetScalePixel(CpuContext ctx)
    {
        var glyph = ctx[CpuRegister.Rdi];
        var width = ctx[CpuRegister.Rsi];
        var height = ctx[CpuRegister.Rdx];
        if (width != 0)
        {
            _ = TryWriteFloat(ctx, width, 0f);
        }
        if (height != 0)
        {
            _ = TryWriteFloat(ctx, height, 0f);
        }
        if (glyph == 0 || !ctx.TryReadUInt16(glyph, out var magic) || magic != 0x0F03 ||
            width == 0 && height == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if (!ctx.TryReadUInt32(glyph + 0x10, out var widthBits) ||
            !ctx.TryReadUInt32(glyph + 0x14, out var heightBits))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var ok = (width == 0 || ctx.TryWriteUInt32(width, widthBits)) &&
            (height == 0 || ctx.TryWriteUInt32(height, heightBits));
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "ntrc3bEWlvQ", ExportName = "sceFontGlyphRefersMetrics",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRefersMetrics(CpuContext ctx) => ReferGlyphMetrics(ctx, 0);

    [SysAbiExport(Nid = "9kTbF59TjLs", ExportName = "sceFontGlyphRefersMetricsHorizontal",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRefersMetricsHorizontal(CpuContext ctx) => ReferGlyphMetrics(ctx, 1);

    [SysAbiExport(Nid = "nJavPEdMDvM", ExportName = "sceFontGlyphRefersMetricsHorizontalAdvance",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRefersMetricsHorizontalAdvance(CpuContext ctx) => ReferGlyphMetrics(ctx, 2);

    [SysAbiExport(Nid = "JCnVgZgcucs", ExportName = "sceFontGlyphRefersMetricsHorizontalX",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRefersMetricsHorizontalX(CpuContext ctx) => ReferGlyphMetrics(ctx, 3);

    private static int ReferGlyphMetrics(CpuContext ctx, int form)
    {
        var glyph = ctx[CpuRegister.Rdi];
        if (glyph == 0 || !ctx.TryReadUInt16(glyph, out var magic) || magic != 0x0F03 ||
            !ctx.TryReadUInt32(glyph + 0x10, out var widthBits) ||
            !ctx.TryReadUInt32(glyph + 0x14, out var heightBits))
        {
            return ReturnValue(ctx, 0);
        }

        var scaleW = BitConverter.UInt32BitsToSingle(widthBits);
        var scaleH = BitConverter.UInt32BitsToSingle(heightBits);
        var metrics = BuildGlyphMetrics('M', scaleW, scaleH);
        var address = glyph + 0x100;
        if (form == 0)
        {
            _ = WriteGlyphMetrics(ctx, address, metrics);
        }
        else if (form == 1)
        {
            _ = TryWriteFloat(ctx, address + 0x00, metrics.Width) &&
                TryWriteFloat(ctx, address + 0x04, metrics.Height) &&
                TryWriteFloat(ctx, address + 0x08, metrics.HorizontalBearingX) &&
                TryWriteFloat(ctx, address + 0x0C, metrics.HorizontalBearingY) &&
                TryWriteFloat(ctx, address + 0x10, metrics.HorizontalAdvance);
        }
        else if (form == 2)
        {
            _ = TryWriteFloat(ctx, address, metrics.HorizontalAdvance);
        }
        else
        {
            _ = TryWriteFloat(ctx, address + 0x00, metrics.Width) &&
                TryWriteFloat(ctx, address + 0x04, metrics.HorizontalBearingX) &&
                TryWriteFloat(ctx, address + 0x08, metrics.HorizontalAdvance);
        }
        return ReturnValue(ctx, address);
    }

    [SysAbiExport(Nid = "R1T4i+DOhNY", ExportName = "sceFontGlyphRefersOutline",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRefersOutline(CpuContext ctx) => ReturnValue(ctx, 0);

    [SysAbiExport(Nid = "amcmrY62BD4", ExportName = "sceFontRendererGetOutlineBufferSize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRendererGetOutlineBufferSize(CpuContext ctx)
    {
        var renderer = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (output != 0)
        {
            _ = ctx.TryWriteUInt32(output, 0);
        }
        if (output == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        return renderer == 0 ? Error(ctx, FontErrorInvalidRenderer) : Ok(ctx);
    }

    [SysAbiExport(Nid = "ai6AfGrBs4o", ExportName = "sceFontRendererResetOutlineBuffer",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRendererResetOutlineBuffer(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0 ? Error(ctx, FontErrorInvalidRenderer) : Ok(ctx);

    [SysAbiExport(Nid = "ydF+WuH0fAk", ExportName = "sceFontRendererSetOutlineBufferPolicy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRendererSetOutlineBufferPolicy(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return Error(ctx, FontErrorInvalidRenderer);
        }
        var basal = (uint)ctx[CpuRegister.Rdx];
        var limit = (uint)ctx[CpuRegister.Rcx];
        return limit != 0 && basal > limit ? Error(ctx, FontErrorInvalidParameter) : Ok(ctx);
    }

    [SysAbiExport(Nid = "lOfduYnjgbo", ExportName = "sceFontStyleFrameGetEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameGetEffectSlant(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (output != 0)
        {
            _ = TryWriteFloat(ctx, output, 0f);
        }
        if (!TryGetStyleFlags(ctx, frame, out var flags))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if ((flags & 2) == 0)
        {
            return Error(ctx, FontErrorUnsetParameter);
        }
        if (output == 0 || !ctx.TryReadUInt32(frame + 0x24, out var bits) ||
            !ctx.TryWriteUInt32(output, bits))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "HIUdjR-+Wl8", ExportName = "sceFontStyleFrameGetEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameGetEffectWeight(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        var x = ctx[CpuRegister.Rsi];
        var y = ctx[CpuRegister.Rdx];
        var mode = ctx[CpuRegister.Rcx];
        if (x != 0)
        {
            _ = TryWriteFloat(ctx, x, 1f);
        }
        if (y != 0)
        {
            _ = TryWriteFloat(ctx, y, 1f);
        }
        if (mode != 0)
        {
            _ = ctx.TryWriteUInt32(mode, 0);
        }
        if (!TryGetStyleFlags(ctx, frame, out var flags))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if ((flags & 4) == 0)
        {
            return Error(ctx, FontErrorUnsetParameter);
        }
        if (x == 0 && y == 0 && mode == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if (x != 0 && ctx.TryReadUInt32(frame + 0x1C, out var xBits))
        {
            _ = TryWriteFloat(ctx, x, BitConverter.UInt32BitsToSingle(xBits) + 1f);
        }
        if (y != 0 && ctx.TryReadUInt32(frame + 0x20, out var yBits))
        {
            _ = TryWriteFloat(ctx, y, BitConverter.UInt32BitsToSingle(yBits) + 1f);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "VSw18Aqzl0U", ExportName = "sceFontStyleFrameGetResolutionDpi",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameGetResolutionDpi(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        var x = ctx[CpuRegister.Rsi];
        var y = ctx[CpuRegister.Rdx];
        if (!TryGetStyleFlags(ctx, frame, out _) || x == 0 && y == 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var ok = (x == 0 || ctx.TryReadUInt32(frame + 0x04, out var dpiX) && ctx.TryWriteUInt32(x, dpiX)) &&
            (y == 0 || ctx.TryReadUInt32(frame + 0x08, out var dpiY) && ctx.TryWriteUInt32(y, dpiY));
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "2QfqfeLblbg", ExportName = "sceFontStyleFrameGetScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameGetScalePixel(CpuContext ctx) => GetStyleFrameScale(ctx, point: false);

    [SysAbiExport(Nid = "7x2xKiiB7MA", ExportName = "sceFontStyleFrameGetScalePoint",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameGetScalePoint(CpuContext ctx) => GetStyleFrameScale(ctx, point: true);

    private static int GetStyleFrameScale(CpuContext ctx, bool point)
    {
        var frame = ctx[CpuRegister.Rdi];
        var x = ctx[CpuRegister.Rsi];
        var y = ctx[CpuRegister.Rdx];
        if (x != 0)
        {
            _ = TryWriteFloat(ctx, x, 0f);
        }
        if (y != 0)
        {
            _ = TryWriteFloat(ctx, y, 0f);
        }
        if (!TryGetStyleFlags(ctx, frame, out var flags))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        if ((flags & 1) == 0)
        {
            return Error(ctx, FontErrorUnsetParameter);
        }
        if (x == 0 && y == 0 || !ctx.TryReadUInt32(frame + 0x0C, out var unit) ||
            !ctx.TryReadUInt32(frame + 0x14, out var xBits) ||
            !ctx.TryReadUInt32(frame + 0x18, out var yBits) ||
            !ctx.TryReadUInt32(frame + 0x04, out var dpiX) ||
            !ctx.TryReadUInt32(frame + 0x08, out var dpiY))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var width = BitConverter.UInt32BitsToSingle(xBits);
        var height = BitConverter.UInt32BitsToSingle(yBits);
        if (point && unit == 0)
        {
            width *= PointsPerInch / (dpiX == 0 ? 72u : dpiX);
            height *= PointsPerInch / (dpiY == 0 ? 72u : dpiY);
        }
        else if (!point && unit != 0)
        {
            width *= (dpiX == 0 ? 72u : dpiX) / PointsPerInch;
            height *= (dpiY == 0 ? 72u : dpiY) / PointsPerInch;
        }
        var ok = (x == 0 || TryWriteFloat(ctx, x, width)) && (y == 0 || TryWriteFloat(ctx, y, height));
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "394sckksiCU", ExportName = "sceFontStyleFrameSetEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameSetEffectSlant(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        if (!TryGetStyleFlags(ctx, frame, out var flags))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var slant = Math.Clamp(ReadXmmFloat(ctx, 0), -1f, 1f);
        var ok = float.IsFinite(slant) && WriteByte(ctx, frame + 0x02, (byte)(flags | 2)) &&
            TryWriteFloat(ctx, frame + 0x24, slant);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "faw77-pEBmU", ExportName = "sceFontStyleFrameSetEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameSetEffectWeight(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        if (!TryGetStyleFlags(ctx, frame, out var flags) || (uint)ctx[CpuRegister.Rsi] != 0)
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var x = Math.Clamp(ReadXmmFloat(ctx, 0) - 1f, -0.04f, 0.04f);
        var y = Math.Clamp(ReadXmmFloat(ctx, 1) - 1f, -0.04f, 0.04f);
        var ok = float.IsFinite(x) && float.IsFinite(y) &&
            WriteByte(ctx, frame + 0x02, (byte)(flags | 4)) &&
            TryWriteFloat(ctx, frame + 0x1C, x) && TryWriteFloat(ctx, frame + 0x20, y);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "dB4-3Wdwls8", ExportName = "sceFontStyleFrameSetResolutionDpi",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameSetResolutionDpi(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        if (!TryGetStyleFlags(ctx, frame, out _))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        var x = (uint)ctx[CpuRegister.Rsi];
        var y = (uint)ctx[CpuRegister.Rdx];
        var ok = ctx.TryWriteUInt32(frame + 0x04, x == 0 ? 72u : x) &&
            ctx.TryWriteUInt32(frame + 0x08, y == 0 ? 72u : y);
        return ok ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);
    }

    [SysAbiExport(Nid = "dUmABkAnVgk", ExportName = "sceFontStyleFrameUnsetEffectSlant",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameUnsetEffectSlant(CpuContext ctx) => UnsetStyleFlag(ctx, 2);

    [SysAbiExport(Nid = "hwsuXgmKdaw", ExportName = "sceFontStyleFrameUnsetEffectWeight",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameUnsetEffectWeight(CpuContext ctx) => UnsetStyleFlag(ctx, 4);

    [SysAbiExport(Nid = "bePC0L0vQWY", ExportName = "sceFontStyleFrameUnsetScale",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontStyleFrameUnsetScale(CpuContext ctx) => UnsetStyleFlag(ctx, 1);

    private static int UnsetStyleFlag(CpuContext ctx, byte flag)
    {
        var frame = ctx[CpuRegister.Rdi];
        if (!TryGetStyleFlags(ctx, frame, out var flags) ||
            !WriteByte(ctx, frame + 0x02, (byte)(flags & ~flag)))
        {
            return Error(ctx, FontErrorInvalidParameter);
        }
        return Ok(ctx);
    }

    private static bool TryGetStyleFlags(CpuContext ctx, ulong frame, out byte flags)
    {
        flags = 0;
        return frame != 0 && ctx.TryReadUInt16(frame, out var magic) && magic == StyleFrameMagic &&
            ctx.TryReadByte(frame + 0x02, out flags);
    }

    [SysAbiExport(Nid = "APTXePHIjLM", ExportName = "Func_00F4D778F1C88CB3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc00F4D778F1C88CB3(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "A8ZQAl+7Dec", ExportName = "Func_03C650025FBB0DE7", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc03C650025FBB0DE7(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "B+q4oWOyfho", ExportName = "Func_07EAB8A163B27E1A", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc07EAB8A163B27E1A(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "CUCOiOT5fOM", ExportName = "Func_09408E88E4F97CE3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc09408E88E4F97CE3(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "CfkpBe2CqBQ", ExportName = "Func_09F92905ED82A814", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc09F92905ED82A814(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "DRQs7hqyGr4", ExportName = "Func_0D142CEE1AB21ABE", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc0D142CEE1AB21ABE(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "FL0unhGcFvI", ExportName = "Func_14BD2E9E119C16F2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc14BD2E9E119C16F2(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "GsU8nt6ujXU", ExportName = "Func_1AC53C9EDEAE8D75", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc1AC53C9EDEAE8D75(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "HUARhdXiTD0", ExportName = "Func_1D401185D5E24C3D", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc1D401185D5E24C3D(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "HoPNIMLMmW8", ExportName = "Func_1E83CD20C2CC996F", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc1E83CD20C2CC996F(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MUsfdluf54o", ExportName = "Func_314B1F765B9FE78A", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc314B1F765B9FE78A(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "NQ5nJf7eKeE", ExportName = "Func_350E6725FEDE29E1", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc350E6725FEDE29E1(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Pbdz8KYEvzk", ExportName = "Func_3DB773F0A604BF39", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc3DB773F0A604BF39(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "T-Sd0h4xGxw", ExportName = "Func_4FF49DD21E311B1C", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc4FF49DD21E311B1C(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "UmKHZkpJOYE", ExportName = "Func_526287664A493981", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc526287664A493981(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "VcpxjbyEpuk", ExportName = "Func_55CA718DBC84A6E9", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc55CA718DBC84A6E9(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Vj-F8HBqi00", ExportName = "Func_563FC5F0706A8B4D", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc563FC5F0706A8B4D(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Vp4uzTQpD0U", ExportName = "Func_569E2ECD34290F45", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc569E2ECD34290F45(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "WgR3W2vkdoU", ExportName = "Func_5A04775B6BE47685", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc5A04775B6BE47685(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "X9k7yrb3l1A", ExportName = "Func_5FD93BCAB6F79750", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc5FD93BCAB6F79750(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "YrU5j4ZL07Q", ExportName = "Func_62B5398F864BD3B4", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc62B5398F864BD3B4(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "b5AQKU2CI2c", ExportName = "Func_6F9010294D822367", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc6F9010294D822367(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "d1fpR0I6emc", ExportName = "Func_7757E947423A7A67", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc7757E947423A7A67(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "fga6Ugd-VPo", ExportName = "Func_7E06BA52077F54FA", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc7E06BA52077F54FA(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "k7Nt6gITEdY", ExportName = "Func_93B36DEA021311D6", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc93B36DEA021311D6(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "lLCJHnERWYo", ExportName = "Func_94B0891E7111598A", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc94B0891E7111598A(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "l4XJEowv580", ExportName = "Func_9785C9128C2FE7CD", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc9785C9128C2FE7CD(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "l9+8m2X7wOE", ExportName = "Func_97DFBC9B65FBC0E1", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFunc97DFBC9B65FBC0E1(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "rNlxdAXX08o", ExportName = "Func_ACD9717405D7D3CA", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncACD9717405D7D3CA(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "sZqK7D-U8W8", ExportName = "Func_B19A8AEC3FD4F16F", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncB19A8AEC3FD4F16F(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "wQ9IitfPED0", ExportName = "Func_C10F488AD7CF103D", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncC10F488AD7CF103D(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "0Mi1-0poJsc", ExportName = "Func_D0C8B5FF4A6826C7", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncD0C8B5FF4A6826C7(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "5I080Bw0KjM", ExportName = "Func_E48D3CD01C342A33", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncE48D3CD01C342A33(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "6slrIYa3HhQ", ExportName = "Func_EAC96B2186B71E14", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncEAC96B2186B71E14(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "-keIqW70YlY", ExportName = "Func_FE4788A96EF46256", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncFE4788A96EF46256(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "-n5a6V0wWPU", ExportName = "Func_FE7E5AE95D3058F5", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontFuncFE7E5AE95D3058F5(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "coCrV6IWplE", ExportName = "sceFontCharacterGetSyllableStringState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCharacterGetSyllableStringState(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "I9R5VC6eZWo", ExportName = "sceFontClearDeviceCache", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontClearDeviceCache(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MpKSBaYKluo", ExportName = "sceFontControl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontControl(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "WBNBaj9XiJU", ExportName = "sceFontCreateGraphicsDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateGraphicsDevice(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "4So0MC3oBIM", ExportName = "sceFontCreateGraphicsService", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateGraphicsService(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "NlO5Qlhjkng", ExportName = "sceFontCreateGraphicsServiceWithEdition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateGraphicsServiceWithEdition(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "cYrMGk1wrMA", ExportName = "sceFontCreateWords", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateWords(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "7rogx92EEyc", ExportName = "sceFontCreateWritingLine", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontCreateWritingLine(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "8h-SOB-asgk", ExportName = "sceFontDefineAttribute", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDefineAttribute(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "5QG71IjgOpQ", ExportName = "sceFontDestroyGraphicsDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyGraphicsDevice(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "zZQD3EwJo3c", ExportName = "sceFontDestroyGraphicsService", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyGraphicsService(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "hWE4AwNixqY", ExportName = "sceFontDestroyWords", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyWords(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "PEjv7CVDRYs", ExportName = "sceFontDestroyWritingLine", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDestroyWritingLine(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "UuY-OJF+f0k", ExportName = "sceFontDettachDeviceCacheBuffer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontDettachDeviceCacheBuffer(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "5kx49CAlO-M", ExportName = "sceFontGetAttribute", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetAttribute(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ZB8xRemRRG8", ExportName = "sceFontGetFontGlyphsCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetFontGlyphsCount(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "4X14YSK4Ldk", ExportName = "sceFontGetFontGlyphsOutlineProfile", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetFontGlyphsOutlineProfile(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "eb9S3zNlV5o", ExportName = "sceFontGetFontMetrics", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetFontMetrics(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "tiIlroGki+g", ExportName = "sceFontGetFontResolution", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetFontResolution(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "3hVv3SNoL6E", ExportName = "sceFontGetFontStyleInformation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetFontStyleInformation(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "gVQpMBuB7fE", ExportName = "sceFontGetGlyphExpandBufferState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetGlyphExpandBufferState(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "BozJej5T6fs", ExportName = "sceFontGetPixelResolution", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetPixelResolution(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ryPlnDDI3rU", ExportName = "sceFontGetRenderScaledKerning", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetRenderScaledKerning(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "8REoLjNGCpM", ExportName = "sceFontGetResolutionDpi", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetResolutionDpi(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "IrXeG0Lc6nA", ExportName = "sceFontGetScriptLanguage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetScriptLanguage(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "7-miUT6pNQw", ExportName = "sceFontGetTypographicDesign", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGetTypographicDesign(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "oO33Uex4Ui0", ExportName = "sceFontGlyphGetAttribute", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphGetAttribute(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "RmkXfBcZnrM", ExportName = "sceFontGlyphRenderImage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRenderImage(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "r4KEihtwxGs", ExportName = "sceFontGlyphRenderImageHorizontal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRenderImageHorizontal(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "n22d-HIdmMg", ExportName = "sceFontGlyphRenderImageVertical", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGlyphRenderImageVertical(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "RL2cAQgyXR8", ExportName = "sceFontGraphicsBeginFrame", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsBeginFrame(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "dUmIK6QjT7E", ExportName = "sceFontGraphicsDrawingCancel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsDrawingCancel(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "X2Vl3yU19Zw", ExportName = "sceFontGraphicsDrawingFinish", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsDrawingFinish(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "DOmdOwV3Aqw", ExportName = "sceFontGraphicsEndFrame", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsEndFrame(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "zdYdKRQC3rw", ExportName = "sceFontGraphicsExchangeResource", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsExchangeResource(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "UkMUIoj-e9s", ExportName = "sceFontGraphicsFillMethodInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillMethodInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "DJURdcnVUqo", ExportName = "sceFontGraphicsFillPlotInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillPlotInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "eQac6ftmBQQ", ExportName = "sceFontGraphicsFillPlotSetLayout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillPlotSetLayout(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "PEYQJa+MWnk", ExportName = "sceFontGraphicsFillPlotSetMapping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillPlotSetMapping(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "21g4m4kYF6g", ExportName = "sceFontGraphicsFillRatesInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillRatesInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "pJzji5FvdxU", ExportName = "sceFontGraphicsFillRatesSetFillEffect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillRatesSetFillEffect(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "scaro-xEuUM", ExportName = "sceFontGraphicsFillRatesSetLayout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillRatesSetLayout(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "W66Kqtt0xU0", ExportName = "sceFontGraphicsFillRatesSetMapping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsFillRatesSetMapping(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "FzpLsBQEegQ", ExportName = "sceFontGraphicsGetDeviceUsage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsGetDeviceUsage(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "W80hs0g5d+E", ExportName = "sceFontGraphicsRegionInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsRegionInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "S48+njg9p-o", ExportName = "sceFontGraphicsRegionInitCircular", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsRegionInitCircular(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "wcOQ8Fz73+M", ExportName = "sceFontGraphicsRegionInitRoundish", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsRegionInitRoundish(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "YBaw2Yyfd5E", ExportName = "sceFontGraphicsRelease", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsRelease(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "qkySrQ4FGe0", ExportName = "sceFontGraphicsRenderResource", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsRenderResource(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "qzNjJYKVli0", ExportName = "sceFontGraphicsSetFramePolicy", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetFramePolicy(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "9iRbHCtcx-o", ExportName = "sceFontGraphicsSetupClipping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupClipping(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "KZ3qPyz5Opc", ExportName = "sceFontGraphicsSetupColorRates", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupColorRates(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "LqclbpVzRvM", ExportName = "sceFontGraphicsSetupFillMethod", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupFillMethod(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Wl4FiI4qKY0", ExportName = "sceFontGraphicsSetupFillRates", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupFillRates(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "WC7s95TccVo", ExportName = "sceFontGraphicsSetupGlyphFill", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupGlyphFill(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "zC6I4ty37NA", ExportName = "sceFontGraphicsSetupGlyphFillPlot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupGlyphFillPlot(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "drZUF0XKTEI", ExportName = "sceFontGraphicsSetupHandleDefault", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupHandleDefault(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MEAmHMynQXE", ExportName = "sceFontGraphicsSetupLocation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupLocation(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "XRUOmQhnYO4", ExportName = "sceFontGraphicsSetupPositioning", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupPositioning(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "98XGr2Bkklg", ExportName = "sceFontGraphicsSetupRotation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupRotation(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Nj-ZUVOVAvc", ExportName = "sceFontGraphicsSetupScaling", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupScaling(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "p0avT2ggev0", ExportName = "sceFontGraphicsSetupShapeFill", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupShapeFill(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "0C5aKg9KghY", ExportName = "sceFontGraphicsSetupShapeFillPlot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsSetupShapeFillPlot(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "4pA3qqAcYco", ExportName = "sceFontGraphicsStructureCanvas", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsStructureCanvas(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "cpjgdlMYdOM", ExportName = "sceFontGraphicsStructureCanvasSequence", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsStructureCanvasSequence(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "774Mee21wKk", ExportName = "sceFontGraphicsStructureDesign", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsStructureDesign(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Hp3NIFhUXvQ", ExportName = "sceFontGraphicsStructureDesignResource", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsStructureDesignResource(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "bhmZlml6NBs", ExportName = "sceFontGraphicsStructureSurfaceTexture", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsStructureSurfaceTexture(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "5sAWgysOBfE", ExportName = "sceFontGraphicsUpdateClipping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateClipping(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "W4e8obm+w6o", ExportName = "sceFontGraphicsUpdateColorRates", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateColorRates(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "EgIn3QBajPs", ExportName = "sceFontGraphicsUpdateFillMethod", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateFillMethod(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MnUYAs2jVuU", ExportName = "sceFontGraphicsUpdateFillRates", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateFillRates(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "R-oVDMusYbc", ExportName = "sceFontGraphicsUpdateGlyphFill", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateGlyphFill(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "b9R+HQuHSMI", ExportName = "sceFontGraphicsUpdateGlyphFillPlot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateGlyphFillPlot(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "IN4P5pJADQY", ExportName = "sceFontGraphicsUpdateLocation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateLocation(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "U+LLXdr2DxM", ExportName = "sceFontGraphicsUpdatePositioning", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdatePositioning(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "yStTYSeb4NM", ExportName = "sceFontGraphicsUpdateRotation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateRotation(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "eDxmMoxE5xU", ExportName = "sceFontGraphicsUpdateScaling", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateScaling(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Ax6LQJJq6HQ", ExportName = "sceFontGraphicsUpdateShapeFill", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateShapeFill(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "I5Rf2rXvBKQ", ExportName = "sceFontGraphicsUpdateShapeFillPlot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontGraphicsUpdateShapeFillPlot(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "Z2cdsqJH+5k", ExportName = "sceFontRebindRenderer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontRebindRenderer(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0 ? Error(ctx, FontErrorInvalidFontHandle) : Ok(ctx);
    [SysAbiExport(Nid = "kihFGYJee7o", ExportName = "sceFontSetFontsOpenMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetFontsOpenMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "PxSR9UfJ+SQ", ExportName = "sceFontSetScriptLanguage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetScriptLanguage(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "SnsZua35ngs", ExportName = "sceFontSetTypographicDesign", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetTypographicDesign(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "71w5DzObuZI", ExportName = "sceFontSupportGlyphs", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSupportGlyphs(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "IPoYwwlMx-g", ExportName = "sceFontTextCodesStepBack", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextCodesStepBack(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "olSmXY+XP1E", ExportName = "sceFontTextCodesStepNext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontTextCodesStepNext(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "H-FNq8isKE0", ExportName = "sceFontWordsFindWordCharacters", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWordsFindWordCharacters(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "fljdejMcG1c", ExportName = "sceFontWritingGetRenderMetrics", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingGetRenderMetrics(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        ZeroGuestStruct(ctx, output, 0x18);
        return output == 0 ? Error(ctx, FontErrorInvalidParameter) : Ok(ctx);
    }
    [SysAbiExport(Nid = "1+DgKL0haWQ", ExportName = "sceFontWritingLineClear", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingLineClear(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "JQKWIsS9joE", ExportName = "sceFontWritingLineGetOrderingSpace", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingLineGetOrderingSpace(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "nlU2VnfpqTM", ExportName = "sceFontWritingLineGetRenderMetrics", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingLineGetRenderMetrics(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "+FYcYefsVX0", ExportName = "sceFontWritingLineRefersRenderStep", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingLineRefersRenderStep(CpuContext ctx) => ReturnValue(ctx, 0);
    [SysAbiExport(Nid = "wyKFUOWdu3Q", ExportName = "sceFontWritingLineWritesOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingLineWritesOrder(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "W-2WOXEHGck", ExportName = "sceFontWritingRefersRenderStep", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingRefersRenderStep(CpuContext ctx) => ReturnValue(ctx, 0);
    [SysAbiExport(Nid = "f4Onl7efPEY", ExportName = "sceFontWritingRefersRenderStepCharacter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingRefersRenderStepCharacter(CpuContext ctx)
    {
        ZeroGuestStruct(ctx, ctx[CpuRegister.Rsi], 0x40);
        return ReturnValue(ctx, 0);
    }
    [SysAbiExport(Nid = "BbCZjJizU4A", ExportName = "sceFontWritingSetMaskInvisible", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontWritingSetMaskInvisible(CpuContext ctx) =>
        (uint)ctx[CpuRegister.Rsi] <= 1 ? Ok(ctx) : Error(ctx, FontErrorInvalidParameter);

    [SysAbiExport(Nid = "e60aorDdpB8", ExportName = "sceFontFtInitAliases", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtInitAliases(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "BxcmiMc3UaA", ExportName = "sceFontFtSetAliasFont", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSetAliasFont(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MEWjebIzDEI", ExportName = "sceFontFtSetAliasPath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSetAliasPath(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ZcQL0iSjvFw", ExportName = "sceFontFtSupportBdf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportBdf(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "LADHEyFTxRQ", ExportName = "sceFontFtSupportCid", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportCid(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "+jqQjsancTs", ExportName = "sceFontFtSupportFontFormats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportFontFormats(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "oakL15-mBtc", ExportName = "sceFontFtSupportOpenType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportOpenType(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "dcQeaDr8UJc", ExportName = "sceFontFtSupportOpenTypeOtf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportOpenTypeOtf(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "2KXS-HkZT3c", ExportName = "sceFontFtSupportOpenTypeTtf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportOpenTypeTtf(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "H0mJnhKwV-s", ExportName = "sceFontFtSupportPcf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportPcf(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "S2mw3sYplAI", ExportName = "sceFontFtSupportPfr", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportPfr(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "+ehNXJPUyhk", ExportName = "sceFontFtSupportSystemFonts", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportSystemFonts(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "4BAhDLdrzUI", ExportName = "sceFontFtSupportTrueType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportTrueType(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Utlzbdf+g9o", ExportName = "sceFontFtSupportTrueTypeGx", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportTrueTypeGx(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "nAfQ6qaL1fU", ExportName = "sceFontFtSupportType1", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportType1(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "X9+pzrGtBus", ExportName = "sceFontFtSupportType42", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportType42(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "w0hI3xsK-hc", ExportName = "sceFontFtSupportWinFonts", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtSupportWinFonts(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "w5sfH9r8ZJ4", ExportName = "sceFontFtTermAliases", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontFtTermAliases(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ojW+VKl4Ehs", ExportName = "sceFontSelectGlyphsFt", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFontFt")]
    public static int FontSelectGlyphsFt(CpuContext ctx) => Ok(ctx);

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

    internal static void ResetAdditionalFontStateForTests()
    {
        FontObjects.Clear();
        FontParents.Clear();
        FontStrings.Clear();
    }

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
