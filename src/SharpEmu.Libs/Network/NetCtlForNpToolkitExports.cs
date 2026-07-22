// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetCtlForNpToolkitExports
{
    private const int MaxCallbacks = 8;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    internal static int CallbackCount
    {
        get
        {
            lock (CallbackGate)
            {
                return Callbacks.Count(static registration => registration.Function != 0);
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }
    }

    [SysAbiExport(Nid = "u5oqtlIP+Fw", ExportName = "sceNetCtlCheckCallbackForNpToolkit", LibraryName = "libSceNetCtlForNpToolkit", Target = Generation.Gen5)]
    public static int NetCtlCheckCallbackForNpToolkit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "saYB0b2ZWtI", ExportName = "sceNetCtlClearEventForNpToolkit", LibraryName = "libSceNetCtlForNpToolkit", Target = Generation.Gen5)]
    public static int NetCtlClearEventForNpToolkit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "wIsKy+TfeLs", ExportName = "sceNetCtlRegisterCallbackForNpToolkit", LibraryName = "libSceNetCtlForNpToolkit", Target = Generation.Gen5)]
    public static int NetCtlRegisterCallbackForNpToolkit(CpuContext ctx)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var callbackIdAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || callbackIdAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (CallbackGate)
        {
            var callbackId = Array.FindIndex(Callbacks, static registration => registration.Function == 0);
            if (callbackId < 0)
            {
                return ctx.SetReturn(unchecked((int)0x80412103));
            }

            if (!ctx.TryWriteInt32(callbackIdAddress, callbackId))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            Callbacks[callbackId] = new CallbackRegistration(function, argument);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "2oUqKR5odGc", ExportName = "sceNetCtlUnregisterCallbackForNpToolkit", LibraryName = "libSceNetCtlForNpToolkit", Target = Generation.Gen5)]
    public static int NetCtlUnregisterCallbackForNpToolkit(CpuContext ctx)
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

        return ctx.SetReturn(0);
    }
}
