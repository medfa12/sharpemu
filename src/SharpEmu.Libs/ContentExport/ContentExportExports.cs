// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ContentExport;

/// <summary>
/// HLE for libSceContentExport. Titles call the init entry points during
/// startup to register malloc/free callbacks for later media exports; nothing
/// is exported until the user triggers it, so validating the parameter block
/// and tracking init state is enough to get boot past this library.
///
/// The init parameter block layout (48 bytes): malloc callback at +0x00, free
/// callback at +0x08, user data at +0x10, buffer size at +0x18, and two
/// reserved qwords at +0x20/+0x28. sceContentExportInit2 additionally requires
/// the reserved fields to be zero and the buffer size to be 0 or at least
/// 0x100.
/// </summary>
public static class ContentExportExports
{
    // libSceContentExport error facility (0x809D3xxx).
    private const int ErrorNoInit = unchecked((int)0x809D3004);
    private const int ErrorMultipleInit = unchecked((int)0x809D3005);
    private const int ErrorInvalidParam = unchecked((int)0x809D3016);

    private const ulong MallocFuncOffset = 0x00;
    private const ulong FreeFuncOffset = 0x08;
    private const ulong BufferSizeOffset = 0x18;
    private const ulong Reserved0Offset = 0x20;
    private const ulong Reserved1Offset = 0x28;

    private const ulong MinBufferSize = 0x100;

    private static int _initialized;

    [SysAbiExport(
        Nid = "FzEWeYnAFlI",
        ExportName = "sceContentExportInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportInit(CpuContext ctx)
    {
        return Initialize(ctx, validateVersion2Fields: false);
    }

    [SysAbiExport(
        Nid = "0GnN4QCgIfs",
        ExportName = "sceContentExportInit2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportInit2(CpuContext ctx)
    {
        return Initialize(ctx, validateVersion2Fields: true);
    }

    [SysAbiExport(
        Nid = "+KDWny9Y-6k",
        ExportName = "sceContentExportTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportTerm(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return ctx.SetReturn(ErrorNoInit);
        }

        TraceContentExport("term");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int Initialize(CpuContext ctx, bool validateVersion2Fields)
    {
        if (Volatile.Read(ref _initialized) != 0)
        {
            return ctx.SetReturn(ErrorMultipleInit);
        }

        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0 ||
            !ctx.TryReadUInt64(paramAddress + MallocFuncOffset, out var mallocFunc) ||
            !ctx.TryReadUInt64(paramAddress + FreeFuncOffset, out var freeFunc) ||
            mallocFunc == 0 ||
            freeFunc == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (validateVersion2Fields)
        {
            if (!ctx.TryReadUInt64(paramAddress + BufferSizeOffset, out var bufferSize) ||
                !ctx.TryReadUInt64(paramAddress + Reserved0Offset, out var reserved0) ||
                !ctx.TryReadUInt64(paramAddress + Reserved1Offset, out var reserved1) ||
                reserved0 != 0 ||
                reserved1 != 0 ||
                (bufferSize != 0 && bufferSize < MinBufferSize))
            {
                return ctx.SetReturn(ErrorInvalidParam);
            }
        }

        Interlocked.Exchange(ref _initialized, 1);

        TraceContentExport($"init version={(validateVersion2Fields ? 2 : 1)} param=0x{paramAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _initialized, 0);
    }

    private static void TraceContentExport(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CONTENT_EXPORT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] content_export.{message}");
    }
}
