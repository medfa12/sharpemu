// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class NpSnsFacebookDialogExports
{
    [SysAbiExport(Nid = "fjV7C8H0Y8k", ExportName = "sceNpSnsFacebookDialogUpdateStatus", LibraryName = "libSceNpSnsFacebookDialog", Target = Generation.Gen5)]
    public static int NpSnsFacebookDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(ImmediateDialogState.StatusFinished);
}
