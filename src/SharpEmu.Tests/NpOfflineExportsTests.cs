// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Offline NP behavior: NpWebApi2 hands out real library context ids but reports the local user
/// as signed out, and the NpManager profile queries write deterministic offline values so titles
/// conclude "offline, proceed" instead of polling for connectivity every frame.
/// </summary>
public sealed class NpOfflineExportsTests
{
    private const ulong OutAddress = 0x4000;
    private const int NpWebApi2ErrorInvalidLibContextId = unchecked((int)0x80553403);
    private const int NpWebApi2ErrorNotSignedIn = unchecked((int)0x80553407);
    private const int LocalUserId = 0x3E8;

    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpOfflineExportsTests()
    {
        NpWebApi2Exports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private int Initialize()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0x10000;
        NpWebApi2Exports.NpWebApi2Initialize(_ctx);
        return Result(_ctx);
    }

    [Fact]
    public void WebApi2Initialize_ReturnsPositiveLibraryContextId()
    {
        Assert.True(Initialize() > 0);
    }

    [Fact]
    public void WebApi2CreateUserContext_ReportsNotSignedIn()
    {
        var libraryContextId = Initialize();

        _ctx[CpuRegister.Rdi] = (ulong)libraryContextId;
        _ctx[CpuRegister.Rsi] = LocalUserId;
        NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);
        Assert.Equal(NpWebApi2ErrorNotSignedIn, Result(_ctx));
    }

    [Fact]
    public void WebApi2CreateUserContext_RejectsUnknownLibraryContext()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = LocalUserId;
        NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);
        Assert.Equal(NpWebApi2ErrorInvalidLibContextId, Result(_ctx));
    }

    [Fact]
    public void WebApi2PushEventCreateHandle_ReturnsPositiveHandle()
    {
        var libraryContextId = Initialize();

        _ctx[CpuRegister.Rdi] = (ulong)libraryContextId;
        NpWebApi2Exports.NpWebApi2PushEventCreateHandle(_ctx);
        Assert.True(Result(_ctx) > 0);
    }

    [Fact]
    public void GetNpReachabilityState_WritesUnavailable()
    {
        Assert.True(_memory.TryWrite(OutAddress, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
        _ctx[CpuRegister.Rdi] = LocalUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;
        NpManagerExports.NpGetNpReachabilityState(_ctx);
        Assert.Equal(0, Result(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutAddress, out var state));
        Assert.Equal(0U, state);
    }

    [Fact]
    public void GetNpReachabilityState_RejectsNullState()
    {
        _ctx[CpuRegister.Rdi] = LocalUserId;
        _ctx[CpuRegister.Rsi] = 0;
        NpManagerExports.NpGetNpReachabilityState(_ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(_ctx));
    }

    [Fact]
    public void GetAccountIdA_WritesEightByteAccountId()
    {
        // Pre-fill past the account id so an oversized write is caught.
        Assert.True(_memory.TryWrite(OutAddress, new byte[16]));
        Assert.True(_ctx.TryWriteUInt64(OutAddress + 8, 0xAAAA_BBBB_CCCC_DDDD));

        _ctx[CpuRegister.Rdi] = LocalUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;
        NpManagerExports.NpGetAccountIdA(_ctx);
        Assert.Equal(0, Result(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var accountId));
        Assert.Equal(1UL, accountId);
        Assert.True(_ctx.TryReadUInt64(OutAddress + 8, out var trailing));
        Assert.Equal(0xAAAA_BBBB_CCCC_DDDDUL, trailing);
    }

    [Fact]
    public void GetAccountCountryA_WritesIsoCountryCode()
    {
        Assert.True(_memory.TryWrite(OutAddress, new byte[4]));
        _ctx[CpuRegister.Rdi] = LocalUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;
        NpManagerExports.NpGetAccountCountryA(_ctx);
        Assert.Equal(0, Result(_ctx));
        Assert.True(_ctx.TryReadNullTerminatedUtf8(OutAddress, 4, out var country));
        Assert.Equal("US", country);
    }
}
