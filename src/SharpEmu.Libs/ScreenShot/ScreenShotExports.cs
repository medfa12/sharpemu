// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.ScreenShot;

public static class ScreenShotExports
{
    private const int AppInfoSize = 64;
    internal const int DrcParamSize = 32;
    private static int _disabled;
    private static int _notificationDisabled;
    private static readonly object DrcSync = new();
    private static readonly byte[] DrcParameter = new byte[DrcParamSize];

    [SysAbiExport(Nid = "AS45QoYHjc4", ExportName = "_Z5dummyv", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int Dummy(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "JuMLLmmvRgk", ExportName = "sceScreenShotCapture", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotCapture(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "tIYf0W5VTi8", ExportName = "sceScreenShotDisable", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotDisable(CpuContext ctx)
    {
        Interlocked.Exchange(ref _disabled, 1);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "ysfza71rm9M", ExportName = "sceScreenShotDisableNotification", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotDisableNotification(CpuContext ctx)
    {
        Interlocked.Exchange(ref _notificationDisabled, 1);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "2xxUtuC-RzE", ExportName = "sceScreenShotEnable", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotEnable(CpuContext ctx)
    {
        Interlocked.Exchange(ref _disabled, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "BDUaqlVdSAY", ExportName = "sceScreenShotEnableNotification", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotEnableNotification(CpuContext ctx)
    {
        Interlocked.Exchange(ref _notificationDisabled, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "hNmK4SdhPT0", ExportName = "sceScreenShotGetAppInfo", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotGetAppInfo(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        return outputAddress == 0
            ? ctx.SetReturn(0)
            : FinishMemory(ctx, ctx.Memory.TryWrite(outputAddress, new byte[AppInfoSize]));
    }

    [SysAbiExport(Nid = "VlAQIgXa2R0", ExportName = "sceScreenShotGetDrcParam", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotGetDrcParam(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        lock (DrcSync)
        {
            return FinishMemory(ctx, ctx.Memory.TryWrite(outputAddress, DrcParameter));
        }
    }

    [SysAbiExport(Nid = "-SV-oTNGFQk", ExportName = "sceScreenShotIsDisabled", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotIsDisabled(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _disabled));

    [SysAbiExport(Nid = "ICNJ-1POs84", ExportName = "sceScreenShotIsVshScreenCaptureDisabled", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotIsVshScreenCaptureDisabled(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "ahHhOf+QNkQ", ExportName = "sceScreenShotSetOverlayImage", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotSetOverlayImage(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "73WQ4Jj0nJI", ExportName = "sceScreenShotSetOverlayImageWithOrigin", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotSetOverlayImageWithOrigin(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "G7KlmIYFIZc", ExportName = "sceScreenShotSetParam", LibraryName = "libSceScreenShot", Target = Generation.Gen5)]
    public static int ScreenShotSetParam(CpuContext ctx) => ctx.SetReturn(0);

    internal static bool NotificationDisabled => Volatile.Read(ref _notificationDisabled) != 0;

    internal static int SetDrcParameter(CpuContext ctx, ulong parameterAddress)
    {
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        Span<byte> parameter = stackalloc byte[DrcParamSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (DrcSync)
        {
            parameter.CopyTo(DrcParameter);
        }

        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _disabled, 0);
        Interlocked.Exchange(ref _notificationDisabled, 0);
        lock (DrcSync)
        {
            Array.Clear(DrcParameter);
        }
    }

    private static int FinishMemory(CpuContext ctx, bool success) =>
        ctx.SetReturn(success ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
