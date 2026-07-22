// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NpMatchingPartySignalingTests
{
    private const ulong InitParamAddress = 0x1000;
    private const ulong ContextParamAddress = 0x2000;
    private const ulong OutputAddress = 0x3000;
    private const ulong RequestAddress = 0x4000;
    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpMatchingPartySignalingTests()
    {
        NpMatching2Exports.ResetForTests();
        NpPartyExports.ResetForTests();
        NpSignalingExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void Matching2ContextLifecycle_UsesPackedSixteenBitHandles()
    {
        InitializeMatching2();
        Assert.True(_ctx.TryWriteUInt64(ContextParamAddress, 0x5000));
        Assert.True(_ctx.TryWriteUInt64(ContextParamAddress + 0x20, 0x28));
        Assert.True(_ctx.TryWriteUInt32(OutputAddress, 0xAABBCCDD));
        _ctx[CpuRegister.Rdi] = ContextParamAddress;
        _ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, NpMatching2Exports.sceNpMatching2CreateContext(_ctx));
        Assert.True(_ctx.TryReadUInt16(OutputAddress, out var contextId));
        Assert.NotEqual((ushort)0, contextId);
        Assert.True(_ctx.TryReadUInt16(OutputAddress + 2, out var trailing));
        Assert.Equal((ushort)0xAABB, trailing);

        _ctx[CpuRegister.Rdi] = contextId;
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2ContextStart(_ctx));
        Assert.Equal(unchecked((int)0x80550C07), NpMatching2Exports.sceNpMatching2ContextStart(_ctx));
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2ContextStop(_ctx));
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2DestroyContext(_ctx));
        Assert.Equal(unchecked((int)0x80550C0B), NpMatching2Exports.sceNpMatching2DestroyContext(_ctx));
    }

    [Fact]
    public void Matching2Requests_ReturnDistinctThirtyTwoBitIds()
    {
        var contextId = CreateMatching2ContextA();
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = RequestAddress;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = OutputAddress;

        Assert.Equal(0, NpMatching2Exports.sceNpMatching2SearchRoom(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutputAddress, out var first));
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2GetWorldInfoList(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutputAddress, out var second));
        Assert.NotEqual(0U, first);
        Assert.Equal(first + 1, second);
    }

    [Fact]
    public void SignalingConnection_ReportsActiveAndUpdatesPackedStatistics()
    {
        Assert.Equal(0, NpSignalingExports.sceNpSignalingInitialize(_ctx));
        _ctx[CpuRegister.Rdi] = 1000;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = OutputAddress;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingCreateContextA(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress, out var contextId));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        _ctx[CpuRegister.Rsi] = RequestAddress;
        _ctx[CpuRegister.Rdx] = OutputAddress + 8;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingActivateConnectionA(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress + 8, out var connectionId));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)connectionId);
        _ctx[CpuRegister.Rdx] = OutputAddress + 0x10;
        _ctx[CpuRegister.Rcx] = OutputAddress + 0x14;
        _ctx[CpuRegister.R8] = OutputAddress + 0x18;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingGetConnectionStatus(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutputAddress + 0x10, out var status));
        Assert.Equal(2U, status);

        _ctx[CpuRegister.Rdi] = OutputAddress + 0x20;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingGetConnectionStatistics(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutputAddress + 0x20, out var peak));
        Assert.True(_ctx.TryReadUInt32(OutputAddress + 0x24, out var active));
        Assert.True(_ctx.TryReadUInt32(OutputAddress + 0x2C, out var established));
        Assert.Equal(1U, peak);
        Assert.Equal(1U, active);
        Assert.Equal(1U, established);
    }

    [Fact]
    public void SignalingContextOption_RoundTrips()
    {
        Assert.Equal(0, NpSignalingExports.sceNpSignalingInitialize(_ctx));
        _ctx[CpuRegister.Rdi] = 0x5000;
        _ctx[CpuRegister.Rcx] = OutputAddress;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingCreateContext(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress, out var contextId));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingSetContextOption(_ctx));
        _ctx[CpuRegister.Rdx] = OutputAddress + 8;
        Assert.Equal(0, NpSignalingExports.sceNpSignalingGetContextOption(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress + 8, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void PartyStateAndVoicePriority_AreDeterministic()
    {
        Assert.Equal(0, NpPartyExports.sceNpPartyInitialize(_ctx));
        Assert.True(_ctx.TryWriteUInt32(OutputAddress, uint.MaxValue));
        _ctx[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(0, NpPartyExports.sceNpPartyGetState(_ctx));
        Assert.True(_ctx.TryReadUInt16(OutputAddress, out var state));
        Assert.True(_ctx.TryReadUInt16(OutputAddress + 2, out var trailing));
        Assert.Equal((ushort)2, state);
        Assert.Equal(ushort.MaxValue, trailing);

        _ctx[CpuRegister.Rdi] = 7;
        Assert.Equal(0, NpPartyExports.sceNpPartySetVoiceChatPriority(_ctx));
        _ctx[CpuRegister.Rdi] = OutputAddress + 8;
        Assert.Equal(0, NpPartyExports.sceNpPartyGetVoiceChatPriority(_ctx));
        Assert.True(_ctx.TryReadInt32(OutputAddress + 8, out var priority));
        Assert.Equal(7, priority);
    }

    private void InitializeMatching2()
    {
        Assert.True(_ctx.TryWriteUInt64(InitParamAddress, 0x40000));
        Assert.True(_ctx.TryWriteUInt64(InitParamAddress + 0x20, 0x30));
        Assert.True(_ctx.TryWriteUInt64(InitParamAddress + 0x28, 0x10000));
        _ctx[CpuRegister.Rdi] = InitParamAddress;
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2Initialize(_ctx));
    }

    private ushort CreateMatching2ContextA()
    {
        InitializeMatching2();
        Assert.True(_ctx.TryWriteInt32(ContextParamAddress, 1000));
        Assert.True(_ctx.TryWriteUInt64(ContextParamAddress + 8, 0x10));
        _ctx[CpuRegister.Rdi] = ContextParamAddress;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NpMatching2Exports.sceNpMatching2CreateContextA(_ctx));
        Assert.True(_ctx.TryReadUInt16(OutputAddress, out var contextId));
        return contextId;
    }
}
