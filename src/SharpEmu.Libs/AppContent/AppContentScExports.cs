// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.AppContent;

public static class AppContentScExports
{
    [SysAbiExport(Nid = "TCqT7kPuGx0", ExportName = "sceAppContentGetDownloadedStoreCountry", Target = Generation.Gen5, LibraryName = "libSceAppContentSc")]
    public static int GetDownloadedStoreCountry(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        return output == 0 || ctx.Memory.TryWrite(output, "US\0"u8)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
