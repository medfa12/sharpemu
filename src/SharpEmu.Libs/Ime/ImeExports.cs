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
    private const int ErrorInvalidAddress = unchecked((int)0x80BC0031);

    private const ulong HandlerOffset = 0x10;

    private static long _keyboardHandler;

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
        // (OrbisImeEventHandler handler) — nothing to deliver, but the pump
        // must succeed while a keyboard listener is registered.
        if (Interlocked.Read(ref _keyboardHandler) == 0)
        {
            return ctx.SetReturn(ErrorNotOpened);
        }

        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _keyboardHandler, 0);
    }
}
