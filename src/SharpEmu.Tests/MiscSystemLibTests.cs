// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using SharpEmu.Libs.CommonDialog;
using SharpEmu.Libs.DiscMap;
using SharpEmu.Libs.GameUpdate;
using SharpEmu.Libs.SystemGesture;
using Xunit;

namespace SharpEmu.Tests;

public sealed class MiscSystemLibTests : IDisposable
{
    private const ulong ParamAddress = 0x1000;
    private const ulong NestedParamAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;

    public MiscSystemLibTests()
    {
        CommonDialogExports.ResetForTests();
        MsgDialogExports.ResetForTests();
        GameUpdateExports.ResetForTests();
    }

    public void Dispose()
    {
        CommonDialogExports.ResetForTests();
        MsgDialogExports.ResetForTests();
        GameUpdateExports.ResetForTests();
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(7, 0, 2)]
    [InlineData(8, 1, 0)]
    public void MsgDialog_SelectsResultForRequestedButtonType(int buttonType, int expectedResult, int expectedButton)
    {
        Assert.True(MsgDialogExports.TrySelectImmediateResult(buttonType, out var result, out var button));
        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedButton, button);
    }

    [Fact]
    public void MsgDialog_OpenImmediatelyFinishesAndWritesCompleteResult()
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt32(ParamAddress + 0x38, 1);
        memory.WriteUInt64(ParamAddress + 0x40, NestedParamAddress);
        memory.WriteUInt32(NestedParamAddress, 7);

        MsgDialogExports.MsgDialogInitialize(ctx);
        ctx[CpuRegister.Rdi] = ParamAddress;
        MsgDialogExports.MsgDialogOpen(ctx);
        Assert.Equal(0, Result(ctx));

        MsgDialogExports.MsgDialogUpdateStatus(ctx);
        Assert.Equal(3, Result(ctx));

        ctx[CpuRegister.Rdi] = ResultAddress;
        MsgDialogExports.MsgDialogGetResult(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(1, ReadInt32(memory, ResultAddress));
        Assert.Equal(0, ReadInt32(memory, ResultAddress + 4));
        Assert.Equal(2, ReadInt32(memory, ResultAddress + 8));

        Span<byte> reserved = stackalloc byte[32];
        Assert.True(memory.TryRead(ResultAddress + 12, reserved));
        Assert.True(reserved.SequenceEqual(new byte[32]));
    }

    [Fact]
    public void CommonDialog_IsUsedTracksMessageDialogLifetime()
    {
        var ctx = NewContext(out _);
        CommonDialogExports.CommonDialogInitialize(ctx);
        CommonDialogExports.CommonDialogIsUsed(ctx);
        Assert.Equal(0, Result(ctx));

        MsgDialogExports.MsgDialogInitialize(ctx);
        CommonDialogExports.CommonDialogIsUsed(ctx);
        Assert.Equal(1, Result(ctx));

        MsgDialogExports.MsgDialogTerminate(ctx);
        CommonDialogExports.CommonDialogIsUsed(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void GameUpdate_CheckReportsNoPendingUpdateAndPreservesResultSize()
    {
        var ctx = NewContext(out var memory);
        GameUpdateExports.GameUpdateInitialize(ctx);
        GameUpdateExports.GameUpdateCreateRequest(ctx);
        var requestId = Result(ctx);

        memory.WriteUInt64(ParamAddress, 48);
        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, 64).ToArray()));
        memory.WriteUInt64(ResultAddress, 64);
        ctx[CpuRegister.Rdi] = (uint)requestId;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = ResultAddress;
        GameUpdateExports.GameUpdateCheck(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.Equal(64UL, ReadUInt64(memory, ResultAddress));
        Span<byte> resultBody = stackalloc byte[40];
        Assert.True(memory.TryRead(ResultAddress + 8, resultBody));
        Assert.True(resultBody.SequenceEqual(new byte[40]));
    }

    [Fact]
    public void GameUpdate_DeleteInvalidatesRequest()
    {
        const int ErrorRequestNotFound = unchecked((int)0x80412805);
        var ctx = NewContext(out _);
        GameUpdateExports.GameUpdateInitialize(ctx);
        GameUpdateExports.GameUpdateCreateRequest(ctx);
        var requestId = Result(ctx);

        ctx[CpuRegister.Rdi] = (uint)requestId;
        GameUpdateExports.GameUpdateDeleteRequest(ctx);
        Assert.Equal(0, Result(ctx));

        GameUpdateExports.GameUpdateAbortRequest(ctx);
        Assert.Equal(ErrorRequestNotFound, Result(ctx));
    }

    [Fact]
    public void AppContent_EmptyAddcontStateIsWellFormed()
    {
        const int ErrorNoEntitlement = unchecked((int)0x80D90007);
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rcx] = ResultAddress;
        AppContentExports.AppContentGetAddcontInfoList(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, ReadInt32(memory, ResultAddress));

        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, 24).ToArray()));
        ctx[CpuRegister.Rsi] = NestedParamAddress;
        ctx[CpuRegister.Rdx] = ResultAddress;
        AppContentExports.AppContentGetAddcontInfo(ctx);
        Assert.Equal(ErrorNoEntitlement, Result(ctx));
        Span<byte> info = stackalloc byte[24];
        Assert.True(memory.TryRead(ResultAddress, info));
        Assert.True(info.SequenceEqual(new byte[24]));
    }

    [Fact]
    public void DiscMap_RequestIsReportedResident()
    {
        var ctx = NewContext(out var memory);
        Assert.True(memory.TryWrite(ParamAddress, "/app0/data.bin\0"u8));
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rcx] = ResultAddress;
        DiscMapExports.DiscMapIsRequestOnHDD(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.Equal(1, ReadInt32(memory, ResultAddress));
    }

    [Fact]
    public void SystemGesture_RecognizerQueriesProduceNoEvents()
    {
        const int RecognizerSize = 361 * sizeof(ulong);
        const int EventSize = 168;
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = 0;
        SystemGestureExports.SystemGestureOpen(ctx);
        Assert.Equal(1, Result(ctx));

        Assert.True(memory.TryWrite(NestedParamAddress, Enumerable.Repeat((byte)0xA5, RecognizerSize).ToArray()));
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = NestedParamAddress;
        SystemGestureExports.SystemGestureCreateTouchRecognizer(ctx);
        Assert.Equal(0, Result(ctx));
        Span<byte> recognizer = stackalloc byte[RecognizerSize];
        Assert.True(memory.TryRead(NestedParamAddress, recognizer));
        Assert.True(recognizer.SequenceEqual(new byte[RecognizerSize]));

        Assert.True(memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, EventSize).ToArray()));
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = NestedParamAddress;
        ctx[CpuRegister.Rdx] = ResultAddress;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = ResultAddress + EventSize;
        SystemGestureExports.SystemGestureGetTouchEvents(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, ReadInt32(memory, ResultAddress + EventSize));
        Span<byte> touchEvent = stackalloc byte[EventSize];
        Assert.True(memory.TryRead(ResultAddress, touchEvent));
        Assert.True(touchEvent.SequenceEqual(new byte[EventSize]));
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static int ReadInt32(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
