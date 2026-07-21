// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.SystemGesture;

public static class SystemGestureExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80890001);
    private const int ErrorEventDataNotFound = unchecked((int)0x80890005);
    private const int ErrorInvalidHandle = unchecked((int)0x80890006);
    private const int GestureHandle = 1;
    private const int TouchRecognizerSize = 361 * sizeof(ulong);
    private const int PrimitiveTouchEventSize = 80;
    private const int TouchEventSize = 168;
    private const int TouchRecognizerInformationSize = 296;

    [SysAbiExport(
        Nid = "3pcAvmwKCvM",
        ExportName = "sceSystemGestureInitializePrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureInitializePrimitiveTouchRecognizer(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "3QYCmMlOlCY",
        ExportName = "sceSystemGestureFinalizePrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureFinalizePrimitiveTouchRecognizer(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "qpo-mEOwje0",
        ExportName = "sceSystemGestureOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureOpen(CpuContext ctx)
    {
        var inputType = unchecked((int)(uint)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(inputType == 0 ? GestureHandle : ErrorInvalidArgument);
    }

    [SysAbiExport(
        Nid = "j4yXIA2jJ68",
        ExportName = "sceSystemGestureClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureClose(CpuContext ctx) => ReturnForHandle(ctx);

    [SysAbiExport(
        Nid = "o11J529VaAE",
        ExportName = "sceSystemGestureResetPrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureResetPrimitiveTouchRecognizer(CpuContext ctx) => ReturnForHandle(ctx);

    [SysAbiExport(
        Nid = "GgFMb22sbbI",
        ExportName = "sceSystemGestureUpdatePrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdatePrimitiveTouchRecognizer(CpuContext ctx) => ReturnForHandle(ctx);

    [SysAbiExport(
        Nid = "L8YmemOeSNY",
        ExportName = "sceSystemGestureGetPrimitiveTouchEvents",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetPrimitiveTouchEvents(CpuContext ctx) =>
        WriteEmptyEventList(ctx, recognizerRequired: false, PrimitiveTouchEventSize);

    [SysAbiExport(
        Nid = "JhwByySf9FY",
        ExportName = "sceSystemGestureGetPrimitiveTouchEventsCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetPrimitiveTouchEventsCount(CpuContext ctx) => ReturnForHandle(ctx);

    [SysAbiExport(
        Nid = "KAeP0+cQPVU",
        ExportName = "sceSystemGestureGetPrimitiveTouchEventByIndex",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetPrimitiveTouchEventByIndex(CpuContext ctx) =>
        ReturnMissingEvent(ctx, ctx[CpuRegister.Rdx], PrimitiveTouchEventSize, recognizerRequired: false);

    [SysAbiExport(
        Nid = "yBaQ0h9m1NM",
        ExportName = "sceSystemGestureGetPrimitiveTouchEventByPrimitiveID",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetPrimitiveTouchEventByPrimitiveId(CpuContext ctx) =>
        ReturnMissingEvent(ctx, ctx[CpuRegister.Rdx], PrimitiveTouchEventSize, recognizerRequired: false);

    [SysAbiExport(
        Nid = "FWF8zkhr854",
        ExportName = "sceSystemGestureCreateTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureCreateTouchRecognizer(CpuContext ctx)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        var recognizerAddress = ctx[CpuRegister.Rsi];
        if (recognizerAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        return WriteZeroed(ctx, recognizerAddress, TouchRecognizerSize);
    }

    [SysAbiExport(
        Nid = "1MMK0W-kMgA",
        ExportName = "sceSystemGestureAppendTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureAppendTouchRecognizer(CpuContext ctx) => ValidateRecognizer(ctx);

    [SysAbiExport(
        Nid = "ELvBVG-LKT0",
        ExportName = "sceSystemGestureRemoveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureRemoveTouchRecognizer(CpuContext ctx) => ValidateRecognizer(ctx);

    [SysAbiExport(
        Nid = "oBuH3zFWYIg",
        ExportName = "sceSystemGestureResetTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureResetTouchRecognizer(CpuContext ctx)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        var recognizerAddress = ctx[CpuRegister.Rsi];
        return recognizerAddress == 0
            ? ctx.SetReturn(ErrorInvalidArgument)
            : WriteZeroed(ctx, recognizerAddress, TouchRecognizerSize);
    }

    [SysAbiExport(
        Nid = "0KrW5eMnrwY",
        ExportName = "sceSystemGestureGetTouchRecognizerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchRecognizerInformation(CpuContext ctx)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        return WriteZeroed(ctx, ctx[CpuRegister.Rdx], TouchRecognizerInformationSize);
    }

    [SysAbiExport(
        Nid = "j4h82CQWENo",
        ExportName = "sceSystemGestureUpdateTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdateTouchRecognizer(CpuContext ctx) => ValidateRecognizer(ctx);

    [SysAbiExport(
        Nid = "wPJGwI2RM2I",
        ExportName = "sceSystemGestureUpdateAllTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdateAllTouchRecognizer(CpuContext ctx) => ReturnForHandle(ctx);

    [SysAbiExport(
        Nid = "4WOA1eTx3V8",
        ExportName = "sceSystemGestureUpdateTouchRecognizerRectangle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdateTouchRecognizerRectangle(CpuContext ctx)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        return ctx.SetReturn(ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0 ? 0 : ErrorInvalidArgument);
    }

    [SysAbiExport(
        Nid = "fLTseA7XiWY",
        ExportName = "sceSystemGestureGetTouchEvents",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchEvents(CpuContext ctx) =>
        WriteEmptyEventList(ctx, recognizerRequired: true, TouchEventSize);

    [SysAbiExport(
        Nid = "h8uongcBNVs",
        ExportName = "sceSystemGestureGetTouchEventsCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchEventsCount(CpuContext ctx) => ValidateRecognizer(ctx);

    [SysAbiExport(
        Nid = "TSKvgSz5ChU",
        ExportName = "sceSystemGestureGetTouchEventByIndex",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchEventByIndex(CpuContext ctx) =>
        ReturnMissingEvent(ctx, ctx[CpuRegister.Rcx], TouchEventSize, recognizerRequired: true);

    [SysAbiExport(
        Nid = "lpsXm7tzeoc",
        ExportName = "sceSystemGestureGetTouchEventByEventID",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchEventByEventId(CpuContext ctx) =>
        ReturnMissingEvent(ctx, ctx[CpuRegister.Rcx], TouchEventSize, recognizerRequired: true);

    private static int WriteEmptyEventList(CpuContext ctx, bool recognizerRequired, int eventSize)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        var argumentOffset = recognizerRequired ? 1 : 0;
        if (recognizerRequired && ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        var eventBufferAddress = argumentOffset == 0 ? ctx[CpuRegister.Rsi] : ctx[CpuRegister.Rdx];
        var capacity = argumentOffset == 0 ? (uint)ctx[CpuRegister.Rdx] : (uint)ctx[CpuRegister.Rcx];
        var countAddress = argumentOffset == 0 ? ctx[CpuRegister.Rcx] : ctx[CpuRegister.R8];
        if (countAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (!ctx.TryWriteUInt32(countAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (eventBufferAddress != 0 && capacity != 0)
        {
            return WriteZeroed(ctx, eventBufferAddress, eventSize);
        }

        return ctx.SetReturn(0);
    }

    private static int ReturnMissingEvent(CpuContext ctx, ulong eventAddress, int eventSize, bool recognizerRequired)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (recognizerRequired && ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (eventAddress != 0)
        {
            var writeResult = WriteZeroed(ctx, eventAddress, eventSize);
            if (writeResult != 0)
            {
                return writeResult;
            }
        }

        return ctx.SetReturn(ErrorEventDataNotFound);
    }

    private static int ValidateRecognizer(CpuContext ctx)
    {
        if (!HasValidHandle(ctx))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        return ctx.SetReturn(ctx[CpuRegister.Rsi] != 0 ? 0 : ErrorInvalidArgument);
    }

    private static int ReturnForHandle(CpuContext ctx) =>
        ctx.SetReturn(HasValidHandle(ctx) ? 0 : ErrorInvalidHandle);

    private static bool HasValidHandle(CpuContext ctx) =>
        unchecked((int)(uint)ctx[CpuRegister.Rdi]) == GestureHandle;

    private static int WriteZeroed(CpuContext ctx, ulong address, int size)
    {
        var bytes = new byte[size];
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
