// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeDialogExports
{
    private const int ErrorBusy = unchecked((int)0x80BC0001);
    private const int ErrorInvalidType = unchecked((int)0x80BC0011);
    private const int ErrorInvalidMaxTextLength = unchecked((int)0x80BC0016);
    private const int ErrorInvalidInputTextBuffer = unchecked((int)0x80BC0017);
    private const int ErrorInvalidAddress = unchecked((int)0x80BC0031);
    private const int ErrorInternal = unchecked((int)0x80BC00FF);
    private const int ErrorDialogNotRunning = unchecked((int)0x80BC0105);
    private const int ErrorDialogNotFinished = unchecked((int)0x80BC0106);
    private const int ErrorDialogNotInUse = unchecked((int)0x80BC0107);
    private const int ErrorImeSuspending = unchecked((int)0x80BC0009);

    private const int StatusNone = 0;
    private const int StatusRunning = 1;
    private const int StatusFinished = 2;
    private const uint EndStatusAborted = 2;
    private const int PositionAndFormSize = 0x1C;
    private const int ResultSize = 0x10;

    private static readonly object DialogGate = new();
    private static int _status;
    private static uint _endStatus;
    private static uint _type;
    private static uint _option;
    private static uint _positionXBits;
    private static uint _positionYBits;
    private static uint _horizontalAlignment;
    private static uint _verticalAlignment;

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);

    private static bool TryClear(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return false;
        }

        Span<byte> data = stackalloc byte[size];
        data.Clear();
        return ctx.Memory.TryWrite(address, data);
    }

    private static int Initialize(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        lock (DialogGate)
        {
            if (_status != StatusNone)
            {
                return ctx.SetReturn(ErrorBusy);
            }

            if (paramAddress == 0 ||
                !ctx.TryReadUInt32(paramAddress + 0x04, out var type) ||
                !ctx.TryReadUInt32(paramAddress + 0x20, out var option) ||
                !ctx.TryReadUInt32(paramAddress + 0x24, out var maxTextLength) ||
                !ctx.TryReadUInt64(paramAddress + 0x28, out var inputTextBuffer) ||
                !ctx.TryReadUInt32(paramAddress + 0x30, out var xBits) ||
                !ctx.TryReadUInt32(paramAddress + 0x34, out var yBits) ||
                !ctx.TryReadUInt32(paramAddress + 0x38, out var horizontal) ||
                !ctx.TryReadUInt32(paramAddress + 0x3C, out var vertical))
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            if (type > 4)
            {
                return ctx.SetReturn(ErrorInvalidType);
            }

            if (maxTextLength == 0 || maxTextLength > 2048)
            {
                return ctx.SetReturn(ErrorInvalidMaxTextLength);
            }

            if (inputTextBuffer == 0)
            {
                return ctx.SetReturn(ErrorInvalidInputTextBuffer);
            }

            _type = type;
            _option = option;
            _positionXBits = xBits;
            _positionYBits = yBits;
            _horizontalAlignment = horizontal;
            _verticalAlignment = vertical;
            _endStatus = 0;
            _status = StatusRunning;
        }

        return Ok(ctx);
    }

    private static int WritePanelSize(CpuContext ctx, bool extended)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var widthAddress = extended ? ctx[CpuRegister.Rdx] : ctx[CpuRegister.Rsi];
        var heightAddress = extended ? ctx[CpuRegister.Rcx] : ctx[CpuRegister.Rdx];
        if (paramAddress == 0 || widthAddress == 0 || heightAddress == 0 ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out var type) ||
            !ctx.TryReadUInt32(paramAddress + 0x20, out var option))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        if (type > 4)
        {
            return ctx.SetReturn(ErrorInvalidType);
        }

        uint width;
        uint height;
        if (type == 4)
        {
            width = 0x172;
            height = 0x20A;
        }
        else
        {
            width = 0x319;
            height = (option & 1) != 0 ? 0x274U : 0x210U;
        }

        if ((option & 0x4000) != 0)
        {
            width <<= 1;
            height <<= 1;
        }

        return ctx.TryWriteUInt32(widthAddress, width) && ctx.TryWriteUInt32(heightAddress, height)
            ? Ok(ctx)
            : ctx.SetReturn(ErrorInvalidAddress);
    }

    [SysAbiExport(Nid = "oBmw4xrmfKs", ExportName = "sceImeDialogAbort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogAbort(CpuContext ctx)
    {
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            if (_status != StatusRunning)
            {
                return ctx.SetReturn(ErrorDialogNotRunning);
            }

            _status = StatusFinished;
            _endStatus = EndStatusAborted;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "UFcyYDf+e88", ExportName = "sceImeDialogForTestFunction", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogForTestFunction(CpuContext ctx) => ctx.SetReturn(ErrorInternal);

    [SysAbiExport(Nid = "bX4H+sxPI-o", ExportName = "sceImeDialogForceClose", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogForceClose(CpuContext ctx)
    {
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            ResetState();
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "fy6ntM25pEc", ExportName = "sceImeDialogGetCurrentStarState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetCurrentStarState(CpuContext ctx)
    {
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            if (ctx[CpuRegister.Rdi] == 0)
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            return ctx.SetReturn(ErrorImeSuspending);
        }
    }

    [SysAbiExport(Nid = "8jqzzPioYl8", ExportName = "sceImeDialogGetPanelPositionAndForm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetPanelPositionAndForm(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        uint x;
        uint y;
        uint horizontal;
        uint vertical;
        uint type;
        uint option;
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            x = _positionXBits;
            y = _positionYBits;
            horizontal = _horizontalAlignment;
            vertical = _verticalAlignment;
            type = _type;
            option = _option;
        }

        if (address == 0 || !TryClear(ctx, address, PositionAndFormSize))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        uint width = type == 4 ? 0x172U : 0x319U;
        uint height = type == 4 ? 0x20AU : (option & 1) != 0 ? 0x274U : 0x210U;
        if (!ctx.TryWriteUInt32(address, 2) ||
            !ctx.TryWriteUInt32(address + 0x04, x) ||
            !ctx.TryWriteUInt32(address + 0x08, y) ||
            !ctx.TryWriteUInt32(address + 0x0C, horizontal) ||
            !ctx.TryWriteUInt32(address + 0x10, vertical) ||
            !ctx.TryWriteUInt32(address + 0x14, width) ||
            !ctx.TryWriteUInt32(address + 0x18, height))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "wqsJvRXwl58", ExportName = "sceImeDialogGetPanelSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetPanelSize(CpuContext ctx) => WritePanelSize(ctx, false);

    [SysAbiExport(Nid = "CRD+jSErEJQ", ExportName = "sceImeDialogGetPanelSizeExtended", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetPanelSizeExtended(CpuContext ctx) => WritePanelSize(ctx, true);

    [SysAbiExport(Nid = "x01jxu+vxlc", ExportName = "sceImeDialogGetResult", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        uint endStatus;
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            if (resultAddress == 0)
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            if (_status != StatusFinished)
            {
                return ctx.SetReturn(ErrorDialogNotFinished);
            }

            endStatus = _endStatus;
        }

        if (!TryClear(ctx, resultAddress, ResultSize) || !ctx.TryWriteUInt32(resultAddress, endStatus))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "IADmD4tScBY", ExportName = "sceImeDialogGetStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetStatus(CpuContext ctx)
    {
        lock (DialogGate)
        {
            return ctx.SetReturn(_status);
        }
    }

    [SysAbiExport(Nid = "NUeBrN7hzf0", ExportName = "sceImeDialogInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogInit(CpuContext ctx) => Initialize(ctx);

    [SysAbiExport(Nid = "KR6QDasuKco", ExportName = "sceImeDialogInitInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogInitInternal(CpuContext ctx) => Initialize(ctx);

    [SysAbiExport(Nid = "oe92cnJQ9HE", ExportName = "sceImeDialogInitInternal2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogInitInternal2(CpuContext ctx) => Initialize(ctx);

    [SysAbiExport(Nid = "IoKIpNf9EK0", ExportName = "sceImeDialogInitInternal3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogInitInternal3(CpuContext ctx) => Initialize(ctx);

    [SysAbiExport(Nid = "-2WqB87KKGg", ExportName = "sceImeDialogSetPanelPosition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogSetPanelPosition(CpuContext ctx)
    {
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            return ctx.SetReturn(ErrorImeSuspending);
        }
    }

    [SysAbiExport(Nid = "gyTyVn+bXMw", ExportName = "sceImeDialogTerm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceImeDialog")]
    public static int ImeDialogTerm(CpuContext ctx)
    {
        lock (DialogGate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorDialogNotInUse);
            }

            if (_status != StatusFinished)
            {
                return ctx.SetReturn(ErrorDialogNotFinished);
            }

            ResetState();
        }

        return Ok(ctx);
    }

    private static void ResetState()
    {
        _status = StatusNone;
        _endStatus = 0;
        _type = 0;
        _option = 0;
        _positionXBits = 0;
        _positionYBits = 0;
        _horizontalAlignment = 0;
        _verticalAlignment = 0;
    }

    internal static void ResetForTests()
    {
        lock (DialogGate)
        {
            ResetState();
        }
    }
}
