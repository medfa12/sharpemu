// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpPartner001Exports
{
    private const int ErrorNotInitialized = unchecked((int)0x819D0001);
    private const int ErrorInvalidArgument = unchecked((int)0x819D0002);
    private static int _initialized;

    [SysAbiExport(Nid = "7CxI50-xlCk", ExportName = "sceNpEAAccessInitialize", Target = Generation.Gen5, LibraryName = "libSceNpPartner001")]
    public static int Initialize(CpuContext ctx) { Volatile.Write(ref _initialized, 1); return ctx.SetReturn(0); }
    [SysAbiExport(Nid = "pMxXhNozUX8", ExportName = "sceNpEAAccessTerminate", Target = Generation.Gen5, LibraryName = "libSceNpPartner001")]
    public static int Terminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return ctx.SetReturn(ErrorNotInitialized);
        return ctx.SetReturn(0);
    }
    [SysAbiExport(Nid = "+OnbUs1CV0M", ExportName = "sceNpHasEAAccessSubscription", Target = Generation.Gen5, LibraryName = "libSceNpPartner001")]
    public static int HasSubscription(CpuContext ctx)
    {
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(ErrorNotInitialized);
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return ctx.SetReturn(ErrorInvalidArgument);
        return ctx.Memory.TryWrite(output, new byte[1])
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    [SysAbiExport(Nid = "pQfYTZHznMc", ExportName = "sceNpHasEAAccessSubscriptionAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpPartner001")]
    public static int AbortRequest(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _initialized) == 0 ? ErrorNotInitialized : 0);

    internal static void ResetForTests() => Volatile.Write(ref _initialized, 0);
}
