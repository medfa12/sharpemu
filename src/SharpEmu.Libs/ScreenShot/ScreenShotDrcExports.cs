// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ScreenShot;

public static class ScreenShotDrcExports
{
    [SysAbiExport(Nid = "itlWFWV3Tzc", ExportName = "sceScreenShotSetDrcParam", LibraryName = "libSceScreenShotDrc", Target = Generation.Gen5)]
    public static int ScreenShotSetDrcParam(CpuContext ctx) => ScreenShotExports.SetDrcParameter(ctx, ctx[CpuRegister.Rdi]);
}
