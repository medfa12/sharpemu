// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class kernel_dmem_aliasing2Exports
{
    [SysAbiExport(Nid = "usHTMoFoBTM", ExportName = "sceKernelEnableDmemAliasing", Target = Generation.Gen5, LibraryName = "libkernel_dmem_aliasing2")]
    public static int EnableDmemAliasing(CpuContext ctx) => ctx.SetReturn(0);
}
