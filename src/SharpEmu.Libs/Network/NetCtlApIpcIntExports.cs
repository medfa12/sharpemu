// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetCtlApIpcIntExports
{
    private const int MaxCallbacks = 8;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];
    private static int _rpState;

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _rpState, 0);
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }
    }

    [SysAbiExport(Nid = "R-4a9Yh4tG8", ExportName = "sceNetCtlApAppInitWpaKey", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApAppInitWpaKey(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "5oLJoOVBbGU", ExportName = "sceNetCtlApAppInitWpaKeyForQa", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApAppInitWpaKeyForQa(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "YtTwZ3pa4aQ", ExportName = "sceNetCtlApAppStartWithRetry", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApAppStartWithRetry(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "sgWeDrEt24U", ExportName = "sceNetCtlApAppStartWithRetryPid", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApAppStartWithRetryPid(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "amqSGH8l--s", ExportName = "sceNetCtlApRestart", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRestart(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "DufQZgH5ISc", ExportName = "sceNetCtlApRpCheckCallback", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpCheckCallback(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "qhZbOi+2qLY", ExportName = "sceNetCtlApRpClearEvent", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpClearEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "VQl16Q+qXeY", ExportName = "sceNetCtlApRpGetInfo", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpGetInfo(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "3pxwYqHzGcw", ExportName = "sceNetCtlApRpGetResult", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpGetResult(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "LEn8FGztKWc", ExportName = "sceNetCtlApRpGetState", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpGetState(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rdi], unchecked((uint)Volatile.Read(ref _rpState)));

    [SysAbiExport(Nid = "ofGsK+xoAaM", ExportName = "sceNetCtlApRpRegisterCallback", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpRegisterCallback(CpuContext ctx)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        lock (CallbackGate)
        {
            var callbackId = Array.FindIndex(Callbacks, static registration => registration.Function == 0);
            if (callbackId < 0)
            {
                return ctx.SetReturn(unchecked((int)0x80412103));
            }

            if (!ctx.TryWriteInt32(outputAddress, callbackId))
            {
                return MemoryFault(ctx);
            }

            Callbacks[callbackId] = new CallbackRegistration(function, argument);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "mjFgpqNavHg", ExportName = "sceNetCtlApRpStart", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpStart(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "HMvaHoZWsn8", ExportName = "sceNetCtlApRpStartConf", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpStartConf(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "9Dxg7XSlr2s", ExportName = "sceNetCtlApRpStartWithRetry", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpStartWithRetry(CpuContext ctx) => Start(ctx);

    [SysAbiExport(Nid = "6uvAl4RlEyk", ExportName = "sceNetCtlApRpStop", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpStop(CpuContext ctx)
    {
        Interlocked.Exchange(ref _rpState, 0);
        NetCtlApExports.SetRunning(false);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "8eyH37Ns8tk", ExportName = "sceNetCtlApRpUnregisterCallback", LibraryName = "libSceNetCtlApIpcInt", Target = Generation.Gen5)]
    public static int NetCtlApRpUnregisterCallback(CpuContext ctx)
    {
        var callbackId = unchecked((int)ctx[CpuRegister.Rdi]);
        if ((uint)callbackId >= MaxCallbacks)
        {
            return ctx.SetReturn(unchecked((int)0x80412105));
        }

        lock (CallbackGate)
        {
            if (Callbacks[callbackId].Function == 0)
            {
                return ctx.SetReturn(unchecked((int)0x80412105));
            }

            Callbacks[callbackId] = default;
        }

        return Ok(ctx);
    }

    private static int Start(CpuContext ctx)
    {
        Interlocked.Exchange(ref _rpState, 1);
        NetCtlApExports.SetRunning(true);
        return Ok(ctx);
    }

    private static int WriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        if (address == 0)
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt32(address, value) ? Ok(ctx) : MemoryFault(ctx);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);
    private static int InvalidArgument(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    private static int MemoryFault(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
