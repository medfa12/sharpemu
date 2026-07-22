// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NpV1ExportsTests : IDisposable
{
    private const ulong ContextAddress = 0x1000;
    private const ulong HandleAddress = 0x1010;
    private const ulong OutputAddress = 0x2000;
    private const ulong DataAddress = 0x4000;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-trophy-v1-{Guid.NewGuid():N}");
    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpV1ExportsTests()
    {
        Directory.CreateDirectory(_storageRoot);
        NpTrophy2Exports.ResetForTests();
        NpTrophy2Exports.TrophyStorageRootOverride = _storageRoot;
        NpTrophy2Exports.TrophyTitleIdOverride = "PPSA54321";
        NpWebApiExports.ResetForTests();
        NpAuthExports.ResetForTests();
        NpManagerExports.ResetExtendedStateForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void TrophyV1_UsesSharedRegistryAndLegacyPacking()
    {
        var (contextId, handleId) = CreateRegisteredTrophyContext();
        NpTrophy2Exports.DefineTrophyForTests(
            contextId,
            new NpTrophy2Exports.TrophyRecord(12, 3, 0, true, "Silver Path", "Complete the silver path", false, 0));

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        _ctx[CpuRegister.Rsi] = (ulong)handleId;
        _ctx[CpuRegister.Rdx] = 12;
        _ctx[CpuRegister.Rcx] = OutputAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophyUnlockTrophy(_ctx));

        _ctx[CpuRegister.Rdx] = 12;
        _ctx[CpuRegister.Rcx] = OutputAddress;
        _ctx[CpuRegister.R8] = DataAddress;
        Assert.Equal(0, NpTrophyExports.NpTrophyGetTrophyInfo(_ctx));

        var details = Read(OutputAddress, NpTrophyExports.TrophyDetailsSize);
        var data = Read(DataAddress, NpTrophyExports.TrophyDataSize);
        Assert.Equal((ulong)NpTrophyExports.TrophyDetailsSize, BinaryPrimitives.ReadUInt64LittleEndian(details));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(8)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(12)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(16)));
        Assert.Equal((byte)1, details[20]);
        Assert.Equal((ulong)NpTrophyExports.TrophyDataSize, BinaryPrimitives.ReadUInt64LittleEndian(data));
        Assert.Equal((byte)1, data[12]);

        _ctx[CpuRegister.Rdx] = OutputAddress;
        _ctx[CpuRegister.Rcx] = DataAddress;
        Assert.Equal(0, NpTrophyExports.NpTrophyGetTrophyUnlockState(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutputAddress, out var firstFlagWord));
        Assert.Equal(1U << 12, firstFlagWord);
        Assert.True(_ctx.TryReadUInt32(DataAddress, out var trophyCount));
        Assert.Equal(1U, trophyCount);
    }

    [Fact]
    public void WebApiV1_TracksRequestsAndClearsOfflineResponse()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0x10000;
        var libraryContextId = NpWebApiExports.NpWebApiInitialize(_ctx);
        Assert.True(libraryContextId > 0);

        _ctx[CpuRegister.Rdi] = (ulong)libraryContextId;
        _ctx[CpuRegister.Rsi] = 1000;
        var userContextId = NpWebApiExports.NpWebApiCreateContextA(_ctx);
        Assert.True(userContextId > 0);

        _memory.WriteUInt64(0x3000, 1);
        _ctx[CpuRegister.Rdi] = (ulong)userContextId;
        _ctx[CpuRegister.Rsi] = 0x3000;
        _ctx[CpuRegister.Rdx] = 0x3000;
        _ctx[CpuRegister.Rcx] = 0;
        _ctx[CpuRegister.R9] = OutputAddress;
        Assert.Equal(0, NpWebApiExports.NpWebApiCreateRequest(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutputAddress, out var requestId));

        Assert.True(_memory.TryWrite(DataAddress, Enumerable.Repeat((byte)0xCC, 32).ToArray()));
        _ctx[CpuRegister.Rdi] = requestId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = DataAddress;
        Assert.Equal(unchecked((int)0x80552907), NpWebApiExports.NpWebApiSendRequest2(_ctx));
        Assert.All(Read(DataAddress, 32), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void AuthAsyncRequest_ReportsSignedOutThroughPoll()
    {
        _memory.WriteUInt64(0x3000, 24);
        _ctx[CpuRegister.Rdi] = 0x3000;
        var requestId = NpAuthExports.NpAuthCreateAsyncRequest(_ctx);

        _memory.WriteUInt64(0x3100, 32);
        _memory.WriteUInt64(0x3108, 1000);
        _memory.WriteUInt64(0x3110, 0x5000);
        _memory.WriteUInt64(0x3118, 0x6000);
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = 0x3100;
        _ctx[CpuRegister.Rdx] = 0x7000;
        _ctx[CpuRegister.Rcx] = 0x7100;
        Assert.Equal(0, NpAuthExports.NpAuthGetAuthorizationCodeA(_ctx));

        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NpAuthExports.NpAuthPollAsync(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress, out var result));
        Assert.Equal(unchecked((int)0x80550006), result);
        Assert.All(Read(0x7000, 136), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void ManagerProfile_UsesStableLocalIdentity()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NpManagerExports.NpGetAccountId(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutputAddress, out var accountId));
        Assert.Equal(1UL, accountId);

        _ctx[CpuRegister.Rdi] = accountId;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NpManagerExports.NpGetUserIdByAccountId(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress, out var userId));
        Assert.Equal(1000, userId);
    }

    public void Dispose()
    {
        NpTrophy2Exports.ResetForTests();
        NpTrophy2Exports.TrophyStorageRootOverride = null;
        NpTrophy2Exports.TrophyTitleIdOverride = null;
        NpWebApiExports.ResetForTests();
        NpAuthExports.ResetForTests();
        NpManagerExports.ResetExtendedStateForTests();
        Directory.Delete(_storageRoot, recursive: true);
    }

    private (int Context, int Handle) CreateRegisteredTrophyContext()
    {
        _ctx[CpuRegister.Rdi] = ContextAddress;
        _ctx[CpuRegister.Rsi] = 1000;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, NpTrophyExports.NpTrophyCreateContext(_ctx));
        Assert.True(_ctx.TryReadInt32(ContextAddress, out var contextId));

        _ctx[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(0, NpTrophyExports.NpTrophyCreateHandle(_ctx));
        Assert.True(_ctx.TryReadInt32(HandleAddress, out var handleId));

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        _ctx[CpuRegister.Rsi] = (ulong)handleId;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NpTrophyExports.NpTrophyRegisterContext(_ctx));
        return (contextId, handleId);
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }
}
