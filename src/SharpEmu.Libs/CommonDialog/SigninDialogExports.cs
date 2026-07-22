// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class SigninDialogExports
{
    private const int ErrorNotInitialized = unchecked((int)0x81350001);
    private const int ErrorAlreadyInitialized = unchecked((int)0x81350002);
    private const int ErrorParamInvalid = unchecked((int)0x81350003);
    private const int ErrorInvalidState = unchecked((int)0x81350005);
    private const int ResultSize = 16;

    private static readonly ImmediateDialogState State = new(
        ErrorNotInitialized,
        ErrorAlreadyInitialized,
        ErrorInvalidState,
        ErrorParamInvalid);

    [SysAbiExport(Nid = "M3OkENHcyiU", ExportName = "sceSigninDialogClose", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "nqG7rqnYw1U", ExportName = "sceSigninDialogGetResult", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation == ImmediateDialogState.ErrorNotFinished)
        {
            validation = ErrorInvalidState;
        }

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

    [SysAbiExport(Nid = "2m077aeC+PA", ExportName = "sceSigninDialogGetStatus", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "mlYGfmqE3fQ", ExportName = "sceSigninDialogInitialize", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "JlpJVoRWv7U", ExportName = "sceSigninDialogOpen", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var result = State.Open(paramAddress);
        if (result == 0 && (!ctx.TryReadUInt32(paramAddress, out var size) || size != 16))
        {
            State.Reset();
            State.Initialize();
            result = ErrorParamInvalid;
        }

        return ctx.SetReturn(result);
    }

    [SysAbiExport(Nid = "LXlmS6PvJdU", ExportName = "sceSigninDialogTerminate", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogTerminate(CpuContext ctx) => ctx.SetReturn(State.Terminate());

    [SysAbiExport(Nid = "Bw31liTFT3A", ExportName = "sceSigninDialogUpdateStatus", LibraryName = "libSceSigninDialog", Target = Generation.Gen5)]
    public static int SigninDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests() => State.Reset();
}
