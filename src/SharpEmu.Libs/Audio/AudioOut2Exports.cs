// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    private const int AudioOut2ContextParamSize = 0x40;
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const uint AudioOut2DefaultQueueDepth = 1;
    private const uint AudioOut2GrainSamples = 512;
    private const uint AudioOut2SampleRate = 48000;
    private const int AudioOut2ErrorInvalidParam = unchecked((int)0x80268001);
    private const int AudioOut2ErrorNotReady = unchecked((int)0x80268008);
    private const int AudioOut2ErrorInvalidPort = unchecked((int)0x80268009);
    private const int AudioOut2ErrorInvalidSampleFrequency = unchecked((int)0x8026800A);
    private const int AudioOut2ErrorInvalidFormat = unchecked((int)0x8026800E);
    private const int AudioOut2ErrorInvalidUser = unchecked((int)0x80268010);
    private const int AudioOut2ErrorInvalidPortType = unchecked((int)0x80268011);
    private const int AudioOut2ErrorPortFull = unchecked((int)0x80268012);
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;

    private sealed class AudioOut2PortState
    {
        public ulong ContextHandle;
        public ushort PortType;
        public uint DataFormat;
        public uint SamplingFrequency;
        public uint Flags;
        public ulong UserHandle;
        public ulong PcmData;
    }

    private static readonly ConcurrentDictionary<ulong, AudioOut2PortState> Ports = new();
    private static readonly ConcurrentDictionary<ulong, byte> Users = new();

    // ContextAdvance stages one queue entry and ContextPush commits all staged
    // entries. Committed audio drains at the configured grain cadence.
    private sealed class AudioOut2ContextState
    {
        public uint MaxPorts;
        public uint QueueDepth = AudioOut2DefaultQueueDepth;
        public uint NumGrains = AudioOut2GrainSamples;
        public uint Committed;
        public uint Pending;
        public long LastUpdateMicros;
        public float DownmixSpreadRadius = 0.1f;
        public bool DownmixSpreadHeightAware;
        public float AmbisonicsDownmixSpreadHeightAwareOff;
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
        if (state.LastUpdateMicros == 0 || state.Committed == 0)
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
        var drained = Math.Min(state.Committed, (uint)Math.Min(elapsed / grainMicros, uint.MaxValue));
        if (drained == 0)
        {
            return;
        }

        state.Committed -= drained;
        state.LastUpdateMicros += drained * grainMicros;
        if (state.Committed == 0)
        {
            state.LastUpdateMicros = now;
        }
    }

    private static bool TryReadContextParam(
        CpuContext ctx,
        ulong address,
        out uint maxPorts,
        out uint maxObjectPorts,
        out uint guaranteeObjectPorts,
        out uint queueDepth,
        out uint numGrains,
        out uint flags)
    {
        maxPorts = 0;
        maxObjectPorts = 0;
        guaranteeObjectPorts = 0;
        queueDepth = 0;
        numGrains = 0;
        flags = 0;
        return ctx.TryReadUInt32(address + 0x00, out maxPorts) &&
            ctx.TryReadUInt32(address + 0x04, out maxObjectPorts) &&
            ctx.TryReadUInt32(address + 0x08, out guaranteeObjectPorts) &&
            ctx.TryReadUInt32(address + 0x0C, out queueDepth) &&
            ctx.TryReadUInt32(address + 0x10, out numGrains) &&
            ctx.TryReadUInt32(address + 0x14, out flags);
    }

    private static bool ContextParamIsValid(
        uint maxPorts,
        uint maxObjectPorts,
        uint guaranteeObjectPorts,
        uint queueDepth,
        uint numGrains,
        uint flags)
    {
        // libSceAudioOut.sprx:0x26D75-0x26DD3 validates the SDK 4.00
        // SceAudioOut2ContextParam prefix before allocating any resources.
        if (maxPorts > 32 || maxObjectPorts < guaranteeObjectPorts || queueDepth == 0 ||
            numGrains < 0x100 || (numGrains & 0xFF) != 0)
        {
            return false;
        }

        if (maxObjectPorts == 0)
        {
            return numGrains <= 0x800;
        }

        return guaranteeObjectPorts == 0 && flags is 1 or 2 && numGrains <= 0x400;
    }

    private static bool TryReadSingle(CpuContext ctx, ulong address, out float value)
    {
        if (ctx.TryReadUInt32(address, out var bits))
        {
            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return true;
        }

        value = 0;
        return false;
    }

    private static bool IsAmbisonicsValue(uint value)
    {
        return value == uint.MaxValue || value <= 15 || value is >= 64 and <= 99;
    }

    private static int ValidatePortParam(ushort portType, uint dataFormat, uint samplingFrequency, uint flags)
    {
        var objectPort = (portType & 0x100) != 0;
        var type = portType & 0xFF;
        if (objectPort ? portType is not (0x100 or 0x102 or 0x104) : type > 6)
        {
            return AudioOut2ErrorInvalidPortType;
        }

        var channels = (dataFormat >> 8) & 0x0F;
        var dataType = dataFormat & 0x7F;
        var standardLayout = (dataFormat & 0x80) != 0;
        if (dataType > 1 || (standardLayout && channels != 8))
        {
            return AudioOut2ErrorInvalidFormat;
        }

        var channelsValid = objectPort
            ? channels == 1
            : type switch
            {
                0 or 1 or 5 => channels is 1 or 2 or 8,
                2 or 4 => channels is 1 or 2,
                3 => channels == 1,
                6 => channels == 2,
                _ => false,
            };
        if (!channelsValid)
        {
            return AudioOut2ErrorInvalidFormat;
        }

        if (samplingFrequency != AudioOut2SampleRate)
        {
            return AudioOut2ErrorInvalidSampleFrequency;
        }

        // Object ports accept only SCE_AUDIO_OUT2_PORT_PARAM_FLAG_RESTRICTED;
        // ordinary ports also accept CHANNEL_PASSTHROUGH.
        var allowedFlags = objectPort ? 1u : 3u;
        return (flags & ~allowedFlags) == 0 ? 0 : AudioOut2ErrorInvalidParam;
    }

    private static int ValidatePortAttribute(
        CpuContext ctx,
        AudioOut2PortState port,
        ulong attributeAddress,
        out ulong pcmData)
    {
        pcmData = 0;
        if (!ctx.TryReadUInt32(attributeAddress, out var attributeId) ||
            !ctx.TryReadUInt64(attributeAddress + 0x08, out var valueAddress) ||
            !ctx.TryReadUInt64(attributeAddress + 0x10, out var valueSize))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (attributeId == 47)
        {
            attributeId = 0;
        }

        switch (attributeId)
        {
            case 0: // SCE_AUDIO_OUT2_PORT_ATTRIBUTE_ID_PCM
                if (valueAddress == 0 || valueSize < 8)
                {
                    return AudioOut2ErrorInvalidParam;
                }

                if (!ctx.TryReadUInt64(valueAddress, out pcmData))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                return pcmData == 0 ? AudioOut2ErrorInvalidParam : 0;

            case 1: // gain: one non-negative finite float per channel
            {
                var channels = (port.DataFormat >> 8) & 0x0F;
                if (valueAddress == 0 || valueSize < channels * sizeof(float))
                {
                    return AudioOut2ErrorInvalidParam;
                }

                for (var channel = 0u; channel < channels; channel++)
                {
                    if (!TryReadSingle(ctx, valueAddress + channel * sizeof(float), out var gain))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    if (!float.IsFinite(gain) || gain < 0)
                    {
                        return AudioOut2ErrorInvalidParam;
                    }
                }

                return 0;
            }

            case 2: // priority
                return valueAddress != 0 && valueSize >= 4 ? 0 : AudioOut2ErrorInvalidParam;

            case 3: // position
                if (valueAddress == 0 || valueSize < 12)
                {
                    return AudioOut2ErrorInvalidParam;
                }

                for (var component = 0u; component < 3; component++)
                {
                    if (!TryReadSingle(ctx, valueAddress + component * sizeof(float), out var position))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    if (!float.IsFinite(position))
                    {
                        return AudioOut2ErrorInvalidParam;
                    }
                }

                return 0;

            case 4: // spread
            case 10: // mix-to-main gain
                if (valueAddress == 0 || valueSize < 4)
                {
                    return AudioOut2ErrorInvalidParam;
                }

                if (!TryReadSingle(ctx, valueAddress, out var scalar))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                return float.IsFinite(scalar) && scalar >= 0 ? 0 : AudioOut2ErrorInvalidParam;

            case 5: // passthrough
                if (valueAddress == 0 || valueSize < 4)
                {
                    return AudioOut2ErrorInvalidParam;
                }

                return ctx.TryReadUInt32(valueAddress, out var passthrough)
                    ? passthrough < 5 ? 0 : AudioOut2ErrorInvalidParam
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

            case 6: // reset state
                return valueAddress == 0 && valueSize == 0 ? 0 : AudioOut2ErrorInvalidParam;

            case 7: // application-specific
                return valueAddress != 0 ? 0 : AudioOut2ErrorInvalidParam;

            case 8: // ambisonics
                if (valueAddress == 0 || valueSize < 4)
                {
                    return AudioOut2ErrorInvalidParam;
                }

                return ctx.TryReadUInt32(valueAddress, out var ambisonics)
                    ? IsAmbisonicsValue(ambisonics) ? 0 : AudioOut2ErrorInvalidParam
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

            case 9: // restricted
                return valueAddress != 0 && valueSize >= 4 ? 0 : AudioOut2ErrorInvalidParam;

            case 11: // debug name
                return valueAddress != 0 && valueSize <= 16 ? 0 : AudioOut2ErrorInvalidParam;

            default:
                return AudioOut2ErrorInvalidParam;
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
    // harmlessly. HOWEVER this is OPT-IN (default off): parking the mixer
    // deadlocks the audio pipeline before the game reaches its title screen --
    // SceSndzRenderThread and sndz_stream_task_service block on event flags the
    // running mixer signals, so refusing the port stalls the whole game (boot
    // titledx1: global stall at material setup, PROCESS EXIT code=4). The mixer
    // crash it avoids only occurs ~79 min in, long after the title is rendered,
    // so the park is not needed for the menu. Set SHARPEMU_DISABLE_SNDZ=1 to
    // park the mixer for a long-run stability experiment; unset or any other
    // value runs audio-out normally. Read per call: port creation is rare
    // (init/retry cadence) and tests flip the variable at runtime.
    private static bool SndzAudioOutDisabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_SNDZ"),
            "1",
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
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        // libSceAudioOut.sprx:0xE675 loads {8, 0, 0, 1}, writes numGrains
        // at +0x10, and clears the remainder of SceAudioOut2ContextParam.
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], AudioOut2DefaultQueueDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 0x100);

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
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
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
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!TryReadContextParam(
                ctx,
                paramAddress,
                out var maxPorts,
                out var maxObjectPorts,
                out var guaranteeObjectPorts,
                out var queueDepth,
                out var numGrains,
                out var flags))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!ContextParamIsValid(
                maxPorts,
                maxObjectPorts,
                guaranteeObjectPorts,
                queueDepth,
                numGrains,
                flags))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!ctx.TryWriteUInt64(outContextAddress, ulong.MaxValue))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Contexts[handle] = new AudioOut2ContextState
        {
            MaxPorts = maxPorts,
            QueueDepth = queueDepth,
            NumGrains = numGrains,
            LastUpdateMicros = NowMicros(),
        };
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
        var contextHandle = ctx[CpuRegister.Rdi];
        Contexts.TryRemove(contextHandle, out _);
        foreach (var port in Ports)
        {
            if (port.Value.ContextHandle == contextHandle)
            {
                Ports.TryRemove(port.Key, out _);
            }
        }

        Trace($"context_destroy handle={contextHandle}");
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
        // port rdx). The SDK and firmware agree on the PortParam layout: u16
        // port_type at +0x00 (low byte = output; 0x100 bit = object port),
        // dataFormat at +0x04, samplingFreq at +0x08, flags at +0x0C, and
        // userHandle at +0x10.
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        if (paramAddress == 0 || outPortAddress == 0)
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Contexts.TryGetValue(contextHandle, out var contextState))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
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
            !ctx.TryReadUInt32(paramAddress + 0x04, out var dataFormat) ||
            !ctx.TryReadUInt32(paramAddress + 0x08, out var samplingFrequency) ||
            !ctx.TryReadUInt32(paramAddress + 0x0C, out var flags) ||
            !ctx.TryReadUInt64(paramAddress + 0x10, out var userHandle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!Users.ContainsKey(userHandle))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidUser);
        }

        // sceAudioOut2LoPortCreate writes SCE_AUDIO_OUT2_PORT_HANDLE_INVALID
        // before validating the port fields (libSceAudioOut.sprx:0x15681).
        if (!ctx.TryWriteUInt64(outPortAddress, ulong.MaxValue))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var validationResult = ValidatePortParam(portType, dataFormat, samplingFrequency, flags);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        var portCount = 0u;
        foreach (var port in Ports.Values)
        {
            if (port.ContextHandle == contextHandle)
            {
                portCount++;
            }
        }

        if (portCount >= contextState.MaxPorts)
        {
            return ctx.SetReturn(AudioOut2ErrorPortFull);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0x7FFF_FFFF;
        var handle = ((contextHandle & uint.MaxValue) << 32) | 0x8000_0000UL | portId;
        Ports[handle] = new AudioOut2PortState
        {
            ContextHandle = contextHandle,
            PortType = portType,
            DataFormat = dataFormat,
            SamplingFrequency = samplingFrequency,
            Flags = flags,
            UserHandle = userHandle,
        };
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
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Ports.TryGetValue(handle, out var port))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidPort);
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
        var handle = ctx[CpuRegister.Rdi];
        Users.TryRemove(handle, out _);
        Trace($"user_destroy handle={handle}");
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
        Users[handle] = 0;
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
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = (uint)ctx[CpuRegister.Rdx];
        if (portHandle == 0 || attributesAddress == 0 || attributeCount == 0)
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Ports.TryGetValue(portHandle, out var port))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidPort);
        }

        ulong pcmData = 0;
        var hasPcm = false;
        for (var index = 0u; index < attributeCount; index++)
        {
            var result = ValidatePortAttribute(ctx, port, attributesAddress + index * 0x18, out var attributePcm);
            if (result != 0)
            {
                return ctx.SetReturn(result);
            }

            if (attributePcm != 0)
            {
                pcmData = attributePcm;
                hasPcm = true;
            }
        }

        if (hasPcm)
        {
            port.PcmData = pcmData;
        }

        if (_traceAudio)
        {
            Trace($"port_set_attributes handle=0x{portHandle:X} attributes=0x{attributesAddress:X} count={attributeCount}");
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "NZu1Z2k14DM",
        ExportName = "sceAudioOut2LoContextSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2LoContextSetAttributes(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = (uint)ctx[CpuRegister.Rdx];
        if (attributesAddress == 0)
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        var downmixSpreadRadius = state.DownmixSpreadRadius;
        var downmixSpreadHeightAware = state.DownmixSpreadHeightAware;
        var ambisonicsDownmixSpreadHeightAwareOff = state.AmbisonicsDownmixSpreadHeightAwareOff;
        for (var index = 0u; index < attributeCount; index++)
        {
            var attributeAddress = attributesAddress + index * 0x18;
            if (!ctx.TryReadUInt32(attributeAddress, out var attributeId) ||
                !ctx.TryReadUInt64(attributeAddress + 0x08, out var valueAddress) ||
                !ctx.TryReadUInt64(attributeAddress + 0x10, out var valueSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            switch (attributeId)
            {
                case 0: // downmix spread radius, [0.1, 2.0]
                    if (valueAddress == 0 || valueSize != 4 ||
                        !TryReadSingle(ctx, valueAddress, out var radius))
                    {
                        return ctx.SetReturn(valueAddress == 0 || valueSize != 4
                            ? AudioOut2ErrorInvalidParam
                            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if (!float.IsFinite(radius) || radius < 0.1f || radius > 2.0f)
                    {
                        return ctx.SetReturn(AudioOut2ErrorInvalidParam);
                    }

                    downmixSpreadRadius = radius;
                    break;

                case 1: // downmix spread height-aware
                    if (valueAddress == 0 || valueSize != 0 || !ctx.TryReadByte(valueAddress, out var heightAware))
                    {
                        return ctx.SetReturn(valueAddress == 0 || valueSize != 0
                            ? AudioOut2ErrorInvalidParam
                            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    downmixSpreadHeightAware = heightAware != 0;
                    break;

                case 2: // follow speaker setting; value is intentionally ignored
                    break;

                case 3: // ambisonics height-aware-off value
                    if (valueAddress == 0 || valueSize != 0 ||
                        !TryReadSingle(ctx, valueAddress, out var heightAwareOff))
                    {
                        return ctx.SetReturn(valueAddress == 0 || valueSize != 0
                            ? AudioOut2ErrorInvalidParam
                            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    ambisonicsDownmixSpreadHeightAwareOff = heightAwareOff;
                    break;

                case 30:
                    if (valueAddress == 0 || valueSize != 4)
                    {
                        return ctx.SetReturn(AudioOut2ErrorInvalidParam);
                    }

                    if (!ctx.TryReadUInt32(valueAddress, out var portCount))
                    {
                        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if (portCount > state.MaxPorts)
                    {
                        return ctx.SetReturn(AudioOut2ErrorInvalidParam);
                    }

                    break;

                case 31:
                    if (valueAddress == 0 || valueSize != 4)
                    {
                        return ctx.SetReturn(AudioOut2ErrorInvalidParam);
                    }

                    if (!TryReadSingle(ctx, valueAddress, out var scalar))
                    {
                        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if (!float.IsFinite(scalar) || scalar < 0)
                    {
                        return ctx.SetReturn(AudioOut2ErrorInvalidParam);
                    }

                    break;

                default:
                    return ctx.SetReturn(AudioOut2ErrorInvalidParam);
            }
        }

        state.DownmixSpreadRadius = downmixSpreadRadius;
        state.DownmixSpreadHeightAware = downmixSpreadHeightAware;
        state.AmbisonicsDownmixSpreadHeightAwareOff = ambisonicsDownmixSpreadHeightAwareOff;
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
        if (contextHandle == 0 || blocking > 1)
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        for (; ; )
        {
            uint sleepMicros;
            lock (state)
            {
                var now = NowMicros();
                DrainQueueLocked(state, now);
                sleepMicros = GrainMicros(state.NumGrains);
                if (state.Pending != 0)
                {
                    if (state.Committed == 0)
                    {
                        state.LastUpdateMicros = now;
                    }

                    state.Committed += state.Pending;
                    state.Pending = 0;
                }

                if (blocking == 0 || state.Committed < state.QueueDepth)
                {
                    if (_traceAudio)
                    {
                        Trace($"context_push handle={contextHandle} blocking={blocking} -> queued={state.Committed}");
                    }
                    return ctx.SetReturn(0);
                }
            }

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
        var outAvailableAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 || (outLevelAddress == 0 && outAvailableAddress == 0))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        uint level;
        uint available;
        lock (state)
        {
            DrainQueueLocked(state, NowMicros());
            level = state.Committed + state.Pending;
            available = level < state.QueueDepth ? state.QueueDepth - level : 0;
        }

        if (outLevelAddress != 0 && !ctx.TryWriteUInt32(outLevelAddress, level))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (outAvailableAddress != 0 && !ctx.TryWriteUInt32(outAvailableAddress, available))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceAudio)
        {
            Trace($"context_get_queue_level handle={contextHandle} -> level={level} available={available}");
        }

        // The high-level wrapper at 0x28467-0x28485 reports NOT_READY when
        // fewer than one complete queue is available, after writing both outs.
        return ctx.SetReturn(outAvailableAddress != 0 && available == 0 ? AudioOut2ErrorNotReady : 0);
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
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        if (!Contexts.TryGetValue(contextHandle, out var state))
        {
            return ctx.SetReturn(AudioOut2ErrorInvalidParam);
        }

        lock (state)
        {
            DrainQueueLocked(state, NowMicros());
            if (state.Committed + state.Pending >= state.QueueDepth)
            {
                return ctx.SetReturn(AudioOut2ErrorNotReady);
            }

            // sceAudioOut2LoContextAdvance increments the staged count at
            // context+0xC34 (0xF9C9); GetQueueLevel includes that staged count.
            state.Pending++;
        }

        if (_traceAudio)
        {
            Trace($"context_advance handle={contextHandle}");
        }
        return ctx.SetReturn(0);
    }
}
