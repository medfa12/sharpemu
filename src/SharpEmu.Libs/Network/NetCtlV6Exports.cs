// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetCtlV6Exports
{
    [SysAbiExport(Nid = "Jy1EO5GdlcM", ExportName = "sceNetCtlGetInfoV6", LibraryName = "libSceNetCtlV6", Target = Generation.Gen5)]
    public static int NetCtlGetInfoV6(CpuContext ctx) => NetCtlExports.NetCtlGetInfo(ctx);

    [SysAbiExport(Nid = "H5yARg37U5g", ExportName = "sceNetCtlGetResultV6", LibraryName = "libSceNetCtlV6", Target = Generation.Gen5)]
    public static int NetCtlGetResultV6(CpuContext ctx) => ctx.SetReturn(0, typeof(long));

    [SysAbiExport(Nid = "+lxqIKeU9UY", ExportName = "sceNetCtlGetStateV6", LibraryName = "libSceNetCtlV6", Target = Generation.Gen5)]
    public static int NetCtlGetStateV6(CpuContext ctx) => NetCtlExports.NetCtlGetState(ctx);
}
