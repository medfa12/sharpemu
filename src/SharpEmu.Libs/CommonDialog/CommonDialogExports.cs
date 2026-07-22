// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class CommonDialogExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "uoUpLGNkygk",
        ExportName = "sceCommonDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "BQ3tey0JmQM",
        ExportName = "sceCommonDialogIsUsed",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogIsUsed(CpuContext ctx)
    {
        return ctx.SetReturn(MsgDialogExports.IsUsed ? 1 : 0);
    }

    [SysAbiExport(Nid = "2RdicdHhtGA", ExportName = "_ZN3sce16CommonDialogUtil12getSelfAppIdEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilGetSelfAppId(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "I+tdxsCap08", ExportName = "_ZN3sce16CommonDialogUtil6Client11closeModuleEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientCloseModule(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "v4+gzuTkv6k", ExportName = "_ZN3sce16CommonDialogUtil6Client11updateStateEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientUpdateState(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "CwCzG0nnLg8", ExportName = "_ZN3sce16CommonDialogUtil6Client15launchCmnDialogEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientLaunchCmnDialog(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "Ib1SMmbr07k", ExportName = "_ZN3sce16CommonDialogUtil6ClientD0Ev", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientDeletingDestructor(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "6TIMpGvsrC4", ExportName = "_ZN3sce16CommonDialogUtil6ClientD1Ev", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientCompleteDestructor(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "+UyKxWAnqIU", ExportName = "_ZN3sce16CommonDialogUtil6ClientD2Ev", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientBaseDestructor(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "bUCx72-9f0g", ExportName = "_ZNK3sce16CommonDialogUtil6Client10isCloseReqEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientIsCloseRequested(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "xZtXq554Lbg", ExportName = "_ZNK3sce16CommonDialogUtil6Client13getFinishDataEPvm", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientGetFinishData(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        var outputSize = ctx[CpuRegister.Rdx];
        if (outputSize > 0x10_0000)
        {
            return ctx.SetReturn(ImmediateDialogState.ErrorParamInvalid);
        }

        return DialogMemory.Finish(ctx, outputSize == 0 || DialogMemory.TryClear(ctx, outputAddress, outputSize));
    }

    [SysAbiExport(Nid = "C-EZ3PkhibQ", ExportName = "_ZNK3sce16CommonDialogUtil6Client14getClientStateEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientGetClientState(CpuContext ctx) => ctx.SetReturn(ImmediateDialogState.StatusFinished);

    [SysAbiExport(Nid = "70niEKUAnZ0", ExportName = "_ZNK3sce16CommonDialogUtil6Client19isInitializedStatusEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientIsInitializedStatus(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _initialized));

    [SysAbiExport(Nid = "mdJgdwoM0Mo", ExportName = "_ZNK3sce16CommonDialogUtil6Client8getAppIdEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientGetAppId(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "87GekE1nowg", ExportName = "_ZNK3sce16CommonDialogUtil6Client8isFinishEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientIsFinish(CpuContext ctx) => ctx.SetReturn(1);

    [SysAbiExport(Nid = "6ljeTSi+fjs", ExportName = "_ZNK3sce16CommonDialogUtil6Client9getResultEv", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientGetResult(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "W2MzrWix2mM", ExportName = "_ZTVN3sce16CommonDialogUtil6ClientE", LibraryName = "libSceCommonDialog", Target = Generation.Gen4 | Generation.Gen5)]
    public static int CommonDialogUtilClientVtable(CpuContext ctx) => ctx.SetReturn(0);

    internal static void ResetForTests() => Interlocked.Exchange(ref _initialized, 0);
}
