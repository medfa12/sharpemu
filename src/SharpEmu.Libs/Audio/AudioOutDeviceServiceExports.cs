// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AudioOutDeviceServiceExports
{
    [SysAbiExport(Nid = "cx2dYFbzIAg", ExportName = "sceAudioOutDeviceIdOpen", Target = Generation.Gen5, LibraryName = "libSceAudioOutDeviceService")]
    public static int DeviceIdOpen(CpuContext ctx) => ctx.SetReturn(0);
}
