// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using SharpEmu.Libs.Voice;
using Xunit;

namespace SharpEmu.Tests;

public sealed class RudpVoiceNetworkExportsTests : IDisposable
{
    private const ulong OutputAddress = 0x1000;
    private const ulong InfoAddress = 0x2000;

    public RudpVoiceNetworkExportsTests() => ResetState();

    public void Dispose() => ResetState();

    [Theory]
    [InlineData(typeof(RudpExports), "libSceRudp", 34)]
    [InlineData(typeof(VoiceExports), "libSceVoice", 30)]
    [InlineData(typeof(NetBweExports), "libSceNetBwe", 9)]
    [InlineData(typeof(NetCtlApExports), "libSceNetCtlAp", 11)]
    [InlineData(typeof(NetCtlApIpcIntExports), "libSceNetCtlApIpcInt", 16)]
    [InlineData(typeof(NetCtlForNpToolkitExports), "libSceNetCtlForNpToolkit", 4)]
    [InlineData(typeof(NetCtlV6Exports), "libSceNetCtlV6", 3)]
    public void AssignedLibraries_ExposeExpectedGen5Surface(Type exportType, string libraryName, int count)
    {
        var exports = exportType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(static method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(static attribute => attribute is not null)
            .Cast<SysAbiExportAttribute>()
            .ToArray();

        Assert.Equal(count, exports.Length);
        Assert.All(exports, export => Assert.Equal(libraryName, export.LibraryName));
        Assert.All(exports, export => Assert.Equal(Generation.Gen5, export.Target));
        Assert.Equal(count, exports.Select(static export => export.Nid).Distinct().Count());
    }

    [Fact]
    public void Rudp_ContextAndPollHandles_HaveIndependentLifetimes()
    {
        var ctx = NewContext(out _);
        RudpExports.RudpInit(ctx);

        ctx[CpuRegister.Rdi] = OutputAddress;
        RudpExports.RudpCreateContext(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var contextId));
        Assert.Equal(1, RudpExports.ContextCount);

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = 960;
        RudpExports.RudpSetMaxSegmentSize(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rsi] = InfoAddress;
        RudpExports.RudpGetMaxSegmentSize(ctx);
        Assert.True(ctx.TryReadUInt32(InfoAddress, out var segmentSize));
        Assert.Equal(960u, segmentSize);

        ctx[CpuRegister.Rdi] = InfoAddress + 8;
        RudpExports.RudpPollCreate(ctx);
        Assert.True(ctx.TryReadUInt32(InfoAddress + 8, out var pollId));
        Assert.Equal(1, RudpExports.PollCount);

        ctx[CpuRegister.Rdi] = contextId;
        RudpExports.RudpEnd(ctx);
        Assert.Equal(0, RudpExports.ContextCount);
        Assert.Equal(1, RudpExports.PollCount);

        ctx[CpuRegister.Rdi] = pollId;
        RudpExports.RudpPollDestroy(ctx);
        Assert.Equal(0, RudpExports.PollCount);
    }

    [Fact]
    public void Voice_PortInfo_UsesSdkPackingAndTracksState()
    {
        var ctx = NewContext(out _);
        VoiceExports.VoiceInit(ctx);

        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = 7;
        VoiceExports.VoiceCreatePort(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var portId));

        VoiceExports.VoiceStart(ctx);
        ctx[CpuRegister.Rdi] = portId;
        ctx[CpuRegister.Rsi] = 24000;
        VoiceExports.VoiceSetBitRate(ctx);

        ctx[CpuRegister.Rsi] = InfoAddress;
        VoiceExports.VoiceGetPortInfo(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadInt32(InfoAddress, out var portType));
        Assert.True(ctx.TryReadInt32(InfoAddress + 4, out var state));
        Assert.True(ctx.TryReadUInt64(InfoAddress + 8, out var edgePointer));
        Assert.True(ctx.TryReadUInt32(InfoAddress + 20, out var frameSize));
        Assert.True(ctx.TryReadUInt16(InfoAddress + 24, out var edgeCount));
        Assert.Equal(7, portType);
        Assert.Equal(1, state);
        Assert.Equal(0UL, edgePointer);
        Assert.Equal(1u, frameSize);
        Assert.Equal((ushort)0, edgeCount);

        ctx[CpuRegister.Rsi] = InfoAddress + 0x40;
        VoiceExports.VoiceGetBitRate(ctx);
        Assert.True(ctx.TryReadUInt32(InfoAddress + 0x40, out var bitRate));
        Assert.Equal(24000u, bitRate);

        VoiceExports.VoicePausePort(ctx);
        ctx[CpuRegister.Rsi] = InfoAddress + 0x80;
        VoiceExports.VoiceGetPortInfo(ctx);
        Assert.True(ctx.TryReadInt32(InfoAddress + 0x84, out state));
        Assert.Equal(2, state);
    }

    [Fact]
    public void NpToolkit_CallbackRegistration_AllocatesAndReleasesId()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x400000;
        ctx[CpuRegister.Rsi] = 0x1234;
        ctx[CpuRegister.Rdx] = OutputAddress;

        NetCtlForNpToolkitExports.NetCtlRegisterCallbackForNpToolkit(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadInt32(OutputAddress, out var callbackId));
        Assert.Equal(0, callbackId);
        Assert.Equal(1, NetCtlForNpToolkitExports.CallbackCount);

        ctx[CpuRegister.Rdi] = unchecked((ulong)callbackId);
        NetCtlForNpToolkitExports.NetCtlUnregisterCallbackForNpToolkit(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, NetCtlForNpToolkitExports.CallbackCount);
    }

    [Fact]
    public void AccessPointStartAndStop_AreVisibleThroughPublicStateQuery()
    {
        var ctx = NewContext(out _);
        NetCtlApExports.NetCtlApInit(ctx);
        NetCtlApIpcIntExports.NetCtlApRpStart(ctx);

        ctx[CpuRegister.Rdi] = OutputAddress;
        NetCtlApExports.NetCtlApGetState(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var state));
        Assert.Equal(1u, state);

        NetCtlApIpcIntExports.NetCtlApRpStop(ctx);
        ctx[CpuRegister.Rdi] = InfoAddress;
        NetCtlApExports.NetCtlApGetState(ctx);
        Assert.True(ctx.TryReadUInt32(InfoAddress, out state));
        Assert.Equal(0u, state);
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static void ResetState()
    {
        RudpExports.ResetForTests();
        VoiceExports.ResetForTests();
        NetBweExports.ResetForTests();
        NetCtlApExports.ResetForTests();
        NetCtlApIpcIntExports.ResetForTests();
        NetCtlForNpToolkitExports.ResetForTests();
    }
}
