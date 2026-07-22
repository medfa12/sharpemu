// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AudioOutSparkControlExports
{
    [SysAbiExport(Nid = "Mt7JB3lOyJk", ExportName = "sceAudioOutSparkControlSetEqCoef", Target = Generation.Gen5, LibraryName = "libSceAudioOutSparkControl")]
    public static int SetEqCoef(CpuContext ctx) => ctx.SetReturn(0);
}
