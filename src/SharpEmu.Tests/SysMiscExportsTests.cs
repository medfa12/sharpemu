// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.HLE;
using SharpEmu.Libs.Compression;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Np;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Tests;

public sealed class SysMiscExportsTests : IDisposable
{
    public SysMiscExportsTests()
    {
        ZExports.ResetForTests();
        PngDecExports.ResetForTests();
        PngEncExports.ResetForTests();
        JpegEncExports.ResetForTests();
        NpPartner001Exports.ResetForTests();
        SystemStateMgrExports.ResetForTests();
    }

    public void Dispose()
    {
        ZExports.ResetForTests();
        PngDecExports.ResetForTests();
        PngEncExports.ResetForTests();
        JpegEncExports.ResetForTests();
        NpPartner001Exports.ResetForTests();
        SystemStateMgrExports.ResetForTests();
    }

    [Fact]
    public void ZlibInflate_CompletesRequestAndReportsExactResult()
    {
        var context = NewContext(out var memory);
        var expected = "SharpEmu PS5 zlib export"u8.ToArray();
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(expected);
            compressed = output.ToArray();
        }

        Assert.True(memory.TryWrite(0x1000, compressed));
        ZExports.Initialize(context);
        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = (uint)compressed.Length;
        context[CpuRegister.Rdx] = 0x2000;
        context[CpuRegister.Rcx] = 2048;
        context[CpuRegister.R8] = 0x3000;
        ZExports.Inflate(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(0x3000, out var requestId));

        context[CpuRegister.Rdi] = 0x3010;
        context[CpuRegister.Rsi] = 0;
        ZExports.WaitForDone(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(0x3010, out var completedId));
        Assert.Equal(requestId, completedId);

