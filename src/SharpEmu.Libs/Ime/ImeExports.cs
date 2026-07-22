// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

/// <summary>
/// HLE for the libSceIme hardware-keyboard surface. Titles open a keyboard
/// listener at startup and pump sceImeUpdate from their frame loop; with no
/// keyboard attached it is enough to validate the open parameters, remember
/// that a handler is registered, and report success from the update pump
/// without ever delivering an event.
///
/// The keyboard param block: option flags at +0x00, reserved at +0x04, user
/// argument at +0x08, event handler at +0x10, reserved through +0x20.
/// </summary>
public static class ImeExports
{
    private const int ErrorBusy = unchecked((int)0x80BC0001);
    private const int ErrorNotOpened = unchecked((int)0x80BC0002);
    private const int ErrorInvalidHandler = unchecked((int)0x80BC0022);
    private const int ErrorInvalidType = unchecked((int)0x80BC0011);
    private const int ErrorInvalidMaxTextLength = unchecked((int)0x80BC0016);
    private const int ErrorInvalidInputTextBuffer = unchecked((int)0x80BC0017);
    private const int ErrorInvalidWork = unchecked((int)0x80BC0020);
    private const int ErrorInvalidParam = unchecked((int)0x80BC0030);
    private const int ErrorInvalidAddress = unchecked((int)0x80BC0031);

    private const ulong HandlerOffset = 0x10;

    private static long _keyboardHandler;
    private static readonly object ImeGate = new();
    private static int _imeModuleInitialized;
    private static bool _imeOpen;
    private static ulong _imeInputTextBuffer;
    private static uint _imeMaxTextLength;
    private static uint _imeTextLength;
    private static uint _imePositionXBits;
    private static uint _imePositionYBits;
    private static uint _imeHorizontalAlignment;
    private static uint _imeVerticalAlignment;

    private const int ImeParamSize = 0x60;
    private const int PositionAndFormSize = 0x1C;

    private static bool TryClear(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return false;
        }

