// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // Sized from guest evidence, not SDK headers: titles keep the
    // SceAudioOut2ContextParam on the stack with the frame canary close behind
    // it (Quake's sits at param+0x60), and an oversized ResetParam write (the
    // earlier 0x80) zeroes that canary -> __stack_chk_fail kills audio init.
    // Stay well below 0x60 and only write the prefix we populate.
    private const int AudioOut2ContextParamSize = 0x30;
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const uint AudioOut2QueueCapacity = 4;
    private const uint AudioOut2GrainSamples = 512;
    private const uint AudioOut2SampleRate = 48000;
    private const int AudioOut2ErrorNotReady = unchecked((int)0x80268008);
    private const int AudioOut2ErrorPortFull = unchecked((int)0x80268012);
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;

    // Per-port creation info, keyed by the handle we hand out. PortGetState
    // must answer with the port's real type and channel count; deriving them
    // from the handle bits alone broke as soon as PortCreate's argument
    // layout was corrected (see AudioOut2PortCreate).
    private static readonly ConcurrentDictionary<ulong, (ushort PortType, uint DataFormat)> Ports = new();

    // Per-context grain queue, keyed by context handle. The Sndz audio-out
    // loop (eboot 0x800EB3800) renders a grain into every port, then calls
    // ContextAdvance + ContextPush(ctx, blocking=1) + ContextGetQueueLevel.
    // On real hardware Push blocks until the DMA drains a queue slot, so that
    // loop IS the game's real-time audio clock. When Push returned instantly
    // the loop free-ran, the game's audio time raced ahead of wall time, and
    // the A/V sync gate on the main thread stalled presents forever while the
    // audio thread kept spinning. Model Kyty's libAudio2: a queue of depth 4
    // that drains one grain (512 samples @ 48kHz, ~10.67ms) per grain period
    // of wall time.
    private sealed class AudioOut2ContextState
    {
        public uint QueueDepth = AudioOut2QueueCapacity;
        public uint NumGrains = AudioOut2GrainSamples;
        public uint Queued;
        public long LastUpdateMicros;
    }

    private static readonly ConcurrentDictionary<ulong, AudioOut2ContextState> Contexts = new();
    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

    // Test seam: lets unit tests drive the drain clock deterministically.
    internal static Func<long>? MicrosecondClockOverride;

    private static long NowMicros()
    {
        var overrideClock = MicrosecondClockOverride;
        if (overrideClock != null)
        {
            return overrideClock();
        }

        var elapsed = Stopwatch.GetTimestamp() - ClockOrigin;
        return (elapsed / Stopwatch.Frequency) * 1_000_000L
            + (elapsed % Stopwatch.Frequency) * 1_000_000L / Stopwatch.Frequency;
    }

    private static uint GrainMicros(uint numGrains)
    {
        var samples = numGrains == 0 ? AudioOut2GrainSamples : numGrains;
        return Math.Max(samples * 1_000_000u / AudioOut2SampleRate, 1000u);
    }

    // Drain queued grains at the real-time grain rate. Mirrors Kyty's
    // audioout2_update_context_locked; caller must hold the state lock.
    private static void DrainQueueLocked(AudioOut2ContextState state, long now)
    {
        if (state.LastUpdateMicros == 0 || state.Queued == 0)
        {
            state.LastUpdateMicros = now;
            return;
        }

        long grainMicros = GrainMicros(state.NumGrains);
        if (now <= state.LastUpdateMicros)
        {
            return;
        }

        var elapsed = now - state.LastUpdateMicros;
        var drained = Math.Min(state.Queued, (uint)Math.Min(elapsed / grainMicros, uint.MaxValue));
        if (drained == 0)
        {
            return;
        }

        state.Queued -= drained;
        state.LastUpdateMicros += drained * grainMicros;
        if (state.Queued == 0)
        {
            state.LastUpdateMicros = now;
        }
    }

    // Kill switch for the guest-side Sndz audio-out pipeline. Astro Bot's
    // SceSndzAudioOutMain thread (thread main at eboot 0x800EB31B0) creates
    // its first port through a wrapper (0x800EB3E10) whose result is the only
    // init return the thread ever checks (test/jns at 0x800EB32BC): on failure
    // it logs through the Sndz printf hook (0x800DFDC80), usleeps one second,
    // and retries forever, never reaching the render loop at 0x800EB3800 that
    // virtual-calls the mixer render (0x800F60400). That render dies on every
    // long session reading a mixer object whose head has been stomped with
    // {x, y, z, 1.0f} NaN vec4s (index dword at obj+0xC8 holds float bits;
    // AVs at 0x800F6053E / 0x800F60549). Until the stomper is found, failing
    // PortCreate parks the thread in that benign retry loop. Every other
    // PortCreate call site (0x800EB2521, 0x800EB25C4, 0x800EB2BFC, 0x800EB3560,
    // 0x800EB3690) compiles to the same log-then-continue pattern, the engine
    // leaves failed slots at -1 and already calls PortSetAttributes(-1)
    // harmlessly, and the ready byte the thread would set (global+0x51) is
    // never read anywhere in the eboot, so nothing blocks on the port coming
    // up. Set SHARPEMU_DISABLE_SNDZ=0 to mint ports again; unset or any other
    // value keeps audio-out disabled. Read per call: port creation is rare
    // (init/retry cadence) and tests flip the variable at runtime.
    private static bool SndzAudioOutDisabled =>
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_SNDZ"),
            "0",
            StringComparison.Ordinal);

    // Cached once: several of these exports (port_set_attributes, port_get_state,
    // context_push, queue level) run thousands of times per second, and a per-call
    // Environment.GetEnvironmentVariable is a P/Invoke plus a transient string.
    // Hot call sites also check this flag before building their interpolated
    // message so tracing-off costs no allocation at all.
    private static readonly bool _traceAudio =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (_traceAudio)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audioout2.{message}");
        }
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        Trace("initialize");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], AudioOut2ContextParamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 0x400);

        Trace($"context_reset_param param=0x{paramAddress:X}");
        return ctx.Memory.TryWrite(paramAddress, param)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var outMemorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || outMemorySizeAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller expects a single u64 (required memory size) written back
        // through rsi -- not a struct. The earlier 0x20-byte write overran the
        // caller's slot and smashed whatever followed it.
        Trace($"context_query_memory param=0x{paramAddress:X} size=0x{AudioOut2ContextMemorySize:X}");
        return ctx.TryWriteUInt64(outMemorySizeAddress, AudioOut2ContextMemorySize)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Contexts[handle] = new AudioOut2ContextState { LastUpdateMicros = NowMicros() };
        Trace($"context_create param=0x{paramAddress:X} mem=0x{memoryAddress:X} size=0x{memorySize:X} -> handle={handle}");
        return ctx.TryWriteUInt64(outContextAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        Contexts.TryRemove(ctx[CpuRegister.Rdi], out _);
        Trace($"context_destroy handle={ctx[CpuRegister.Rdi]}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        // sceAudioOut2PortCreate(context rdi, const PortParam* rsi, out u64*
        // port rdx). Kyty and Astro Bot's call sites (eboot 0x800EB2587,
        // 0x800EB3F4E+) agree on the PortParam layout: u16 port_type at +0x00
        // (low byte = output: 0 main, 3 pad speaker; 0x100 bit = object
        // port), u32 data_format at +0x04 (channels = (fmt >> 8) & 0xFF),
        // u32 sampling_freq at +0x08, u32 flags, u64 user handle. The old
        // reading treated rdi as a port type, so every port inherited the
        // context handle's value (2) and PortGetState reported the mono pad
        // speaker shape for the game's 12-channel float main port.
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        if (paramAddress == 0 || outPortAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (SndzAudioOutDisabled)
        {
            // PORT_FULL is what the real library reports when no port slot is
            // free (KytyPS5 libAudio2 PortCreate does the same when out of
            // slots), so retry-styled callers treat it as a transient miss.
            // The out slot stays untouched: the Sndz wrapper leaves its port
            // handle at -1 and only stores on success (eboot 0x800EB3FB0).
            Trace($"port_create context={contextHandle} refused (SHARPEMU_DISABLE_SNDZ)");
            return ctx.SetReturn(AudioOut2ErrorPortFull);
        }

        if (!ctx.TryReadUInt16(paramAddress, out var portType) ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out var dataFormat))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)portType << 16) | portId;
        Ports[handle] = (portType, dataFormat);
        Trace($"port_create context={contextHandle} type=0x{portType:X} format=0x{dataFormat:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outPortAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceAudioOut2PortState (0x40 bytes, Kyty layout): u16 output, u8
        // numChannels, u8 pad, i16 volume, u16 rerouteCounter, u32 flags, then
        // reserved zeros. Volume is 0..127; the earlier -1 here fed a negative
        // volume into Astro Bot's mixer gain math. Output and channel count
        // come from the port's creation params (see AudioOut2PortCreate): the
        // low type byte selects the output (3 = pad speaker, bit 6, which the
        // Sndz main loop tests at eboot 0x800EB3890), and the channel count is
        // the data format's (fmt >> 8) & 0xFF with 0 meaning stereo, capped at
        // 16, exactly as Kyty's audioout2_data_format_channels does.
        Ports.TryGetValue(handle, out var port);
        var isPadSpeaker = (port.PortType & 0xFF) == 3;
        var output = isPadSpeaker ? 0x40 : 0x01;
        var channels = (int)((port.DataFormat >> 8) & 0xFF);
        channels = channels == 0 ? 2 : Math.Min(channels, 16);
        Span<byte> state = stackalloc byte[0x40];
        state.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], 127);

        if (_traceAudio)
        {
            Trace($"port_get_state handle=0x{handle:X} type=0x{port.PortType:X} output=0x{output:X} channels={channels}");
        }
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceAudioOut2SpeakerInfo (0x50 bytes, Kyty layout): u8 type, pad[3],
        // u32 availableBits, u32 flags, u32 pad, then 16 {i16 azimuth, i16
        // elevation} speaker angles. Astro Bot's SceSndzAudioOutMain thread
        // re-reads this on its speaker-reconfig path (eboot 0x800DED1A8+): it
        // walks availableBits per channel, converts the i16 azimuths, and on
        // any change rewrites its speaker layout records and panning matrices.
        // The old reply (u32 1, u32 2, u32 48000) marked front-left absent and
        // put a sample rate in the flags field, and the resulting bad rebuild
        // smashed a 16-byte gain row over the propagation mixer's shared_ptr
        // at +0x58 (AV at 0x800F6053E). Stereo is type 0, bits 0x3, angles
        // -30/+30 degrees, matching real hardware defaults.
        Span<byte> info = stackalloc byte[0x50];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 0x3);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x10..], -30);
        BinaryPrimitives.WriteInt16LittleEndian(info[0x14..], 30);

        Trace("get_speaker_info");
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        Ports.TryRemove(ctx[CpuRegister.Rdi], out _);
        Trace($"port_destroy handle=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx)
    {
        Trace($"user_destroy handle={ctx[CpuRegister.Rdi]}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 255) || outUserAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        Trace($"user_create userId={userId} -> handle={handle}");
        return ctx.TryWriteUInt64(outUserAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        var portHandle = ctx[CpuRegister.Rdi];
        if (portHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (_traceAudio)
        {
            Trace($"port_set_attributes handle=0x{portHandle:X} arg1=0x{ctx[CpuRegister.Rsi]:X} arg2=0x{ctx[CpuRegister.Rdx]:X} arg3=0x{ctx[CpuRegister.Rcx]:X}");
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var blocking = (uint)ctx[CpuRegister.Rsi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!Contexts.TryGetValue(contextHandle, out var state))
        {
            // Unknown handle: accept it rather than blocking forever.
            if (_traceAudio)
            {
                Trace($"context_push handle={contextHandle} blocking={blocking} (untracked)");
            }
            return ctx.SetReturn(0);
        }

        for (; ; )
        {
            uint sleepMicros;
            lock (state)
            {
                var now = NowMicros();
                DrainQueueLocked(state, now);
                sleepMicros = GrainMicros(state.NumGrains);
                if (state.Queued < state.QueueDepth)
                {
                    if (state.Queued == 0)
                    {
                        state.LastUpdateMicros = now;
                    }

                    state.Queued++;
                    if (_traceAudio)
                    {
                        Trace($"context_push handle={contextHandle} blocking={blocking} -> queued={state.Queued}");
                    }
                    return ctx.SetReturn(0);
                }
            }

            if (blocking == 0)
            {
                if (_traceAudio)
                {
                    Trace($"context_push handle={contextHandle} blocking=0 -> not_ready");
                }
                return ctx.SetReturn(AudioOut2ErrorNotReady);
            }

            // Queue full: wait one grain period, exactly the cadence the DMA
            // would drain a slot at. This is what paces SceSndzAudioOutMain.
            GuestThreadExecution.Scheduler?.Pump(ctx, "sceAudioOut2ContextPush");
            Thread.Sleep((int)Math.Max(sleepMicros / 1000u, 1u));
        }
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var outLevelAddress = ctx[CpuRegister.Rsi];
        var outCapacityAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 || outLevelAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Report the real, wall-clock-drained queue level (Kyty's model). The
        // second out parameter is the number of free queue slots, not the
        // total capacity.
        var level = 0u;
        var available = AudioOut2QueueCapacity;
        if (Contexts.TryGetValue(contextHandle, out var state))
        {
            lock (state)
            {
                DrainQueueLocked(state, NowMicros());
                level = state.Queued;
                available = state.Queued < state.QueueDepth ? state.QueueDepth - state.Queued : 0;
            }
        }

        if (!ctx.TryWriteUInt32(outLevelAddress, level))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (outCapacityAddress != 0 && !ctx.TryWriteUInt32(outCapacityAddress, available))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceAudio)
        {
            Trace($"context_get_queue_level handle={contextHandle} -> level={level} available={available}");
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (Contexts.TryGetValue(contextHandle, out var state))
        {
            lock (state)
            {
                DrainQueueLocked(state, NowMicros());
            }
        }

        if (_traceAudio)
        {
            Trace($"context_advance handle={contextHandle}");
        }
        return ctx.SetReturn(0);
    }
}
