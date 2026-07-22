// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Rtc;
using SharpEmu.Libs.Videodec;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AvMediaCompatExportsTests
{
    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void VideodecResourceInfo_PacksSizesAndAlignment()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var config = new byte[0x28];
        BinaryPrimitives.WriteUInt64LittleEndian(config, 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(0x14), 1920);
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(0x18), 1080);
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(0x1C), 8);
        Assert.True(memory.TryWrite(0x1000, config));
        Assert.True(ctx.TryWriteUInt64(0x2000, 0x38));

        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = 0x2000;
        VideodecExports.VideodecQueryResourceInfo(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x2008, out var cpuSize));
        Assert.True(ctx.TryReadUInt64(0x2018, out var cpuGpuSize));
        Assert.True(ctx.TryReadUInt64(0x2028, out var frameSize));
        Assert.True(ctx.TryReadUInt32(0x2030, out var alignment));
        Assert.Equal(16UL * 1024 * 1024, cpuSize);
        Assert.True(cpuGpuSize > frameSize);
        Assert.Equal(0UL, frameSize & 0xFF);
        Assert.Equal(0x100u, alignment);
    }

    [Fact]
    public void Videodec2Decoder_DeleteInvalidatesHandle()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(ctx.TryWriteUInt64(0x1000, 0x48));
        Assert.True(ctx.TryWriteUInt32(0x100C, 1));
        Assert.True(ctx.TryWriteUInt32(0x1018, 1280));
        Assert.True(ctx.TryWriteUInt32(0x101C, 720));
        Assert.True(ctx.TryWriteUInt64(0x2000, 0x48));

        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = 0x2000;
        ctx[CpuRegister.Rdx] = 0x3000;
        Videodec2Exports.Videodec2CreateDecoder(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x3000, out var decoder));
        Assert.NotEqual(0UL, decoder);

        ctx[CpuRegister.Rdi] = decoder;
        Videodec2Exports.Videodec2DeleteDecoder(ctx);
        Assert.Equal(0, Result(ctx));
        Videodec2Exports.Videodec2Reset(ctx);
        Assert.Equal(unchecked((int)0x811D0103), Result(ctx));
    }

    [Fact]
    public void RtcRfc3339_FormatAndParseRoundTripsMicroseconds()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var expected = new DateTime(2026, 7, 21, 14, 35, 12, DateTimeKind.Utc).AddTicks(3456780);
        var tick = unchecked((ulong)(expected.Ticks / 10));
        Assert.True(ctx.TryWriteUInt64(0x1000, tick));

        ctx[CpuRegister.Rdi] = 0x2000;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 0;
        RtcExports.RtcFormatRfc3339Precise(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal("2026-07-21T14:35:12.345678Z", ReadAsciiString(ctx, 0x2000));

        ctx[CpuRegister.Rdi] = 0x3000;
        ctx[CpuRegister.Rsi] = 0x2000;
        RtcExports.RtcParseRfc3339(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x3000, out var parsedTick));
        Assert.Equal(tick, parsedTick);
    }

    private static string ReadAsciiString(CpuContext ctx, ulong address)
    {
        var bytes = new List<byte>();
        for (ulong offset = 0; offset < 128; offset++)
        {
            Assert.True(ctx.TryReadByte(address + offset, out var value));
            if (value == 0)
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(value);
        }
        throw new InvalidOperationException("String was not terminated.");
    }
}
