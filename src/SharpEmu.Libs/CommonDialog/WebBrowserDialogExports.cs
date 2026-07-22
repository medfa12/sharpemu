// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class WebBrowserDialogExports
{
    private const int ResultSize = 48;
    private static readonly ImmediateDialogState State = new();

    [SysAbiExport(Nid = "PSK+Eik919Q", ExportName = "sceWebBrowserDialogClose", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "Wit4LjeoeX4", ExportName = "sceWebBrowserDialogGetEvent", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogGetEvent(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "vCaW0fgVQmc", ExportName = "sceWebBrowserDialogGetResult", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (!DialogMemory.TryClear(ctx, resultAddress, ResultSize) ||
            !ctx.TryWriteUInt32(resultAddress + 4, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "CFTG6a8TjOU", ExportName = "sceWebBrowserDialogGetStatus", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "jqb7HntFQFc", ExportName = "sceWebBrowserDialogInitialize", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "uYELOMVnmNQ", ExportName = "sceWebBrowserDialogNavigate", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogNavigate(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "FraP7debcdg", ExportName = "sceWebBrowserDialogOpen", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogOpen(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "O7dIZQrwVFY", ExportName = "sceWebBrowserDialogOpenForPredeterminedContent", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogOpenForPredeterminedContent(CpuContext ctx) => ctx.SetReturn(State.Open(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "Cya+jvTtPqg", ExportName = "sceWebBrowserDialogResetCookie", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogResetCookie(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "TZnDVkP91Rg", ExportName = "sceWebBrowserDialogSetCookie", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogSetCookie(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "RLhKBOoNyXY", ExportName = "sceWebBrowserDialogSetZoom", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogSetZoom(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "ocHtyBwHfys", ExportName = "sceWebBrowserDialogTerminate", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogTerminate(CpuContext ctx) => ctx.SetReturn(State.Terminate());

    [SysAbiExport(Nid = "h1dR-t5ISgg", ExportName = "sceWebBrowserDialogUpdateStatus", LibraryName = "libSceWebBrowserDialog", Target = Generation.Gen5)]
    public static int WebBrowserDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests() => State.Reset();
}