        Span<byte> data = size <= 256 ? stackalloc byte[size] : new byte[size];
        data.Clear();
        return ctx.Memory.TryWrite(address, data);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);

    private static int WritePanelSize(CpuContext ctx, ulong paramAddress, ulong widthAddress, ulong heightAddress)
    {
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
            height = 0x192;
        }
        else
        {
            width = 0x319;
            height = 0x198;
        }

        if ((option & 0x4000) != 0)
        {
            width <<= 1;
            height <<= 1;
        }

        return ctx.TryWriteUInt32(widthAddress, width) && ctx.TryWriteUInt32(heightAddress, height)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ErrorInvalidAddress);
    }

    [SysAbiExport(Nid = "mN+ZoSN-8hQ", ExportName = "FinalizeImeModule", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int FinalizeImeModule(CpuContext ctx)
    {
        lock (ImeGate)
        {
            _imeModuleInitialized = 0;
            _imeOpen = false;
            _imeInputTextBuffer = 0;
            _imeMaxTextLength = 0;
            _imeTextLength = 0;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "uTW+63goeJs", ExportName = "InitializeImeModule", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int InitializeImeModule(CpuContext ctx)
    {
        Interlocked.Exchange(ref _imeModuleInitialized, 1);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Lf3DeGWC6xg", ExportName = "sceImeCheckFilterText", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeCheckFilterText(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "zHuMUGb-AQI", ExportName = "sceImeCheckRemoteEventParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeCheckRemoteEventParam(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "OTb0Mg+1i1k", ExportName = "sceImeCheckUpdateTextInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeCheckUpdateTextInfo(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "TmVP8LzcFcY", ExportName = "sceImeClose", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeClose(CpuContext ctx)
    {
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ErrorNotOpened);
            }

            _imeOpen = false;
            _imeInputTextBuffer = 0;
            _imeMaxTextLength = 0;
            _imeTextLength = 0;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Ho5NVQzpKHo", ExportName = "sceImeConfigGet", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeConfigGet(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "P5dPeiLwm-M", ExportName = "sceImeConfigSet", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeConfigSet(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "tKLmVIUkpyM", ExportName = "sceImeConfirmCandidate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeConfirmCandidate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "NYDsL9a0oEo", ExportName = "sceImeDicAddWord", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicAddWord(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "l01GKoyiQrY", ExportName = "sceImeDicDeleteLearnDics", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicDeleteLearnDics(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "E2OcGgi-FPY", ExportName = "sceImeDicDeleteUserDics", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicDeleteUserDics(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "JAiMBkOTYKI", ExportName = "sceImeDicDeleteWord", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicDeleteWord(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "JoPdCUXOzMU", ExportName = "sceImeDicGetWords", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicGetWords(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "FuEl46uHDyo", ExportName = "sceImeDicReplaceWord", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDicReplaceWord(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "E+f1n8e8DAw", ExportName = "sceImeDisableController", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeDisableController(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "evjOsE18yuI", ExportName = "sceImeFilterText", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeFilterText(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "wVkehxutK-U", ExportName = "sceImeForTestFunction", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeForTestFunction(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "T6FYjZXG93o", ExportName = "sceImeGetPanelPositionAndForm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeGetPanelPositionAndForm(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Ok(ctx);
        }

        uint x;
        uint y;
        uint horizontal;
        uint vertical;
        lock (ImeGate)
        {
            x = _imePositionXBits;
            y = _imePositionYBits;
            horizontal = _imeHorizontalAlignment;
            vertical = _imeVerticalAlignment;
        }

        if (!TryClear(ctx, address, PositionAndFormSize) ||
            !ctx.TryWriteUInt32(address, 1) ||
            !ctx.TryWriteUInt32(address + 0x04, x) ||
            !ctx.TryWriteUInt32(address + 0x08, y) ||
            !ctx.TryWriteUInt32(address + 0x0C, horizontal) ||
            !ctx.TryWriteUInt32(address + 0x10, vertical) ||
            !ctx.TryWriteUInt32(address + 0x14, 0x319) ||
            !ctx.TryWriteUInt32(address + 0x18, 0x198))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "ziPDcIjO0Vk", ExportName = "sceImeGetPanelSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeGetPanelSize(CpuContext ctx) =>
        WritePanelSize(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);

    [SysAbiExport(Nid = "VkqLPArfFdc", ExportName = "sceImeKeyboardGetInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeKeyboardGetInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        return infoAddress == 0 || !TryClear(ctx, infoAddress, 0x24)
            ? ctx.SetReturn(ErrorInvalidAddress)
            : Ok(ctx);
    }

    [SysAbiExport(Nid = "oYkJlMK51SA", ExportName = "sceImeKeyboardOpenInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeKeyboardOpenInternal(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "ua+13Hk9kKs", ExportName = "sceImeKeyboardSetMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeKeyboardSetMode(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "3Hx2Uw9xnv8", ExportName = "sceImeKeyboardUpdate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeKeyboardUpdate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "RPydv-Jr1bc", ExportName = "sceImeOpen", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        lock (ImeGate)
        {
            if (_imeOpen)
            {
                return ctx.SetReturn(ErrorBusy);
            }

            if (paramAddress == 0 ||
                !ctx.TryReadUInt32(paramAddress + 0x04, out var type) ||
                !ctx.TryReadUInt32(paramAddress + 0x24, out var maxTextLength) ||
                !ctx.TryReadUInt64(paramAddress + 0x28, out var inputTextBuffer) ||
                !ctx.TryReadUInt64(paramAddress + 0x40, out var work) ||
                !ctx.TryReadUInt64(paramAddress + 0x50, out var handler))
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

            if (work == 0 || (work & 3) != 0)
            {
                return ctx.SetReturn(ErrorInvalidWork);
            }

            if (handler == 0)
            {
                return ctx.SetReturn(ErrorInvalidHandler);
            }

            if (!ctx.TryReadUInt32(paramAddress + 0x30, out _imePositionXBits) ||
                !ctx.TryReadUInt32(paramAddress + 0x34, out _imePositionYBits) ||
                !ctx.TryReadUInt32(paramAddress + 0x38, out _imeHorizontalAlignment) ||
                !ctx.TryReadUInt32(paramAddress + 0x3C, out _imeVerticalAlignment))
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            _imeInputTextBuffer = inputTextBuffer;
            _imeMaxTextLength = maxTextLength;
            _imeTextLength = 0;
            Span<byte> character = stackalloc byte[2];
            for (; _imeTextLength < maxTextLength; _imeTextLength++)
            {
                if (!ctx.Memory.TryRead(inputTextBuffer + _imeTextLength * 2, character))
                {
                    return ctx.SetReturn(ErrorInvalidInputTextBuffer);
                }

                if (character[0] == 0 && character[1] == 0)
                {
                    break;
                }
            }

            _imeOpen = true;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "16UI54cWRQk", ExportName = "sceImeOpenInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeOpenInternal(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "WmYDzdC4EHI", ExportName = "sceImeParamInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeParamInit(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address != 0 && TryClear(ctx, address, ImeParamSize))
        {
            _ = ctx.TryWriteUInt32(address, uint.MaxValue);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "TQaogSaqkEk", ExportName = "sceImeSetCandidateIndex", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeSetCandidateIndex(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "WLxUN2WMim8", ExportName = "sceImeSetCaret", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeSetCaret(CpuContext ctx)
    {
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ErrorNotOpened);
            }

            var caretAddress = ctx[CpuRegister.Rdi];
            if (caretAddress == 0 || !ctx.TryReadUInt32(caretAddress + 0x0C, out var index))
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            return index <= _imeTextLength ? Ok(ctx) : ctx.SetReturn(ErrorInvalidParam);
        }
    }

    [SysAbiExport(Nid = "ieCNrVrzKd4", ExportName = "sceImeSetText", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeSetText(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rdi];
        var length = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ErrorNotOpened);
            }

            if (sourceAddress == 0)
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            if (length > _imeMaxTextLength)
            {
                return ctx.SetReturn(ErrorInvalidParam);
            }

            var text = new byte[checked((int)((length + 1) * 2))];
            if (length != 0 && !ctx.Memory.TryRead(sourceAddress, text.AsSpan(0, checked((int)(length * 2)))))
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }

            if (!ctx.Memory.TryWrite(_imeInputTextBuffer, text))
            {
                return ctx.SetReturn(ErrorInvalidInputTextBuffer);
            }

            _imeTextLength = length;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "TXYHFRuL8UY", ExportName = "sceImeSetTextGeometry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeSetTextGeometry(CpuContext ctx)
    {
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ErrorNotOpened);
            }
        }

        var mode = unchecked((uint)ctx[CpuRegister.Rdi]);
        var geometryAddress = ctx[CpuRegister.Rsi];
        if (geometryAddress == 0 ||
            !ctx.TryReadUInt32(geometryAddress, out var xBits) ||
            !ctx.TryReadUInt32(geometryAddress + 4, out var yBits))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        var x = BitConverter.UInt32BitsToSingle(xBits);
        var y = BitConverter.UInt32BitsToSingle(yBits);
        return mode is 0 or 1 && x >= 0 && x < 1920 && y >= 0 && y < 1080
            ? Ok(ctx)
            : ctx.SetReturn(ErrorInvalidParam);
    }

    [SysAbiExport(Nid = "oOwl47ouxoM", ExportName = "sceImeVshClearPreedit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshClearPreedit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "gtoTsGM9vEY", ExportName = "sceImeVshClose", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshClose(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "wTKF4mUlSew", ExportName = "sceImeVshConfirmPreedit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshConfirmPreedit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "rM-1hkuOhh0", ExportName = "sceImeVshDisableController", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshDisableController(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "42xMaQ+GLeQ", ExportName = "sceImeVshGetPanelPositionAndForm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshGetPanelPositionAndForm(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        return address == 0 || TryClear(ctx, address, PositionAndFormSize)
            ? Ok(ctx)
            : ctx.SetReturn(ErrorInvalidAddress);
    }

    [SysAbiExport(Nid = "ZmmV6iukhyo", ExportName = "sceImeVshInformConfirmdString", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshInformConfirmdString(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "EQBusz6Uhp8", ExportName = "sceImeVshInformConfirmdString2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshInformConfirmdString2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "LBicRa-hj3A", ExportName = "sceImeVshOpen", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshOpen(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "-IAOwd2nO7g", ExportName = "sceImeVshSendTextInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSendTextInfo(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "qDagOjvJdNk", ExportName = "sceImeVshSetCaretGeometry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetCaretGeometry(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "tNOlmxee-Nk", ExportName = "sceImeVshSetCaretIndexInPreedit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetCaretIndexInPreedit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "rASXozKkQ9g", ExportName = "sceImeVshSetPanelPosition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetPanelPosition(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "idvMaIu5H+k", ExportName = "sceImeVshSetParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetParam(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "ga5GOgThbjo", ExportName = "sceImeVshSetPreeditGeometry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetPreeditGeometry(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "RuSca8rS6yA", ExportName = "sceImeVshSetSelectGeometry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetSelectGeometry(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "J7COZrgSFRA", ExportName = "sceImeVshSetSelectionText", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshSetSelectionText(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "WqAayyok5p0", ExportName = "sceImeVshUpdate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshUpdate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "O7Fdd+Oc-qQ", ExportName = "sceImeVshUpdateContext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshUpdateContext(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "fwcPR7+7Rks", ExportName = "sceImeVshUpdateContext2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceIme")]
    public static int ImeVshUpdateContext2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        // (userId, const OrbisImeKeyboardParam* param)
        var paramAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || !ctx.TryReadUInt64(paramAddress + HandlerOffset, out var handler))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        if (handler == 0)
        {
            return ctx.SetReturn(ErrorInvalidHandler);
        }

        if (Interlocked.CompareExchange(ref _keyboardHandler, unchecked((long)handler), 0) != 0)
        {
            return ctx.SetReturn(ErrorBusy);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PMVehSlfZ94",
        ExportName = "sceImeKeyboardClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardClose(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _keyboardHandler, 0) == 0)
        {
            return ctx.SetReturn(ErrorNotOpened);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        // Nothing to deliver. Titles (Quake among them) pump this from their
        // frame loop AND from bring-up paths before any keyboard is opened, so
        // it must report success ("no pending IME events") in both cases rather
        // than erroring when no listener is registered yet.
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _keyboardHandler, 0);
    }
}
