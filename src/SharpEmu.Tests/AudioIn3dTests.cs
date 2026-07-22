// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AudioIn3dTests : IDisposable
{
    private const ulong BufferAddress = 0x2000;
    private const ulong ParameterAddress = 0x4000;
    private const ulong OutputAddress = 0x5000;

    public AudioIn3dTests()
    {
        AudioInExports.ResetForTests();
        Audio3dExports.ResetForTests();
    }

    public void Dispose()
    {
        AudioInExports.ResetForTests();
        Audio3dExports.ResetForTests();
    }

    private static CpuContext NewContext() => new(new SparseGuestMemory(), Generation.Gen5);
    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void AudioInOpenAndInput_ReturnsExactSilentFrameCount()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rcx] = 256;
        ctx[CpuRegister.R8] = 48000;
        ctx[CpuRegister.R9] = 2;

        AudioInExports.sceAudioInOpen(ctx);
        var handle = Result(ctx);
        Assert.True(handle > 0);

        Assert.True(ctx.Memory.TryWrite(BufferAddress, Enumerable.Repeat((byte)0xA5, 1024).ToArray()));
        ctx[CpuRegister.Rdi] = unchecked((uint)handle);
        ctx[CpuRegister.Rsi] = BufferAddress;
        AudioInExports.sceAudioInInput(ctx);

        Assert.Equal(256, Result(ctx));
        var samples = new byte[1024];
        Assert.True(ctx.Memory.TryRead(BufferAddress, samples));
        Assert.All(samples, value => Assert.Equal(0, value));
    }

    [Fact]
    public void AudioInClose_InvalidatesHandle()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rcx] = 128;
        ctx[CpuRegister.R8] = 16000;
        ctx[CpuRegister.R9] = 0;
        AudioInExports.sceAudioInOpen(ctx);
        var handle = unchecked((uint)Result(ctx));

        ctx[CpuRegister.Rdi] = handle;
        AudioInExports.sceAudioInClose(ctx);
        Assert.Equal(0, Result(ctx));
        AudioInExports.sceAudioInClose(ctx);
        Assert.Equal(unchecked((int)0x80260109), Result(ctx));
    }

    [Fact]
    public void Audio3dDefaultParameters_HaveSdkCompatiblePrefix()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = ParameterAddress;
        Audio3dExports.sceAudio3dGetDefaultOpenParameters(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(ParameterAddress, out var size));
        Assert.True(ctx.TryReadUInt32(ParameterAddress + 8, out var granularity));
        Assert.True(ctx.TryReadUInt32(ParameterAddress + 16, out var maxObjects));
        Assert.True(ctx.TryReadUInt32(ParameterAddress + 20, out var queueDepth));
        Assert.Equal(0x20u, size);
        Assert.Equal(0x100u, granularity);
        Assert.Equal(0x200u, maxObjects);
        Assert.Equal(2u, queueDepth);
    }

    [Fact]
    public void Audio3dPortObjectsAndQueue_KeepCoherentMonotonicState()
    {
        var ctx = NewContext();
        Audio3dExports.sceAudio3dInitialize(ctx);
        Assert.Equal(0, Result(ctx));

        Assert.True(ctx.TryWriteUInt64(ParameterAddress, 0x20));
        Assert.True(ctx.TryWriteUInt32(ParameterAddress + 8, 0x100));
        Assert.True(ctx.TryWriteUInt32(ParameterAddress + 12, 0));
        Assert.True(ctx.TryWriteUInt32(ParameterAddress + 16, 4));
        Assert.True(ctx.TryWriteUInt32(ParameterAddress + 20, 2));
        Assert.True(ctx.TryWriteUInt32(ParameterAddress + 24, 2));
        ctx[CpuRegister.Rdi] = 0xFF;
        ctx[CpuRegister.Rsi] = ParameterAddress;
        ctx[CpuRegister.Rdx] = OutputAddress;
        Audio3dExports.sceAudio3dPortOpen(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var port));

        ctx[CpuRegister.Rdi] = port;
        ctx[CpuRegister.Rsi] = OutputAddress + 8;
        Audio3dExports.sceAudio3dObjectReserve(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress + 8, out var firstObject));
        Audio3dExports.sceAudio3dObjectReserve(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress + 8, out var secondObject));
        Assert.True(secondObject > firstObject);

        ctx[CpuRegister.Rdi] = port;
        Audio3dExports.sceAudio3dPortAdvance(ctx);
        Audio3dExports.sceAudio3dPortAdvance(ctx);
        ctx[CpuRegister.Rsi] = OutputAddress + 16;
        ctx[CpuRegister.Rdx] = OutputAddress + 20;
        Audio3dExports.sceAudio3dPortGetQueueLevel(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress + 16, out var queued));
        Assert.True(ctx.TryReadUInt32(OutputAddress + 20, out var available));
        Assert.Equal(2u, queued);
        Assert.Equal(0u, available);

        ctx[CpuRegister.Rdi] = port;
        ctx[CpuRegister.Rsi] = 0;
        Audio3dExports.sceAudio3dPortPush(ctx);
        ctx[CpuRegister.Rsi] = OutputAddress + 24;
        ctx[CpuRegister.Rdx] = 0;
        Audio3dExports.sceAudio3dPortGetQueueLevel(ctx);
        Assert.True(ctx.TryReadUInt32(OutputAddress + 24, out queued));
        Assert.Equal(1u, queued);
    }
}
