// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class kernel_unityExports
{
    private const int KernelErrorInvalidArgument = unchecked((int)0x80020016);

    [SysAbiExport(Nid = "il03nluKfMk", ExportName = "sceKernelRaiseException", Target = Generation.Gen5, LibraryName = "libkernel_unity")]
    public static int RaiseException(CpuContext ctx) => ctx.SetReturn(unchecked((int)ctx[CpuRegister.Rsi]) == 30 ? 0 : KernelErrorInvalidArgument);
}
