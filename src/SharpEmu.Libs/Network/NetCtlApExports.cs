// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetCtlApExports
{
    private const int MaxCallbacks = 8;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];
    private static int _initialized;
    private static int _state;

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    internal static void SetRunning(bool running) => Interlocked.Exchange(ref _state, running ? 1 : 0);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _state, 0);
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }
    }

    [SysAbiExport(Nid = "19Ec7WkMFfQ", ExportName = "sceNetCtlApCheckCallback", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApCheckCallback(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "meFMaDpdsVI", ExportName = "sceNetCtlApClearEvent", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApClearEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "hfkLVdXmfnU", ExportName = "sceNetCtlApGetConnectInfo", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApGetConnectInfo(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        Span<byte> info = stackalloc byte[64];
        info.Clear();
        return ctx.Memory.TryWrite(outputAddress, info) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "LXADzTIzM9I", ExportName = "sceNetCtlApGetInfo", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApGetInfo(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "4jkLJc954+Q", ExportName = "sceNetCtlApGetResult", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApGetResult(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "AKZOzsb9whc", ExportName = "sceNetCtlApGetState", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApGetState(CpuContext ctx) => WriteUInt32(ctx, ctx[CpuRegister.Rdi], unchecked((uint)Volatile.Read(ref _state)));

    [SysAbiExport(Nid = "FdN+edNRtiw", ExportName = "sceNetCtlApInit", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApInit(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        SetRunning(false);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "pmjobSVHuY0", ExportName = "sceNetCtlApRegisterCallback", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApRegisterCallback(CpuContext ctx) => RegisterCallback(ctx);

    [SysAbiExport(Nid = "r-pOyN6AhsM", ExportName = "sceNetCtlApStop", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApStop(CpuContext ctx)
    {
        SetRunning(false);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "cv5Y2efOTeg", ExportName = "sceNetCtlApTerm", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApTerm(CpuContext ctx)
    {
        ResetForTests();
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "NpTcFtaQ-0E", ExportName = "sceNetCtlApUnregisterCallback", LibraryName = "libSceNetCtlAp", Target = Generation.Gen5)]
    public static int NetCtlApUnregisterCallback(CpuContext ctx) => UnregisterCallback(ctx);

    private static int RegisterCallback(CpuContext ctx)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || outputAddress == 0 || Volatile.Read(ref _initialized) == 0)
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

    private static int UnregisterCallback(CpuContext ctx)
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
