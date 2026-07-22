// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpCommonExports
{
    [SysAbiExport(Nid = "Pglk7zFj0DI", ExportName = "sceNpGetSdkVersion", Target = Generation.Gen5, LibraryName = "libSceNpCommon")]
    public static int GetSdkVersion(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        if (output == 0) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        Span<byte> version = stackalloc byte[8];
        "8.00"u8.CopyTo(version);
        return ctx.Memory.TryWrite(output, version)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
