// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.UserService;

public static class UserServiceForShellCoreExports
{
    [SysAbiExport(Nid = "Psl9mfs3duM", ExportName = "sceUserServiceInitializeForShellCore", Target = Generation.Gen5, LibraryName = "libSceUserServiceForShellCore")]
    public static int Initialize(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "CydP+QtA0KI", ExportName = "sceUserServiceTerminateForShellCore", Target = Generation.Gen5, LibraryName = "libSceUserServiceForShellCore")]
    public static int Terminate(CpuContext ctx) => ctx.SetReturn(0);
}
