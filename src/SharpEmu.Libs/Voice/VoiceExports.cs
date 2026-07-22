// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Voice;

public static class VoiceExports
{
    private const uint DefaultBitRate = 48000;
    private const uint UnityVolumeBits = 0x3F800000;
    private const int PortInfoSize = 32;
    private static readonly ConcurrentDictionary<uint, VoicePortState> Ports = new();
    private static readonly object ConnectionGate = new();
    private static readonly HashSet<(uint Input, uint Output)> Connections = [];
    private static int _initialized;
    private static int _started;
    private static int _nextPortId;

    private sealed class VoicePortState
    {
        public int PortType;
        public int State;
        public uint BitRate = DefaultBitRate;
        public uint Mute;
        public uint Attributes = 0;
        public uint VolumeBits = UnityVolumeBits;
    }

    internal static int PortCount => Ports.Count;

    internal static void ResetForTests()
    {
        Ports.Clear();
        lock (ConnectionGate)
        {
            Connections.Clear();
        }

        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _started, 0);
        Interlocked.Exchange(ref _nextPortId, 0);
    }

    [SysAbiExport(Nid = "oV9GAdJ23Gw", ExportName = "sceVoiceConnectIPortToOPort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceConnectIPortToOPort(CpuContext ctx)
    {
        var inputId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Ports.ContainsKey(inputId) || !Ports.ContainsKey(outputId))
        {
            return InvalidArgument(ctx);
        }

        lock (ConnectionGate)
        {
            Connections.Add((inputId, outputId));
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "nXpje5yNpaE", ExportName = "sceVoiceCreatePort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceCreatePort(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (Volatile.Read(ref _initialized) == 0 || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId));
        var state = new VoicePortState
        {
            PortType = unchecked((int)ctx[CpuRegister.Rsi]),
            State = Volatile.Read(ref _started) == 0 ? 0 : 1,
        };
        Ports[portId] = state;
        if (ctx.TryWriteUInt32(outputAddress, portId))
        {
            return Ok(ctx);
        }

        Ports.TryRemove(portId, out _);
        return MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "b7kJI+nx2hg", ExportName = "sceVoiceDeletePort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceDeletePort(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryRemove(portId, out _))
        {
            return InvalidArgument(ctx);
        }

        lock (ConnectionGate)
        {
            Connections.RemoveWhere(connection => connection.Input == portId || connection.Output == portId);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "ajVj3QG2um4", ExportName = "sceVoiceDisconnectIPortFromOPort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceDisconnectIPortFromOPort(CpuContext ctx)
    {
        var inputId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputId = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (ConnectionGate)
        {
            Connections.Remove((inputId, outputId));
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Oo0S5PH7FIQ", ExportName = "sceVoiceEnd", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceEnd(CpuContext ctx)
    {
        ResetForTests();
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "cJLufzou6bc", ExportName = "sceVoiceGetBitRate", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetBitRate(CpuContext ctx) => WritePortUInt32(ctx, static state => state.BitRate);

    [SysAbiExport(Nid = "Pc4z1QjForU", ExportName = "sceVoiceGetMuteFlag", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetMuteFlag(CpuContext ctx) => WritePortUInt32(ctx, static state => state.Mute);

    [SysAbiExport(Nid = "elcxZTEfHZM", ExportName = "sceVoiceGetPortAttr", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetPortAttr(CpuContext ctx) => WritePortUInt32(ctx, static state => state.Attributes);

    [SysAbiExport(Nid = "CrLqDwWLoXM", ExportName = "sceVoiceGetPortInfo", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetPortInfo(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Ports.TryGetValue(portId, out var state) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        Span<byte> info = stackalloc byte[PortInfoSize];
        info.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(info[0x00..], state.PortType);
        BinaryPrimitives.WriteInt32LittleEndian(info[0x04..], state.State);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x14..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[0x18..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(info[0x1A..], 0);
        return ctx.Memory.TryWrite(outputAddress, info) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "Z6QV6j7igvE", ExportName = "sceVoiceGetResourceInfo", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetResourceInfo(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        Span<byte> info = stackalloc byte[16];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], unchecked((uint)Ports.Count));
        return ctx.Memory.TryWrite(outputAddress, info) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "jjkCjneOYSs", ExportName = "sceVoiceGetVolume", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceGetVolume(CpuContext ctx) => WritePortUInt32(ctx, static state => state.VolumeBits);

    [SysAbiExport(Nid = "9TrhuGzberQ", ExportName = "sceVoiceInit", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceInit(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "IPHvnM5+g04", ExportName = "sceVoiceInitHQ", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceInitHQ(CpuContext ctx) => VoiceInit(ctx);

    [SysAbiExport(Nid = "x0slGBQW+wY", ExportName = "sceVoicePausePort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoicePausePort(CpuContext ctx) => SetPortState(ctx, 2);

    [SysAbiExport(Nid = "Dinob0yMRl8", ExportName = "sceVoicePausePortAll", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoicePausePortAll(CpuContext ctx)
    {
        foreach (var state in Ports.Values)
        {
            state.State = 2;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "cQ6DGsQEjV4", ExportName = "sceVoiceReadFromOPort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceReadFromOPort(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.ContainsKey(portId))
        {
            return InvalidArgument(ctx);
        }

        var sizeAddress = ctx[CpuRegister.Rdx];
        return sizeAddress == 0 || ctx.TryWriteUInt32(sizeAddress, 0) ? Ok(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(Nid = "udAxvCePkUs", ExportName = "sceVoiceResetPort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceResetPort(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(portId, out var state))
        {
            return InvalidArgument(ctx);
        }

        state.State = Volatile.Read(ref _started) == 0 ? 0 : 1;
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "gAgN+HkiEzY", ExportName = "sceVoiceResumePort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceResumePort(CpuContext ctx) => SetPortState(ctx, Volatile.Read(ref _started) == 0 ? 0 : 1);

    [SysAbiExport(Nid = "jbkJFmOZ9U0", ExportName = "sceVoiceResumePortAll", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceResumePortAll(CpuContext ctx)
    {
        var stateValue = Volatile.Read(ref _started) == 0 ? 0 : 1;
        foreach (var state in Ports.Values)
        {
            state.State = stateValue;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "TexwmOHQsDg", ExportName = "sceVoiceSetBitRate", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceSetBitRate(CpuContext ctx) => SetPortUInt32(ctx, static (state, value) => state.BitRate = value);

    [SysAbiExport(Nid = "gwUynkEgNFY", ExportName = "sceVoiceSetMuteFlag", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceSetMuteFlag(CpuContext ctx) => SetPortUInt32(ctx, static (state, value) => state.Mute = value == 0 ? 0u : 1u);

    [SysAbiExport(Nid = "oUha0S-Ij9Q", ExportName = "sceVoiceSetMuteFlagAll", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceSetMuteFlagAll(CpuContext ctx)
    {
        var mute = ctx[CpuRegister.Rdi] == 0 ? 0u : 1u;
        foreach (var state in Ports.Values)
        {
            state.Mute = mute;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "clyKUyi3RYU", ExportName = "sceVoiceSetThreadsParams", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceSetThreadsParams(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "QBFoAIjJoXQ", ExportName = "sceVoiceSetVolume", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceSetVolume(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(portId, out var state))
        {
            return InvalidArgument(ctx);
        }

        ctx.GetXmmRegister(0, out var low, out _);
        state.VolumeBits = unchecked((uint)low);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "54phPH2LZls", ExportName = "sceVoiceStart", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceStart(CpuContext ctx)
    {
        Interlocked.Exchange(ref _started, 1);
        foreach (var state in Ports.Values)
        {
            state.State = 1;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Ao2YNSA7-Qo", ExportName = "sceVoiceStop", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceStop(CpuContext ctx)
    {
        Interlocked.Exchange(ref _started, 0);
        foreach (var state in Ports.Values)
        {
            state.State = 0;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "jSZNP7xJrcw", ExportName = "sceVoiceUpdatePort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceUpdatePort(CpuContext ctx) => ValidatePort(ctx);

    [SysAbiExport(Nid = "hg9T73LlRiU", ExportName = "sceVoiceVADAdjustment", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceVADAdjustment(CpuContext ctx) => ValidatePort(ctx);

    [SysAbiExport(Nid = "wFeAxEeEi-8", ExportName = "sceVoiceVADSetVersion", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceVADSetVersion(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "YeJl6yDlhW0", ExportName = "sceVoiceWriteToIPort", LibraryName = "libSceVoice", Target = Generation.Gen5)]
    public static int VoiceWriteToIPort(CpuContext ctx) => ValidatePort(ctx);

    private static int WritePortUInt32(CpuContext ctx, Func<VoicePortState, uint> selector)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (!Ports.TryGetValue(portId, out var state) || outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt32(outputAddress, selector(state)) ? Ok(ctx) : MemoryFault(ctx);
    }

    private static int SetPortUInt32(CpuContext ctx, Action<VoicePortState, uint> setter)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(portId, out var state))
        {
            return InvalidArgument(ctx);
        }

        setter(state, unchecked((uint)ctx[CpuRegister.Rsi]));
        return Ok(ctx);
    }

    private static int SetPortState(CpuContext ctx, int stateValue)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(portId, out var state))
        {
            return InvalidArgument(ctx);
        }

        state.State = stateValue;
        return Ok(ctx);
    }

    private static int ValidatePort(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        return Ports.ContainsKey(portId) ? Ok(ctx) : InvalidArgument(ctx);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);
    private static int InvalidArgument(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    private static int MemoryFault(CpuContext ctx) => ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
