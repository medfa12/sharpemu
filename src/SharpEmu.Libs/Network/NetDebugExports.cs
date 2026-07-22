// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetDebugExports
{
    [SysAbiExport(Nid = "JK1oZe4UysY", ExportName = "sceNetEmulationGet", Target = Generation.Gen5, LibraryName = "libSceNetDebug")]
    public static int EmulationGet(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "pfn3Fha1ydc", ExportName = "sceNetEmulationSet", Target = Generation.Gen5, LibraryName = "libSceNetDebug")]
    public static int EmulationSet(CpuContext ctx) => ctx.SetReturn(0);
}
