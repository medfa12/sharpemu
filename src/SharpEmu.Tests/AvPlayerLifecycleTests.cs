// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceAvPlayer lifecycle state machine driven through the guest ABI: a title
/// initializes a player, adds its intro movie, starts playback, polls for
/// frames, and waits for IsActive to drop before moving on. The HLE never
/// decodes frames; the first poll after start must deliver one synthetic
/// black NV12 frame, the next must raise end-of-stream (with a StateStop
/// event) so that wait terminates, and state events must reach the guest
/// callback without ever being fired for a null callback.
/// </summary>
public sealed class AvPlayerLifecycleTests
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const ulong InitDataAddress = 0x10_0000;
    private const ulong PathAddress = 0x20_0000;
    private const ulong StreamInfoAddress = 0x30_0000;
    private const ulong FrameInfoAddress = 0x40_0000;
    private const ulong AllocatorObject = 0x1111_0000;
    private const ulong TextureCallbackEntry = 0x2222_0000;
    private const ulong EventObject = 0x4444_0000;
    private const ulong EventCallbackEntry = 0x5555_0000;
    private const ulong GuestTextureAddress = 0x6000_0000;

    private static CpuContext NewContext()
        => new(new AllocatingGuestMemory(), Generation.Gen5);

    /// <summary>
    /// Writes the 112-byte SceAvPlayerInitData image the exports parse:
    /// allocator object +0, allocate-texture callback +24, event object +80,
    /// event callback +88, auto-start byte +108.
    /// </summary>
    private static void WriteInitData(
        CpuContext ctx,
        ulong eventCallback,
        bool autoStart)
    {
        Assert.True(ctx.Memory.TryWrite(InitDataAddress, new byte[112]));
        Assert.True(ctx.TryWriteUInt64(InitDataAddress, AllocatorObject));
        Assert.True(ctx.TryWriteUInt64(InitDataAddress + 24, TextureCallbackEntry));
        Assert.True(ctx.TryWriteUInt64(InitDataAddress + 80, EventObject));
        Assert.True(ctx.TryWriteUInt64(InitDataAddress + 88, eventCallback));
        Assert.True(ctx.Memory.TryWrite(InitDataAddress + 108, new[] { autoStart ? (byte)1 : (byte)0 }));
    }

    private static ulong Init(CpuContext ctx, ulong eventCallback, bool autoStart)
    {
        WriteInitData(ctx, eventCallback, autoStart);
        ctx[CpuRegister.Rdi] = InitDataAddress;
        AvPlayerExports.AvPlayerInit(ctx);
        var handle = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static void WritePath(CpuContext ctx, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        Assert.True(ctx.Memory.TryWrite(PathAddress, bytes));
        Assert.True(ctx.Memory.TryWrite(PathAddress + (ulong)bytes.Length, new byte[] { 0 }));
    }

    private static int AddSource(CpuContext ctx, ulong handle, string path)
    {
        WritePath(ctx, path);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = PathAddress;
        return AvPlayerExports.AvPlayerAddSource(ctx);
    }

    private static int Call(CpuContext ctx, ulong handle, Func<CpuContext, int> export)
    {
        ctx[CpuRegister.Rdi] = handle;
        return export(ctx);
    }

    [Fact]
    public void Lifecycle_InitThroughClose_SkipsIntroViaEndOfStream()
    {
        var ctx = NewContext();
        var handle = Init(ctx, eventCallback: 0, autoStart: false);

        // Start before any source is rejected.
        Assert.Equal(InvalidParameters, Call(ctx, handle, AvPlayerExports.AvPlayerStart));

        Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));
        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive)); // no auto-start

        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerStart));
        Assert.Equal(1, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

        // First frame poll delivers a single synthetic black NV12 frame
        // (16x-aligned 1080p layout) so a title that waits for its first
        // frame still transitions.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = FrameInfoAddress;
        Assert.Equal(1, AvPlayerExports.AvPlayerGetVideoData(ctx));
        Assert.True(ctx.TryReadUInt64(FrameInfoAddress, out var frameAddress));
        Assert.True(ctx.TryReadUInt64(FrameInfoAddress + 16, out var timestamp));
        Assert.True(ctx.TryReadUInt32(FrameInfoAddress + 24, out var frameWidth));
        Assert.True(ctx.TryReadUInt32(FrameInfoAddress + 28, out var frameHeight));
        Assert.NotEqual(0UL, frameAddress);
        Assert.Equal(0UL, timestamp);
        Assert.Equal(1920u, frameWidth);
        Assert.Equal(1088u, frameHeight);

        // Luma at video black, chroma at the unsigned midpoint.
        var pixel = new byte[1];
        Assert.True(ctx.Memory.TryRead(frameAddress, pixel));
        Assert.Equal(0x10, pixel[0]);
        Assert.True(ctx.Memory.TryRead(frameAddress + (1920UL * 1088UL), pixel));
        Assert.Equal(0x80, pixel[0]);

        Assert.Equal(1, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

        // The second poll returns no frame and finishes the stream.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = FrameInfoAddress;
        Assert.Equal(0, AvPlayerExports.AvPlayerGetVideoData(ctx));
        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerStop));
        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerClose));

        // The handle is gone after close.
        Assert.Equal(InvalidParameters, Call(ctx, handle, AvPlayerExports.AvPlayerClose));
        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));
    }

    [Fact]
    public void Lifecycle_AutoStart_AudioPollsCannotCancelTheSyntheticFrame()
    {
        var ctx = NewContext();
        var handle = Init(ctx, eventCallback: 0, autoStart: true);

        Assert.Equal(0, AddSource(ctx, handle, "movies/intro.mp4"));
        Assert.Equal(1, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

        // Synthetic mode produces no audio, and an audio poll racing ahead
        // of the video poll must not end the stream before the single black
        // frame is delivered.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = FrameInfoAddress;
        Assert.Equal(0, AvPlayerExports.AvPlayerGetAudioData(ctx));
        Assert.Equal(1, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = FrameInfoAddress;
        Assert.Equal(1, AvPlayerExports.AvPlayerGetVideoData(ctx));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = FrameInfoAddress;
        Assert.Equal(0, AvPlayerExports.AvPlayerGetVideoData(ctx));
        Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));
    }

    [Fact]
    public void StreamInfo_ReportsSyntheticVideoAndAudioMetadata()
    {
        var ctx = NewContext();
        var handle = Init(ctx, eventCallback: 0, autoStart: false);

        // Before a source is added there is no stream metadata.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = StreamInfoAddress;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerGetStreamInfo(ctx));

        Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));
        Assert.Equal(2, Call(ctx, handle, AvPlayerExports.AvPlayerStreamCount));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0; // video stream
        ctx[CpuRegister.Rdx] = StreamInfoAddress;
        Assert.Equal(0, AvPlayerExports.AvPlayerGetStreamInfo(ctx));
        Assert.True(ctx.TryReadUInt32(StreamInfoAddress + 8, out var width));
        Assert.True(ctx.TryReadUInt32(StreamInfoAddress + 12, out var height));
        Assert.True(ctx.TryReadUInt64(StreamInfoAddress + 24, out var duration));
        Assert.Equal(1920u, width);
        Assert.Equal(1080u, height);
        Assert.Equal(1000UL, duration);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1; // audio stream
        ctx[CpuRegister.Rdx] = StreamInfoAddress;
        Assert.Equal(0, AvPlayerExports.AvPlayerGetStreamInfo(ctx));
        Assert.True(ctx.TryReadUInt32(StreamInfoAddress + 12, out var sampleRate));
        Assert.Equal(48_000u, sampleRate);
    }

    [Fact]
    public void Events_ReachGuestCallbackInLifecycleOrder()
    {
        var ctx = NewContext();
        var scheduler = new RecordingScheduler();
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var handle = Init(ctx, EventCallbackEntry, autoStart: true);

            Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));
            Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerStop));
            Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerStart));

            // AddSource: StateReady(2) then StatePlay(3) because of auto-start;
            // Stop: StateStop(1); Start: StatePlay(3).
            Assert.Equal(
                new[] { 2UL, 3UL, 1UL, 3UL },
                scheduler.Calls.Select(call => call.Arg1).ToArray());
            Assert.All(scheduler.Calls, call =>
            {
                Assert.Equal(EventCallbackEntry, call.EntryPoint);
                Assert.Equal(EventObject, call.Arg0);
                Assert.Equal(0UL, call.Arg2);
            });

            // Events must go through the returnValue overload so a blocked
            // callback resumes via the scheduler instead of being dropped.
            Assert.Equal(0, scheduler.LegacyOverloadCalls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void Events_SkippedWhenCallbackIsNull()
    {
        var ctx = NewContext();
        var scheduler = new RecordingScheduler();
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var handle = Init(ctx, eventCallback: 0, autoStart: true);

            Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));
            Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerStop));

            Assert.Empty(scheduler.Calls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    /// <summary>
    /// A title that waits for the StateStop event (instead of polling
    /// IsActive) must see it once the synthetic stream ends, and the single
    /// synthetic frame must land in buffers obtained from the title's own
    /// texture allocator callback when one is registered.
    /// </summary>
    [Fact]
    public void Events_StopEventFiresOnSyntheticEndOfStream()
    {
        var ctx = NewContext();
        var scheduler = new RecordingScheduler { NextReturnValue = GuestTextureAddress };
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var handle = Init(ctx, EventCallbackEntry, autoStart: true);
            Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));

            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = FrameInfoAddress;
            Assert.Equal(1, AvPlayerExports.AvPlayerGetVideoData(ctx));
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = FrameInfoAddress;
            Assert.Equal(0, AvPlayerExports.AvPlayerGetVideoData(ctx));

            // Ready(2), Play(3) from auto-start, then Stop(1) at end-of-stream.
            Assert.Equal(
                new[] { 2UL, 3UL, 1UL },
                scheduler.Calls
                    .Where(call => call.EntryPoint == EventCallbackEntry)
                    .Select(call => call.Arg1)
                    .ToArray());

            // The frame buffers came from the guest texture allocator: three
            // ring slots, 0x100-aligned, sized for the 16x-aligned 1080p NV12
            // layout, and the black frame was written to the returned address.
            var textureCalls = scheduler.Calls
                .Where(call => call.EntryPoint == TextureCallbackEntry)
                .ToArray();
            Assert.Equal(3, textureCalls.Length);
            Assert.All(textureCalls, call =>
            {
                Assert.Equal(AllocatorObject, call.Arg0);
                Assert.Equal(0x100UL, call.Arg1);
                Assert.Equal(1920UL * 1088UL * 3UL / 2UL, call.Arg2);
            });
            Assert.True(ctx.TryReadUInt64(FrameInfoAddress, out var frameAddress));
            Assert.Equal(GuestTextureAddress, frameAddress);
            var pixel = new byte[1];
            Assert.True(ctx.Memory.TryRead(frameAddress, pixel));
            Assert.Equal(0x10, pixel[0]);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    /// <summary>
    /// A title that only watches IsActive (never polling data) must still
    /// see the synthetic stream finish once its synthetic duration elapses,
    /// including the StateStop event.
    /// </summary>
    [Fact]
    public void IsActive_SyntheticPlaybackTimesOutWithoutDataPolls()
    {
        var ctx = NewContext();
        var scheduler = new RecordingScheduler();
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var handle = Init(ctx, EventCallbackEntry, autoStart: true);
            Assert.Equal(0, AddSource(ctx, handle, "/app0/movies/intro.mp4"));
            Assert.Equal(1, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));

            // The synthetic duration is 1000ms on the playback clock.
            Thread.Sleep(1150);
            Assert.Equal(0, Call(ctx, handle, AvPlayerExports.AvPlayerIsActive));
            Assert.Equal(
                new[] { 2UL, 3UL, 1UL },
                scheduler.Calls.Select(call => call.Arg1).ToArray());
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public List<(ulong EntryPoint, ulong Arg0, ulong Arg1, ulong Arg2)> Calls { get; } = new();

        public int LegacyOverloadCalls { get; private set; }

        public ulong NextReturnValue { get; set; }

        public bool SupportsGuestContextTransfer => false;

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            LegacyOverloadCalls++;
            return TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                0,
                stackAddress,
                stackSize,
                reason,
                out _,
                out error);
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            error = null;
            returnValue = NextReturnValue;
            Calls.Add((entryPoint, arg0, arg1, arg2));
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }

    /// <summary>
    /// Sparse guest memory that also satisfies the virtual-range allocator so
    /// KernelMemoryCompatExports.TryAllocateHleData (behind player handles)
    /// works under test; allocations land at the aligned desired address and
    /// the allocator's own zero-fill maps the bytes.
    /// </summary>
    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestAddressSpace
    {
        private const int PageShift = 12;
        private const ulong OffsetMask = (1UL << PageShift) - 1;

        // Page-backed so multi-megabyte NV12 frame writes stay fast; a read
        // of a page nobody has written still fails like unmapped memory.
        private readonly Dictionary<ulong, byte[]> _pages = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var address = virtualAddress + (ulong)offset;
                if (!_pages.TryGetValue(address >> PageShift, out var page))
                {
                    return false;
                }

                var pageOffset = (int)(address & OffsetMask);
                var chunk = Math.Min(destination.Length - offset, page.Length - pageOffset);
                page.AsSpan(pageOffset, chunk).CopyTo(destination.Slice(offset, chunk));
                offset += chunk;
            }

            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            var offset = 0;
            while (offset < source.Length)
            {
                var address = virtualAddress + (ulong)offset;
                var pageIndex = address >> PageShift;
                if (!_pages.TryGetValue(pageIndex, out var page))
                {
                    page = new byte[1 << PageShift];
                    _pages[pageIndex] = page;
                }

                var pageOffset = (int)(address & OffsetMask);
                var chunk = Math.Min(source.Length - offset, page.Length - pageOffset);
                source.Slice(offset, chunk).CopyTo(page.AsSpan(pageOffset, chunk));
                offset += chunk;
            }

            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
            => desiredAddress;

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            var effectiveAlignment = Math.Max(alignment, 1UL);
            actualAddress = (desiredAddress + effectiveAlignment - 1) & ~(effectiveAlignment - 1);
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }
    }
}
