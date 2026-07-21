// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NetworkExportsTests : IDisposable
{
    private const ulong TextAddress = 0x1000;
    private const ulong BinaryAddress = 0x2000;
    private const ulong OutputAddress = 0x3000;

    public NetworkExportsTests() => NetExports.ResetForTests();

    public void Dispose() => NetExports.ResetForTests();

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void ByteOrderExports_ReturnConvertedValuesInRax()
    {
        var ctx = NewContext(out _);

        ctx[CpuRegister.Rdi] = 0x1234;
        NetExports.NetHtons(ctx);
        Assert.Equal(0x3412UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0x12345678;
        NetExports.NetHtonl(ctx);
        Assert.Equal(0x78563412UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0x3412;
        NetExports.NetNtohs(ctx);
        Assert.Equal(0x1234UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0x78563412;
        NetExports.NetNtohl(ctx);
        Assert.Equal(0x12345678UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void InetPtonAndNtop_RoundTripIpv4NetworkBytes()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(TextAddress, Encoding.ASCII.GetBytes("192.0.2.45\0"));

        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = TextAddress;
        ctx[CpuRegister.Rdx] = BinaryAddress;
        NetExports.NetInetPton(ctx);

        Assert.Equal(1, Result(ctx));
        Span<byte> address = stackalloc byte[4];
        Assert.True(memory.TryRead(BinaryAddress, address));
        Assert.Equal(new byte[] { 192, 0, 2, 45 }, address.ToArray());

        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = BinaryAddress;
        ctx[CpuRegister.Rdx] = OutputAddress;
        ctx[CpuRegister.Rcx] = 16;
        NetExports.NetInetNtop(ctx);

        Assert.Equal(OutputAddress, ctx[CpuRegister.Rax]);
        Span<byte> text = stackalloc byte[10];
        Assert.True(memory.TryRead(OutputAddress, text));
        Assert.Equal("192.0.2.45", Encoding.ASCII.GetString(text));
    }

    [Fact]
    public void InetPton_RejectsMalformedAddressWithoutWritingOutput()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(TextAddress, Encoding.ASCII.GetBytes("999.1.2.3\0"));

        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = TextAddress;
        ctx[CpuRegister.Rdx] = BinaryAddress;
        NetExports.NetInetPton(ctx);

        Assert.Equal(0, Result(ctx));
        Span<byte> output = stackalloc byte[4];
        Assert.False(memory.TryRead(BinaryAddress, output));
    }

    [Fact]
    public void SocketHandleTable_AddsAndRemovesHostSocket()
    {
        var ctx = NewContext(out _);
        NetExports.NetInit(ctx);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = 6;
        NetExports.NetSocket(ctx);
        var handle = Result(ctx);

        Assert.True(handle > 0);
        Assert.Equal(1, NetExports.SocketHandleCount);

        ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        NetExports.NetSocketClose(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, NetExports.SocketHandleCount);

        NetExports.NetSocketClose(ctx);
        Assert.Equal(unchecked((int)0x80410109), Result(ctx));
    }

    [Fact]
    public void EpollHandleTable_RegistersAndRemovesSocket()
    {
        var ctx = NewContext(out var memory);
        NetExports.NetInit(ctx);

        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 2;
        ctx[CpuRegister.Rcx] = 17;
        NetExports.NetSocket(ctx);
        var socket = Result(ctx);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        NetExports.NetEpollCreate(ctx);
        var epoll = Result(ctx);

        memory.WriteUInt32(BinaryAddress, 2);
        memory.WriteUInt64(BinaryAddress + 16, 0x1122334455667788);
        ctx[CpuRegister.Rdi] = unchecked((ulong)epoll);
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = unchecked((ulong)socket);
        ctx[CpuRegister.Rcx] = BinaryAddress;
        NetExports.NetEpollControl(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rsi] = 3;
        ctx[CpuRegister.Rcx] = 0;
        NetExports.NetEpollControl(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = unchecked((ulong)epoll);
        NetExports.NetEpollDestroy(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = unchecked((ulong)socket);
        NetExports.NetSocketClose(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Theory]
    [InlineData(SocketError.WouldBlock, unchecked((int)0x80410123))]
    [InlineData(SocketError.InProgress, unchecked((int)0x80410124))]
    [InlineData(SocketError.ConnectionRefused, unchecked((int)0x8041013D))]
    [InlineData(SocketError.TimedOut, unchecked((int)0x8041013C))]
    [InlineData(SocketError.HostUnreachable, unchecked((int)0x80410141))]
    public void SocketErrors_MapToOrbisNetErrors(SocketError error, int expected)
    {
        Assert.Equal(expected, NetExports.TranslateSocketError(error));
    }

    [Fact]
    public void NetCtl_ReportsConnectedPrivateLan()
    {
        var ctx = NewContext(out var memory);

        ctx[CpuRegister.Rdi] = OutputAddress;
        NetCtlExports.NetCtlGetState(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var state));
        Assert.Equal(3U, state);

        ctx[CpuRegister.Rdi] = 4;
        ctx[CpuRegister.Rsi] = OutputAddress;
        NetCtlExports.NetCtlGetInfo(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var link));
        Assert.Equal(1U, link);

        ctx[CpuRegister.Rdi] = 14;
        NetCtlExports.NetCtlGetInfo(ctx);
        Span<byte> address = stackalloc byte[11];
        Assert.True(memory.TryRead(OutputAddress, address));
        Assert.Equal("192.168.0.2", Encoding.ASCII.GetString(address));
    }
}
