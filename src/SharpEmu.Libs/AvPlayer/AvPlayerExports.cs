// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// sceAvPlayer HLE. The full player state machine is implemented
/// (init/addSource/start/stop/pause/resume plus guest event callbacks).
/// When the guest source resolves to a host file and FFmpeg is available,
/// video is decoded to NV12 frames (guest-allocated textures when the title
/// provides an allocator callback) and audio to 48kHz stereo s16le frames.
/// When the source cannot be resolved or probed, the player falls back to
/// synthetic metadata: the first video poll after start delivers a single
/// black NV12 frame and the next poll raises end-of-stream, so titles that
/// gate progress on their intro video skip straight past it whether they
/// wait for a first frame, for IsActive to drop, or for the StateStop
/// event. A title that never polls data still finishes: IsActive reports
/// the synthetic stream as ended once its synthetic duration elapses.
/// </summary>
public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int FrameBufferCount = 3;
    private const int FrameInfoSize = 40;
    private const int FrameInfoExSize = 104;
    private const int StreamInfoSize = 40;
    private const int MaxGuestPathLength = 4096;
    private const int VideoPitchAlignment = 256;

    // sceAvPlayer state ids delivered to the guest event callback.
    private const ulong StateStop = 1;
    private const ulong StateReady = 2;
    private const ulong StatePlay = 3;
    private const ulong StatePause = 4;

    // Audio is always delivered as 48kHz stereo s16le 1024-sample frames.
    private const int AudioSamplesPerFrame = 1024;
    private const int AudioChannelCount = 2;
    private const int AudioSampleRate = 48_000;
    private const int AudioFrameSize = AudioSamplesPerFrame * AudioChannelCount * sizeof(short);
    private const int AudioRingSlots = 8;

    // Synthetic source metadata reported while no real decoder is attached.
    private const int SyntheticWidth = 1920;
    private const int SyntheticHeight = 1080;
    private const double SyntheticFramesPerSecond = 30.0;
    private const ulong SyntheticDurationMilliseconds = 1000;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, PlayerState> Players = new();
    private static int _traceCount;

    private sealed class PlayerState : IDisposable
    {
        public required ulong Handle { get; init; }
        public bool AutoStart { get; init; }

        // Parsed from the init struct so the decoder path can call the guest
        // texture allocator and event callback.
        public ulong AllocatorObject { get; init; }
        public ulong AllocateTextureCallback { get; init; }
        public ulong EventObject { get; init; }
        public ulong EventCallback { get; init; }
        public string? SourcePath { get; set; }

        // Host file the guest source resolved to; null keeps the player in
        // the synthetic skip mode where the first poll raises end-of-stream.
        public string? HostPath { get; set; }
        public bool HasRealSource => HostPath is not null;
        public int Width { get; set; }
        public int Height { get; set; }
        public double FramesPerSecond { get; set; } = SyntheticFramesPerSecond;
        public ulong DurationMilliseconds { get; set; }
        public bool Started { get; set; }
        public bool Paused { get; set; }
        public bool Looping { get; set; }
        public bool EndOfStream { get; set; }
        public bool SyntheticFrameDelivered { get; set; }
        public Process? Decoder { get; set; }
        public Stream? DecoderOutput { get; set; }
        public Process? AudioDecoder { get; set; }
        public Stream? AudioDecoderOutput { get; set; }
        public Stopwatch PlaybackClock { get; } = new();
        public byte[]? RawFrame { get; set; }
        public byte[]? RawAudioFrame { get; set; }
        public byte[]? PaddedFrame { get; set; }
        public ulong[] GuestBuffers { get; } = new ulong[FrameBufferCount];
        public bool TextureAllocatorFailed { get; set; }
        public int GuestBufferStride { get; set; }
        public int NextGuestBuffer { get; set; }
        public ulong LastGuestBuffer { get; set; }
        public long NextFrameIndex { get; set; }
        public ulong AudioBufferBase { get; set; }
        public int NextAudioBuffer { get; set; }
        public long NextAudioFrameIndex { get; set; }

        public void Dispose()
        {
            DecoderOutput?.Dispose();
            DecoderOutput = null;
            AudioDecoderOutput?.Dispose();
            AudioDecoderOutput = null;
            Decoder = KillDecoder(Decoder);
            AudioDecoder = KillDecoder(AudioDecoder);
        }

        private static Process? KillDecoder(Process? process)
        {
            if (process is null)
            {
                return null;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
            return null;
        }

        public void ResetPlayback()
        {
            Dispose();
            PlaybackClock.Reset();
            NextFrameIndex = 0;
            NextAudioFrameIndex = 0;
            EndOfStream = false;
            SyntheticFrameDelivered = false;
        }
    }

    [SysAbiExport(
        Nid = "aS66RI0gGgo",
        ExportName = "sceAvPlayerInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInit(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        if (initDataAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var player = new PlayerState
        {
            Handle = handle,
            AutoStart = ctx.TryReadByte(initDataAddress + 108, out var autoStart) && autoStart != 0,
            AllocatorObject = ctx.TryReadUInt64(initDataAddress, out var allocatorObject) ? allocatorObject : 0,
            AllocateTextureCallback = ctx.TryReadUInt64(initDataAddress + 24, out var allocateTexture) ? allocateTexture : 0,
            EventObject = ctx.TryReadUInt64(initDataAddress + 80, out var eventObject) ? eventObject : 0,
            EventCallback = ctx.TryReadUInt64(initDataAddress + 88, out var eventCallback) ? eventCallback : 0,
        };
        lock (StateGate)
        {
            Players.Add(handle, player);
        }

        Trace($"init handle=0x{handle:X16} event_cb=0x{player.EventCallback:X16} alloc_texture=0x{player.AllocateTextureCallback:X16} auto_start={player.AutoStart}");
        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(
        Nid = "HD1YKVU26-M",
        ExportName = "sceAvPlayerPostInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPostInit(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            return ctx.SetReturn(
                handle != 0 && dataAddress != 0 && Players.ContainsKey(handle)
                    ? 0
                    : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "o9eWRkSL+M4",
        ExportName = "sceAvPlayerInitEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInitEx(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        var playerOutAddress = ctx[CpuRegister.Rsi];
        if (initDataAddress == 0 ||
            playerOutAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle) ||
            !ctx.TryWriteUInt64(playerOutAddress, handle))
        {
            return ctx.SetReturn(InvalidParameters);
        }

        var player = new PlayerState
        {
            Handle = handle,
            AutoStart = ctx.TryReadByte(initDataAddress + 164, out var autoStart) && autoStart != 0,
            AllocatorObject = ctx.TryReadUInt64(initDataAddress + 8, out var allocatorObject) ? allocatorObject : 0,
            AllocateTextureCallback = ctx.TryReadUInt64(initDataAddress + 32, out var allocateTexture) ? allocateTexture : 0,
            EventObject = ctx.TryReadUInt64(initDataAddress + 88, out var eventObject) ? eventObject : 0,
            EventCallback = ctx.TryReadUInt64(initDataAddress + 96, out var eventCallback) ? eventCallback : 0,
        };
        lock (StateGate)
        {
            Players.Add(handle, player);
        }

        Trace($"init_ex handle=0x{handle:X16} event_cb=0x{player.EventCallback:X16} alloc_texture=0x{player.AllocateTextureCallback:X16} auto_start={player.AutoStart}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "eBTreZ84JFY",
        ExportName = "sceAvPlayerSetLogCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLogCallback(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "NkJwDzKmIlw",
        ExportName = "sceAvPlayerClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerClose(CpuContext ctx)
    {
        PlayerState? player;
        lock (StateGate)
        {
            if (!Players.Remove(ctx[CpuRegister.Rdi], out player))
            {
                return ctx.SetReturn(InvalidParameters);
            }
        }

        player.Dispose();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "KMcEa+rHsIo",
        ExportName = "sceAvPlayerAddSource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSource(CpuContext ctx)
    {
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], MaxGuestPathLength, out var path))
        {
            return ctx.SetReturn(InvalidParameters);
        }

        return AddSource(ctx, path);
    }

    [SysAbiExport(
        Nid = "x8uvuFOPZhU",
        ExportName = "sceAvPlayerAddSourceEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSourceEx(CpuContext ctx)
    {
        var uriType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var detailsAddress = ctx[CpuRegister.Rdx];
        if (uriType != 0 || detailsAddress == 0 ||
            !ctx.TryReadUInt64(detailsAddress, out var pathAddress) ||
            !ctx.TryReadUInt32(detailsAddress + sizeof(ulong), out var pathLength) ||
            pathLength == 0 || pathLength > MaxGuestPathLength ||
            !TryReadUtf8(ctx, pathAddress, checked((int)pathLength), out var path))
        {
            return ctx.SetReturn(InvalidParameters);
        }

        return AddSource(ctx, path.TrimEnd('\0'));
    }

    [SysAbiExport(
        Nid = "ET4Gr-Uu07s",
        ExportName = "sceAvPlayerStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStart(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer) || foundPlayer.SourcePath is null)
            {
                return ctx.SetReturn(InvalidParameters);
            }
            player = foundPlayer;

            player.Started = true;
            player.Paused = false;
            player.EndOfStream = false;
            if (!player.HasRealSource)
            {
                // Real sources start their clock when the decoder launches so
                // slow FFmpeg startup does not force an initial frame skip.
                player.PlaybackClock.Start();
            }
            Trace($"start handle=0x{player.Handle:X16}");
        }

        // Event callbacks are guest code and can immediately query the player.
        // Never hold StateGate while waiting for one or the callback deadlocks
        // when it re-enters an AvPlayer export on another guest worker.
        NotifyEvent(ctx, player, StatePlay);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ZC17w3vB5Lo",
        ExportName = "sceAvPlayerStop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStop(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return ctx.SetReturn(InvalidParameters);
            }
            player = foundPlayer;

            player.ResetPlayback();
            player.Started = false;
            Trace($"stop handle=0x{player.Handle:X16}");
        }

        NotifyEvent(ctx, player, StateStop);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9y5v+fGN4Wk",
        ExportName = "sceAvPlayerPause",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPause(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return ctx.SetReturn(InvalidParameters);
            }
            player = foundPlayer;

            player.Paused = true;
            player.PlaybackClock.Stop();
        }

        NotifyEvent(ctx, player, StatePause);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "w5moABNwnRY",
        ExportName = "sceAvPlayerResume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerResume(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return ctx.SetReturn(InvalidParameters);
            }

            player.Paused = false;
            if (player.Started && !player.EndOfStream &&
                (!player.HasRealSource || player.DecoderOutput is not null))
            {
                player.PlaybackClock.Start();
            }
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "OVths0xGfho",
        ExportName = "sceAvPlayerSetLooping",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLooping(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return ctx.SetReturn(InvalidParameters);
            }

            player.Looping = ctx[CpuRegister.Rsi] != 0;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "ODJK2sn9w4A",
        ExportName = "sceAvPlayerEnableStream",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerEnableStream(CpuContext ctx) => ValidatePlayer(ctx);

    [SysAbiExport(
        Nid = "k-q+xOxdc3E",
        ExportName = "sceAvPlayerSetAvSyncMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetAvSyncMode(CpuContext ctx)
    {
        Trace($"set_av_sync_mode handle=0x{ctx[CpuRegister.Rdi]:X16} mode={ctx[CpuRegister.Rsi]}");
        return ValidatePlayer(ctx);
    }

    [SysAbiExport(
        Nid = "XC9wM+xULz8",
        ExportName = "sceAvPlayerJumpToTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerJumpToTime(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return ctx.SetReturn(InvalidParameters);
            }

            player.ResetPlayback();
            player.Started = true;
            if (!player.HasRealSource)
            {
                player.PlaybackClock.Start();
            }
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "yN7Jhuv8g24",
        ExportName = "sceAvPlayerVprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerVprintf(CpuContext ctx) => ctx.SetReturn(0);

    // "UbQoYawOsfY" is sceAvPlayerIsActive, not sceAvPlayerGetVideoDataEx (an earlier NID here
    // was wrong - verified by hashing both names against scripts/ps5_names.txt).
    [SysAbiExport(
        Nid = "UbQoYawOsfY",
        ExportName = "sceAvPlayerIsActive",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerIsActive(CpuContext ctx)
    {
        PlayerState? stoppedPlayer = null;
        int active;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                active = 0;
            }
            else
            {
                // A synthetic source must never keep the title waiting: even
                // if it polls no data at all, playback ends once the synthetic
                // duration elapses so an IsActive-only wait loop terminates.
                if (!player.HasRealSource && player.Started && !player.EndOfStream &&
                    (ulong)player.PlaybackClock.ElapsedMilliseconds >= SyntheticDurationMilliseconds)
                {
                    player.EndOfStream = true;
                    player.PlaybackClock.Stop();
                    stoppedPlayer = player;
                    Trace($"end_of_stream handle=0x{player.Handle:X16} reason=synthetic_timeout");
                }
                active = player.Started && !player.EndOfStream ? 1 : 0;
            }
        }

        if (stoppedPlayer is not null)
        {
            NotifyEvent(ctx, stoppedPlayer, StateStop);
        }
        return ctx.SetReturn(active);
    }

    [SysAbiExport(
        Nid = "o3+RWnHViSg",
        ExportName = "sceAvPlayerGetVideoData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoData(CpuContext ctx) => GetVideoData(ctx, extended: false);

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx) => GetVideoData(ctx, extended: true);

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return ctx.SetReturn(0);
            }
            if (!player.HasRealSource)
            {
                // Synthetic mode never produces audio. End-of-stream is driven
                // by the video poll (or the IsActive timeout) so an audio poll
                // racing ahead cannot cancel the single synthetic video frame.
                return ctx.SetReturn(0);
            }
            if (infoAddress == 0 || !player.Started || player.Paused || player.EndOfStream ||
                !EnsureAudioDecoder(player))
            {
                return ctx.SetReturn(0);
            }

            if (player.RawAudioFrame is null ||
                !ReadExactly(player.AudioDecoderOutput, player.RawAudioFrame))
            {
                return ctx.SetReturn(0);
            }
            if (player.AudioBufferBase == 0)
            {
                if (!KernelMemoryCompatExports.TryAllocateHleData(
                        ctx,
                        AudioFrameSize * (ulong)AudioRingSlots,
                        0x100,
                        out var audioBufferBase))
                {
                    return ctx.SetReturn(0);
                }
                player.AudioBufferBase = audioBufferBase;
            }

            var bufferAddress = player.AudioBufferBase +
                checked((ulong)(player.NextAudioBuffer * AudioFrameSize));
            player.NextAudioBuffer = (player.NextAudioBuffer + 1) % AudioRingSlots;
            if (!ctx.Memory.TryWrite(bufferAddress, player.RawAudioFrame))
            {
                return ctx.SetReturn(0);
            }

            var timestamp = checked((ulong)(player.NextAudioFrameIndex * AudioSamplesPerFrame * 1000L / AudioSampleRate));
            player.NextAudioFrameIndex++;
            Span<byte> info = stackalloc byte[FrameInfoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(info[24..], AudioChannelCount);
            BinaryPrimitives.WriteUInt32LittleEndian(info[28..], AudioSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[32..], AudioFrameSize);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return ctx.SetReturn(0);
            }
            Trace($"audio_frame handle=0x{player.Handle:X16} ts={timestamp} data=0x{bufferAddress:X16}");
            return ctx.SetReturn(1);
        }
    }

    [SysAbiExport(
        Nid = "wwM99gjFf1Y",
        ExportName = "sceAvPlayerCurrentTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerCurrentTime(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return ctx.SetReturn(InvalidParameters);
            }

            var milliseconds = (ulong)player.PlaybackClock.ElapsedMilliseconds;
            ctx[CpuRegister.Rax] = milliseconds;
            return unchecked((int)milliseconds);
        }
    }

    [SysAbiExport(
        Nid = "hdTyRzCXQeQ",
        ExportName = "sceAvPlayerStreamCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStreamCount(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Players.ContainsKey(ctx[CpuRegister.Rdi]) ? 2 : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "d8FcbzfAdQw",
        ExportName = "sceAvPlayerGetStreamInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfo(CpuContext ctx) => GetStreamInfo(ctx);

    // "ctTAcF5DiKQ" hashes to sceAvPlayerGetStreamInfoEx (checked against scripts/ps5_names.txt);
    // an external reference labeled it sceAvPlayerSetDecoderMode, which does not match the hash.
    // The Ex info struct leads with the same type/details/duration fields, so share the writer.
    [SysAbiExport(
        Nid = "ctTAcF5DiKQ",
        ExportName = "sceAvPlayerGetStreamInfoEx",
        Target = Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfoEx(CpuContext ctx) => GetStreamInfo(ctx);

    private static int GetStreamInfo(CpuContext ctx)
    {
        var streamIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                streamIndex > 1 || infoAddress == 0 || player.Width <= 0 || player.Height <= 0)
            {
                return ctx.SetReturn(InvalidParameters);
            }

            Span<byte> info = stackalloc byte[StreamInfoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(info[0..], streamIndex); // 0=video, 1=audio
            if (streamIndex == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(info[8..], checked((uint)player.Width));
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], checked((uint)player.Height));
                BinaryPrimitives.WriteSingleLittleEndian(info[16..], (float)player.Width / player.Height);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(info[8..], AudioChannelCount);
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], AudioSampleRate);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(info[24..], player.DurationMilliseconds);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return ctx.SetReturn(InvalidParameters);
            }

            return ctx.SetReturn(0);
        }
    }

    private static int AddSource(CpuContext ctx, string guestPath)
    {
        PlayerState player;
        bool autoStart;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return ctx.SetReturn(InvalidParameters);
            }
            player = foundPlayer;

            player.ResetPlayback();
            player.SourcePath = guestPath;
            var hostPath = ResolveGuestPath(guestPath);
            if (hostPath is not null &&
                ProbeVideo(hostPath, out var width, out var height, out var fps, out var duration))
            {
                player.HostPath = hostPath;
                player.Width = width;
                player.Height = height;
                player.FramesPerSecond = fps;
                player.DurationMilliseconds = duration;
                Trace($"source handle=0x{player.Handle:X16} guest='{guestPath}' host='{hostPath}' {width}x{height} fps={fps:F3} duration_ms={duration} auto_start={player.AutoStart}");
            }
            else
            {
                // No host file or no FFmpeg: accept the source anyway and
                // report synthetic 1080p30 metadata so the title sees a
                // playable stream it can immediately run to end-of-stream.
                player.HostPath = null;
                player.Width = SyntheticWidth;
                player.Height = SyntheticHeight;
                player.FramesPerSecond = SyntheticFramesPerSecond;
                player.DurationMilliseconds = SyntheticDurationMilliseconds;
                Console.Error.WriteLine(
                    $"[AVPLAYER][WARN] Could not open guest video '{guestPath}' " +
                    $"(resolved '{hostPath ?? "<none>"}'); skipping playback via synthetic end-of-stream.");
            }

            player.Started = player.AutoStart;
            if (player.Started && !player.HasRealSource)
            {
                player.PlaybackClock.Start();
            }
            autoStart = player.AutoStart;
        }

        NotifyEvent(ctx, player, StateReady);
        if (autoStart)
        {
            NotifyEvent(ctx, player, StatePlay);
        }
        return ctx.SetReturn(0);
    }

    private static int GetVideoData(CpuContext ctx, bool extended)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        PlayerState? stoppedPlayer = null;
        int result;
        lock (StateGate)
        {
            result = GetVideoDataLocked(ctx, extended, infoAddress, ref stoppedPlayer);
        }

        // The real player raises a StateStop event when playback completes.
        // Fire it outside StateGate: the callback is guest code and may
        // immediately re-enter an AvPlayer export from another guest worker.
        if (stoppedPlayer is not null)
        {
            NotifyEvent(ctx, stoppedPlayer, StateStop);
        }
        return result;
    }

    private static int GetVideoDataLocked(
        CpuContext ctx,
        bool extended,
        ulong infoAddress,
        ref PlayerState? stoppedPlayer)
    {
        if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
        {
            return ctx.SetReturn(0);
        }
        if (!player.HasRealSource)
        {
            return PollSyntheticVideo(ctx, player, infoAddress, extended, ref stoppedPlayer);
        }
        if (infoAddress == 0 || !player.Started || player.Paused || player.EndOfStream)
        {
            return ctx.SetReturn(0);
        }

        if (!EnsureDecoder(player))
        {
            player.EndOfStream = true;
            player.PlaybackClock.Stop();
            stoppedPlayer = player;
            Trace($"end_of_stream handle=0x{player.Handle:X16} reason=decoder_unavailable");
            return ctx.SetReturn(0);
        }

        var fps = Math.Max(1.0, player.FramesPerSecond);
        var expectedFrame = (long)Math.Floor(player.PlaybackClock.Elapsed.TotalSeconds * fps);
        while (player.NextFrameIndex < expectedFrame)
        {
            if (!ReadFrame(player))
            {
                return FinishStream(ctx, player, ref stoppedPlayer);
            }
            player.NextFrameIndex++;
        }

        if (!ReadFrame(player))
        {
            return FinishStream(ctx, player, ref stoppedPlayer);
        }

        var timestamp = checked((ulong)Math.Round(player.NextFrameIndex * 1000.0 / fps));
        player.NextFrameIndex++;
        if (!WriteVideoFrame(ctx, player, infoAddress, timestamp, extended))
        {
            return ctx.SetReturn(0);
        }

        Trace($"video_frame handle=0x{player.Handle:X16} ex={extended} ts={timestamp} data=0x{player.LastGuestBuffer:X16}");
        if (ShouldDirectPresentVideoFrames())
        {
            PresentVideoFrameToSwapchain(player);
        }
        return ctx.SetReturn(1);
    }

    private static int FinishStream(CpuContext ctx, PlayerState player, ref PlayerState? stoppedPlayer)
    {
        if (player.Looping)
        {
            player.ResetPlayback();
            player.Started = true;
        }
        else
        {
            player.EndOfStream = true;
            player.PlaybackClock.Stop();
            stoppedPlayer = player;
            Trace($"end_of_stream handle=0x{player.Handle:X16}");
        }
        return ctx.SetReturn(0);
    }

    // Skip path taken by GetVideoData/GetVideoDataEx when no real source is
    // attached: the first poll of a started player delivers a single black
    // NV12 frame through the normal frame machinery (so titles that require
    // one presented frame before transitioning still get it) and the next
    // poll raises end-of-stream so IsActive drops and the caller finishes
    // its video.
    private static int PollSyntheticVideo(
        CpuContext ctx,
        PlayerState player,
        ulong infoAddress,
        bool extended,
        ref PlayerState? stoppedPlayer)
    {
        if (!player.Started || player.EndOfStream)
        {
            return ctx.SetReturn(0);
        }

        if (infoAddress != 0 && !player.Paused && !player.SyntheticFrameDelivered)
        {
            EnsureSyntheticBlackFrame(player);
            if (WriteVideoFrame(ctx, player, infoAddress, timestamp: 0, extended))
            {
                player.SyntheticFrameDelivered = true;
                Trace($"video_frame handle=0x{player.Handle:X16} ex={extended} ts=0 data=0x{player.LastGuestBuffer:X16} synthetic=1");
                return ctx.SetReturn(1);
            }
        }

        player.EndOfStream = true;
        player.PlaybackClock.Stop();
        stoppedPlayer = player;
        Trace($"end_of_stream handle=0x{player.Handle:X16}");
        return ctx.SetReturn(0);
    }

    // Black in NV12: luma at video black level, chroma at the unsigned
    // midpoint.
    private static void EnsureSyntheticBlackFrame(PlayerState player)
    {
        var lumaSize = checked(player.Width * player.Height);
        var frameSize = checked(lumaSize * 3 / 2);
        if (player.RawFrame is null || player.RawFrame.Length != frameSize)
        {
            player.RawFrame = new byte[frameSize];
        }
        player.RawFrame.AsSpan(0, lumaSize).Fill(0x10);
        player.RawFrame.AsSpan(lumaSize).Fill(0x80);
    }

    private static bool EnsureDecoder(PlayerState player)
    {
        if (player.DecoderOutput is not null)
        {
            return true;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || player.HostPath is null)
        {
            Console.Error.WriteLine("[AVPLAYER][ERROR] FFmpeg was not found. Set SHARPEMU_FFMPEG_PATH.");
            return false;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(player.HostPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("nv12");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            var decoder = Process.Start(startInfo);
            if (decoder is null)
            {
                return false;
            }
            player.Decoder = decoder;
            decoder.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Error.WriteLine($"[AVPLAYER][FFMPEG] {eventArgs.Data}");
                }
            };
            decoder.BeginErrorReadLine();
            player.DecoderOutput = decoder.StandardOutput.BaseStream;
            player.RawFrame = new byte[checked(player.Width * player.Height * 3 / 2)];
            player.PlaybackClock.Start();
            Trace($"decoder_started pid={decoder.Id} source='{player.HostPath}'");
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to launch FFmpeg: {exception.Message}");
            player.Dispose();
            return false;
        }
    }

    private static bool EnsureAudioDecoder(PlayerState player)
    {
        if (player.AudioDecoderOutput is not null)
        {
            return true;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || player.HostPath is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(player.HostPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            var decoder = Process.Start(startInfo);
            if (decoder is null)
            {
                return false;
            }
            player.AudioDecoder = decoder;
            decoder.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Error.WriteLine($"[AVPLAYER][FFMPEG-AUDIO] {eventArgs.Data}");
                }
            };
            decoder.BeginErrorReadLine();
            player.AudioDecoderOutput = decoder.StandardOutput.BaseStream;
            player.RawAudioFrame = new byte[AudioFrameSize];
            Trace($"audio_decoder_started pid={decoder.Id} source='{player.HostPath}'");
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to launch FFmpeg audio decoder: {exception.Message}");
            player.AudioDecoderOutput?.Dispose();
            player.AudioDecoderOutput = null;
            player.AudioDecoder?.Dispose();
            player.AudioDecoder = null;
            return false;
        }
    }

    private static bool ReadFrame(PlayerState player)
    {
        if (player.DecoderOutput is null || player.RawFrame is null)
        {
            return false;
        }

        try
        {
            return ReadExactly(player.DecoderOutput, player.RawFrame);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] FFmpeg stream read failed: {exception.Message}");
            return false;
        }
    }

    private static bool ReadExactly(Stream? stream, byte[] buffer)
    {
        if (stream is null)
        {
            return false;
        }
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static bool WriteVideoFrame(
        CpuContext ctx,
        PlayerState player,
        ulong infoAddress,
        ulong timestamp,
        bool extended)
    {
        if (player.RawFrame is null)
        {
            return false;
        }

        var alignedWidth = AlignUp(player.Width, 16);
        var alignedHeight = AlignUp(player.Height, 16);
        var pitch = extended ? CalculateNv12Pitch(player.Width) : alignedWidth;
        var bufferHeight = extended ? player.Height : alignedHeight;
        var bufferStride = CalculateNv12BufferSize(pitch, bufferHeight);
        if (player.GuestBuffers[0] == 0)
        {
            if (!AllocateGuestVideoBuffers(ctx, player, bufferStride))
            {
                return false;
            }
            player.GuestBufferStride = bufferStride;
            Trace(
                $"video_layout ex={extended} width={player.Width} height={player.Height} " +
                $"pitch={pitch} uv_offset={checked(pitch * bufferHeight)} size={bufferStride}");
        }

        var frameData = player.RawFrame;
        if (extended)
        {
            if (player.PaddedFrame is null || player.PaddedFrame.Length != bufferStride)
            {
                player.PaddedFrame = new byte[bufferStride];
            }
            CopyNv12ToGuestBuffer(
                player.RawFrame,
                player.PaddedFrame,
                player.Width,
                player.Height,
                player.Width,
                player.Width,
                pitch);
            frameData = player.PaddedFrame;
        }
        else if (alignedWidth != player.Width || alignedHeight != player.Height)
        {
            if (player.PaddedFrame is null || player.PaddedFrame.Length != bufferStride)
            {
                player.PaddedFrame = new byte[bufferStride];
            }
            player.PaddedFrame.AsSpan().Clear();
            for (var row = 0; row < player.Height; row++)
            {
                player.RawFrame.AsSpan(row * player.Width, player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(row * alignedWidth, player.Width));
            }
            var rawChromaOffset = player.Width * player.Height;
            var paddedChromaOffset = alignedWidth * alignedHeight;
            for (var row = 0; row < player.Height / 2; row++)
            {
                player.RawFrame.AsSpan(rawChromaOffset + (row * player.Width), player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(paddedChromaOffset + (row * alignedWidth), player.Width));
            }
            frameData = player.PaddedFrame;
        }

        var bufferAddress = player.GuestBuffers[player.NextGuestBuffer];
        player.NextGuestBuffer = (player.NextGuestBuffer + 1) % FrameBufferCount;
        player.LastGuestBuffer = bufferAddress;
        if (!ctx.Memory.TryWrite(bufferAddress, frameData))
        {
            return false;
        }

        Span<byte> info = extended
            ? stackalloc byte[FrameInfoExSize]
            : stackalloc byte[FrameInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(info[24..], checked((uint)(extended ? player.Width : alignedWidth)));
        BinaryPrimitives.WriteUInt32LittleEndian(info[28..], checked((uint)(extended ? player.Height : alignedHeight)));
        BinaryPrimitives.WriteSingleLittleEndian(info[32..], 1.0f);
        if (extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info[60..], checked((uint)pitch));
            info[64] = 8; // luma bit depth
            info[65] = 8; // chroma bit depth
        }
        return ctx.Memory.TryWrite(infoAddress, info);
    }

    internal static int CalculateNv12Pitch(int width) =>
        AlignUp(width, VideoPitchAlignment);

    internal static int CalculateNv12BufferSize(int pitch, int height) =>
        checked(pitch * height * 3 / 2);

    internal static void CopyNv12ToGuestBuffer(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        int sourceLumaStride,
        int sourceChromaStride,
        int destinationPitch)
    {
        var sourceChromaOffset = checked(sourceLumaStride * height);
        var destinationChromaOffset = checked(destinationPitch * height);
        var destinationSize = CalculateNv12BufferSize(destinationPitch, height);
        destination[..destinationSize].Clear();

        for (var row = 0; row < height; row++)
        {
            source.Slice(row * sourceLumaStride, width)
                .CopyTo(destination.Slice(row * destinationPitch, width));
        }
        for (var row = 0; row < height / 2; row++)
        {
            source.Slice(sourceChromaOffset + (row * sourceChromaStride), width)
                .CopyTo(destination.Slice(destinationChromaOffset + (row * destinationPitch), width));
        }
    }

    // SHARPEMU_AVPLAYER_PRESENT: push each decoded video frame straight onto the
    // swapchain, bypassing the guest's own texture-allocator blit. Default OFF
    // so the normal delivery path is byte-identical.
    internal static bool ShouldDirectPresentVideoFrames() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT"),
            "1",
            StringComparison.Ordinal);

    // Converts the just-decoded NV12 frame to BGRA8 and hands it to the presenter
    // for a direct, deadlock-free swapchain present. RawFrame is the tightly
    // packed FFmpeg output (luma stride == Width, chroma plane at Width*Height),
    // so it is the cleanest conversion source. Never throws out to the guest
    // caller: a bad frame is logged and skipped.
    private static void PresentVideoFrameToSwapchain(PlayerState player)
    {
        var frame = player.RawFrame;
        if (frame is null || player.Width <= 0 || player.Height <= 0)
        {
            return;
        }

        try
        {
            var bgra = ConvertNv12ToBgra(
                frame,
                player.Width,
                player.Height,
                player.Width,
                checked(player.Width * player.Height));
            if (bgra.Length == 0)
            {
                Trace($"present_skip handle=0x{player.Handle:X16} reason=convert_failed");
                return;
            }
            if (!VulkanVideoPresenter.TryPresentVideoFrame(bgra, player.Width, player.Height))
            {
                Trace($"present_skip handle=0x{player.Handle:X16} reason=present_failed");
                return;
            }
            Trace($"present handle=0x{player.Handle:X16} {player.Width}x{player.Height}");
        }
        catch (Exception exception)
        {
            Trace($"present_error handle=0x{player.Handle:X16}: {exception.Message}");
        }
    }

    // NV12 -> tightly packed BGRA8 (width*height*4, matching the swapchain's
    // B8G8R8A8 byte order). BT.709 limited-range coefficients in Q8 fixed point;
    // correctness over speed. The Y plane has stride lumaPitch; the interleaved
    // U/V plane starts at uvOffset with the same stride and half the height.
    // Returns an empty array when the source is too small for that geometry.
    internal static byte[] ConvertNv12ToBgra(
        ReadOnlySpan<byte> nv12,
        int width,
        int height,
        int lumaPitch,
        int uvOffset)
    {
        if (width <= 0 ||
            height <= 0 ||
            (height & 1) != 0 ||
            lumaPitch < width ||
            uvOffset < lumaPitch * height)
        {
            return [];
        }

        var chromaRows = height / 2;
        var lumaBytes = checked(lumaPitch * height);
        var chromaBytes = checked(uvOffset + (chromaRows - 1) * lumaPitch + width);
        if (nv12.Length < lumaBytes || nv12.Length < chromaBytes)
        {
            return [];
        }

        var bgra = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var lumaRow = y * lumaPitch;
            var chromaRow = uvOffset + ((y >> 1) * lumaPitch);
            var outRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var c = nv12[lumaRow + x] - 16;
                var chromaColumn = (x >> 1) << 1;
                var d = nv12[chromaRow + chromaColumn] - 128;
                var e = nv12[chromaRow + chromaColumn + 1] - 128;

                var r = ((298 * c) + (459 * e) + 128) >> 8;
                var g = ((298 * c) - (55 * d) - (136 * e) + 128) >> 8;
                var b = ((298 * c) + (541 * d) + 128) >> 8;

                var o = outRow + (x * 4);
                bgra[o] = ClampToByte(b);
                bgra[o + 1] = ClampToByte(g);
                bgra[o + 2] = ClampToByte(r);
                bgra[o + 3] = 0xFF;
            }
        }

        return bgra;
    }

    private static byte ClampToByte(int value) =>
        (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private static bool AllocateGuestVideoBuffers(CpuContext ctx, PlayerState player, int bufferSize)
    {
        var scheduler = GuestThreadExecution.Scheduler;
        if (!player.TextureAllocatorFailed && player.AllocateTextureCallback != 0 && scheduler is not null)
        {
            for (var index = 0; index < player.GuestBuffers.Length; index++)
            {
                if (!scheduler.TryCallGuestFunction(
                        ctx,
                        player.AllocateTextureCallback,
                        player.AllocatorObject,
                        0x100,
                        checked((ulong)bufferSize),
                        0,
                        0,
                        "avplayer_allocate_texture",
                        out var buffer,
                        out var error) || buffer == 0)
                {
                    Console.Error.WriteLine(
                        $"[AVPLAYER][ERROR] Guest texture allocation failed index={index} " +
                        $"callback=0x{player.AllocateTextureCallback:X16}: {error ?? "returned null"}");
                    player.TextureAllocatorFailed = true;
                    Array.Clear(player.GuestBuffers);
                    break;
                }
                player.GuestBuffers[index] = buffer;
                Trace($"texture_buffer index={index} data=0x{buffer:X16} size={bufferSize}");
            }
            if (!player.TextureAllocatorFailed)
            {
                return true;
            }
        }

        if (!KernelMemoryCompatExports.TryAllocateHleData(
                ctx,
                checked((ulong)bufferSize * FrameBufferCount),
                0x1000,
                out var bufferBase))
        {
            return false;
        }
        for (var index = 0; index < player.GuestBuffers.Length; index++)
        {
            player.GuestBuffers[index] = bufferBase + checked((ulong)(index * bufferSize));
        }
        Console.Error.WriteLine("[AVPLAYER][WARN] Guest texture allocator unavailable; using generic HLE memory.");
        return true;
    }

    private static bool ProbeVideo(
        string path,
        out int width,
        out int height,
        out double framesPerSecond,
        out ulong durationMilliseconds)
    {
        width = 0;
        height = 0;
        framesPerSecond = SyntheticFramesPerSecond;
        durationMilliseconds = 0;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return false;
        }
        var ffprobe = FindFfprobe();
        if (ffprobe is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=width,height,avg_frame_rate,duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"[AVPLAYER][FFPROBE] {error.Trim()}");
                return false;
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator < 1)
                {
                    continue;
                }
                var key = line[..separator];
                var value = line[(separator + 1)..];
                switch (key)
                {
                    case "width":
                        _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                        break;
                    case "height":
                        _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                        break;
                    case "avg_frame_rate":
                        var parts = value.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                            denominator != 0)
                        {
                            framesPerSecond = numerator / denominator;
                        }
                        break;
                    case "duration":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                        {
                            durationMilliseconds = checked((ulong)Math.Max(0, Math.Round(duration * 1000.0)));
                        }
                        break;
                }
            }
            return width > 0 && height > 0 && framesPerSecond > 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to probe video: {exception.Message}");
            return false;
        }
    }

    private static readonly string[] FfmpegSearchDirs =
    {
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        @"C:\ffmpeg\bin",
        @"C:\Program Files\ffmpeg\bin",
        @"C:\Program Files (x86)\ffmpeg\bin",
        @"C:\ProgramData\chocolatey\bin",
    };

    private static string? FindFfmpeg() => FindFfmpegTool("ffmpeg", "SHARPEMU_FFMPEG_PATH");

    private static string? FindFfprobe() => FindFfmpegTool("ffprobe", "SHARPEMU_FFPROBE_PATH");

    // Locates an ffmpeg-family binary across host OSes: honours an explicit
    // override, then the directory of a configured ffmpeg (so ffprobe is found
    // beside a hand-set ffmpeg), then PATH, then common install dirs. Appends
    // the platform executable suffix so the Windows VM resolves ffmpeg.exe.
    private static string? FindFfmpegTool(string tool, string envVar)
    {
        var configured = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var exeName = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        var ffmpegConfigured = Environment.GetEnvironmentVariable("SHARPEMU_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(ffmpegConfigured) && File.Exists(ffmpegConfigured))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpegConfigured) ?? string.Empty, exeName);
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim(), exeName);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var dir in FfmpegSearchDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? ResolveGuestPath(string guestPath)
    {
        if (string.IsNullOrWhiteSpace(guestPath))
        {
            return null;
        }

        var normalized = guestPath.Replace('\\', '/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            normalized = uri.LocalPath;
        }
        if (File.Exists(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        var app0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0))
        {
            return null;
        }
        foreach (var prefix in new[] { "app0:/", "/app0/", "app0:", "/app0" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }
        var candidate = Path.GetFullPath(Path.Combine(app0, normalized.TrimStart('/')));
        var root = Path.GetFullPath(app0).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    private static void NotifyEvent(CpuContext ctx, PlayerState player, ulong eventId)
    {
        if (player.EventCallback == 0)
        {
            Trace($"event skipped handle=0x{player.Handle:X16} id={eventId} callback=0");
            return;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        if (scheduler is null ||
            !scheduler.TryCallGuestFunction(
                ctx,
                player.EventCallback,
                player.EventObject,
                eventId,
                0,
                0,
                0,
                $"avplayer_event_{eventId}",
                out _,
                out error))
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] Event callback failed handle=0x{player.Handle:X16} " +
                $"event={eventId} callback=0x{player.EventCallback:X16}: {error ?? "scheduler unavailable"}");
            return;
        }

        Trace($"event handle=0x{player.Handle:X16} id={eventId} callback=0x{player.EventCallback:X16}");
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private static int ValidatePlayer(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Players.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : InvalidParameters);
        }
    }

    private static bool TryReadUtf8(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        if (address == 0 || length <= 0)
        {
            return false;
        }
        var bytes = new byte[length];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static void Trace(string message)
    {
        var count = Interlocked.Increment(ref _traceCount);
        if (count <= 32 || count % 300 == 0)
        {
            Console.Error.WriteLine($"[AVPLAYER][INFO] {message}");
        }
    }
}
