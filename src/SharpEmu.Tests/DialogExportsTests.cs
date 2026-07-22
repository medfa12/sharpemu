// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using SharpEmu.Libs.ScreenShot;
using Xunit;

namespace SharpEmu.Tests;

public sealed class DialogExportsTests : IDisposable
{
    private const ulong ParamAddress = 0x1000;
    private const ulong ResultAddress = 0x2000;
    private const ulong NestedResultAddress = 0x3000;

    public DialogExportsTests() => Reset();

    public void Dispose() => Reset();

    [Fact]
    public void SigninDialog_OpenFinishesAndWritesCompletePs5Result()
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt32(ParamAddress, 16);

        SigninDialogExports.SigninDialogInitialize(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = ParamAddress;
        SigninDialogExports.SigninDialogOpen(ctx);
        Assert.Equal(0, Result(ctx));

        SigninDialogExports.SigninDialogUpdateStatus(ctx);
        Assert.Equal(3, Result(ctx));

        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, 16).ToArray()));
        ctx[CpuRegister.Rdi] = ResultAddress;
        SigninDialogExports.SigninDialogGetResult(ctx);
        Assert.Equal(0, Result(ctx));

        Span<byte> result = stackalloc byte[16];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.True(result[4..].SequenceEqual(new byte[12]));
    }

    [Fact]
    public void InvitationDialog_ResultPreservesCallbackAndClearsRecipientList()
    {
        const ulong callbackArgument = 0x1122_3344_5566_7788;
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(ParamAddress + 0x40, callbackArgument);

        InvitationDialogExports.InvitationDialogInitialize(ctx);
        ctx[CpuRegister.Rdi] = ParamAddress;
        InvitationDialogExports.InvitationDialogOpen(ctx);
        Assert.Equal(0, Result(ctx));

        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, 56).ToArray()));
        memory.WriteUInt64(ResultAddress + 0x10, NestedResultAddress);
        memory.WriteUInt32(NestedResultAddress, 0xFFFF_FFFF);
        ctx[CpuRegister.Rdi] = ResultAddress;
        InvitationDialogExports.InvitationDialogGetResult(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.Equal(callbackArgument, ReadUInt64(memory, ResultAddress));
        Assert.Equal(NestedResultAddress, ReadUInt64(memory, ResultAddress + 0x10));
        Assert.Equal(0u, ReadUInt32(memory, NestedResultAddress));

        Span<byte> reserved = stackalloc byte[32];
        Assert.True(memory.TryRead(ResultAddress + 0x18, reserved));
        Assert.True(reserved.SequenceEqual(new byte[32]));
    }

    [Fact]
    public void PlayGoDialog_ResultUsesProceedValueAtPackedOffset()
    {
        var ctx = NewContext(out var memory);
        PlayGoDialogExports.PlayGoDialogInitialize(ctx);
        ctx[CpuRegister.Rdi] = ParamAddress;
        PlayGoDialogExports.PlayGoDialogOpen(ctx);

        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, 0x28).ToArray()));
        ctx[CpuRegister.Rdi] = ResultAddress;
        PlayGoDialogExports.PlayGoDialogGetResult(ctx);

        Span<byte> result = stackalloc byte[0x28];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(result[4..]));
        Assert.True(result[8..].SequenceEqual(new byte[32]));
    }

    [Fact]
    public void ScreenShot_DisableAndNotificationControlsAreStateful()
    {
        var ctx = NewContext(out _);
        ScreenShotExports.ScreenShotDisable(ctx);
        ScreenShotExports.ScreenShotIsDisabled(ctx);
        Assert.Equal(1, Result(ctx));

        ScreenShotExports.ScreenShotDisableNotification(ctx);
        Assert.True(ScreenShotExports.NotificationDisabled);

        ScreenShotExports.ScreenShotEnable(ctx);
        ScreenShotExports.ScreenShotIsDisabled(ctx);
        Assert.Equal(0, Result(ctx));

        ScreenShotExports.ScreenShotEnableNotification(ctx);
        Assert.False(ScreenShotExports.NotificationDisabled);
    }

    [Fact]
    public void ScreenShot_DrcParametersRoundTripAcrossLibraries()
    {
        var ctx = NewContext(out var memory);
        var expected = Enumerable.Range(1, ScreenShotExports.DrcParamSize).Select(value => (byte)value).ToArray();
        Assert.True(memory.TryWrite(ParamAddress, expected));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ScreenShotDrcExports.ScreenShotSetDrcParam(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = ResultAddress;
        ScreenShotExports.ScreenShotGetDrcParam(ctx);
        Assert.Equal(0, Result(ctx));

        Span<byte> actual = stackalloc byte[ScreenShotExports.DrcParamSize];
        Assert.True(memory.TryRead(ResultAddress, actual));
        Assert.True(actual.SequenceEqual(expected));
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static uint ReadUInt32(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void Reset()
    {
        ErrorDialogExports.ResetForTests();
        HmdSetupDialogExports.ResetForTests();
        InvitationDialogExports.ResetForTests();
        NpProfileDialogExports.ResetForTests();
        PlayGoDialogExports.ResetForTests();
        SigninDialogExports.ResetForTests();
        WebBrowserDialogExports.ResetForTests();
        ScreenShotExports.ResetForTests();
    }
}
