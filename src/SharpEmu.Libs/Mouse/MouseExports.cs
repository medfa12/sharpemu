// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Mouse;

/// <summary>
/// HLE for libSceMouse with no physical mouse attached. Open succeeds so the
/// title can set up its input stack; Read reports a single disconnected packet
/// (all fields zero), which is what the real library returns while no mouse is
/// plugged in.
///
/// A mouse data packet is 0x28 bytes: timestamp at +0x00, connected flag at
/// +0x08, buttons at +0x0C, x/y deltas at +0x10/+0x14, wheel at +0x18, tilt at
/// +0x1C, reserved through +0x28.
/// </summary>
public static class MouseExports
{
    private const int ErrorInvalidArg = unchecked((int)0x80DF0001);
    private const int ErrorInvalidHandle = unchecked((int)0x80DF0003);
    private const int ErrorAlreadyOpened = unchecked((int)0x80DF0004);
    private const int ErrorNotInitialized = unchecked((int)0x80DF0005);

    private const int MouseDataSize = 0x28;
    private const int MaxReadPackets = 64;
    private const int MaxPortIndex = 1;

    private static int _initialized;
    private static int _openPorts;

    // This NID was previously misbound as an sceNgs2VoiceGetState alias.
    [SysAbiExport(
        Nid = "Qs0wWulgl7U",
        ExportName = "sceMouseInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseInit(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RaqxZIf6DvE",
        ExportName = "sceMouseOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseOpen(CpuContext ctx)
    {
        // (userId, type, index, OrbisMouseOpenParam* param) -> handle
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (Volatile.Read(ref _initialized) == 0)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        if (type != 0 || index < 0 || index > MaxPortIndex)
        {
            return ctx.SetReturn(ErrorInvalidArg);
        }

        var portBit = 1 << index;
        int ports;
        do
        {
            ports = Volatile.Read(ref _openPorts);
            if ((ports & portBit) != 0)
            {
                return ctx.SetReturn(ErrorAlreadyOpened);
            }
        }
        while (Interlocked.CompareExchange(ref _openPorts, ports | portBit, ports) != ports);

        return ctx.SetReturn(index);
    }

    [SysAbiExport(
        Nid = "cAnT0Rw-IwU",
        ExportName = "sceMouseClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (handle < 0 || handle > MaxPortIndex || (Volatile.Read(ref _openPorts) & (1 << handle)) == 0)
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        Interlocked.And(ref _openPorts, ~(1 << handle));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "x8qnXqh-tiM",
        ExportName = "sceMouseRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseRead(CpuContext ctx)
    {
        // (handle, OrbisMouseData* data, count) -> packets written
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (dataAddress == 0 || count < 1 || count > MaxReadPackets)
        {
            return ctx.SetReturn(ErrorInvalidArg);
        }

        if (handle < 0 || handle > MaxPortIndex || (Volatile.Read(ref _openPorts) & (1 << handle)) == 0)
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        Span<byte> packet = stackalloc byte[MouseDataSize];
        packet.Clear();
        return ctx.Memory.TryWrite(dataAddress, packet)
            ? ctx.SetReturn(1)
            : ctx.SetReturn(ErrorInvalidArg);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _openPorts, 0);
    }
}
