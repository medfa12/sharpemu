// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class ErrorDialogExports
{
    private static readonly ImmediateDialogState State = new();

    [SysAbiExport(Nid = "ekXHb1kDBl0", ExportName = "sceErrorDialogClose", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "t2FvHRXzgqk", ExportName = "sceErrorDialogGetStatus", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "I88KChlynSs", ExportName = "sceErrorDialogInitialize", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "M2ZF-ClLhgY", ExportName = "sceErrorDialogOpen", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogOpen(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "jrpnVQfJYgQ", ExportName = "sceErrorDialogOpenDetail", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogOpenDetail(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi], false));

    [SysAbiExport(Nid = "wktCiyWoDTI", ExportName = "sceErrorDialogOpenWithReport", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogOpenWithReport(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi], false));

    [SysAbiExport(Nid = "9XAxK2PMwk8", ExportName = "sceErrorDialogTerminate", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogTerminate(CpuContext ctx) => ctx.SetReturn(State.Terminate());

    [SysAbiExport(Nid = "WWiGuh9XfgQ", ExportName = "sceErrorDialogUpdateStatus", LibraryName = "libSceErrorDialog", Target = Generation.Gen5)]
    public static int ErrorDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests() => State.Reset();
}
