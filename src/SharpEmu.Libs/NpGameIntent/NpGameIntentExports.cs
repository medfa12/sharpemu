// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.NpGameIntent;

public static class NpGameIntentExports
{
    private const int NpGameIntentErrorInvalidArgument = unchecked((int)0x80553804);
    private const int NpGameIntentErrorIntentNotFound = unchecked((int)0x80553806);
    private const int NpGameIntentErrorValueNotFound = unchecked((int)0x80553807);
    private const int IntentTypeSize = 33;
    private const int IntentDataSize = 16_392;
    private static int _initialized;

    [SysAbiExport(
        Nid = "m87BHxt-H60",
        ExportName = "sceNpGameIntentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0HBYxYAjmf0",
        ExportName = "sceNpGameIntentTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentTerminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "jEIXUAr9XE8",
        ExportName = "sceNpGameIntentReceiveIntent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentReceiveIntent(CpuContext ctx)
    {
        var intentInfoAddress = ctx[CpuRegister.Rdi];
        if (intentInfoAddress == 0)
        {
            return ctx.SetReturn(NpGameIntentErrorInvalidArgument);
        }

        if (!ctx.TryWriteInt32(intentInfoAddress + 8, -1, checkNil: true) ||
            !ctx.Memory.TryWrite(intentInfoAddress + 12, new byte[IntentTypeSize]) ||
            !ctx.Memory.TryWrite(intentInfoAddress + 308, new byte[IntentDataSize]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpGameIntentErrorIntentNotFound);
    }

    [SysAbiExport(
        Nid = "rPl0INNc-M8",
        ExportName = "sceNpGameIntentGetPropertyValueString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentGetPropertyValueString(CpuContext ctx)
    {
        var intentDataAddress = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        var valueSize = ctx[CpuRegister.Rcx];
        if (intentDataAddress == 0 || keyAddress == 0 || valueAddress == 0 || valueSize == 0)
        {
            return ctx.SetReturn(NpGameIntentErrorInvalidArgument);
        }

        Span<byte> probe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(intentDataAddress, probe) ||
            !ctx.TryReadNullTerminatedUtf8(keyAddress, 256, out _) ||
            !ctx.Memory.TryWrite(valueAddress, new byte[1]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpGameIntentErrorValueNotFound);
    }
}
