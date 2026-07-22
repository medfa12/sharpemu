// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NpCoreCompatExportsTests : IDisposable
{
    private const ulong InputAddress = 0x1000;
    private const ulong OutputAddress = 0x2000;
    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpCoreCompatExportsTests()
    {
        NpScoreExports.ResetForTests();
        NpCommerceExports.ResetForTests();
        NpCommonCompatExports.ResetForTests();
        NpWebApiCompatExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ScoreContextsOwnRequestsAndAbortCompletesWait()
    {
        Assert.True(_memory.TryWrite(InputAddress, new byte[36]));
        _ctx[CpuRegister.Rdi] = 7;
        _ctx[CpuRegister.Rsi] = InputAddress;
        var titleContextId = NpScoreExports.NpScoreCreateNpTitleCtx(_ctx);
        Assert.True(titleContextId > 0);

        _ctx[CpuRegister.Rdi] = (ulong)titleContextId;
        var requestId = NpScoreExports.NpScoreCreateRequest(_ctx);
        Assert.True(requestId > 0);

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        Assert.Equal(0, NpScoreExports.NpScoreAbortRequest(_ctx));
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NpScoreExports.NpScoreWaitAsync(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress, out var asyncResult));
        Assert.Equal(unchecked((int)0x80550707), asyncResult);

        _ctx[CpuRegister.Rdi] = (ulong)titleContextId;
        Assert.Equal(0, NpScoreExports.NpScoreDeleteNpTitleCtx(_ctx));
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        Assert.Equal(unchecked((int)0x8055070E), NpScoreExports.NpScoreWaitAsync(_ctx));
    }

    [Fact]
    public void ScoreBoardInfoClearsExactlyTwentyFourBytes()
    {
        Assert.True(_memory.TryWrite(InputAddress, new byte[36]));
        _ctx[CpuRegister.Rdi] = 4;
        _ctx[CpuRegister.Rsi] = InputAddress;
        var titleContextId = NpScoreExports.NpScoreCreateNpTitleCtx(_ctx);
        _ctx[CpuRegister.Rdi] = (ulong)titleContextId;
        var requestId = NpScoreExports.NpScoreCreateRequest(_ctx);

        Assert.True(_memory.TryWrite(OutputAddress, Enumerable.Repeat((byte)0xCC, 32).ToArray()));
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = OutputAddress;
        Assert.Equal(0, NpScoreExports.NpScoreGetBoardInfo(_ctx));

        var bytes = Read(OutputAddress, 32);
        Assert.All(bytes[..24], value => Assert.Equal((byte)0, value));
        Assert.All(bytes[24..], value => Assert.Equal((byte)0xCC, value));
    }

    [Fact]
    public void CommerceCompletesOfflineDialogAndPacksCanceledResult()
    {
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_ctx));
        var parameter = new byte[0x80];
        BinaryPrimitives.WriteInt32LittleEndian(parameter.AsSpan(0x38), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(parameter.AsSpan(0x58), 0x1122_3344_5566_7788);
        Assert.True(_memory.TryWrite(InputAddress, parameter));

        _ctx[CpuRegister.Rdi] = InputAddress;
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen(_ctx));
        Assert.Equal(3, NpCommerceExports.NpCommerceDialogGetStatus(_ctx));

        Assert.True(_memory.TryWrite(OutputAddress, Enumerable.Repeat((byte)0xCC, 0x30).ToArray()));
        _ctx[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(1, NpCommerceExports.NpCommerceDialogGetResult(_ctx));
        var result = Read(OutputAddress, 0x30);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(0, result[4]);
        Assert.Equal(0x1122_3344_5566_7788UL, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(8)));
        Assert.All(result[16..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void CommonCompatValidatesOnlineIdsAndModelsMutexBusyState()
    {
        var onlineId = new byte[20];
        "Player-1"u8.CopyTo(onlineId);
        Assert.True(_memory.TryWrite(InputAddress, onlineId));
        _ctx[CpuRegister.Rdi] = InputAddress;
        Assert.Equal(1, NpCommonCompatExports.NpIntIsValidOnlineId(_ctx));

        _ctx[CpuRegister.Rdi] = OutputAddress;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NpCommonCompatExports.NpMutexInit(_ctx));
        Assert.Equal(0, NpCommonCompatExports.NpMutexLock(_ctx));
        Assert.Equal(unchecked((int)0x8055800F), NpCommonCompatExports.NpMutexTryLock(_ctx));
        Assert.Equal(0, NpCommonCompatExports.NpMutexUnlock(_ctx));
        Assert.Equal(0, NpCommonCompatExports.NpMutexDestroy(_ctx));
    }

    public void Dispose()
    {
        NpScoreExports.ResetForTests();
        NpCommerceExports.ResetForTests();
        NpCommonCompatExports.ResetForTests();
        NpWebApiCompatExports.ResetForTests();
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }
}
