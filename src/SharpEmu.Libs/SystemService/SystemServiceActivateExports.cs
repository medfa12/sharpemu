// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.SystemService;

public static class SystemServiceActivateExports
{
    [SysAbiExport(Nid = "DILuzcvXjGQ", ExportName = "sceSystemServiceSaveVideoToken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceVideoToken")]
    public static int SystemServiceSaveVideoToken(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "0TDfP7R4fiQ", ExportName = "sceSystemServiceGetDbgExecutablePath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceDbg")]
    public static int SystemServiceGetDbgExecutablePath(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "tIdXUhSLyOU", ExportName = "sceSystemServiceAddLocalProcessForPs2Emu", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePs2Emu")]
    public static int SystemServiceAddLocalProcessForPs2Emu(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "qhPJ1EfqLjQ", ExportName = "sceSystemServiceGetParentSocketForPs2Emu", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePs2Emu")]
    public static int SystemServiceGetParentSocketForPs2Emu(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "fKqJTnoZ8C8", ExportName = "sceSystemServiceKillLocalProcessForPs2Emu", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePs2Emu")]
    public static int SystemServiceKillLocalProcessForPs2Emu(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "YtDk7X3FF08", ExportName = "sceSystemServiceShowImposeMenuForPs2Emu", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePs2Emu")]
    public static int SystemServiceShowImposeMenuForPs2Emu(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "Zj5FGJQPFxs", ExportName = "sceSystemServiceLaunchStore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceStore")]
    public static int SystemServiceLaunchStore(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "+2uXfrrQCyk", ExportName = "sceSystemServiceActivateHevc", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevc(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "VXA8STT529w", ExportName = "sceSystemServiceActivateHevcAbort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcAbort(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "-9LzYPdangA", ExportName = "sceSystemServiceActivateHevcGetStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcGetStatus(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "BgjPgbXKYjE", ExportName = "sceSystemServiceActivateHevcInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcInit(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "2HHfdrT+rnQ", ExportName = "sceSystemServiceActivateHevcIsActivated", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcIsActivated(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "E9FdusyklCA", ExportName = "sceSystemServiceActivateHevcStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcStart(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "tImUgGSSHpc", ExportName = "sceSystemServiceActivateHevcTerm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevc")]
    public static int SystemServiceActivateHevcTerm(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "d4imyunHryo", ExportName = "sceSystemServiceRequestPowerOff", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePowerControl")]
    public static int SystemServiceRequestPowerOff(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "oEJqGsNtFIw", ExportName = "sceSystemServiceRequestReboot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePowerControl")]
    public static int SystemServiceRequestReboot(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "uhD7g7zXIQo", ExportName = "sceSystemServiceShowClosedCaptionAdvancedSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceClosedCaption")]
    public static int SystemServiceShowClosedCaptionAdvancedSettings(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "5W6LurzMZaY", ExportName = "sceSystemServiceShowClosedCaptionSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceClosedCaption")]
    public static int SystemServiceShowClosedCaptionSettings(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "3nn7rnOdt1g", ExportName = "sceSystemServiceTelemetrySetData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceTelemetry")]
    public static int SystemServiceTelemetrySetData(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "jPKapVQLX70", ExportName = "sceSystemServiceAddLocalProcessForJvm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService_jvm")]
    public static int SystemServiceAddLocalProcessForJvm(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "zqjkZ5VKFSg", ExportName = "sceSystemServiceGetParentSocketForJvm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService_jvm")]
    public static int SystemServiceGetParentSocketForJvm(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "2TJ5KzC73gY", ExportName = "sceSystemServiceKillLocalProcessForJvm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService_jvm")]
    public static int SystemServiceKillLocalProcessForJvm(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "45QrFvUkrjg", ExportName = "sceSystemServiceDisablePartyVoice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePartyVoice")]
    public static int SystemServiceDisablePartyVoice(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "hU3bSlF2OKs", ExportName = "sceSystemServiceReenablePartyVoice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePartyVoice")]
    public static int SystemServiceReenablePartyVoice(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "EqcPA3ugRP8", ExportName = "sceSystemServiceDeclareReadyForSuspend", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceSuspend")]
    public static int SystemServiceDeclareReadyForSuspend(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "Mi0qwCb+rvo", ExportName = "sceSystemServiceDisableSuspendNotification", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceSuspend")]
    public static int SystemServiceDisableSuspendNotification(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "a5Kjjq6HgcU", ExportName = "sceSystemServiceEnableSuspendNotification", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceSuspend")]
    public static int SystemServiceEnableSuspendNotification(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "F-nn3DvNKww", ExportName = "sceSystemServiceActivateMpeg2Abort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2Abort(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "W-U8F5o2SHg", ExportName = "sceSystemServiceActivateMpeg2GetStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2GetStatus(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "PkRTWNBI4IQ", ExportName = "sceSystemServiceActivateMpeg2Init", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2Init(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "aVZb961bWBU", ExportName = "sceSystemServiceActivateMpeg2IsActivated", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2IsActivated(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "-7zMNJ1Ap1c", ExportName = "sceSystemServiceActivateMpeg2Start", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2Start(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "JjIspXDbL6o", ExportName = "sceSystemServiceActivateMpeg2Term", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateMpeg2")]
    public static int SystemServiceActivateMpeg2Term(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "rTa0Vp-4nKA", ExportName = "sceSystemServiceInvokeAppLaunchLink", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceAppLaunchLink")]
    public static int SystemServiceInvokeAppLaunchLink(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "gD4wh2+nuuU", ExportName = "sceSystemServiceInitializeForShellCore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceForShellCoreOnly")]
    public static int SystemServiceInitializeForShellCore(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "f-WtMqIKo20", ExportName = "sceSystemServiceActivateHevcSoft", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoft(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "s6ucQ90BW3g", ExportName = "sceSystemServiceActivateHevcSoftAbort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftAbort(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "MyDvxh8+ckI", ExportName = "sceSystemServiceActivateHevcSoftGetStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftGetStatus(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "ytMU6x1nlmU", ExportName = "sceSystemServiceActivateHevcSoftInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftInit(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "djVe06YjzkI", ExportName = "sceSystemServiceActivateHevcSoftIsActivated", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftIsActivated(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "PNO2xlDVdzg", ExportName = "sceSystemServiceActivateHevcSoftStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftStart(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "P-awBIrXrTQ", ExportName = "sceSystemServiceActivateHevcSoftTerm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceActivateHevcSoft")]
    public static int SystemServiceActivateHevcSoftTerm(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "d3OnoKtNjGg", ExportName = "sceSystemServiceDisableVoiceRecognition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceVoiceRecognition")]
    public static int SystemServiceDisableVoiceRecognition(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "c-aFKhn74h0", ExportName = "sceSystemServiceReenableVoiceRecognition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceVoiceRecognition")]
    public static int SystemServiceReenableVoiceRecognition(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "5u2WeL-PR2w", ExportName = "sceSystemServiceGetPlatformPrivacyDefinitionData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePlatformPrivacy")]
    public static int SystemServiceGetPlatformPrivacyDefinitionData(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "t5K+IeMVD1Q", ExportName = "sceSystemServiceGetPlatformPrivacyDefinitionVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePlatformPrivacy")]
    public static int SystemServiceGetPlatformPrivacyDefinitionVersion(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "hvoLYhc4cq0", ExportName = "sceSystemServiceGetPlatformPrivacySetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePlatformPrivacy")]
    public static int SystemServiceGetPlatformPrivacySetting(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "f34qn7XA3QE", ExportName = "sceSystemServiceLaunchWebApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceWebApp")]
    public static int SystemServiceLaunchWebApp(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "YNoDjc1BPJI", ExportName = "sceSystemServiceLaunchUdsApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceUdsApp")]
    public static int SystemServiceLaunchUdsApp(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "AmTvo3RT5ss", ExportName = "sceSystemServiceLoadExecVideoServiceWebApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServiceVideoServiceWebApp")]
    public static int SystemServiceLoadExecVideoServiceWebApp(CpuContext ctx) => SceOk(ctx);

    [SysAbiExport(Nid = "nT-7-iG55M8", ExportName = "sceSystemServiceSetPowerSaveLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemServicePowerSaveLevel")]
    public static int SystemServiceSetPowerSaveLevel(CpuContext ctx) => SceOk(ctx);

    private static int SceOk(CpuContext ctx) => ctx.SetReturn(0);
}
