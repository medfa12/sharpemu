// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpCommerceExports
{
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorParameterInvalid = unchecked((int)0x80B8000A);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorAlreadyClose = unchecked((int)0x80B8000C);
    private const int ErrorArgumentNull = unchecked((int)0x80B8000D);
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;
    private const int ResultUserCanceled = 1;

    private static int _status;
    private static int _result = ResultUserCanceled;
    private static int _mode;
    private static ulong _userData;
    private static int _iconVisible;
    private static int _iconLayout;
    private static int _iconPosition;

    [SysAbiExport(Nid = "0aR2aWmQal4", ExportName = "sceNpCommerceDialogInitialize", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogInitialize(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone) != StatusNone)
        {
            return ctx.SetReturn(ErrorAlreadyInitialized);
        }
        _result = ResultUserCanceled;
        _mode = 0;
        _userData = 0;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "9ZiLXAGG5rg", ExportName = "sceNpCommerceDialogInitializeInternal", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogInitializeInternal(CpuContext ctx) => NpCommerceDialogInitialize(ctx);

    [SysAbiExport(Nid = "m-I92Ab50W8", ExportName = "sceNpCommerceDialogTerminate", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _status, StatusNone) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "DfSCDRA3EjY", ExportName = "sceNpCommerceDialogOpen", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogOpen(CpuContext ctx)
    {
        if (Volatile.Read(ref _status) is not (StatusInitialized or StatusFinished))
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentNull);
        }
        if (!ctx.TryReadInt32(parameterAddress + 0x38, out var mode) || mode is < 0 or > 5)
        {
            Interlocked.Exchange(ref _status, StatusFinished);
            _result = ErrorParameterInvalid;
            return ctx.SetReturn(ErrorParameterInvalid);
        }
        if (!ctx.TryReadUInt64(parameterAddress + 0x58, out var userData))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        _mode = mode;
        _userData = userData;
        _result = ResultUserCanceled;
        Interlocked.Exchange(ref _status, StatusRunning);
        Interlocked.Exchange(ref _status, StatusFinished);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "NU3ckGHMFXo", ExportName = "sceNpCommerceDialogClose", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogClose(CpuContext ctx)
    {
        var status = Volatile.Read(ref _status);
        if (status == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }
        if (status == StatusInitialized)
        {
            return ctx.SetReturn(ErrorNotRunning);
        }
        if (status == StatusFinished)
        {
            return ctx.SetReturn(ErrorAlreadyClose);
        }
        Interlocked.Exchange(ref _status, StatusFinished);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "CCbC+lqqvF0", ExportName = "sceNpCommerceDialogGetStatus", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogGetStatus(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _status));

    [SysAbiExport(Nid = "LR5cwFMMCVE", ExportName = "sceNpCommerceDialogUpdateStatus", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _status));

    [SysAbiExport(Nid = "r42bWcQbtZY", ExportName = "sceNpCommerceDialogGetResult", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentNull);
        }
        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return ctx.SetReturn(ErrorNotFinished);
        }
        Span<byte> result = stackalloc byte[0x30];
        result.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result, _result);
        result[4] = (byte)(_mode == 5 && _result == 2 ? 1 : 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(result[8..], _userData);
        return ctx.Memory.TryWrite(resultAddress, result)
            ? ctx.SetReturn(_result)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "dsqCVsNM0Zg", ExportName = "sceNpCommerceHidePsStoreIcon", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceHidePsStoreIcon(CpuContext ctx)
    {
        Interlocked.Exchange(ref _iconVisible, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "uKTDW8hk-ts", ExportName = "sceNpCommerceSetPsStoreIconLayout", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceSetPsStoreIconLayout(CpuContext ctx)
    {
        var layout = unchecked((int)ctx[CpuRegister.Rdi]);
        if (layout is >= 0 and <= 2)
        {
            Interlocked.Exchange(ref _iconLayout, layout);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "DHmwsa6S8Tc", ExportName = "sceNpCommerceShowPsStoreIcon", Target = Generation.Gen5, LibraryName = "libSceNpCommerce")]
    public static int NpCommerceShowPsStoreIcon(CpuContext ctx)
    {
        var position = unchecked((int)ctx[CpuRegister.Rdi]);
        Interlocked.Exchange(ref _iconPosition, position is >= 0 and <= 2 ? position : 1);
        Interlocked.Exchange(ref _iconVisible, 1);
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _status, StatusNone);
        Interlocked.Exchange(ref _iconVisible, 0);
        _result = ResultUserCanceled;
        _mode = 0;
        _userData = 0;
        _iconLayout = 0;
        _iconPosition = 0;
    }
}
