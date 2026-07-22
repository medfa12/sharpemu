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
    private const string DisableSndzVariable = "SHARPEMU_DISABLE_SNDZ";

    /// <summary>
    /// Sndz audio-out runs by default; parking the mixer (SHARPEMU_DISABLE_SNDZ=1)
    /// is an opt-in long-run stability experiment because refusing the port
    /// deadlocks the audio pipeline before the title screen. Ports are minted
    /// unless the variable is exactly "1".
    /// </summary>
    private static EnvScope SndzEnabled() => new EnvScope(DisableSndzVariable, null);

    private static EnvScope SndzDisabled(string? value) => new EnvScope(DisableSndzVariable, value);

    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private const int AudioOut2ErrorInvalidParam = unchecked((int)0x80268001);
    private const int AudioOut2ErrorNotReady = unchecked((int)0x80268008);
    private const int AudioOut2ErrorInvalidPort = unchecked((int)0x80268009);
    private const int AudioOut2ErrorPortFull = unchecked((int)0x80268012);

    private static ulong CreateUser(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x3200;
        AudioOut2Exports.AudioOut2UserCreate(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x3200, out var user));
        return user;
    }

    private static ulong CreatePort(CpuContext ctx)
    {
        // sceAudioOut2PortCreate(context, const PortParam*, out u64*): the
        // param struct carries the u16 port type at +0x00 and the u32 data
        // format at +0x04 (main port, stereo float here).
        using var _ = SndzEnabled();
        var context = CreateContext(ctx);
        var user = CreateUser(ctx);
        ctx.TryWriteUInt16(0x3000, 0); // port type: main
        ctx.TryWriteUInt32(0x3004, 0x200); // data format: 2-channel float
        ctx.TryWriteUInt32(0x3008, 48000);
        ctx.TryWriteUInt32(0x300C, 0);
        ctx.TryWriteUInt64(0x3010, user);
        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x3000; // param address
        ctx[CpuRegister.Rdx] = 0x3100; // out port address
        AudioOut2Exports.AudioOut2PortCreate(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x3100, out var port));
        return port;
    }

    private static ulong CreateContext(CpuContext ctx)
    {
        // SDK 4.00 SceAudioOut2ContextParam: maxPorts +0x00,
        // queueDepth +0x0C, numGrains +0x10.
        Assert.True(ctx.Memory.TryWrite(0x4000, new byte[0x40]));
        ctx.TryWriteUInt32(0x4000, 8);
        ctx.TryWriteUInt32(0x400C, 4);
        ctx.TryWriteUInt32(0x4010, 512);
        ctx[CpuRegister.Rdi] = 0x4000; // param address
        ctx[CpuRegister.Rsi] = 0x5000; // memory address
        ctx[CpuRegister.Rdx] = 0x10000; // memory size
        ctx[CpuRegister.Rcx] = 0x4100; // out context address
        AudioOut2Exports.AudioOut2ContextCreate(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x4100, out var context));
        Assert.NotEqual(0UL, context);
        return context;
    }

    [Fact]
    public void ContextResetParam_WritesSdkLayoutAndSize()
    {
        var ctx = NewContext(out _);
        Assert.True(ctx.TryWriteUInt64(0x4040, 0xDEADBEEFCAFEF00D));
        ctx[CpuRegister.Rdi] = 0x4000;

        AudioOut2Exports.AudioOut2ContextResetParam(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x4000, out var maxPorts));
        Assert.Equal(8u, maxPorts);
        Assert.True(ctx.TryReadUInt32(0x400C, out var queueDepth));
        Assert.Equal(1u, queueDepth);
        Assert.True(ctx.TryReadUInt32(0x4010, out var numGrains));
        Assert.Equal(0x100u, numGrains);
        Assert.True(ctx.TryReadUInt64(0x4040, out var sentinel));
        Assert.Equal(0xDEADBEEFCAFEF00D, sentinel);
    }

    [Fact]
    public void PortCreate_RefusedWhenParked_AndLeavesOutSlotUntouched()
    {
        using var scope = SndzDisabled("1");
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);
        var user = CreateUser(ctx);

        // The Sndz wrapper keeps its port slot at -1 until PortCreate stores a
        // handle; a refused create must not write through the out pointer.
        ctx.TryWriteUInt64(0x3100, ulong.MaxValue);
        ctx.TryWriteUInt16(0x3000, 0);
        ctx.TryWriteUInt32(0x3004, 0x200);
        ctx.TryWriteUInt32(0x3008, 48000);
        ctx.TryWriteUInt64(0x3010, user);
        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x3000;
        ctx[CpuRegister.Rdx] = 0x3100;
        AudioOut2Exports.AudioOut2PortCreate(ctx);

        Assert.Equal(AudioOut2ErrorPortFull, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x3100, out var slot));
        Assert.Equal(ulong.MaxValue, slot);
    }

    [Fact]
    public void PortCreate_RefusedWhenVariableIsNotZero()
    {
        using var scope = SndzDisabled("1");
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);
        var user = CreateUser(ctx);

        ctx.TryWriteUInt16(0x3000, 0);
        ctx.TryWriteUInt32(0x3004, 0x200);
        ctx.TryWriteUInt32(0x3008, 48000);
        ctx.TryWriteUInt64(0x3010, user);
        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x3000;
        ctx[CpuRegister.Rdx] = 0x3100;
        AudioOut2Exports.AudioOut2PortCreate(ctx);

        Assert.Equal(AudioOut2ErrorPortFull, Result(ctx));
    }

    [Fact]
    public void PortCreate_NullArgumentsStillRejectedWhileParked()
    {
        using var scope = SndzDisabled("1");
        var ctx = NewContext(out _);

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0x3100;
        AudioOut2Exports.AudioOut2PortCreate(ctx);

        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void PortCreate_MintsPortWhenExplicitlyEnabled()
    {
        var ctx = NewContext(out _);

        var port = CreatePort(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.NotEqual(0UL, port);
    }

    [Fact]
    public void PortSetAttributes_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);

        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void PortSetAttributes_SucceedsForRealPort()
    {
        var ctx = NewContext(out _);
        var port = CreatePort(ctx);

        ctx.TryWriteUInt64(0x3400, 0x9000); // SceAudioOut2Pcm.data
        ctx.TryWriteUInt32(0x3300, 0); // PCM attributeId
        ctx.TryWriteUInt64(0x3308, 0x3400); // value
        ctx.TryWriteUInt64(0x3310, 8); // valueSize
        ctx[CpuRegister.Rdi] = port;
        ctx[CpuRegister.Rsi] = 0x3300;
        ctx[CpuRegister.Rdx] = 1;
        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextPush_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2ContextPush(ctx);

        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void ContextPush_SucceedsForRealContext()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0;
        AudioOut2Exports.AudioOut2ContextPush(ctx);

        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void ContextAdvance_RejectsNullHandle()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        AudioOut2Exports.AudioOut2ContextAdvance(ctx);

        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
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
        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
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
    public void ContextGetQueueLevel_ToleratesNullLevelOutput()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0x6010;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x6010, out var available));
        Assert.Equal(4u, available);
    }

    [Fact]
    public void LoContextSetAttributes_ValidatesRadiusRangeAndAttributeSize()
    {
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);
        ctx.TryWriteUInt32(0x6200, unchecked((uint)BitConverter.SingleToInt32Bits(0.5f)));
        ctx.TryWriteUInt32(0x6100, 0); // downmix spread radius
        ctx.TryWriteUInt64(0x6108, 0x6200);
        ctx.TryWriteUInt64(0x6110, 4);
        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x6100;
        ctx[CpuRegister.Rdx] = 1;

        AudioOut2Exports.AudioOut2LoContextSetAttributes(ctx);
        Assert.Equal(0, Result(ctx));

        ctx.TryWriteUInt32(0x6200, unchecked((uint)BitConverter.SingleToInt32Bits(2.1f)));
        AudioOut2Exports.AudioOut2LoContextSetAttributes(ctx);
        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));

        ctx.TryWriteUInt32(0x6200, unchecked((uint)BitConverter.SingleToInt32Bits(0.5f)));
        ctx.TryWriteUInt64(0x6110, 8);
        AudioOut2Exports.AudioOut2LoContextSetAttributes(ctx);
        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void PortSetAttributes_RejectsNullPcmAndUnknownPort()
    {
        var ctx = NewContext(out _);
        var port = CreatePort(ctx);
        ctx.TryWriteUInt64(0x3400, 0);
        ctx.TryWriteUInt32(0x3300, 0);
        ctx.TryWriteUInt64(0x3308, 0x3400);
        ctx.TryWriteUInt64(0x3310, 8);
        ctx[CpuRegister.Rdi] = port;
        ctx[CpuRegister.Rsi] = 0x3300;
        ctx[CpuRegister.Rdx] = 1;

        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);
        Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));

        ctx.TryWriteUInt64(0x3400, 0x9000);
        ctx[CpuRegister.Rdi] = 0xDEADBEEF;
        AudioOut2Exports.AudioOut2PortSetAttributes(ctx);
        Assert.Equal(AudioOut2ErrorInvalidPort, Result(ctx));
    }

    [Fact]
    public void PortGetState_WritesKytyLayoutWithNonNegativeVolume()
    {
        var ctx = NewContext(out _);
        var port = CreatePort(ctx);

        ctx[CpuRegister.Rdi] = port;
        ctx[CpuRegister.Rsi] = 0x7000;
        AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt16(0x7000, out var output));
        Assert.Equal(0x01, output); // main output for a non-pad port type
        Assert.True(ctx.TryReadByte(0x7002, out var channels));
        Assert.Equal(2, channels);
        Assert.True(ctx.TryReadUInt16(0x7004, out var volume));
        Assert.Equal(127, unchecked((short)volume)); // i16 volume, never -1
    }

    [Fact]
    public void GetSpeakerInfo_ReportsStereoBitsAndAngles_WithinFiftyBytes()
    {
        var ctx = NewContext(out _);

        // Sentinel right after the 0x50-byte struct; the caller's frame has
        // exactly 0x50 bytes reserved (canary sits at info+0x50 in the eboot).
        Assert.True(ctx.TryWriteUInt64(0x8050, 0xDEADBEEFCAFEF00D));

        ctx[CpuRegister.Rdi] = 0x8000;
        ctx[CpuRegister.Rsi] = 1; // flags argument
        AudioOut2Exports.AudioOut2GetSpeakerInfo(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadByte(0x8000, out var type));
        Assert.Equal(0, type); // stereo speakers
        Assert.True(ctx.TryReadUInt32(0x8004, out var availableBits));
        Assert.Equal(0x3u, availableBits); // front-left | front-right
        Assert.True(ctx.TryReadUInt32(0x8008, out var flags));
        Assert.Equal(0u, flags); // no sample rate smuggled into flags
        Assert.True(ctx.TryReadUInt16(0x8010, out var azimuth0));
        Assert.Equal(-30, unchecked((short)azimuth0));
        Assert.True(ctx.TryReadUInt16(0x8012, out var elevation0));
        Assert.Equal(0, unchecked((short)elevation0));
        Assert.True(ctx.TryReadUInt16(0x8014, out var azimuth1));
        Assert.Equal(30, unchecked((short)azimuth1));
        Assert.True(ctx.TryReadUInt64(0x8050, out var sentinel));
        Assert.Equal(0xDEADBEEFCAFEF00D, sentinel);
    }

    // One grain is 512 samples at 48kHz: 10666 microseconds.
    private const long GrainMicros = 512L * 1_000_000L / 48_000L;

    private static uint ReadQueueLevel(
        CpuContext ctx,
        ulong context,
        out uint available,
        int expectedResult = 0)
    {
        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 0x6010;
        AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
        Assert.Equal(expectedResult, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x6000, out var level));
        Assert.True(ctx.TryReadUInt32(0x6010, out available));
        return level;
    }

    [Fact]
    public void ContextAdvance_StagesGrains_AndPushCommitsThemForDrain()
    {
        // The Sndz audio-out loop (eboot 0x800EB3800) is paced by
        // ContextPush(ctx, blocking=1): each pushed grain must take one grain
        // period (512 samples @ 48kHz) of wall time to drain, or the game's
        // audio clock runs away from real time and the A/V sync gate stalls
        // presents while the audio thread spins.
        long now = 1_000_000; // nonzero: 0 is the "clock unset" sentinel in the drain
        AudioOut2Exports.MicrosecondClockOverride = () => now;
        try
        {
            var ctx = NewContext(out _);
            var context = CreateContext(ctx);

            ctx[CpuRegister.Rdi] = context;
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            Assert.Equal(2u, ReadQueueLevel(ctx, context, out var available));
            Assert.Equal(2u, available);

            ctx[CpuRegister.Rsi] = 0; // commit without waiting
            AudioOut2Exports.AudioOut2ContextPush(ctx);
            Assert.Equal(2u, ReadQueueLevel(ctx, context, out available));
            Assert.Equal(2u, available);

            // One grain period of wall time drains exactly one grain.
            now += GrainMicros;
            Assert.Equal(1u, ReadQueueLevel(ctx, context, out available));
            Assert.Equal(3u, available);

            // Long idle drains the rest but never underflows.
            now += 100 * GrainMicros;
            Assert.Equal(0u, ReadQueueLevel(ctx, context, out available));
            Assert.Equal(4u, available);
        }
        finally
        {
            AudioOut2Exports.MicrosecondClockOverride = null;
        }
    }

    [Fact]
    public void ContextAdvance_ReportsNotReadyWhenFull_AndRecoversAfterDrain()
    {
        long now = 1_000_000; // nonzero: 0 is the "clock unset" sentinel in the drain
        AudioOut2Exports.MicrosecondClockOverride = () => now;
        try
        {
            var ctx = NewContext(out _);
            var context = CreateContext(ctx);

            ctx[CpuRegister.Rdi] = context;
            ctx[CpuRegister.Rsi] = 0; // non-blocking
            for (var i = 0; i < 4; i++)
            {
                AudioOut2Exports.AudioOut2ContextAdvance(ctx);
                Assert.Equal(0, Result(ctx));
                AudioOut2Exports.AudioOut2ContextPush(ctx);
                Assert.Equal(0, Result(ctx));
            }

            Assert.Equal(4u, ReadQueueLevel(ctx, context, out var available, AudioOut2ErrorNotReady));
            Assert.Equal(0u, available);

            // Queue depth is 4: the fifth Advance with frozen time fails.
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            Assert.Equal(AudioOut2ErrorNotReady, Result(ctx));

            // After one grain of wall time a slot frees up again.
            now += GrainMicros;
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            Assert.Equal(0, Result(ctx));
            ctx[CpuRegister.Rsi] = 0;
            AudioOut2Exports.AudioOut2ContextPush(ctx);
            Assert.Equal(0, Result(ctx));
            Assert.Equal(4u, ReadQueueLevel(ctx, context, out _, AudioOut2ErrorNotReady));
        }
        finally
        {
            AudioOut2Exports.MicrosecondClockOverride = null;
        }
    }

    [Fact]
    public void ContextGetQueueLevel_DrainsCommittedQueueOnWallClock()
    {
        long now = 1_000_000; // nonzero: 0 is the "clock unset" sentinel in the drain
        AudioOut2Exports.MicrosecondClockOverride = () => now;
        try
        {
            var ctx = NewContext(out _);
            var context = CreateContext(ctx);

            ctx[CpuRegister.Rdi] = context;
            ctx[CpuRegister.Rsi] = 0;
            for (var index = 0; index < 3; index++)
            {
                AudioOut2Exports.AudioOut2ContextAdvance(ctx);
                AudioOut2Exports.AudioOut2ContextPush(ctx);
            }

            now += 2 * GrainMicros;
            Assert.Equal(1u, ReadQueueLevel(ctx, context, out _));
        }
        finally
        {
            AudioOut2Exports.MicrosecondClockOverride = null;
        }
    }

    [Fact]
    public void ContextPush_Blocking_UnblocksOnRealClockWhenFull()
    {
        // Real clock: fill the queue, then a blocking push must return once a
        // grain (~10.7ms) drains rather than hanging or failing.
        var ctx = NewContext(out _);
        var context = CreateContext(ctx);

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = 1;
        for (var i = 0; i < 4; i++)
        {
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            Assert.Equal(0, Result(ctx));
            AudioOut2Exports.AudioOut2ContextPush(ctx);
            Assert.Equal(0, Result(ctx));
        }
    }

    [Fact]
    public void ContextDestroy_ForgetsQueueState()
    {
        long now = 1_000_000; // nonzero: 0 is the "clock unset" sentinel in the drain
        AudioOut2Exports.MicrosecondClockOverride = () => now;
        try
        {
            var ctx = NewContext(out _);
            var context = CreateContext(ctx);

            ctx[CpuRegister.Rdi] = context;
            ctx[CpuRegister.Rsi] = 0;
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            AudioOut2Exports.AudioOut2ContextPush(ctx);

            ctx[CpuRegister.Rdi] = context;
            AudioOut2Exports.AudioOut2ContextDestroy(ctx);
            Assert.Equal(0, Result(ctx));

            ctx[CpuRegister.Rsi] = 0x6000;
            ctx[CpuRegister.Rdx] = 0x6010;
            AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);
            Assert.Equal(AudioOut2ErrorInvalidParam, Result(ctx));
        }
        finally
        {
            AudioOut2Exports.MicrosecondClockOverride = null;
        }
    }
}
