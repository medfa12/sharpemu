// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceVideoOutGetVblankStatus semantics: the reported vblank count merges the
/// wall-clock estimate with the pump-driven count monotonically, the 0x28-byte
/// status struct uses the documented field offsets, and pointer/handle
/// validation follows the usual VideoOut error paths.
/// </summary>
public sealed class VideoOutVblankStatusTests
{
    private const ulong StatusAddress = 0x2000;
    private const int VblankStatusSize = 0x28;

    private const int ErrorInvalidAddress = unchecked((int)0x80290002);
    private const int ErrorInvalidHandle = unchecked((int)0x8029000B);

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int OpenPort(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0; // userId
        ctx[CpuRegister.Rsi] = 0; // SceVideoOutBusTypeMain
        ctx[CpuRegister.Rdx] = 0; // index
        ctx[CpuRegister.Rcx] = 0; // param
        var handle = VideoOutExports.VideoOutOpen(ctx);
        Assert.True(handle > 0);
        return handle;
    }

    private static void ClosePort(CpuContext ctx, int handle)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        VideoOutExports.VideoOutClose(ctx);
    }

    private static byte[] ReadStatus(SparseGuestMemory memory)
    {
        var status = new byte[VblankStatusSize];
        Assert.True(memory.TryRead(StatusAddress, status));
        return status;
    }

    [Fact]
    public void GetVblankStatus_NeverRegressesPumpDrivenCountAndFillsStruct()
    {
        var ctx = NewContext(out var memory);
        var handle = OpenPort(ctx);
        try
        {
            // Poison the destination so zero-cleared padding is observable.
            for (ulong offset = 0; offset < VblankStatusSize; offset += sizeof(ulong))
            {
                memory.WriteUInt64(StatusAddress + offset, ulong.MaxValue);
            }

            // A stored count far beyond the wall-clock estimate must win.
            const ulong pumpCount = 1_000_000;
            VideoOutExports.SetVblankCountForTests(handle, pumpCount);

            ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            ctx[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(ctx));

            var status = ReadStatus(memory);
            var count = BitConverter.ToUInt64(status, 0x00);
            var elapsedMicroseconds = BitConverter.ToUInt64(status, 0x08);
            var timestamp = BitConverter.ToUInt64(status, 0x10);

            Assert.True(count >= pumpCount);
            Assert.True(elapsedMicroseconds < 600_000_000);
            Assert.NotEqual(0UL, timestamp);
            Assert.Equal(0UL, BitConverter.ToUInt64(status, 0x18));
            Assert.Equal(0, status[0x20]); // flags: not inside a vblank window
            for (var i = 0x21; i < VblankStatusSize; i++)
            {
                Assert.Equal(0, status[i]);
            }

            // A second query must never report a smaller count.
            ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            ctx[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(ctx));
            var secondCount = BitConverter.ToUInt64(ReadStatus(memory), 0x00);
            Assert.True(secondCount >= count);
        }
        finally
        {
            ClosePort(ctx, handle);
        }
    }

    [Fact]
    public void GetVblankStatus_CountAdvancesWithWallClock()
    {
        var ctx = NewContext(out var memory);
        var handle = OpenPort(ctx);
        try
        {
            Thread.Sleep(120);

            ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            ctx[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(ctx));

            var status = ReadStatus(memory);
            var count = BitConverter.ToUInt64(status, 0x00);
            var elapsedMicroseconds = BitConverter.ToUInt64(status, 0x08);

            // 120 ms at 60 Hz is 7 intervals; allow generous scheduler slack.
            Assert.True(count >= 4);
            Assert.True(elapsedMicroseconds >= 100_000);
        }
        finally
        {
            ClosePort(ctx, handle);
        }
    }

    [Fact]
    public void GetVblankStatus_NullPointer_ReturnsInvalidAddress()
    {
        var ctx = NewContext(out _);
        var handle = OpenPort(ctx);
        try
        {
            ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(ErrorInvalidAddress, VideoOutExports.VideoOutGetVblankStatus(ctx));
        }
        finally
        {
            ClosePort(ctx, handle);
        }
    }

    [Fact]
    public void GetVblankStatus_UnknownHandle_ReturnsInvalidHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x7FFF;
        ctx[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(ErrorInvalidHandle, VideoOutExports.VideoOutGetVblankStatus(ctx));
    }

    [Fact]
    public void IsOutputSupported_MainBusOnly()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0; // SceVideoOutBusTypeMain
        Assert.Equal(1, VideoOutExports.VideoOutIsOutputSupported(ctx));

        ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, VideoOutExports.VideoOutIsOutputSupported(ctx));
    }
}
