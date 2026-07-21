// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class MsgDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorInvalidState = unchecked((int)0x80B80006);
    private const int ErrorBusy = unchecked((int)0x80B80008);
    private const int ErrorParamInvalid = unchecked((int)0x80B8000A);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    private const int ModeUserMessage = 1;
    private const int ModeProgressBar = 2;
    private const int ModeSystemMessage = 3;
    private const int ResultSize = 44;
    private const int ResultOk = 0;
    private const int ResultUserCanceled = 1;
    private const int ButtonIdInvalid = 0;
    private const int ButtonIdFirst = 1;
    private const int ButtonIdSecond = 2;

    private static int _status;
    private static int _mode;
    private static int _result;
    private static int _buttonId;

    internal static bool IsUsed => Volatile.Read(ref _status) != StatusNone;

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "ePw-kqZmelo",
        ExportName = "sceMsgDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _status, StatusNone) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        Volatile.Write(ref _mode, 0);
        Volatile.Write(ref _result, 0);
        Volatile.Write(ref _buttonId, 0);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "b06Hh0DPEaE",
        ExportName = "sceMsgDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        var status = Volatile.Read(ref _status);
        if (status == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        if (status == StatusRunning)
        {
            return ctx.SetReturn(ErrorBusy);
        }

        if (status is not (StatusInitialized or StatusFinished))
        {
            return ctx.SetReturn(ErrorInvalidState);
        }

        if (!ctx.TryReadInt32(paramAddress + 0x38, out var mode))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        int result;
        int buttonId;
        switch (mode)
        {
            case ModeUserMessage:
                if (!ctx.TryReadUInt64(paramAddress + 0x40, out var userParamAddress))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (userParamAddress == 0)
                {
                    return ctx.SetReturn(ErrorParamInvalid);
                }

                if (!ctx.TryReadInt32(userParamAddress, out var buttonType))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (!TrySelectImmediateResult(buttonType, out result, out buttonId))
                {
                    return ctx.SetReturn(ErrorParamInvalid);
                }

                break;
            case ModeProgressBar:
                result = ResultOk;
                buttonId = ButtonIdInvalid;
                break;
            case ModeSystemMessage:
                result = ResultOk;
                buttonId = ButtonIdFirst;
                break;
            default:
                return ctx.SetReturn(ErrorParamInvalid);
        }

        Volatile.Write(ref _mode, mode);
        Volatile.Write(ref _result, result);
        Volatile.Write(ref _buttonId, buttonId);
        Interlocked.Exchange(ref _status, StatusFinished);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "CWVW78Qc3fI",
        ExportName = "sceMsgDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetStatus(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "6fIC3XKt2k0",
        ExportName = "sceMsgDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "Lr8ovHH9l6A",
        ExportName = "sceMsgDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return ctx.SetReturn(ErrorNotFinished);
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result, Volatile.Read(ref _mode));
        BinaryPrimitives.WriteInt32LittleEndian(result[0x04..], Volatile.Read(ref _result));
        BinaryPrimitives.WriteInt32LittleEndian(result[0x08..], Volatile.Read(ref _buttonId));
        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "HTrcDKlFKuM",
        ExportName = "sceMsgDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogClose(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning) != StatusRunning)
        {
            return ctx.SetReturn(ErrorNotRunning);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "wTpfglkmv34",
        ExportName = "sceMsgDialogProgressBarSetValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetValue(CpuContext ctx) => ProgressBarNoOp(ctx);

    [SysAbiExport(
        Nid = "Gc5k1qcK4fs",
        ExportName = "sceMsgDialogProgressBarInc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarInc(CpuContext ctx) => ProgressBarNoOp(ctx);

    [SysAbiExport(
        Nid = "6H-71OdrpXM",
        ExportName = "sceMsgDialogProgressBarSetMsg",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetMsg(CpuContext ctx) => ProgressBarNoOp(ctx);

    internal static bool TrySelectImmediateResult(int buttonType, out int result, out int buttonId)
    {
        result = ResultOk;
        buttonId = ButtonIdFirst;
        switch (buttonType)
        {
            case 0:
            case 1:
            case 3:
            case 5:
            case 6:
            case 9:
                return true;
            case 2:
                buttonId = ButtonIdInvalid;
                return true;
            case 7:
                buttonId = ButtonIdSecond;
                return true;
            case 8:
                result = ResultUserCanceled;
                buttonId = ButtonIdInvalid;
                return true;
            default:
                result = 0;
                buttonId = 0;
                return false;
        }
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _status, StatusNone);
        Volatile.Write(ref _mode, 0);
        Volatile.Write(ref _result, 0);
        Volatile.Write(ref _buttonId, 0);
    }

    private static int ProgressBarNoOp(CpuContext ctx) =>
        ctx.SetReturn(Volatile.Read(ref _status) == StatusNone ? ErrorNotInitialized : ErrorOk);
}
