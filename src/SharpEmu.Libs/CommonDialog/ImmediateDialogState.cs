// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

internal sealed class ImmediateDialogState(
    int errorNotInitialized = ImmediateDialogState.ErrorNotInitialized,
    int errorAlreadyInitialized = ImmediateDialogState.ErrorAlreadyInitialized,
    int errorInvalidState = ImmediateDialogState.ErrorInvalidState,
    int errorParamInvalid = ImmediateDialogState.ErrorArgNull)
{
    internal const int StatusNone = 0;
    internal const int StatusInitialized = 1;
    internal const int StatusRunning = 2;
    internal const int StatusFinished = 3;

    internal const int ErrorOk = 0;
    internal const int ErrorNotInitialized = unchecked((int)0x80B80003);
    internal const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    internal const int ErrorNotFinished = unchecked((int)0x80B80005);
    internal const int ErrorInvalidState = unchecked((int)0x80B80006);
    internal const int ErrorParamInvalid = unchecked((int)0x80B8000A);
    internal const int ErrorNotRunning = unchecked((int)0x80B8000B);
    internal const int ErrorArgNull = unchecked((int)0x80B8000D);

    private int _status;

    internal int Status => Volatile.Read(ref _status);

    internal int Initialize()
    {
        return Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone) == StatusNone
            ? ErrorOk
            : errorAlreadyInitialized;
    }

    internal int Open(ulong parameterAddress, bool parameterRequired = true)
    {
        var status = Status;
        if (status is not (StatusInitialized or StatusFinished))
        {
            return errorInvalidState;
        }

        if (parameterRequired && parameterAddress == 0)
        {
            return errorParamInvalid;
        }

        Interlocked.Exchange(ref _status, StatusFinished);
        return ErrorOk;
    }

    internal int Close()
    {
        if (Status == StatusNone)
        {
            return errorNotInitialized;
        }

        Interlocked.Exchange(ref _status, StatusFinished);
        return ErrorOk;
    }

    internal int Terminate()
    {
        return Interlocked.Exchange(ref _status, StatusNone) == StatusNone
            ? errorNotInitialized
            : ErrorOk;
    }

    internal int ValidateResult(ulong resultAddress)
    {
        if (Status == StatusNone)
        {
            return errorNotInitialized;
        }

        if (Status != StatusFinished)
        {
            return ErrorNotFinished;
        }

        return resultAddress == 0 ? errorParamInvalid : ErrorOk;
    }

    internal void Reset() => Interlocked.Exchange(ref _status, StatusNone);
}

internal static class DialogMemory
{
    internal static bool TryClear(CpuContext ctx, ulong address, ulong size)
    {
        if (size == 0)
        {
            return true;
        }

        if (address == 0 || address > ulong.MaxValue - size)
        {
            return false;
        }

        Span<byte> zeros = stackalloc byte[256];
        while (size != 0)
        {
            var count = (int)Math.Min((ulong)zeros.Length, size);
            if (!ctx.Memory.TryWrite(address, zeros[..count]))
            {
                return false;
            }

            address += (uint)count;
            size -= (uint)count;
        }

        return true;
    }

    internal static int Finish(CpuContext ctx, bool success) =>
        ctx.SetReturn(success ? ImmediateDialogState.ErrorOk : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
