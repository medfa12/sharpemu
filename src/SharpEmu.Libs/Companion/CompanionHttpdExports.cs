// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Companion;

public static class CompanionHttpdExports
{
    private const int ErrorNoEvent = unchecked((int)0x80E40008);
    private const int EventSize = 260;
    private const int DisconnectEvent = 0x10000002;
    private static int _initialized;
    private static int _started;
    private static ulong _requestCallback;
    private static ulong _requestCallbackArgument;
    private static ulong _bodyCallback;
    private static ulong _bodyCallbackArgument;

    private static int Success(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "8pWltDG7h6A", ExportName = "sceCompanionHttpdAddHeader", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int AddHeader(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "B-QBMeFdNgY", ExportName = "sceCompanionHttpdGet2ndScreenStatus", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int GetSecondScreenStatus(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "Vku4big+IYM", ExportName = "sceCompanionHttpdGetEvent", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int GetEvent(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        var data = new byte[EventSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data, DisconnectEvent);
        return ctx.Memory.TryWrite(address, data)
            ? ctx.SetReturn(ErrorNoEvent)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    [SysAbiExport(Nid = "0SySxcuVNG0", ExportName = "sceCompanionHttpdGetUserId", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int GetUserId(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return ctx.TryWriteInt32(output, 1000) ? Success(ctx) : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    [SysAbiExport(Nid = "ykNpWs3ktLY", ExportName = "sceCompanionHttpdInitialize", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int Initialize(CpuContext ctx) { Volatile.Write(ref _initialized, 1); return Success(ctx); }
    [SysAbiExport(Nid = "OA6FbORefbo", ExportName = "sceCompanionHttpdInitialize2", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int Initialize2(CpuContext ctx) { Volatile.Write(ref _initialized, 1); return Success(ctx); }
    [SysAbiExport(Nid = "r-2-a0c7Kfc", ExportName = "sceCompanionHttpdOptParamInitialize", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int OptParamInitialize(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "fHNmij7kAUM", ExportName = "sceCompanionHttpdRegisterRequestBodyReceptionCallback", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int RegisterRequestBodyReceptionCallback(CpuContext ctx) { _bodyCallback = ctx[CpuRegister.Rdi]; _bodyCallbackArgument = ctx[CpuRegister.Rsi]; return Success(ctx); }
    [SysAbiExport(Nid = "OaWw+IVEdbI", ExportName = "sceCompanionHttpdRegisterRequestCallback", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int RegisterRequestCallback(CpuContext ctx) { _requestCallback = ctx[CpuRegister.Rdi]; _requestCallbackArgument = ctx[CpuRegister.Rsi]; return Success(ctx); }
    [SysAbiExport(Nid = "-0c9TCTwnGs", ExportName = "sceCompanionHttpdRegisterRequestCallback2", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int RegisterRequestCallback2(CpuContext ctx) => RegisterRequestCallback(ctx);
    [SysAbiExport(Nid = "h3OvVxzX4qM", ExportName = "sceCompanionHttpdSetBody", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int SetBody(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "w7oz0AWHpT4", ExportName = "sceCompanionHttpdSetStatus", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int SetStatus(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "k7F0FcDM-Xc", ExportName = "sceCompanionHttpdStart", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int Start(CpuContext ctx) { Volatile.Write(ref _started, 1); return Success(ctx); }
    [SysAbiExport(Nid = "0SCgzfVQHpo", ExportName = "sceCompanionHttpdStop", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int Stop(CpuContext ctx) { Volatile.Write(ref _started, 0); return Success(ctx); }
    [SysAbiExport(Nid = "+-du9tWgE9s", ExportName = "sceCompanionHttpdTerminate", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int Terminate(CpuContext ctx) { Volatile.Write(ref _initialized, 0); Volatile.Write(ref _started, 0); return Success(ctx); }
    [SysAbiExport(Nid = "ZSHiUfYK+QI", ExportName = "sceCompanionHttpdUnregisterRequestBodyReceptionCallback", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int UnregisterRequestBodyReceptionCallback(CpuContext ctx) { _bodyCallback = 0; _bodyCallbackArgument = 0; return Success(ctx); }
    [SysAbiExport(Nid = "xweOi2QT-BE", ExportName = "sceCompanionHttpdUnregisterRequestCallback", Target = Generation.Gen5, LibraryName = "libSceCompanionHttpd")]
    public static int UnregisterRequestCallback(CpuContext ctx) { _requestCallback = 0; _requestCallbackArgument = 0; return Success(ctx); }
}
