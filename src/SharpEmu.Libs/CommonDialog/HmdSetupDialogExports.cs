// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class HmdSetupDialogExports
{
    private const int ResultSize = 36;
    private static readonly ImmediateDialogState State = new();

    [SysAbiExport(Nid = "nmHzU4Gh0xs", ExportName = "sceHmdSetupDialogClose", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "6lVRHMV5LY0", ExportName = "sceHmdSetupDialogGetResult", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (!DialogMemory.TryClear(ctx, resultAddress, ResultSize) || !ctx.TryWriteUInt32(resultAddress, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "J9eBpW1udl4", ExportName = "sceHmdSetupDialogGetStatus", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "NB1Y2kA2jCY", ExportName = "sceHmdSetupDialogInitialize", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "NNgiV4T+akU", ExportName = "sceHmdSetupDialogOpen", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogOpen(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "+z4OJmFreZc", ExportName = "sceHmdSetupDialogTerminate", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogTerminate(CpuContext ctx) => ctx.SetReturn(State.Terminate());

    [SysAbiExport(Nid = "Ud7j3+RDIBg", ExportName = "sceHmdSetupDialogUpdateStatus", LibraryName = "libSceHmdSetupDialog", Target = Generation.Gen5)]
    public static int HmdSetupDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests() => State.Reset();
}
