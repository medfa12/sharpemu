// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Companion;

public static class CompanionUtilExports
{
    private const int NoEvent = unchecked((int)0x80AD0008);

    [SysAbiExport(Nid = "cE5Msy11WhU", ExportName = "sceCompanionUtilGetEvent", Target = Generation.Gen5, LibraryName = "libSceCompanionUtil")]
    public static int GetEvent(CpuContext ctx) => ctx.SetReturn(NoEvent);
    [SysAbiExport(Nid = "MaVrz79mT5o", ExportName = "sceCompanionUtilGetRemoteOskEvent", Target = Generation.Gen5, LibraryName = "libSceCompanionUtil")]
    public static int GetRemoteOskEvent(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "xb1xlIhf0QY", ExportName = "sceCompanionUtilInitialize", Target = Generation.Gen5, LibraryName = "libSceCompanionUtil")]
    public static int Initialize(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "IPN-FRSrafk", ExportName = "sceCompanionUtilOptParamInitialize", Target = Generation.Gen5, LibraryName = "libSceCompanionUtil")]
    public static int OptParamInitialize(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "H1fYQd5lFAI", ExportName = "sceCompanionUtilTerminate", Target = Generation.Gen5, LibraryName = "libSceCompanionUtil")]
    public static int Terminate(CpuContext ctx) => ctx.SetReturn(0);
}
