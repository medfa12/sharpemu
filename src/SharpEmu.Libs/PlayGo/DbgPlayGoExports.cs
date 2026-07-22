// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.PlayGo;

public static class DbgPlayGoExports
{
    [SysAbiExport(Nid = "uEqMfMITvEI", ExportName = "sceDbgPlayGoRequestNextChunk", Target = Generation.Gen5, LibraryName = "libSceDbgPlayGo")]
    public static int RequestNextChunk(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "vU+FqrH+pEY", ExportName = "sceDbgPlayGoSnapshot", Target = Generation.Gen5, LibraryName = "libSceDbgPlayGo")]
    public static int Snapshot(CpuContext ctx) => ctx.SetReturn(0);
}
