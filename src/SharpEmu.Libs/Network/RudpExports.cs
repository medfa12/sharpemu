// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class RudpExports
{
    private const uint DefaultMaxSegmentSize = 1200;
    private static readonly ConcurrentDictionary<uint, RudpContextState> Contexts = new();
    private static readonly ConcurrentDictionary<uint, byte> Polls = new();
    private static int _initialized;
    private static int _nextContextId;
    private static int _nextPollId;

    private sealed class RudpContextState
    {
        public uint MaxSegmentSize = DefaultMaxSegmentSize;
        public int Status;
        public ulong EventHandler;
        public ulong EventArgument;
    }

    internal static int ContextCount => Contexts.Count;
    internal static int PollCount => Polls.Count;

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Polls.Clear();
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextPollId, 0);
    }

    [SysAbiExport(Nid = "uQiK7fjU6y8", ExportName = "sceRudpAccept", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpAccept(CpuContext ctx)
    {
        var sourceId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Contexts.ContainsKey(sourceId) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        var contextId = NextContextId();
        Contexts[contextId] = new RudpContextState { Status = 2 };
        return ctx.TryWriteUInt32(outputAddress, contextId) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "J-6d0WTjzMc", ExportName = "sceRudpActivate", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpActivate(CpuContext ctx) => SetContextStatus(ctx, 2);

    [SysAbiExport(Nid = "l4SLBpKUDK4", ExportName = "sceRudpBind", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpBind(CpuContext ctx) => ValidateContext(ctx);

    [SysAbiExport(Nid = "CAbbX6BuQZ0", ExportName = "sceRudpCreateContext", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpCreateContext(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (Volatile.Read(ref _initialized) == 0 || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        var contextId = NextContextId();
        Contexts[contextId] = new RudpContextState();
        if (ctx.TryWriteUInt32(outputAddress, contextId))
        {
            return Ok(ctx);
        }

        Contexts.TryRemove(contextId, out _);
        return MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "6PBNpsgyaxw", ExportName = "sceRudpEnableInternalIOThread", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpEnableInternalIOThread(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "fJ51weR1WAI", ExportName = "sceRudpEnableInternalIOThread2", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpEnableInternalIOThread2(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "3hBvwqEwqj8", ExportName = "sceRudpEnd", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpEnd(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Contexts.TryRemove(contextId, out _) ? Ok(ctx) : InvalidArgument(ctx);
    }

    [SysAbiExport(Nid = "Ms0cLK8sTtE", ExportName = "sceRudpFlush", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpFlush(CpuContext ctx) => ValidateContext(ctx);

    [SysAbiExport(Nid = "wIJsiqY+BMk", ExportName = "sceRudpGetContextStatus", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetContextStatus(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Contexts.TryGetValue(contextId, out var state) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteInt32(outputAddress, state.Status) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "2G7-vVz9SIg", ExportName = "sceRudpGetLocalInfo", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetLocalInfo(CpuContext ctx) => WriteEmptyInfo(ctx);

    [SysAbiExport(Nid = "vfrL8gPlm2Y", ExportName = "sceRudpGetMaxSegmentSize", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetMaxSegmentSize(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Contexts.TryGetValue(contextId, out var state) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt32(outputAddress, state.MaxSegmentSize) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "Px0miD2LuW0", ExportName = "sceRudpGetNumberOfPacketsToRead", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetNumberOfPacketsToRead(CpuContext ctx) => WriteContextUInt32(ctx, 0);

    [SysAbiExport(Nid = "mCQIhSmCP6o", ExportName = "sceRudpGetOption", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetOption(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rdx];
        if (!Contexts.ContainsKey(contextId))
        {
            return InvalidArgument(ctx);
        }

        if (outputAddress == 0)
        {
            return Ok(ctx);
        }

        return ctx.TryWriteUInt32(outputAddress, 0) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "Qignjmfgha0", ExportName = "sceRudpGetRemoteInfo", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetRemoteInfo(CpuContext ctx) => WriteEmptyInfo(ctx);

    [SysAbiExport(Nid = "sAZqO2+5Qqo", ExportName = "sceRudpGetSizeReadable", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetSizeReadable(CpuContext ctx) => WriteContextUInt32(ctx, 0);

    [SysAbiExport(Nid = "fRc1ahQppR4", ExportName = "sceRudpGetSizeWritable", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetSizeWritable(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return InvalidArgument(ctx);
        }

        return WriteUInt32(ctx, ctx[CpuRegister.Rsi], state.MaxSegmentSize);
    }

    [SysAbiExport(Nid = "i3STzxuwPx0", ExportName = "sceRudpGetStatus", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpGetStatus(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        return WriteUInt32(ctx, outputAddress, Volatile.Read(ref _initialized) == 0 ? 0u : 1u);
    }

    [SysAbiExport(Nid = "amuBfI-AQc4", ExportName = "sceRudpInit", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpInit(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "szEVu+edXV4", ExportName = "sceRudpInitiate", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpInitiate(CpuContext ctx) => SetContextStatus(ctx, 1);

    [SysAbiExport(Nid = "tYVWcWDnctE", ExportName = "sceRudpListen", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpListen(CpuContext ctx) => SetContextStatus(ctx, 1);

    [SysAbiExport(Nid = "+BJ9svDmjYs", ExportName = "sceRudpNetFlush", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpNetFlush(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "vPzJldDSxXc", ExportName = "sceRudpNetReceived", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpNetReceived(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "yzeXuww-UWg", ExportName = "sceRudpPollCancel", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpPollCancel(CpuContext ctx) => ValidatePoll(ctx);

    [SysAbiExport(Nid = "haMpc7TFx0A", ExportName = "sceRudpPollControl", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpPollControl(CpuContext ctx) => ValidatePoll(ctx);

    [SysAbiExport(Nid = "MVbmLASjn5M", ExportName = "sceRudpPollCreate", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpPollCreate(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        var pollId = unchecked((uint)Interlocked.Increment(ref _nextPollId));
        Polls[pollId] = 0;
        if (ctx.TryWriteUInt32(outputAddress, pollId))
        {
            return Ok(ctx);
        }

        Polls.TryRemove(pollId, out _);
        return MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "LjwbHpEeW0A", ExportName = "sceRudpPollDestroy", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpPollDestroy(CpuContext ctx)
    {
        var pollId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Polls.TryRemove(pollId, out _) ? Ok(ctx) : InvalidArgument(ctx);
    }

    [SysAbiExport(Nid = "M6ggviwXpLs", ExportName = "sceRudpPollWait", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpPollWait(CpuContext ctx) => ValidatePoll(ctx);

    [SysAbiExport(Nid = "9U9m1YH0ScQ", ExportName = "sceRudpProcessEvents", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpProcessEvents(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "rZqWV3eXgOA", ExportName = "sceRudpRead", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpRead(CpuContext ctx) => ValidateContext(ctx);

    [SysAbiExport(Nid = "SUEVes8gvmw", ExportName = "sceRudpSetEventHandler", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpSetEventHandler(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return InvalidArgument(ctx);
        }

        state.EventHandler = ctx[CpuRegister.Rsi];
        state.EventArgument = ctx[CpuRegister.Rdx];
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "beAsSTVWVPQ", ExportName = "sceRudpSetMaxSegmentSize", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpSetMaxSegmentSize(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var segmentSize = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state) || segmentSize == 0)
        {
            return InvalidArgument(ctx);
        }

        state.MaxSegmentSize = segmentSize;
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "0yzYdZf0IwE", ExportName = "sceRudpSetOption", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpSetOption(CpuContext ctx) => ValidateContext(ctx);

    [SysAbiExport(Nid = "OMYRTU0uc4w", ExportName = "sceRudpTerminate", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpTerminate(CpuContext ctx)
    {
        Contexts.Clear();
        Polls.Clear();
        Interlocked.Exchange(ref _initialized, 0);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "KaPL3fbTLCA", ExportName = "sceRudpWrite", LibraryName = "libSceRudp", Target = Generation.Gen5)]
    public static int RudpWrite(CpuContext ctx) => ValidateContext(ctx);

    private static uint NextContextId() => unchecked((uint)Interlocked.Increment(ref _nextContextId));

    private static int SetContextStatus(CpuContext ctx, int status)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return InvalidArgument(ctx);
        }

        state.Status = status;
        return Ok(ctx);
    }

    private static int ValidateContext(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Contexts.ContainsKey(contextId) ? Ok(ctx) : InvalidArgument(ctx);
    }

    private static int ValidatePoll(CpuContext ctx)
    {
        var pollId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Polls.ContainsKey(pollId) ? Ok(ctx) : InvalidArgument(ctx);
    }

    private static int WriteContextUInt32(CpuContext ctx, uint value)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Contexts.ContainsKey(contextId)
            ? WriteUInt32(ctx, ctx[CpuRegister.Rsi], value)
            : InvalidArgument(ctx);
    }

    private static int WriteEmptyInfo(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Contexts.ContainsKey(contextId) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        Span<byte> info = stackalloc byte[32];
        info.Clear();
        return ctx.Memory.TryWrite(outputAddress, info) ? Ok(ctx) : MemoryFault(ctx);
    }

    private static int WriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        if (address == 0)
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt32(address, value) ? Ok(ctx) : MemoryFault(ctx);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);
    private static int InvalidArgument(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    private static int MemoryFault(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
