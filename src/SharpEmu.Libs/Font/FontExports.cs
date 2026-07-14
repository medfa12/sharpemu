// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
/// and glyph paths are implemented. The out-parameter positions follow the
/// public libSceFont signatures (out-handle is the trailing pointer argument);
/// writes are guarded by TryWriteUInt64 so a wrong guess cannot corrupt memory.
/// </summary>
public static class FontExports
{
    // Opaque font objects are backed by generously sized zeroed scratch so the
    // game's field reads (observed up to +0x370 on a library object) land in
    // mapped, zeroed memory rather than faulting.
    private const int ScratchObjectSize = 0x1000;
    private const int ScratchObjectAlign = 16;

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
    public static int FontSetScalePixel(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "6vGCkkQJOcI", ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceFont")]
    public static int FontSetupRenderScalePixel(CpuContext ctx) => Ok(ctx);

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
}
