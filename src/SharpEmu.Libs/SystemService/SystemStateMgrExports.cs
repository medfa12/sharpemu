// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.SystemService;

public static class SystemStateMgrExports
{
    private static int _state;
    private static int _mediaPlayback;

    private static int Success(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "eBFzDYThras", ExportName = "sceSystemStateMgrCancelShutdownTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int CancelShutdownTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Ap5dJ0zHRVY", ExportName = "sceSystemStateMgrEnterMediaPlaybackMode", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int EnterMediaPlaybackMode(CpuContext ctx) { Volatile.Write(ref _mediaPlayback, 1); return Success(ctx); }
    [SysAbiExport(Nid = "Laac0S4FuhE", ExportName = "sceSystemStateMgrEnterStandby", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int EnterStandby(CpuContext ctx) { Volatile.Write(ref _state, 1); return Success(ctx); }
    [SysAbiExport(Nid = "rSquvOtwQmk", ExportName = "sceSystemStateMgrExtendShutdownTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int ExtendShutdownTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "FzjISMWw5Xg", ExportName = "sceSystemStateMgrExtendShutdownTimerForPostAutoUpdateProcess", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int ExtendShutdownTimerForPostAutoUpdateProcess(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ze0ky5Q1yE8", ExportName = "sceSystemStateMgrGetCurrentState", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int GetCurrentState(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _state));
    [SysAbiExport(Nid = "wlxvESTUplk", ExportName = "sceSystemStateMgrGetTriggerCode", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int GetTriggerCode(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "cmjuYpVujQs", ExportName = "sceSystemStateMgrIsBdDriveReady", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int IsBdDriveReady(CpuContext ctx) => ctx.SetReturn(1);
    [SysAbiExport(Nid = "texLPLDXDso", ExportName = "sceSystemStateMgrIsGpuPerformanceNormal", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int IsGpuPerformanceNormal(CpuContext ctx) => ctx.SetReturn(1);
    [SysAbiExport(Nid = "asLBe0esmIY", ExportName = "sceSystemStateMgrIsShellUIShutdownInProgress", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int IsShellUiShutdownInProgress(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "j3IrOCL+DmM", ExportName = "sceSystemStateMgrIsStandbyModeEnabled", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int IsStandbyModeEnabled(CpuContext ctx) => ctx.SetReturn(1);
    [SysAbiExport(Nid = "88y5DztlXBE", ExportName = "sceSystemStateMgrLeaveMediaPlaybackMode", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int LeaveMediaPlaybackMode(CpuContext ctx) { Volatile.Write(ref _mediaPlayback, 0); return Success(ctx); }
    [SysAbiExport(Nid = "H2f6ZwIqLJg", ExportName = "sceSystemStateMgrNotifySystemSuspendResumeProgress", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int NotifySystemSuspendResumeProgress(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "uR1wFHXX1XQ", ExportName = "sceSystemStateMgrReboot", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int Reboot(CpuContext ctx) { Volatile.Write(ref _state, 0); return Success(ctx); }
    [SysAbiExport(Nid = "gPx1b36zyMY", ExportName = "sceSystemStateMgrSendCecOneTouchPlayCommand", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int SendCecOneTouchPlayCommand(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "PcJ5DLzZXSs", ExportName = "sceSystemStateMgrStartRebootTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int StartRebootTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "7qf7mhzOQPo", ExportName = "sceSystemStateMgrStartShutdownTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int StartShutdownTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ZwhQSHTqGpE", ExportName = "sceSystemStateMgrStartStadbyTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int StartStandbyTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "YWftBq50hcA", ExportName = "sceSystemStateMgrStartVshAutoUpdateTimer", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int StartVshAutoUpdateTimer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ypl-BoZZKOM", ExportName = "sceSystemStateMgrTickMusicPlayback", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int TickMusicPlayback(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "GvqPsPX4EUI", ExportName = "sceSystemStateMgrTickPartyChat", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int TickPartyChat(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "gK3EX6ZKtKc", ExportName = "sceSystemStateMgrTurnOff", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int TurnOff(CpuContext ctx) { Volatile.Write(ref _state, 2); return Success(ctx); }
    [SysAbiExport(Nid = "U1dZXAjkBVo", ExportName = "sceSystemStateMgrVshAutoUpdate", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int VshAutoUpdate(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "geg26leOsvw", ExportName = "sceSystemStateMgrWaitVshAutoUpdateVerifyDone", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int WaitVshAutoUpdateVerifyDone(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "6gtqLPVTdJY", ExportName = "sceSystemStateMgrWakeUp", Target = Generation.Gen5, LibraryName = "libSceSystemStateMgr")]
    public static int WakeUp(CpuContext ctx) { Volatile.Write(ref _state, 0); return Success(ctx); }

    internal static void ResetForTests()
    {
        Volatile.Write(ref _state, 0);
        Volatile.Write(ref _mediaPlayback, 0);
    }
}
