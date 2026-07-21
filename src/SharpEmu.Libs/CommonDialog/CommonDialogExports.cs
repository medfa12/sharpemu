// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class CommonDialogExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "uoUpLGNkygk",
        ExportName = "sceCommonDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "BQ3tey0JmQM",
        ExportName = "sceCommonDialogIsUsed",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogIsUsed(CpuContext ctx)
    {
        return ctx.SetReturn(MsgDialogExports.IsUsed ? 1 : 0);
    }

    internal static void ResetForTests() => Interlocked.Exchange(ref _initialized, 0);
}
