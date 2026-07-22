// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetBweExports
{
    private const int MaxCallbacks = 8;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];
    private static int _testResult;
    private static int _testRunning;

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _testResult, 0);
        Interlocked.Exchange(ref _testRunning, 0);
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }
    }

    [SysAbiExport(Nid = "XtClSOC1xcU", ExportName = "sceNetBweCheckCallbackIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweCheckCallbackIpcInt(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "YALqoY4aeY0", ExportName = "sceNetBweClearEventIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweClearEventIpcInt(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "ouyROWhGUbM", ExportName = "sceNetBweFinishInternetConnectionTestIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweFinishInternetConnectionTestIpcInt(CpuContext ctx)
    {
        Interlocked.Exchange(ref _testRunning, 0);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "G4vltQ0Vs+0", ExportName = "sceNetBweGetInfoIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweGetInfoIpcInt(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        Span<byte> info = stackalloc byte[16];
        info.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(info[0x00..], Volatile.Read(ref _testRunning));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(info[0x04..], Volatile.Read(ref _testResult));
        return ctx.Memory.TryWrite(outputAddress, info) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "GqETL5+INhU", ExportName = "sceNetBweRegisterCallbackIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweRegisterCallbackIpcInt(CpuContext ctx)
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

    [SysAbiExport(Nid = "mEUt-phGd5E", ExportName = "sceNetBweSetInternetConnectionTestResultIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweSetInternetConnectionTestResultIpcInt(CpuContext ctx)
    {
        Interlocked.Exchange(ref _testResult, unchecked((int)ctx[CpuRegister.Rdi]));
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "pQLJV5SEAqk", ExportName = "sceNetBweStartInternetConnectionTestBandwidthTestIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweStartInternetConnectionTestBandwidthTestIpcInt(CpuContext ctx) => StartTest(ctx);

    [SysAbiExport(Nid = "c+aYh130SV0", ExportName = "sceNetBweStartInternetConnectionTestIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweStartInternetConnectionTestIpcInt(CpuContext ctx) => StartTest(ctx);

    [SysAbiExport(Nid = "0lViPaTB-R8", ExportName = "sceNetBweUnregisterCallbackIpcInt", LibraryName = "libSceNetBwe", Target = Generation.Gen5)]
    public static int NetBweUnregisterCallbackIpcInt(CpuContext ctx)
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

    private static int StartTest(CpuContext ctx)
    {
        Interlocked.Exchange(ref _testResult, 0);
        Interlocked.Exchange(ref _testRunning, 1);
        return Ok(ctx);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);
    private static int InvalidArgument(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    private static int MemoryFault(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
