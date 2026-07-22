// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Compression;

public static class RandomExports
{
    private const int ErrorInvalid = unchecked((int)0x817C0016);

    [SysAbiExport(Nid = "PI7jIZj4pcE", ExportName = "sceRandomGetRandomNumber", Target = Generation.Gen5, LibraryName = "libSceRandom")]
    public static int GetRandomNumber(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        if (size > 64 || (size != 0 && address == 0)) return ctx.SetReturn(ErrorInvalid);
        if (size == 0) return ctx.SetReturn(0);
        var bytes = new byte[(int)size];
        RandomNumberGenerator.Fill(bytes);
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
