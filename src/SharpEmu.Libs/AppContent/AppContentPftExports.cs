// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.AppContent;

public static class AppContentPftExports
{
    [SysAbiExport(Nid = "xmhnAoxN3Wk", ExportName = "sceAppContentGetPftFlag", Target = Generation.Gen5, LibraryName = "libSceAppContentPft")]
    public static int GetPftFlag(CpuContext ctx) => ctx.SetReturn(0);
}
