// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);
    private const int NpWebApi2ErrorInvalidLibContextId = unchecked((int)0x80553403);
    private const int NpWebApi2ErrorNotSignedIn = unchecked((int)0x80553407);

    private static int _initialized;
    private static int _lastLibraryContextId;
    private static int _lastPushEventHandleId;

    // sceNpWebApi2Initialize returns the library context id, not plain success. Handing back 0
    // makes titles carry libCtxId=0 into sceNpWebApi2CreateUserContext, whose invalid-argument
    // failure they treat as a transient bug and retry every frame (observed with ASTRO's
    // online-init loop).
    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        Interlocked.Exchange(ref _initialized, 1);
        var libraryContextId = Interlocked.Increment(ref _lastLibraryContextId);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsKnownLibraryContext(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidLibContextId);
        }

        var handleId = Interlocked.Increment(ref _lastPushEventHandleId);
        TraceNpWebApi2("push-event-create-handle", libraryContextId, (ulong)handleId);
        return ctx.SetReturn(handleId);
    }

    // No PSN backend: report the local user as signed out. Unlike invalid-argument, titles treat
    // NOT_SIGNED_IN as a terminal offline condition and stop retrying context creation.
    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceNpWebApi2("create-user-context", libraryContextId, ctx[CpuRegister.Rsi]);
        if (!IsKnownLibraryContext(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidLibContextId);
        }

        return ctx.SetReturn(NpWebApi2ErrorNotSignedIn);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        Interlocked.Exchange(ref _initialized, 0);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static bool IsKnownLibraryContext(int libraryContextId) =>
        libraryContextId > 0 && libraryContextId <= Volatile.Read(ref _lastLibraryContextId);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _lastLibraryContextId, 0);
        Interlocked.Exchange(ref _lastPushEventHandleId, 0);
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
