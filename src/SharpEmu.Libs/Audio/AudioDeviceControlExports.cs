// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AudioDeviceControlExports
{
    [SysAbiExport(Nid = "tKumjQSzhys", ExportName = "sceAudioDeviceControlGet", Target = Generation.Gen5, LibraryName = "libSceAudioDeviceControl")]
    public static int Get(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "5ChfcHOf3SM", ExportName = "sceAudioDeviceControlSet", Target = Generation.Gen5, LibraryName = "libSceAudioDeviceControl")]
    public static int Set(CpuContext ctx) => ctx.SetReturn(0);
}