        context[CpuRegister.Rdi] = requestId;
        context[CpuRegister.Rsi] = 0x3020;
        context[CpuRegister.Rdx] = 0x3024;
        ZExports.GetResult(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt32(0x3020, out var length));
        Assert.Equal((uint)expected.Length, length);
        Assert.True(context.TryReadInt32(0x3024, out var status));
        Assert.Equal(0, status);
        var actual = new byte[expected.Length];
        Assert.True(memory.TryRead(0x2000, actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PngEncodeParseAndDecode_RoundTripsRgbaPixels()
    {
        var context = NewContext(out var memory);
        Span<byte> create = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(create, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(create[8..], 64);
        BinaryPrimitives.WriteUInt32LittleEndian(create[12..], 5);
        Assert.True(memory.TryWrite(0x1000, create));

        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = 0x2000;
        context[CpuRegister.Rdx] = 16;
        context[CpuRegister.Rcx] = 0x3000;
        PngEncExports.Create(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(0x3000, out var encoder));

        byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 128];
        Assert.True(memory.TryWrite(0x4000, pixels));
        Span<byte> encode = stackalloc byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(encode, 0x4000);
        BinaryPrimitives.WriteUInt64LittleEndian(encode[8..], 0x5000);
        BinaryPrimitives.WriteUInt32LittleEndian(encode[16..], (uint)pixels.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(encode[20..], 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(encode[24..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(encode[28..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(encode[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(encode[38..], 19);
        BinaryPrimitives.WriteUInt16LittleEndian(encode[40..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(encode[46..], 6);
        Assert.True(memory.TryWrite(0x6000, encode));
        context[CpuRegister.Rdi] = encoder;
        context[CpuRegister.Rsi] = 0x6000;
        context[CpuRegister.Rdx] = 0x7000;
        PngEncExports.Encode(context);
        var pngLength = Result(context);
        Assert.True(pngLength > 0);

        Span<byte> parse = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(parse, 0x5000);
        BinaryPrimitives.WriteUInt32LittleEndian(parse[8..], (uint)pngLength);
        Assert.True(memory.TryWrite(0x7100, parse));
        context[CpuRegister.Rdi] = 0x7100;
        context[CpuRegister.Rsi] = 0x7200;
        PngDecExports.ParseHeader(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt32(0x7200, out var width));
        Assert.True(context.TryReadUInt32(0x7204, out var height));
        Assert.Equal(2u, width);
        Assert.Equal(1u, height);

        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = 0x8000;
        context[CpuRegister.Rdx] = 16;
        context[CpuRegister.Rcx] = 0x8100;
        PngDecExports.Create(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(0x8100, out var decoder));
        Span<byte> decode = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(decode, 0x5000);
        BinaryPrimitives.WriteUInt64LittleEndian(decode[8..], 0x9000);
        BinaryPrimitives.WriteUInt32LittleEndian(decode[16..], (uint)pngLength);
        BinaryPrimitives.WriteUInt32LittleEndian(decode[20..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(decode[26..], 255);
        BinaryPrimitives.WriteUInt32LittleEndian(decode[28..], 8);
        Assert.True(memory.TryWrite(0x8200, decode));
        context[CpuRegister.Rdi] = decoder;
        context[CpuRegister.Rsi] = 0x8200;
        context[CpuRegister.Rdx] = 0x8300;
        PngDecExports.Decode(context);
        Assert.Equal(0x20001, Result(context));
        var decoded = new byte[pixels.Length];
        Assert.True(memory.TryRead(0x9000, decoded));
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void JpegCreate_AlignsAndInvalidatesGuestMemoryHandle()
    {
        var context = NewContext(out _);
        context.TryWriteUInt32(0x1000, 8);
        context.TryWriteUInt32(0x1004, 0);
        context[CpuRegister.Rdi] = 0x1000;
        context[CpuRegister.Rsi] = 0x2011;
        context[CpuRegister.Rdx] = 0x800;
        context[CpuRegister.Rcx] = 0x3000;
        JpegEncExports.Create(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(0x3000, out var handle));
        Assert.Equal(0UL, handle & 31);
        Assert.True(context.TryReadUInt64(handle, out var self));
        Assert.Equal(handle, self);

        context[CpuRegister.Rdi] = handle;
        JpegEncExports.Delete(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadUInt64(handle, out self));
        Assert.Equal(0UL, self);
    }

    [Fact]
    public void NpPartner_ReportsNoSubscriptionAndTracksInitialization()
    {
        const int ErrorNotInitialized = unchecked((int)0x819D0001);
        var context = NewContext(out var memory);
        context[CpuRegister.Rsi] = 0x1000;
        NpPartner001Exports.HasSubscription(context);
        Assert.Equal(ErrorNotInitialized, Result(context));

        NpPartner001Exports.Initialize(context);
        Assert.True(memory.TryWrite(0x1000, new byte[] { 0xA5 }));
        context[CpuRegister.Rsi] = 0x1000;
        NpPartner001Exports.HasSubscription(context);
        Assert.Equal(0, Result(context));
        Assert.True(context.TryReadByte(0x1000, out var subscribed));
        Assert.Equal(0, subscribed);
    }

    [Fact]
    public void SystemState_StandbyAndWakeAreObservable()
    {
        var context = NewContext(out _);
        SystemStateMgrExports.EnterStandby(context);
        SystemStateMgrExports.GetCurrentState(context);
        Assert.Equal(1, Result(context));
        SystemStateMgrExports.WakeUp(context);
        SystemStateMgrExports.GetCurrentState(context);
        Assert.Equal(0, Result(context));
    }

    [Fact]
    public void CoredumpUnregister_ClearsRegisteredHandler()
    {
        const int ErrorNotRegistered = unchecked((int)0x81180001);
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0x1234;
        context[CpuRegister.Rsi] = 0x5678;
        KernelExports.CoredumpRegisterHandler(context);
        KernelExports.CoredumpUnregisterHandler(context);
        Assert.Equal(0, Result(context));
        KernelExports.CoredumpUnregisterHandler(context);
        Assert.Equal(ErrorNotRegistered, Result(context));
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext context) => unchecked((int)(uint)context[CpuRegister.Rax]);
}
