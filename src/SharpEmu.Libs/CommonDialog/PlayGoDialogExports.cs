// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class PlayGoDialogExports
{
    private const int ResultSize = 0x28;
    private static readonly ImmediateDialogState State = new();

    [SysAbiExport(Nid = "fbigNQiZpm0", ExportName = "scePlayGoDialogClose", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "wx9TDplJKB4", ExportName = "scePlayGoDialogGetResult", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (!DialogMemory.TryClear(ctx, resultAddress, ResultSize) || !ctx.TryWriteUInt32(resultAddress + 4, 3))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "NOAMxY2EGS0", ExportName = "scePlayGoDialogGetStatus", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "fECamTJKpsM", ExportName = "scePlayGoDialogInitialize", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "kHd72ukqbxw", ExportName = "scePlayGoDialogOpen", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogOpen(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "okgIGdr5Iz0", ExportName = "scePlayGoDialogTerminate", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogTerminate(CpuContext ctx) => ctx.SetReturn(State.Terminate());

    [SysAbiExport(Nid = "Yb60K7BST48", ExportName = "scePlayGoDialogUpdateStatus", LibraryName = "libScePlayGoDialog", Target = Generation.Gen5)]
    public static int PlayGoDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests() => State.Reset();
}
