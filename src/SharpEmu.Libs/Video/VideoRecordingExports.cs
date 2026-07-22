// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Video;

public static class VideoRecordingExports
{
    private const int ErrorInvalidValue = unchecked((int)0x80A80003);

    [SysAbiExport(Nid = "Fc8qxlKINYQ", ExportName = "sceVideoRecordingSetInfo", Target = Generation.Gen5, LibraryName = "libSceVideoRecording")]
    public static int SetInfo(CpuContext ctx)
    {
        var kind = unchecked((uint)ctx[CpuRegister.Rdi]);
        var valid = kind is 0x2 or 0x6 or 0x7 or 0x8 or 0xD or 0xA01 or 0xA007 or 0xA008 or 0xA009;
        return ctx.SetReturn(valid && ctx[CpuRegister.Rsi] != 0 ? 0 : ErrorInvalidValue);
    }
}
