// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// sceAvPlayer lifecycle HLE. The full player state machine is implemented
/// (init/addSource/start/stop/pause/resume plus guest event callbacks), but
/// no frames are decoded: every accepted source reports synthetic metadata
/// and the first data poll after start raises end-of-stream, so titles that
/// gate progress on their intro video (poll GetVideoData, wait for IsActive
/// to drop) skip straight past it.
/// </summary>
public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int StreamInfoSize = 40;
    private const int MaxGuestPathLength = 4096;

    // sceAvPlayer state ids delivered to the guest event callback.
    private const ulong StateStop = 1;
    private const ulong StateReady = 2;
    private const ulong StatePlay = 3;
    private const ulong StatePause = 4;

    // Synthetic source metadata reported while no real decoder is attached.
    private const int SyntheticWidth = 1920;
    private const int SyntheticHeight = 1080;
    private const double SyntheticFramesPerSecond = 30.0;
    private const ulong SyntheticDurationMilliseconds = 1000;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, PlayerState> Players = new();
    private static int _traceCount;

    private sealed class PlayerState
    {
        public required ulong Handle { get; init; }
        public bool AutoStart { get; init; }

        // Parsed from the init struct now so the real decoder path can call
        // the guest texture allocator later without another ABI change.
        public ulong AllocatorObject { get; init; }
        public ulong AllocateTextureCallback { get; init; }
        public ulong EventObject { get; init; }
        public ulong EventCallback { get; init; }
        public string? SourcePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FramesPerSecond { get; set; } = SyntheticFramesPerSecond;
        public ulong DurationMilliseconds { get; set; }
        public bool Started { get; set; }
        public bool Paused { get; set; }
        public bool Looping { get; set; }
        public bool EndOfStream { get; set; }
        public Stopwatch PlaybackClock { get; } = new();

        public void ResetPlayback()
        {
            PlaybackClock.Reset();
            EndOfStream = false;
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
        lock (StateGate)
        {
            return ctx.SetReturn(Players.Remove(ctx[CpuRegister.Rdi]) ? 0 : InvalidParameters);
        }
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
            player.PlaybackClock.Start();
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
            if (player.Started && !player.EndOfStream)
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
            player.PlaybackClock.Start();
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
        lock (StateGate)
        {
            return ctx.SetReturn(
                Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) &&
                player.Started && !player.EndOfStream ? 1 : 0);
        }
    }

    [SysAbiExport(
        Nid = "o3+RWnHViSg",
        ExportName = "sceAvPlayerGetVideoData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoData(CpuContext ctx) => PollDataAsEndOfStream(ctx);

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx) => PollDataAsEndOfStream(ctx);

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx) => PollDataAsEndOfStream(ctx);

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
                BinaryPrimitives.WriteUInt16LittleEndian(info[8..], 2);
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], 48_000);
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

            // No decoder is attached at this HLE level; accept every source
            // and report synthetic 1080p30 metadata so the title sees a
            // playable stream it can immediately run to end-of-stream.
            player.ResetPlayback();
            player.SourcePath = guestPath;
            player.Width = SyntheticWidth;
            player.Height = SyntheticHeight;
            player.FramesPerSecond = SyntheticFramesPerSecond;
            player.DurationMilliseconds = SyntheticDurationMilliseconds;
            player.Started = player.AutoStart;
            if (player.Started)
            {
                player.PlaybackClock.Start();
            }
            autoStart = player.AutoStart;
            Trace($"source handle=0x{player.Handle:X16} guest='{guestPath}' auto_start={autoStart}");
        }

        NotifyEvent(ctx, player, StateReady);
        if (autoStart)
        {
            NotifyEvent(ctx, player, StatePlay);
        }
        return ctx.SetReturn(0);
    }

    // Skip path shared by GetVideoData/GetVideoDataEx/GetAudioData: never
    // produce a frame, and let the first poll of a started player raise
    // end-of-stream so IsActive drops and the caller finishes its video.
    private static int PollDataAsEndOfStream(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) &&
                player.Started && !player.EndOfStream)
            {
                player.EndOfStream = true;
                player.PlaybackClock.Stop();
                Trace($"end_of_stream handle=0x{player.Handle:X16}");
            }

            return ctx.SetReturn(0);
        }
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
