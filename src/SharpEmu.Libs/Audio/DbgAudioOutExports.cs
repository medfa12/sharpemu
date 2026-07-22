// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class DbgAudioOutExports
{
    [SysAbiExport(Nid = "7UsdDOEvjlk", ExportName = "sceAudioOutSetSystemDebugState", Target = Generation.Gen5, LibraryName = "libSceDbgAudioOut")]
    public static int SetSystemDebugState(CpuContext ctx) => ctx.SetReturn(0);
}
