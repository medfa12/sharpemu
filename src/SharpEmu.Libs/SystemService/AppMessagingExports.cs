// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.SystemService;

public static class AppMessagingExports
{
    private static int Success(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "alZfRdr2RP8", ExportName = "sceAppMessagingClearEventFlag", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int ClearEventFlag(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "jKgAUl6cLy0", ExportName = "sceAppMessagingReceiveMsg", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int ReceiveMsg(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "+zuv20FsXrA", ExportName = "sceAppMessagingSendMsg", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int SendMsg(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "HIwEvx4kf6o", ExportName = "sceAppMessagingSendMsgToShellCore", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int SendMsgToShellCore(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "5ygy1IPUh5c", ExportName = "sceAppMessagingSendMsgToShellUI", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int SendMsgToShellUi(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "hdoMbMFIDdE", ExportName = "sceAppMessagingSetEventFlag", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int SetEventFlag(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "iKNXKsUtOjY", ExportName = "sceAppMessagingTryGetEventFlag", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int TryGetEventFlag(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ZVRXXqj1n80", ExportName = "sceAppMessagingTryReceiveMsg", Target = Generation.Gen5, LibraryName = "libSceAppMessaging")]
    public static int TryReceiveMsg(CpuContext ctx) => Success(ctx);
}
