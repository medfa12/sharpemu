// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class NpProfileDialogExports
{
    private const int ResultSize = 48;
    private static readonly ImmediateDialogState State = new();
    private static ulong _userData;

    [SysAbiExport(Nid = "wkwjz0Xdo2A", ExportName = "sceNpProfileDialogClose", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "8rhLl1-0W-o", ExportName = "sceNpProfileDialogGetResult", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (!DialogMemory.TryClear(ctx, resultAddress, ResultSize) || !ctx.TryWriteUInt64(resultAddress + 8, _userData))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "3BqoiFOjSsk", ExportName = "sceNpProfileDialogGetStatus", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "Lg+NCE6pTwQ", ExportName = "sceNpProfileDialogInitialize", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "nrQRlLKzdwE", ExportName = "sceNpProfileDialogOpenA", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogOpenA(CpuContext ctx) => Open(ctx, 0x50);

    [SysAbiExport(Nid = "0Sp9vJcB1-w", ExportName = "sceNpProfileDialogTerminate", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogTerminate(CpuContext ctx)
    {
        _userData = 0;
        return ctx.SetReturn(State.Terminate());
    }

    [SysAbiExport(Nid = "haVZE9FgKqE", ExportName = "sceNpProfileDialogUpdateStatus", LibraryName = "libSceNpProfileDialog", Target = Generation.Gen5)]
    public static int NpProfileDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static int OpenCompat(CpuContext ctx) => Open(ctx, 0x58);

    internal static void ResetForTests()
    {
        _userData = 0;
        State.Reset();
    }

    private static int Open(CpuContext ctx, ulong userDataOffset)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var result = State.Open(paramAddress);
        if (result == 0)
        {
            _userData = ctx.TryReadUInt64(paramAddress + userDataOffset, out var userData) ? userData : 0;
        }

        return ctx.SetReturn(result);
    }
}
