// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class NpProfileDialogCompatExports
{
    [SysAbiExport(Nid = "uj9Cz7Tk0cc", ExportName = "sceNpProfileDialogOpen", LibraryName = "libSceNpProfileDialogCompat", Target = Generation.Gen5)]
    public static int NpProfileDialogOpen(CpuContext ctx) => NpProfileDialogExports.OpenCompat(ctx);
}
