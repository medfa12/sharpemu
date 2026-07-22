// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.AppContent;

public static class AppContentBundleExports
{
    [SysAbiExport(Nid = "xZo2-418Wdo", ExportName = "Func_C59A36FB8D7C59DA", Target = Generation.Gen5, LibraryName = "libSceAppContentBundle")]
    public static int BundleFunction(CpuContext ctx) => ctx.SetReturn(0);
}
