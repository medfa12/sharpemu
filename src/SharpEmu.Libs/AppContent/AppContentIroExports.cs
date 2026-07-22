// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.AppContent;

public static class AppContentIroExports
{
    [SysAbiExport(Nid = "kJmjt81mXKQ", ExportName = "sceAppContentAddcontEnqueueDownloadByEntitlementId", Target = Generation.Gen5, LibraryName = "libSceAppContentIro")]
    public static int EnqueueDownloadByEntitlementId(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "efX3lrPwdKA", ExportName = "sceAppContentAddcontMountByEntitlementId", Target = Generation.Gen5, LibraryName = "libSceAppContentIro")]
    public static int MountByEntitlementId(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "z9hgjLd1SGA", ExportName = "sceAppContentGetAddcontInfoByEntitlementId", Target = Generation.Gen5, LibraryName = "libSceAppContentIro")]
    public static int GetInfoByEntitlementId(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "3wUaDTGmjcQ", ExportName = "sceAppContentGetAddcontInfoListByIroTag", Target = Generation.Gen5, LibraryName = "libSceAppContentIro")]
    public static int GetInfoListByIroTag(CpuContext ctx) => ctx.SetReturn(0);
}
