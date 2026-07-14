// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// libSceAudioOut2 per-frame update surface: PortSetAttributes, ContextPush,
/// ContextGetQueueLevel, and ContextAdvance argument validation and return
/// semantics against handles minted by the existing create calls.
/// </summary>
public sealed class AudioOut2Tests
{
    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static ulong CreatePort(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0; // type
        ctx[CpuRegister.Rsi] = 0x3000; // param address (unused content)
        ctx[CpuRegister.Rdx] = 0x3100; // out port address
        ctx[CpuRegister.Rcx] = 1; // context handle placeholder
        AudioOut2Exports.AudioOut2PortCreate(ctx);
        ctx.TryReadUInt64(0x3100, out var port);
        return port;
    }

    private static ulong CreateContext(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0x4000; // param address
        ctx[CpuRegister.Rsi] = 0x5000; // memory address
        ctx[CpuRegister.Rdx] = 0x10000; // memory size
        ctx[CpuRegister.Rcx] = 0x4100; // out context address
        AudioOut2Exports.AudioOut2ContextCreate(ctx);
        ctx.TryReadUInt64(0x4100, out var context);
        return context;
    }

    [Fact]
    public void PortSetAttributes_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void PortSetAttributes_SucceedsForRealPort()
    {
        var ctx = NewContext(out _);
        var port = CreatePort(ctx);

        ctx[CpuRegister.Rdi] = port;
        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextPush_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2ContextPush(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void ContextPush_SucceedsForRealContext()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        AudioOut2Exports.AudioOut2ContextPush(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextAdvance_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2ContextAdvance(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void ContextAdvance_SucceedsForRealContext()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        AudioOut2Exports.AudioOut2ContextAdvance(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextGetQueueLevel_RejectsNullHandleOrOutPointer()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x6000;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void ContextGetQueueLevel_WritesLevelAndCapacity()
    {
        var ctx = NewContext(out var memory);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 0x6010;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x6000, out var level));
        Assert.Equal(0u, level);
        Assert.True(ctx.TryReadUInt32(0x6010, out var capacity));
        Assert.True(capacity > 0);
    }

    [Fact]
    public void ContextGetQueueLevel_ToleratesNullCapacityOutput()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 0;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextGetQueueLevel_AlwaysReportsEmpty_PushAndAdvanceDoNotChangeIt()
    {
        // Real hardware drains this queue via DMA essentially instantly from
        // the CPU's perspective, so it never meaningfully backs up; Push and
        // Advance are accepted but do not affect what GetQueueLevel reports.
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        AudioOut2Exports.AudioOut2ContextPush(ctx);
        AudioOut2Exports.AudioOut2ContextPush(ctx);
        AudioOut2Exports.AudioOut2ContextAdvance(ctx);

        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 0;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
        Assert.True(ctx.TryReadUInt32(0x6000, out var level));
        Assert.Equal(0u, level);
    }
}
