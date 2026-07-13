// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using SharpEmu.Libs.Mouse;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// libSceMouse and libSceIme keyboard semantics with no physical device:
/// init/open/read/close ordering rules, disconnected-packet reads, keyboard
/// open validation, and the update pump's not-opened error.
/// </summary>
public sealed class MouseImeTests : IDisposable
{
    private const ulong DataAddress = 0x1000;
    private const ulong ParamAddress = 0x2000;
    private const ulong HandlerAddress = 0x40_0000;

    private const int MouseErrorInvalidArg = unchecked((int)0x80DF0001);
    private const int MouseErrorInvalidHandle = unchecked((int)0x80DF0003);
    private const int MouseErrorAlreadyOpened = unchecked((int)0x80DF0004);
    private const int MouseErrorNotInitialized = unchecked((int)0x80DF0005);

    private const int ImeErrorBusy = unchecked((int)0x80BC0001);
    private const int ImeErrorNotOpened = unchecked((int)0x80BC0002);
    private const int ImeErrorInvalidHandler = unchecked((int)0x80BC0022);
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0031);

    public MouseImeTests()
    {
        MouseExports.ResetForTests();
        ImeExports.ResetForTests();
    }

    public void Dispose()
    {
        MouseExports.ResetForTests();
        ImeExports.ResetForTests();
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static int OpenMouse(CpuContext ctx, int index = 0)
    {
        ctx[CpuRegister.Rdi] = 1; // user id
        ctx[CpuRegister.Rsi] = 0; // type
        ctx[CpuRegister.Rdx] = (ulong)index;
        ctx[CpuRegister.Rcx] = 0; // param
        MouseExports.MouseOpen(ctx);
        return Result(ctx);
    }

    [Fact]
    public void MouseOpen_BeforeInit_ReturnsNotInitialized()
    {
        var ctx = NewContext(out _);
        Assert.Equal(MouseErrorNotInitialized, OpenMouse(ctx));
    }

    [Fact]
    public void MouseOpen_AfterInit_ReturnsHandle()
    {
        var ctx = NewContext(out _);
        MouseExports.MouseInit(ctx);
        Assert.Equal(0, OpenMouse(ctx));
    }

    [Fact]
    public void MouseOpen_InvalidType_ReturnsInvalidArg()
    {
        var ctx = NewContext(out _);
        MouseExports.MouseInit(ctx);
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 1; // unsupported type
        ctx[CpuRegister.Rdx] = 0;
        MouseExports.MouseOpen(ctx);
        Assert.Equal(MouseErrorInvalidArg, Result(ctx));
    }

    [Fact]
    public void MouseOpen_SamePortTwice_ReturnsAlreadyOpened()
    {
        var ctx = NewContext(out _);
        MouseExports.MouseInit(ctx);
        Assert.Equal(0, OpenMouse(ctx));
        Assert.Equal(MouseErrorAlreadyOpened, OpenMouse(ctx));
    }

    [Fact]
    public void MouseRead_ReturnsOneDisconnectedPacket()
    {
        var ctx = NewContext(out var memory);
        MouseExports.MouseInit(ctx);
        var handle = OpenMouse(ctx);

        memory.TryWrite(DataAddress, new byte[0x28]);
        ctx[CpuRegister.Rdi] = (ulong)handle;
        ctx[CpuRegister.Rsi] = DataAddress;
        ctx[CpuRegister.Rdx] = 8;
        MouseExports.MouseRead(ctx);

        Assert.Equal(1, Result(ctx));
        Assert.True(ctx.TryReadUInt64(DataAddress + 0x08, out var connectedAndButtons));
        Assert.Equal(0UL, connectedAndButtons);
    }

    [Fact]
    public void MouseRead_BadArguments_ReturnInvalidArgOrHandle()
    {
        var ctx = NewContext(out var memory);
        MouseExports.MouseInit(ctx);
        var handle = OpenMouse(ctx);
        memory.TryWrite(DataAddress, new byte[0x28]);

        ctx[CpuRegister.Rdi] = (ulong)handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 1;
        MouseExports.MouseRead(ctx);
        Assert.Equal(MouseErrorInvalidArg, Result(ctx));

        ctx[CpuRegister.Rsi] = DataAddress;
        ctx[CpuRegister.Rdx] = 65;
        MouseExports.MouseRead(ctx);
        Assert.Equal(MouseErrorInvalidArg, Result(ctx));

        ctx[CpuRegister.Rdi] = 1; // never opened
        ctx[CpuRegister.Rdx] = 1;
        MouseExports.MouseRead(ctx);
        Assert.Equal(MouseErrorInvalidHandle, Result(ctx));
    }

    [Fact]
    public void MouseClose_ReleasesPortForReopen()
    {
        var ctx = NewContext(out _);
        MouseExports.MouseInit(ctx);
        var handle = OpenMouse(ctx);

        ctx[CpuRegister.Rdi] = (ulong)handle;
        MouseExports.MouseClose(ctx);
        Assert.Equal(0, Result(ctx));

        MouseExports.MouseClose(ctx);
        Assert.Equal(MouseErrorInvalidHandle, Result(ctx));

        Assert.Equal(0, OpenMouse(ctx));
    }

    private static CpuContext NewKeyboardContext(ulong handler)
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(ParamAddress + 0x00, 0); // option + reserved
        memory.WriteUInt64(ParamAddress + 0x08, 0); // arg
        memory.WriteUInt64(ParamAddress + 0x10, handler);
        memory.WriteUInt64(ParamAddress + 0x18, 0); // reserved
        ctx[CpuRegister.Rdi] = 1; // user id
        ctx[CpuRegister.Rsi] = ParamAddress;
        return ctx;
    }

    [Fact]
    public void ImeKeyboardOpen_ValidParam_Succeeds()
    {
        var ctx = NewKeyboardContext(HandlerAddress);
        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ImeKeyboardOpen_NullParam_ReturnsInvalidAddress()
    {
        var ctx = NewKeyboardContext(HandlerAddress);
        ctx[CpuRegister.Rsi] = 0;
        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(ImeErrorInvalidAddress, Result(ctx));
    }

    [Fact]
    public void ImeKeyboardOpen_NullHandler_ReturnsInvalidHandler()
    {
        var ctx = NewKeyboardContext(0);
        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(ImeErrorInvalidHandler, Result(ctx));
    }

    [Fact]
    public void ImeKeyboardOpen_Twice_ReturnsBusy()
    {
        var ctx = NewKeyboardContext(HandlerAddress);
        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(0, Result(ctx));

        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(ImeErrorBusy, Result(ctx));
    }

    [Fact]
    public void ImeUpdate_WithoutOpen_ReturnsNotOpened()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = HandlerAddress;
        ImeExports.ImeUpdate(ctx);
        Assert.Equal(ImeErrorNotOpened, Result(ctx));
    }

    [Fact]
    public void ImeUpdate_AfterOpen_SucceedsAndCloseStopsIt()
    {
        var ctx = NewKeyboardContext(HandlerAddress);
        ImeExports.ImeKeyboardOpen(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = HandlerAddress;
        ImeExports.ImeUpdate(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = 1;
        ImeExports.ImeKeyboardClose(ctx);
        Assert.Equal(0, Result(ctx));

        ImeExports.ImeUpdate(ctx);
        Assert.Equal(ImeErrorNotOpened, Result(ctx));
    }
}
