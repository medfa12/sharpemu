// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.GameLiveStreaming;

public static class GameLiveStreaming_debugExports
{
    [SysAbiExport(Nid = "caqgDl+V9qA", ExportName = "sceGameLiveStreamingStartDebugBroadcast", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming_debug")]
    public static int StartDebugBroadcast(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "0i8Lrllxwow", ExportName = "sceGameLiveStreamingStopDebugBroadcast", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming_debug")]
    public static int StopDebugBroadcast(CpuContext ctx) => ctx.SetReturn(0);
}
