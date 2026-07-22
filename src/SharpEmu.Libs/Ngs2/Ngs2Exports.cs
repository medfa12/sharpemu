// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.Ngs2;

public static class Ngs2Exports
{
    private const int ErrorInvalidMaxGrainSamples = unchecked((int)0x804A0050);
    private const int ErrorInvalidNumGrainSamples = unchecked((int)0x804A0051);
    private const int ErrorInvalidNumChannels = unchecked((int)0x804A0052);
    private const int ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int ErrorInvalidOutSize = unchecked((int)0x804A0054);
    private const int ErrorInvalidOptionAddress = unchecked((int)0x804A0080);
    private const int ErrorInvalidOptionSize = unchecked((int)0x804A0081);
    private const int ErrorInvalidOptionFlag = unchecked((int)0x804A0082);
    private const int ErrorInvalidMaxVoices = unchecked((int)0x804A0103);
    private const int ErrorInvalidHandle = unchecked((int)0x804A0200);
    private const int ErrorInvalidSampleRate = unchecked((int)0x804A0201);
    private const int ErrorInvalidBufferInfo = unchecked((int)0x804A0206);
    private const int ErrorInvalidBufferAddress = unchecked((int)0x804A0207);
    private const int ErrorInvalidBufferSize = unchecked((int)0x804A0209);
    private const int ErrorInvalidBufferAllocator = unchecked((int)0x804A020A);
    private const int ErrorInvalidReportHandler = unchecked((int)0x804A0203);
    private const int ErrorInvalidReportHandle = unchecked((int)0x804A0204);
    private const int ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int ErrorInvalidRackId = unchecked((int)0x804A0260);
    private const int ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    private const int ErrorInvalidVoiceIndex = unchecked((int)0x804A0302);
    private const int ErrorInvalidEventType = unchecked((int)0x804A0303);
    private const int ErrorInvalidVoiceControlId = unchecked((int)0x804A0308);
    private const int ErrorInvalidVoiceControlAddress = unchecked((int)0x804A0309);
    private const int ErrorInvalidVoiceControlSize = unchecked((int)0x804A030A);
    private const int ErrorCircularVoiceControl = unchecked((int)0x804A030B);
    private const int ErrorInvalidOperation = unchecked((int)0x804A030F);
    private const int ErrorInvalidWaveformAddress = unchecked((int)0x804A0406);
    private const int ErrorInvalidWaveformSize = unchecked((int)0x804A0407);
    private const int ErrorInvalidWaveformType = unchecked((int)0x804A0402);
    private const int ErrorInvalidGeomDistance = unchecked((int)0x804A0920);
    private const int ErrorInvalidGeomListener = unchecked((int)0x804A0921);
    private const int ErrorInvalidGeomSource = unchecked((int)0x804A0922);
    private const int ErrorInvalidGeomFlag = unchecked((int)0x804A0923);
    private const int ErrorInvalidGeomCone = unchecked((int)0x804A0924);
    private const int ErrorInvalidVoiceStateSize = unchecked((int)0x804A0A06);

    private const ulong HandleStorageSize = 0x1000;
    private const int RenderBufferInfoSize = 0x18;
    private const int ContextBufferInfoSize = 0x40;
    private const int SystemInfoSize = 0x88;
    private const int RackInfoSize = 0xA8;
    private const int WaveformFormatSize = 0x18;
    private const int WaveformBlockSize = 0x20;
    private const int WaveformInfoSize = 0xC8;
    private const ulong MaximumWaveformBufferSize = 16 * 1024 * 1024;
    private const uint MinimumGrainSamples = 64;
    private const uint DefaultMaximumGrainSamples = 512;
    private const uint DefaultGrainSamples = 256;
    private const uint DefaultSampleRate = 48000;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static readonly Dictionary<ulong, StreamState> Streams = new();
    private static readonly HashSet<ulong> ReportHandles = new();
    private static long _nextUid;
    private static long _renderCount;

    private sealed class SystemState
    {
        public uint Uid;
        public string Name = string.Empty;
        public uint MaximumGrainSamples = DefaultMaximumGrainSamples;
        public uint GrainSamples = DefaultGrainSamples;
        public uint SampleRate = DefaultSampleRate;
        public ulong RenderCount;
        public ulong LastRenderTick;
        public float LastRenderRatio;
        public byte[] BufferInfo = new byte[ContextBufferInfoSize];
        public ulong UserData;
    }

    private sealed class RackState
    {
        public ulong SystemHandle;
        public uint RackId;
        public uint Uid;
        public string Name = string.Empty;
        public uint MaximumGrainSamples = DefaultMaximumGrainSamples;
        public uint MaximumVoices;
        public uint MaximumMatrices;
        public uint MaximumPorts;
        public ulong RenderCount;
        public byte[] BufferInfo = new byte[ContextBufferInfoSize];
        public ulong UserData;
    }

    private enum VoicePhase
    {
        Empty,
        Setup,
        Playing,
        Paused,
        Stopped,
    }

    private enum VoiceEvent
    {
        None,
        Play,
        Stop,
        StopImmediate,
        Kill,
        Pause,
    }

    private sealed class VoiceState
    {
        public ulong RackHandle;
        public uint VoiceIndex;
        public VoicePhase Phase;
        public VoiceEvent PendingEvent;
    }

    private sealed class StreamState
    {
        public ulong SystemHandle { get; init; }
    }

    private readonly record struct SystemOption(
        string Name,
        uint MaximumGrainSamples,
        uint GrainSamples,
        uint SampleRate);

    private readonly record struct RackOption(
        string Name,
        uint MaximumGrainSamples,
        uint MaximumVoices,
        uint MaximumMatrices,
        uint MaximumPorts);

    private readonly record struct RenderOutput(
        ulong Address,
        ulong Size,
        uint WaveformType,
        uint Channels,
        ulong RequiredSize);

    [SysAbiExport(
        Nid = "AQkj7C0f3PY",
        ExportName = "sceNgs2SystemResetOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemResetOption(CpuContext ctx)
    {
        var optionAddress = ctx[CpuRegister.Rdi];
        if (optionAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOptionAddress);
        }

        // The PS5 compatibility library's setup helper compares option.size with 0x40 at 0xd778.
        var option = new byte[0x40];
        BinaryPrimitives.WriteUInt64LittleEndian(option, 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x1C), DefaultMaximumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x20), DefaultGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x24), DefaultSampleRate);
        return ctx.Memory.TryWrite(optionAddress, option)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pgFAiLR5qT4",
        ExportName = "sceNgs2SystemQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryBufferSize(CpuContext ctx)
    {
        if (!TryReadSystemOption(ctx, ctx[CpuRegister.Rdi], out _, out var error))
        {
            return ctx.SetReturn(error);
        }
        return WriteContextBufferInfo(ctx, ctx[CpuRegister.Rsi], HandleStorageSize);
    }

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var optionAddress = ctx[CpuRegister.Rdi];
        var allocatorAddress = ctx[CpuRegister.Rsi];
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (!TryValidateAllocator(ctx, allocatorAddress))
        {
            return ctx.SetReturn(ErrorInvalidBufferAllocator);
        }
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryReadSystemOption(ctx, optionAddress, out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }

        return CreateSystem(ctx, option, outHandleAddress, null);
    }

    [SysAbiExport(
        Nid = "koBbCMvOKWw",
        ExportName = "sceNgs2SystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreate(CpuContext ctx)
    {
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        if (bufferInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryReadSystemOption(ctx, ctx[CpuRegister.Rdi], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        var bufferInfo = new byte[ContextBufferInfoSize];
        if (!ctx.Memory.TryRead(bufferInfoAddress, bufferInfo))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var hostBuffer = BinaryPrimitives.ReadUInt64LittleEndian(bufferInfo);
        var hostBufferSize = BinaryPrimitives.ReadUInt64LittleEndian(bufferInfo.AsSpan(8));
        if (hostBuffer == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferAddress);
        }
        if (hostBufferSize < HandleStorageSize)
        {
            return ctx.SetReturn(ErrorInvalidBufferSize);
        }
        return CreateSystem(ctx, option, ctx[CpuRegister.Rdx], bufferInfo);
    }

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            foreach (var rackHandle in Racks
                         .Where(pair => pair.Value.SystemHandle == handle)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                RemoveRackLocked(rackHandle);
            }
        }

        var outInfoAddress = ctx[CpuRegister.Rsi];
        if (outInfoAddress != 0 && !TryClearGuestBuffer(ctx, outInfoAddress, ContextBufferInfoSize))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "vU7TQ62pItw",
        ExportName = "sceNgs2SystemGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemGetInfo(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var outAddress = ctx[CpuRegister.Rsi];
        var outSize = ctx[CpuRegister.Rdx];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (outSize < SystemInfoSize)
        {
            return ctx.SetReturn(ErrorInvalidOutSize);
        }

        SystemState system;
        uint rackCount;
        lock (StateGate)
        {
            if (!Systems.TryGetValue(handle, out system!))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            rackCount = (uint)Racks.Count(pair => pair.Value.SystemHandle == handle);
        }

        var info = new byte[SystemInfoSize];
        WriteName(info.AsSpan(0, 16), system.Name);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(16), handle);
        system.BufferInfo.CopyTo(info, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(88), system.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(92), MinimumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(96), system.MaximumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(100), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(104), rackCount);
        BinaryPrimitives.WriteSingleLittleEndian(info.AsSpan(108), system.LastRenderRatio);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(112), system.LastRenderTick);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(120), system.RenderCount);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(128), system.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(132), system.GrainSamples);
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0eFLVCfWVds",
        ExportName = "sceNgs2RackQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryBufferSize(CpuContext ctx)
    {
        var rackId = (uint)ctx[CpuRegister.Rdi];
        if (!TryReadRackOption(ctx, rackId, ctx[CpuRegister.Rsi], out var option, out var error))
        {
            return ctx.SetReturn(error);
        }
        var required = HandleStorageSize + (ulong)option.MaximumVoices * 0x40;
        return WriteContextBufferInfo(ctx, ctx[CpuRegister.Rdx], required);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = (uint)ctx[CpuRegister.Rsi];
        if (ExpectedRackOptionSize(rackId) == 0)
        {
            return ctx.SetReturn(ErrorInvalidRackId);
        }
        if (!TryValidateAllocator(ctx, ctx[CpuRegister.Rcx]))
        {
            return ctx.SetReturn(ErrorInvalidBufferAllocator);
        }
        if (ctx[CpuRegister.R8] == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryReadRackOption(ctx, rackId, ctx[CpuRegister.Rdx], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        return CreateRack(ctx, systemHandle, rackId, option, ctx[CpuRegister.R8], null);
    }

    [SysAbiExport(
        Nid = "cLV4aiT9JpA",
        ExportName = "sceNgs2RackCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = (uint)ctx[CpuRegister.Rsi];
        var bufferInfoAddress = ctx[CpuRegister.Rcx];
        if (ExpectedRackOptionSize(rackId) == 0)
        {
            return ctx.SetReturn(ErrorInvalidRackId);
        }
        if (bufferInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }
        if (ctx[CpuRegister.R8] == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryReadRackOption(ctx, rackId, ctx[CpuRegister.Rdx], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        var bufferInfo = new byte[ContextBufferInfoSize];
        if (!ctx.Memory.TryRead(bufferInfoAddress, bufferInfo))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var hostBuffer = BinaryPrimitives.ReadUInt64LittleEndian(bufferInfo);
        var hostBufferSize = BinaryPrimitives.ReadUInt64LittleEndian(bufferInfo.AsSpan(8));
        if (hostBuffer == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferAddress);
        }
        var requiredSize = HandleStorageSize + (ulong)option.MaximumVoices * 0x40;
        if (hostBufferSize < requiredSize)
        {
            return ctx.SetReturn(ErrorInvalidBufferSize);
        }
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        return CreateRack(ctx, systemHandle, rackId, option, ctx[CpuRegister.R8], bufferInfo);
    }

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Racks.ContainsKey(ctx[CpuRegister.Rdi]))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            RemoveRackLocked(ctx[CpuRegister.Rdi]);
        }
        var outInfoAddress = ctx[CpuRegister.Rsi];
        if (outInfoAddress != 0 && !TryClearGuestBuffer(ctx, outInfoAddress, ContextBufferInfoSize))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "M4LYATRhRUE",
        ExportName = "sceNgs2RackGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetInfo(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var outAddress = ctx[CpuRegister.Rsi];
        var outSize = ctx[CpuRegister.Rdx];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (outSize < RackInfoSize)
        {
            return ctx.SetReturn(ErrorInvalidOutSize);
        }

        RackState rack;
        uint activeVoices;
        lock (StateGate)
        {
            if (!Racks.TryGetValue(rackHandle, out rack!))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            activeVoices = (uint)Voices.Count(pair =>
                pair.Value.RackHandle == rackHandle &&
                pair.Value.Phase is VoicePhase.Playing or VoicePhase.Paused);
        }

        var info = new byte[RackInfoSize];
        WriteName(info.AsSpan(0, 16), rack.Name);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(16), rackHandle);
        rack.BufferInfo.CopyTo(info, 24);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(88), rack.SystemHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(96), RackType(rack.RackId));
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(100), rack.RackId);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(104), rack.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(108), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(112), rack.MaximumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(116), rack.MaximumVoices);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(128), rack.MaximumMatrices);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(132), rack.MaximumPorts);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(136), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(152), rack.RenderCount);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(160), activeVoices);
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = (uint)ctx[CpuRegister.Rsi];
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }

        lock (StateGate)
        {
            if (!Racks.TryGetValue(rackHandle, out var rack))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            if (voiceIndex >= rack.MaximumVoices)
            {
                return ctx.SetReturn(ErrorInvalidVoiceIndex);
            }
            var existing = Voices.FirstOrDefault(pair =>
                pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? ctx.SetReturn(0)
                    : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (!TryCreateHandle(ctx, type: 3, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        lock (StateGate)
        {
            Voices[handle] = new VoiceState { RackHandle = rackHandle, VoiceIndex = voiceIndex };
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidVoiceControlAddress);
        }

        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return ctx.SetReturn(ErrorInvalidVoiceHandle);
            }
            if (!Racks.TryGetValue(voice.RackHandle, out var rack))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            return ctx.SetReturn(ApplyVoiceParameters(ctx, rack, voice, parameterAddress));
        }
    }

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Voices.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidVoiceHandle);
        }
    }

    [SysAbiExport(
        Nid = "-TOuuAQ-buE",
        ExportName = "sceNgs2VoiceGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetState(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        var stateSize = ctx[CpuRegister.Rdx];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (stateSize < 4)
        {
            return ctx.SetReturn(ErrorInvalidOutSize);
        }
        if (stateSize > 8)
        {
            return ctx.SetReturn(ErrorInvalidVoiceStateSize);
        }

        VoiceState voice;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out voice!))
            {
                return ctx.SetReturn(ErrorInvalidVoiceHandle);
            }
        }

        var state = new byte[(int)stateSize];
        BinaryPrimitives.WriteUInt32LittleEndian(state, StateFlags(voice.Phase));
        if (state.Length >= 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(4), 0);
        }
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rEh728kXk3w",
        ExportName = "sceNgs2VoiceGetStateFlags",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetStateFlags(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var flagsAddress = ctx[CpuRegister.Rsi];
        if (flagsAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        uint flags;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                if (!ctx.TryWriteUInt32(flagsAddress, 0))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
                return ctx.SetReturn(ErrorInvalidVoiceHandle);
            }
            flags = StateFlags(voice.Phase);
        }
        return ctx.TryWriteUInt32(flagsAddress, flags)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "W-Z8wWMBnhk",
        ExportName = "sceNgs2VoiceGetOwner",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetOwner(CpuContext ctx)
    {
        var outRackAddress = ctx[CpuRegister.Rsi];
        var outVoiceIdAddress = ctx[CpuRegister.Rdx];
        if (outRackAddress == 0 || outVoiceIdAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        lock (StateGate)
        {
            if (!Voices.TryGetValue(ctx[CpuRegister.Rdi], out var voice))
            {
                return ctx.SetReturn(ErrorInvalidVoiceHandle);
            }
            return ctx.TryWriteUInt64(outRackAddress, voice.RackHandle) &&
                   ctx.TryWriteUInt32(outVoiceIdAddress, voice.VoiceIndex)
                ? ctx.SetReturn(0)
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        var bufferInfoCount = (uint)ctx[CpuRegister.Rdx];
        SystemState system;
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out system!))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        // libSceNgs2.sprx 0xe9a6/0xe9af rejects both a null array and counts outside 1..16.
        if (bufferInfoAddress == 0 || bufferInfoCount is 0 or > 16)
        {
            return ctx.SetReturn(ErrorInvalidBufferAddress);
        }

        var outputs = new RenderOutput[bufferInfoCount];
        for (uint index = 0; index < bufferInfoCount; index++)
        {
            var entryAddress = bufferInfoAddress + index * RenderBufferInfoSize;
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize) ||
                !ctx.TryReadUInt32(entryAddress + 16, out var waveformType) ||
                !ctx.TryReadUInt32(entryAddress + 20, out var channels))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            outputs[index] = new RenderOutput(bufferAddress, bufferSize, waveformType, channels, 0);
        }

        for (var index = 0; index < outputs.Length; index++)
        {
            var output = outputs[index];
            if (output.Channels is not (1 or 2 or 6 or 8))
            {
                return ClearRenderOutputsAndReturn(ctx, outputs, ErrorInvalidNumChannels);
            }
            var bytesPerSample = output.WaveformType switch
            {
                0x12 or 0x13 => 2UL,
                0x18 or 0x19 => 4UL,
                _ => 0UL,
            };
            if (bytesPerSample == 0)
            {
                return ClearRenderOutputsAndReturn(ctx, outputs, ErrorInvalidWaveformType);
            }
            if (output.Address == 0)
            {
                return ClearRenderOutputsAndReturn(ctx, outputs, ErrorInvalidBufferAddress);
            }
            var requiredSize = (ulong)system.GrainSamples * output.Channels * bytesPerSample;
            outputs[index] = output with { RequiredSize = requiredSize };
            if (output.Size < requiredSize)
            {
                return ClearRenderOutputsAndReturn(ctx, outputs, ErrorInvalidBufferSize);
            }
        }

        foreach (var output in outputs)
        {
            if (!TryClearGuestBuffer(ctx, output.Address, output.RequiredSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        lock (StateGate)
        {
            system.RenderCount++;
            system.LastRenderTick = system.RenderCount;
            system.LastRenderRatio = 0;
            foreach (var pair in Racks.Where(pair => pair.Value.SystemHandle == systemHandle))
            {
                pair.Value.RenderCount++;
                foreach (var voice in Voices.Values.Where(value => value.RackHandle == pair.Key))
                {
                    ApplyPendingEvent(voice);
                }
            }
        }

        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 10_000 == 0))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ngs2.render#{count} system=0x{systemHandle:X16} buffers={bufferInfoCount}");
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "l4Q2dWEH6UM",
        ExportName = "sceNgs2SystemSetGrainSamples",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetGrainSamples(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            var samples = (uint)ctx[CpuRegister.Rsi];
            if (!IsValidGrainSamples(samples) || samples > system.MaximumGrainSamples)
            {
                return ctx.SetReturn(ErrorInvalidNumGrainSamples);
            }
            system.GrainSamples = samples;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "-tbc2SxQD60",
        ExportName = "sceNgs2SystemSetSampleRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetSampleRate(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            var sampleRate = (uint)ctx[CpuRegister.Rsi];
            if (!IsValidSampleRate(sampleRate))
            {
                return ctx.SetReturn(ErrorInvalidSampleRate);
            }
            system.SampleRate = sampleRate;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "GZB2v0XnG0k",
        ExportName = "sceNgs2SystemSetUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetUserData(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            system.UserData = ctx[CpuRegister.Rsi];
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "4lFaRxd-aLs",
        ExportName = "sceNgs2SystemGetUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemGetUserData(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        lock (StateGate)
        {
            if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], system.UserData)
                ? ctx.SetReturn(0)
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "JNTMIaBIbV4",
        ExportName = "sceNgs2RackSetUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackSetUserData(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Racks.TryGetValue(ctx[CpuRegister.Rdi], out var rack))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            rack.UserData = ctx[CpuRegister.Rsi];
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "Mn4XNDg03XY",
        ExportName = "sceNgs2RackGetUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetUserData(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        lock (StateGate)
        {
            if (!Racks.TryGetValue(ctx[CpuRegister.Rdi], out var rack))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
            return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], rack.UserData)
                ? ctx.SetReturn(0)
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "gThZqM5PYlQ",
        ExportName = "sceNgs2SystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemLock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "JXRC5n0RQls",
        ExportName = "sceNgs2SystemUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemUnlock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "MzTa7VLjogY",
        ExportName = "sceNgs2RackLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackLock(CpuContext ctx) => ValidateRack(ctx);

    [SysAbiExport(
        Nid = "++YZ7P9e87U",
        ExportName = "sceNgs2RackUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackUnlock(CpuContext ctx) => ValidateRack(ctx);

    [SysAbiExport(
        Nid = "ekGJmmoc8j4",
        ExportName = "sceNgs2GetWaveformFrameInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GetWaveformFrameInfo(CpuContext ctx)
    {
        var formatAddress = ctx[CpuRegister.Rdi];
        if (formatAddress == 0 || !ctx.TryReadUInt32(formatAddress + 4, out var channels) ||
            !ctx.TryReadUInt32(formatAddress + 12, out var config))
        {
            return ctx.SetReturn(ErrorInvalidWaveformAddress);
        }
        channels = Math.Max(channels, 1);
        var bytesPerSample = config is 8 or 24 or 32 ? (config + 7) / 8 : 2u;
        var frameSize = channels * bytesPerSample;
        if (!TryWriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], frameSize) ||
            !TryWriteOptionalUInt32(ctx, ctx[CpuRegister.Rdx], 1) ||
            !TryWriteOptionalUInt32(ctx, ctx[CpuRegister.Rcx], 1) ||
            !TryWriteOptionalUInt32(ctx, ctx[CpuRegister.R8], 0))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "hyVLT2VlOYk",
        ExportName = "sceNgs2ParseWaveformData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformData(CpuContext ctx)
    {
        var dataAddress = ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rsi];
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (dataAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidWaveformAddress);
        }
        if (dataSize == 0 || dataSize > MaximumWaveformBufferSize)
        {
            return ctx.SetReturn(ErrorInvalidWaveformSize);
        }
        if (outInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }

        var data = new byte[(int)dataSize];
        if (!ctx.Memory.TryRead(dataAddress, data))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var info = new byte[WaveformInfoSize];
        if (!TryParseWave(ctx, dataAddress, data, info))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), DefaultSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)dataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(44), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(48), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(52), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(56), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(60), 1);
        }
        return ctx.Memory.TryWrite(outInfoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3pCNbVM11UA",
        ExportName = "sceNgs2CalcWaveformBlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2CalcWaveformBlock(CpuContext ctx)
    {
        var formatAddress = ctx[CpuRegister.Rdi];
        var samplePosition = (uint)ctx[CpuRegister.Rsi];
        var sampleCount = (uint)ctx[CpuRegister.Rdx];
        var outBlockAddress = ctx[CpuRegister.Rcx];
        if (formatAddress == 0 || outBlockAddress == 0 ||
            !ctx.TryReadUInt32(formatAddress + 4, out var channels) ||
            !ctx.TryReadUInt32(formatAddress + 12, out var config))
        {
            return ctx.SetReturn(ErrorInvalidWaveformAddress);
        }
        channels = Math.Max(channels, 1);
        var bytesPerSample = config is 8 or 24 or 32 ? (config + 7) / 8 : 2u;
        var blockAlign = channels * bytesPerSample;
        Span<byte> block = stackalloc byte[WaveformBlockSize];
        block.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(block, samplePosition * blockAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], sampleCount * blockAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(block[16..], sampleCount);
        return ctx.Memory.TryWrite(outBlockAddress, block)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "xa8oL9dmXkM",
        ExportName = "sceNgs2PanInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2PanInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "1WsleK-MTkE",
        ExportName = "sceNgs2GeomCalcListener",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomCalcListener(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "0lbbayqDNoE",
        ExportName = "sceNgs2GeomResetSourceParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetSourceParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "7Lcfo8SmpsU",
        ExportName = "sceNgs2GeomResetListenerParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetListenerParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "6qN1zaEZuN0", ExportName = "sceNgs2CustomRackGetModuleInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2CustomRackGetModuleInfo(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var moduleIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outAddress = ctx[CpuRegister.Rdx];
        var outSize = ctx[CpuRegister.Rcx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return ctx.SetReturn(ErrorInvalidRackHandle);
            }
        }
        if (moduleIndex >= 24)
        {
            return ctx.SetReturn(unchecked((int)0x804A0B00));
        }
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (outSize < 0x20 || outSize > 0x1000)
        {
            return ctx.SetReturn(unchecked((int)0x804A0B01));
        }
        return ctx.SetReturn(TryClearGuestBuffer(ctx, outAddress, outSize)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "Kg1MA5j7KFk", ExportName = "sceNgs2FftInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2FftInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "D8eCqBxSojA", ExportName = "sceNgs2FftProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2FftProcess(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "-YNfTO6KOMY", ExportName = "sceNgs2FftQuerySize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2FftQuerySize(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "eF8yRCC6W64", ExportName = "sceNgs2GeomApply", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2GeomApply(CpuContext ctx)
    {
        var listenerAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (listenerAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidGeomListener);
        }
        if (sourceAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidGeomSource);
        }
        if (outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        // The oracle rejects zero, bits above 0x1f, and matrix+ambisonics at 0x2b24..0x2b42.
        if (flags is 0 or > 0x1F || (flags & 0x14) == 0x14)
        {
            return ctx.SetReturn(ErrorInvalidGeomFlag);
        }

        var listener = new byte[0x60];
        var source = new byte[0x68];
        if (!ctx.Memory.TryRead(listenerAddress, listener) || !ctx.Memory.TryRead(sourceAddress, source))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var rolloffModel = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(0x34));
        var maxDistance = ReadSingle(source, 0x38);
        var rolloffFactor = ReadSingle(source, 0x3C);
        var referenceDistance = ReadSingle(source, 0x40);
        if (rolloffModel > 5 || maxDistance < 0 || rolloffFactor < 0 || referenceDistance < 0)
        {
            return ctx.SetReturn(ErrorInvalidGeomDistance);
        }
        if ((flags & 1) != 0 && !IsValidCone(source))
        {
            return ctx.SetReturn(ErrorInvalidGeomCone);
        }

        var x = ReadSingle(source, 0);
        var y = ReadSingle(source, 4);
        var z = ReadSingle(source, 8);
        var tx = x * ReadSingle(listener, 0x00) + y * ReadSingle(listener, 0x10) +
                 z * ReadSingle(listener, 0x20) + ReadSingle(listener, 0x30);
        var ty = x * ReadSingle(listener, 0x04) + y * ReadSingle(listener, 0x14) +
                 z * ReadSingle(listener, 0x24) + ReadSingle(listener, 0x34);
        var tz = x * ReadSingle(listener, 0x08) + y * ReadSingle(listener, 0x18) +
                 z * ReadSingle(listener, 0x28) + ReadSingle(listener, 0x38);
        if (BinaryPrimitives.ReadUInt32LittleEndian(listener.AsSpan(0x50)) != 0)
        {
            tz = -tz;
        }

        var distance = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
        var level = CalculateDistanceLevel(
            rolloffModel, distance, maxDistance, rolloffFactor, referenceDistance);
        var minLevel = MathF.Max(0, ReadSingle(source, 0x54));
        var maxLevel = MathF.Max(0, ReadSingle(source, 0x50));
        if (maxLevel < minLevel)
        {
            (minLevel, maxLevel) = (maxLevel, minLevel);
        }
        level = Math.Clamp(level, minLevel, maxLevel);
        if ((flags & 1) != 0)
        {
            level *= CalculateConeLevel(source, tx, ty, tz);
        }

        var attribute = new byte[0x134];
        _ = ctx.Memory.TryRead(outputAddress, attribute);
        if ((flags & 2) != 0)
        {
            WriteSingle(attribute, 0, CalculateDopplerPitch(listener, source, tx, ty, tz, distance));
        }
        if ((flags & 4) != 0)
        {
            attribute.AsSpan(4, 64 * sizeof(float)).Clear();
            var matrixChannels = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(0x60));
            if (matrixChannels is not (1 or 2 or 6 or 8))
            {
                matrixChannels = 1;
            }
            for (var channel = 0; channel < matrixChannels; channel++)
            {
                WriteSingle(attribute, 4 + channel * (8 + 1) * sizeof(float), level);
            }
        }
        if ((flags & 8) != 0)
        {
            WriteSingle(attribute, 0x104, tx);
            WriteSingle(attribute, 0x108, ty);
            WriteSingle(attribute, 0x10C, tz);
            WriteSingle(attribute, 0x110, level);
        }
        if ((flags & 0x10) != 0)
        {
            attribute.AsSpan(4, 8 * sizeof(float)).Clear();
            WriteSingle(attribute, 4, level);
        }
        return ctx.Memory.TryWrite(outputAddress, attribute)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "BcoPfWfpvVI", ExportName = "sceNgs2JobSchedulerResetOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2JobSchedulerResetOption(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "EEemGEQCjO8", ExportName = "sceNgs2ModuleArrayEnumItems", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ModuleArrayEnumItems(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "TaoNtmMKkXQ", ExportName = "sceNgs2ModuleEnumConfigs", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ModuleEnumConfigs(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "ve6bZi+1sYQ", ExportName = "sceNgs2ModuleQueueEnumItems", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ModuleQueueEnumItems(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "gbMKV+8Enuo", ExportName = "sceNgs2PanGetVolumeMatrix", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2PanGetVolumeMatrix(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(unchecked((int)0x804A0914));
        }
        var outputAddress = ctx[CpuRegister.R8];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        Span<byte> matrix = stackalloc byte[8 * 8 * sizeof(float)];
        matrix.Clear();
        return ctx.Memory.TryWrite(outputAddress, matrix)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "iprCTXPVWMI", ExportName = "sceNgs2ParseWaveformFile", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformFile(CpuContext ctx)
    {
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rdi], 4096, out _))
        {
            return ctx.SetReturn(ErrorInvalidWaveformAddress);
        }
        return WriteEmptyWaveformInfo(ctx, ctx[CpuRegister.Rdx]);
    }

    [SysAbiExport(Nid = "t9T0QM17Kvo", ExportName = "sceNgs2ParseWaveformUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformUser(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidWaveformAddress);
        }
        return WriteEmptyWaveformInfo(ctx, ctx[CpuRegister.Rdx]);
    }

    [SysAbiExport(Nid = "TZqb8E-j3dY", ExportName = "sceNgs2RackQueryInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryInfo(CpuContext ctx) => Ngs2RackGetInfo(ctx);

    [SysAbiExport(Nid = "MI2VmBx2RbM", ExportName = "sceNgs2RackRunCommands", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2RackRunCommands(CpuContext ctx) => ValidateRack(ctx);

    [SysAbiExport(Nid = "uBIN24Tv2MI", ExportName = "sceNgs2ReportRegisterHandler", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ReportRegisterHandler(CpuContext ctx)
    {
        var handler = ctx[CpuRegister.Rsi];
        var outAddress = ctx[CpuRegister.Rcx];
        if (handler == 0)
        {
            return ctx.SetReturn(ErrorInvalidReportHandler);
        }
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryCreateHandle(ctx, 4, 0, out var handle) || !ctx.TryWriteUInt64(outAddress, handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        lock (StateGate)
        {
            ReportHandles.Add(handle);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "nPzb7Ly-VjE", ExportName = "sceNgs2ReportUnregisterHandler", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2ReportUnregisterHandler(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(ReportHandles.Remove(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidReportHandle);
        }
    }

    [SysAbiExport(Nid = "sU2St3agdjg", ExportName = "sceNgs2StreamCreate", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamCreate(CpuContext ctx) => CreateStream(ctx, withAllocator: false);

    [SysAbiExport(Nid = "I+RLwaauggA", ExportName = "sceNgs2StreamCreateWithAllocator", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamCreateWithAllocator(CpuContext ctx) => CreateStream(ctx, withAllocator: true);

    [SysAbiExport(Nid = "bfoMXnTRtwE", ExportName = "sceNgs2StreamDestroy", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamDestroy(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Streams.Remove(ctx[CpuRegister.Rdi]))
            {
                return ctx.SetReturn(ErrorInvalidHandle);
            }
        }
        var outInfoAddress = ctx[CpuRegister.Rsi];
        if (outInfoAddress != 0 && !TryClearGuestBuffer(ctx, outInfoAddress, ContextBufferInfoSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "dxulc33msHM", ExportName = "sceNgs2StreamQueryBufferSize", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamQueryBufferSize(CpuContext ctx) =>
        WriteContextBufferInfo(ctx, ctx[CpuRegister.Rsi], HandleStorageSize);

    [SysAbiExport(Nid = "rfw6ufRsmow", ExportName = "sceNgs2StreamQueryInfo", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamQueryInfo(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var outAddress = ctx[CpuRegister.Rsi];
        var outSize = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Streams.TryGetValue(handle, out var stream))
            {
                return ctx.SetReturn(ErrorInvalidHandle);
            }
            if (outAddress == 0)
            {
                return ctx.SetReturn(ErrorInvalidOutAddress);
            }
            if (outSize < 0x20 || outSize > 0x1000)
            {
                return ctx.SetReturn(ErrorInvalidOutSize);
            }
            var info = new byte[checked((int)outSize)];
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x08), handle);
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(0x10), stream.SystemHandle);
            return ctx.Memory.TryWrite(outAddress, info)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "q+2W8YdK0F8", ExportName = "sceNgs2StreamResetOption", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamResetOption(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorInvalidOptionAddress);
        }
        Span<byte> option = stackalloc byte[0x80];
        option.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(option, 0x80);
        return ctx.Memory.TryWrite(address, option)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "qQHCi9pjDps", ExportName = "sceNgs2StreamRunCommands", Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2StreamRunCommands(CpuContext ctx) => ValidateStream(ctx);

    [SysAbiExport(Nid = "vubFP0T6MP0", ExportName = "sceNgs2SystemEnumHandles", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemEnumHandles(CpuContext ctx)
    {
        ulong[] handles;
        lock (StateGate)
        {
            handles = Systems.Keys.OrderBy(static handle => handle).ToArray();
        }
        return WriteHandleArray(ctx, ctx[CpuRegister.Rdi], unchecked((uint)ctx[CpuRegister.Rsi]), handles);
    }

    [SysAbiExport(Nid = "U-+7HsswcIs", ExportName = "sceNgs2SystemEnumRackHandles", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemEnumRackHandles(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        ulong[] handles;
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
            handles = Racks.Where(pair => pair.Value.SystemHandle == systemHandle)
                .Select(pair => pair.Key)
                .OrderBy(static handle => handle)
                .ToArray();
        }
        return WriteHandleArray(ctx, ctx[CpuRegister.Rsi], unchecked((uint)ctx[CpuRegister.Rdx]), handles);
    }

    [SysAbiExport(Nid = "3oIK7y7O4k0", ExportName = "sceNgs2SystemQueryInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryInfo(CpuContext ctx) => Ngs2SystemGetInfo(ctx);

    [SysAbiExport(Nid = "gXiormHoZZ4", ExportName = "sceNgs2SystemRunCommands", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRunCommands(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(Nid = "Wdlx0ZFTV9s", ExportName = "sceNgs2SystemSetLoudThreshold", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetLoudThreshold(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(Nid = "jjBVvPN9964", ExportName = "sceNgs2VoiceGetMatrixInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetMatrixInfo(CpuContext ctx)
    {
        if (!TryValidateVoiceOutput(ctx, 0x104, out var outAddress, out var error))
        {
            return ctx.SetReturn(error);
        }
        Span<byte> info = stackalloc byte[0x104];
        info.Clear();
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "WCayTgob7-o", ExportName = "sceNgs2VoiceGetPortInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetPortInfo(CpuContext ctx)
    {
        if (!TryValidateVoiceOutput(ctx, 0x18, out var outAddress, out var error))
        {
            return ctx.SetReturn(error);
        }
        Span<byte> info = stackalloc byte[0x18];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info, uint.MaxValue);
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "9eic4AmjGVI", ExportName = "sceNgs2VoiceQueryInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceQueryInfo(CpuContext ctx) => Ngs2VoiceGetState(ctx);

    private static int CreateStream(CpuContext ctx, bool withAllocator)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var storageAddress = ctx[CpuRegister.Rdx];
        var outAddress = ctx[CpuRegister.Rcx];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (withAllocator && !TryValidateAllocator(ctx, storageAddress))
        {
            return ctx.SetReturn(ErrorInvalidBufferAllocator);
        }
        if (!withAllocator && (storageAddress == 0 || !ctx.TryReadUInt64(storageAddress, out var buffer) || buffer == 0))
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }
        if (!TryCreateHandle(ctx, 5, systemHandle, out var handle) || !ctx.TryWriteUInt64(outAddress, handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        lock (StateGate)
        {
            Streams.Add(handle, new StreamState { SystemHandle = systemHandle });
        }
        return ctx.SetReturn(0);
    }

    private static int ValidateStream(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Streams.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidHandle);
        }
    }

    private static int WriteHandleArray(CpuContext ctx, ulong outAddress, uint maximumHandles, ReadOnlySpan<ulong> handles)
    {
        if (maximumHandles != 0 && outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        var count = Math.Min(checked((int)Math.Min(maximumHandles, int.MaxValue)), handles.Length);
        for (var index = 0; index < count; index++)
        {
            if (!ctx.TryWriteUInt64(outAddress + unchecked((ulong)index * sizeof(ulong)), handles[index]))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }
        return ctx.SetReturn(count);
    }

    private static int WriteEmptyWaveformInfo(CpuContext ctx, ulong outAddress)
    {
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        var info = new byte[WaveformInfoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), DefaultSampleRate);
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryValidateVoiceOutput(CpuContext ctx, ulong requiredSize, out ulong outAddress, out int error)
    {
        outAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(ctx[CpuRegister.Rdi]))
            {
                error = ErrorInvalidVoiceHandle;
                return false;
            }
        }
        if (outAddress == 0)
        {
            error = ErrorInvalidOutAddress;
            return false;
        }
        if (ctx[CpuRegister.Rcx] < requiredSize)
        {
            error = ErrorInvalidOutSize;
            return false;
        }
        error = 0;
        return true;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);

    private static void WriteSingle(Span<byte> data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data[offset..], value);

    private static bool IsValidCone(ReadOnlySpan<byte> source)
    {
        var innerLevel = ReadSingle(source, 0x24);
        var innerAngle = ReadSingle(source, 0x28);
        var outerLevel = ReadSingle(source, 0x2C);
        var outerAngle = ReadSingle(source, 0x30);
        return innerLevel is >= 0 and <= 2 && outerLevel is >= 0 and <= 2 &&
               innerAngle is >= 0 and <= 360 && outerAngle is >= 0 and <= 360 &&
               innerAngle <= outerAngle;
    }

    private static float CalculateDistanceLevel(
        uint model,
        float distance,
        float maxDistance,
        float rolloffFactor,
        float referenceDistance)
    {
        var clampedDistance = Math.Clamp(distance, referenceDistance, MathF.Max(referenceDistance, maxDistance));
        var selectedDistance = model >= 3 ? clampedDistance : distance;
        var baseModel = model % 3;
        if (rolloffFactor == 0)
        {
            return 1;
        }
        return baseModel switch
        {
            0 => referenceDistance <= 0
                ? 1
                : MathF.Max(0, referenceDistance /
                    (referenceDistance + rolloffFactor * (selectedDistance - referenceDistance))),
            1 => maxDistance <= referenceDistance
                ? (selectedDistance <= referenceDistance ? 1 : 0)
                : MathF.Max(0, 1 - rolloffFactor *
                    (selectedDistance - referenceDistance) / (maxDistance - referenceDistance)),
            2 => referenceDistance <= 0 || selectedDistance <= 0
                ? 1
                : MathF.Pow(selectedDistance / referenceDistance, -rolloffFactor),
            _ => 1,
        };
    }

    private static float CalculateConeLevel(ReadOnlySpan<byte> source, float x, float y, float z)
    {
        var distance = MathF.Sqrt(x * x + y * y + z * z);
        var dx = ReadSingle(source, 0x18);
        var dy = ReadSingle(source, 0x1C);
        var dz = ReadSingle(source, 0x20);
        var directionLength = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance == 0 || directionLength == 0)
        {
            return ReadSingle(source, 0x24);
        }
        var cosine = Math.Clamp((dx * -x + dy * -y + dz * -z) / (directionLength * distance), -1, 1);
        var angle = MathF.Acos(cosine) * (180 / MathF.PI) * 2;
        var innerAngle = ReadSingle(source, 0x28);
        var outerAngle = ReadSingle(source, 0x30);
        var innerLevel = ReadSingle(source, 0x24);
        var outerLevel = ReadSingle(source, 0x2C);
        if (angle <= innerAngle)
        {
            return innerLevel;
        }
        if (angle >= outerAngle || outerAngle <= innerAngle)
        {
            return outerLevel;
        }
        return innerLevel + (outerLevel - innerLevel) *
            ((angle - innerAngle) / (outerAngle - innerAngle));
    }

    private static float CalculateDopplerPitch(
        ReadOnlySpan<byte> listener,
        ReadOnlySpan<byte> source,
        float x,
        float y,
        float z,
        float distance)
    {
        var soundSpeed = ReadSingle(listener, 0x4C);
        var dopplerFactor = ReadSingle(source, 0x44);
        if (soundSpeed <= 0 || dopplerFactor <= 0 || distance == 0)
        {
            return 1;
        }
        var nx = x / distance;
        var ny = y / distance;
        var nz = z / distance;
        var listenerVelocity = ReadSingle(listener, 0x40) * nx +
                               ReadSingle(listener, 0x44) * ny +
                               ReadSingle(listener, 0x48) * nz;
        var sourceVelocity = ReadSingle(source, 0x0C) * nx +
                             ReadSingle(source, 0x10) * ny +
                             ReadSingle(source, 0x14) * nz;
        var numerator = soundSpeed - dopplerFactor * listenerVelocity;
        var denominator = soundSpeed - dopplerFactor * sourceVelocity;
        return denominator <= 0 ? 4 : Math.Clamp(numerator / denominator, 0, 4);
    }

    private static int CreateSystem(CpuContext ctx, SystemOption option, ulong outHandleAddress, byte[]? bufferInfo)
    {
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryCreateHandle(ctx, 1, 0, out var handle) || !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var effectiveBufferInfo = bufferInfo ?? new byte[ContextBufferInfoSize];
        if (bufferInfo is null)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(effectiveBufferInfo, handle);
            BinaryPrimitives.WriteUInt64LittleEndian(effectiveBufferInfo.AsSpan(8), HandleStorageSize);
        }
        lock (StateGate)
        {
            Systems[handle] = new SystemState
            {
                Uid = unchecked((uint)Interlocked.Increment(ref _nextUid)),
                Name = option.Name,
                MaximumGrainSamples = option.MaximumGrainSamples,
                GrainSamples = option.GrainSamples,
                SampleRate = option.SampleRate,
                BufferInfo = effectiveBufferInfo,
            };
        }
        return ctx.SetReturn(0);
    }

    private static int CreateRack(
        CpuContext ctx,
        ulong systemHandle,
        uint rackId,
        RackOption option,
        ulong outHandleAddress,
        byte[]? bufferInfo)
    {
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryCreateHandle(ctx, 2, systemHandle, out var handle) || !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var effectiveBufferInfo = bufferInfo ?? new byte[ContextBufferInfoSize];
        if (bufferInfo is null)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(effectiveBufferInfo, handle);
            BinaryPrimitives.WriteUInt64LittleEndian(
                effectiveBufferInfo.AsSpan(8),
                HandleStorageSize + (ulong)option.MaximumVoices * 0x40);
        }
        lock (StateGate)
        {
            Racks[handle] = new RackState
            {
                SystemHandle = systemHandle,
                RackId = rackId,
                Uid = unchecked((uint)Interlocked.Increment(ref _nextUid)),
                Name = option.Name,
                MaximumGrainSamples = option.MaximumGrainSamples,
                MaximumVoices = option.MaximumVoices,
                MaximumMatrices = option.MaximumMatrices,
                MaximumPorts = option.MaximumPorts,
                BufferInfo = effectiveBufferInfo,
            };
        }
        return ctx.SetReturn(0);
    }

    private static bool TryReadSystemOption(CpuContext ctx, ulong address, out SystemOption option, out int error)
    {
        option = new SystemOption(string.Empty, DefaultMaximumGrainSamples, DefaultGrainSamples, DefaultSampleRate);
        error = 0;
        if (address == 0)
        {
            return true;
        }
        if (!ctx.TryReadUInt64(address, out var size))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        if (size != 0x40)
        {
            error = ErrorInvalidOptionSize;
            return false;
        }
        if (!ctx.TryReadUInt32(address + 0x1C, out var maximumGrainSamples) ||
            !ctx.TryReadUInt32(address + 0x20, out var grainSamples) ||
            !ctx.TryReadUInt32(address + 0x24, out var sampleRate))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        if (!IsValidGrainSamples(maximumGrainSamples))
        {
            error = ErrorInvalidMaxGrainSamples;
            return false;
        }
        if (!IsValidGrainSamples(grainSamples) || grainSamples > maximumGrainSamples)
        {
            error = ErrorInvalidNumGrainSamples;
            return false;
        }
        if (!IsValidSampleRate(sampleRate))
        {
            error = ErrorInvalidSampleRate;
            return false;
        }
        if (!TryReadName(ctx, address + 8, 16, out var name))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        option = new SystemOption(name, maximumGrainSamples, grainSamples, sampleRate);
        return true;
    }

    private static bool TryReadRackOption(
        CpuContext ctx,
        uint rackId,
        ulong address,
        out RackOption option,
        out int error)
    {
        option = DefaultRackOption(rackId);
        error = 0;
        if (option.MaximumVoices == 0)
        {
            error = ErrorInvalidRackId;
            return false;
        }
        if (address == 0)
        {
            return true;
        }
        if (!ctx.TryReadUInt64(address, out var size))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        var expected = ExpectedRackOptionSize(rackId);
        if (expected == 0 || size != expected)
        {
            error = expected == 0 ? ErrorInvalidRackId : ErrorInvalidOptionSize;
            return false;
        }
        if (!ctx.TryReadUInt32(address + 0x18, out var flags) ||
            !ctx.TryReadUInt32(address + 0x1C, out var maximumGrainSamples) ||
            !ctx.TryReadUInt32(address + 0x20, out var maximumVoices) ||
            !ctx.TryReadUInt32(address + 0x28, out var maximumMatrices) ||
            !ctx.TryReadUInt32(address + 0x2C, out var maximumPorts))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        if ((flags & 0x07FF_FFFF) != 0)
        {
            error = ErrorInvalidOptionFlag;
            return false;
        }
        if (!IsValidGrainSamples(maximumGrainSamples))
        {
            error = ErrorInvalidMaxGrainSamples;
            return false;
        }
        if (maximumVoices == 0 || maximumVoices > 4096)
        {
            error = ErrorInvalidMaxVoices;
            return false;
        }
        if (!TryReadName(ctx, address + 8, 16, out var name))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        option = new RackOption(name, maximumGrainSamples, maximumVoices, maximumMatrices, maximumPorts);
        return true;
    }

    private static RackOption DefaultRackOption(uint rackId) => rackId switch
    {
        0x1000 => new RackOption(string.Empty, 512, 256, 1, 8),
        0x2000 => new RackOption(string.Empty, 512, 1, 1, 8),
        0x2001 => new RackOption(string.Empty, 512, 1, 1, 8),
        0x3000 => new RackOption(string.Empty, 512, 1, 0, 0),
        0x4001 => new RackOption(string.Empty, 512, 256, 1, 8),
        0x4002 => new RackOption(string.Empty, 512, 1, 1, 8),
        0x4003 => new RackOption(string.Empty, 512, 1, 1, 8),
        _ => default,
    };

    private static ulong ExpectedRackOptionSize(uint rackId) => rackId switch
    {
        0x1000 => 0xA4,
        0x2000 => 0x94,
        0x2001 => 0x88,
        0x3000 => 0x88,
        0x4001 => 0x4E8,
        0x4002 => 0x4D8,
        0x4003 => 0x4D8,
        _ => 0,
    };

    private static bool IsValidGrainSamples(uint samples) =>
        samples is >= MinimumGrainSamples and <= 1024 && (samples & 0x3F) == 0;

    private static bool IsValidSampleRate(uint sampleRate) => sampleRate is
        11025 or 12000 or 22050 or 24000 or 44100 or 48000 or 88200 or 96000 or 176400 or 192000;

    private static int ApplyVoiceParameters(CpuContext ctx, RackState rack, VoiceState voice, ulong startAddress)
    {
        var address = startAddress;
        var visited = new HashSet<ulong>();
        for (var count = 0; count < 256; count++)
        {
            if (!visited.Add(address))
            {
                return ErrorCircularVoiceControl;
            }
            if (!ctx.TryReadUInt16(address, out var size) ||
                !ctx.TryReadUInt16(address + 2, out var nextRaw) ||
                !ctx.TryReadUInt32(address + 4, out var id))
            {
                return ErrorInvalidVoiceControlAddress;
            }
            if (size < 8)
            {
                return ErrorInvalidVoiceControlSize;
            }

            var rackId = id >> 16;
            var controlId = id & 0x7FFF;
            if (rackId == 0)
            {
                var requiredSize = controlId switch
                {
                    1 => 24,
                    2 or 3 or 4 => 16,
                    5 => 24,
                    6 => 12,
                    7 => 32,
                    _ => 0,
                };
                if (requiredSize == 0)
                {
                    return ErrorInvalidVoiceControlId;
                }
                if (size != requiredSize)
                {
                    return ErrorInvalidVoiceControlSize;
                }
                if (controlId == 6)
                {
                    if (!ctx.TryReadUInt32(address + 8, out var eventId))
                    {
                        return ErrorInvalidVoiceControlAddress;
                    }
                    voice.PendingEvent = eventId switch
                    {
                        1 => VoiceEvent.Play,
                        2 => VoiceEvent.Stop,
                        3 => VoiceEvent.StopImmediate,
                        4 => VoiceEvent.Kill,
                        5 => VoiceEvent.Pause,
                        _ => VoiceEvent.None,
                    };
                    if (voice.PendingEvent == VoiceEvent.None)
                    {
                        return ErrorInvalidEventType;
                    }
                }
            }
            else if (rackId == rack.RackId)
            {
                if (!IsSupportedRackControl(rack.RackId, controlId))
                {
                    return ErrorInvalidVoiceControlId;
                }
                if (controlId == 0 && size != ExpectedVoiceSetupSize(rack.RackId))
                {
                    return ErrorInvalidVoiceControlSize;
                }
                if (controlId == 0 && voice.Phase == VoicePhase.Empty)
                {
                    voice.Phase = VoicePhase.Setup;
                }
            }
            else
            {
                return ErrorInvalidVoiceControlId;
            }

            var next = unchecked((short)nextRaw);
            if (next == 0)
            {
                return 0;
            }
            address = unchecked((ulong)((long)address + next));
        }
        return ErrorCircularVoiceControl;
    }

    private static ushort ExpectedVoiceSetupSize(uint rackId) => rackId switch
    {
        0x1000 or 0x4001 => 0x28,
        0x2000 or 0x3000 or 0x4003 => 0x10,
        0x2001 or 0x4002 => 0x18,
        _ => 0,
    };

    private static bool IsSupportedRackControl(uint rackId, uint controlId) => rackId switch
    {
        0x1000 => controlId <= 0x0A,
        0x2000 => controlId <= 0x06,
        0x2001 => controlId <= 0x01,
        0x3000 => controlId <= 0x06,
        0x4001 => controlId <= 0x05,
        0x4002 => controlId == 0,
        0x4003 => controlId <= 0x01,
        _ => false,
    };

    private static void ApplyPendingEvent(VoiceState voice)
    {
        switch (voice.PendingEvent)
        {
            case VoiceEvent.Play when voice.Phase is VoicePhase.Empty or VoicePhase.Setup or VoicePhase.Stopped:
                voice.Phase = VoicePhase.Playing;
                break;
            case VoiceEvent.Stop when voice.Phase is VoicePhase.Playing or VoicePhase.Paused:
                voice.Phase = VoicePhase.Stopped;
                break;
            case VoiceEvent.StopImmediate:
                voice.Phase = VoicePhase.Stopped;
                break;
            case VoiceEvent.Kill:
                voice.Phase = VoicePhase.Empty;
                break;
            case VoiceEvent.Pause when voice.Phase == VoicePhase.Playing:
                voice.Phase = VoicePhase.Paused;
                break;
        }
        voice.PendingEvent = VoiceEvent.None;
    }

    private static uint StateFlags(VoicePhase phase) => phase switch
    {
        VoicePhase.Empty => 0,
        VoicePhase.Setup => 1,
        VoicePhase.Playing => 3,
        VoicePhase.Paused => 5,
        VoicePhase.Stopped => 0x9,
        _ => 0,
    };

    private static int ClearRenderOutputsAndReturn(CpuContext ctx, RenderOutput[] outputs, int error)
    {
        foreach (var output in outputs)
        {
            if (output.Address != 0 && output.Size != 0 &&
                !TryClearGuestBuffer(ctx, output.Address, output.RequiredSize == 0
                    ? output.Size
                    : Math.Min(output.Size, output.RequiredSize)))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }
        return ctx.SetReturn(error);
    }

    private static bool TryParseWave(CpuContext ctx, ulong baseAddress, ReadOnlySpan<byte> data, Span<byte> info)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }
        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        uint dataOffset = 0;
        uint dataSize = 0;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payloadOffset = offset + 8;
            if ((ulong)payloadOffset + chunkSize > (ulong)data.Length)
            {
                return false;
            }
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payloadOffset + 4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset + 12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payloadOffset + 14, 2));
            }
            else if (data.Slice(offset, 4).SequenceEqual("data"u8))
            {
                dataOffset = (uint)payloadOffset;
                dataSize = chunkSize;
            }
            offset = payloadOffset + (int)((chunkSize + 1) & ~1u);
        }
        if (formatTag is not (1 or 3) || channels == 0 || sampleRate == 0 || blockAlign == 0 || dataSize == 0)
        {
            return false;
        }

        var samples = dataSize / blockAlign;
        BinaryPrimitives.WriteUInt32LittleEndian(info, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(info[4..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(info[8..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(info[12..], bitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(info[24..], dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info[28..], dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[40..], samples);
        BinaryPrimitives.WriteUInt32LittleEndian(info[44..], blockAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(info[48..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[52..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[56..], blockAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(info[60..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[68..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[72..], dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info[76..], dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[88..], samples);
        BinaryPrimitives.WriteUInt64LittleEndian(info[96..], baseAddress + dataOffset);
        _ = ctx;
        return true;
    }

    private static int WriteContextBufferInfo(CpuContext ctx, ulong outAddress, ulong requiredSize)
    {
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        Span<byte> info = stackalloc byte[ContextBufferInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[8..], requiredSize);
        return ctx.Memory.TryWrite(outAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryValidateAllocator(CpuContext ctx, ulong address) =>
        address != 0 && ctx.TryReadUInt64(address, out var allocHandler) && allocHandler != 0 &&
        ctx.TryReadUInt64(address + 8, out _);

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }
        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], type);
        if (!ctx.Memory.TryWrite(handle, data))
        {
            return false;
        }
        KernelMemoryCompatExports.TryWriteDummyVtable(ctx, handle);
        return true;
    }

    private static bool TryReadName(CpuContext ctx, ulong address, int length, out string name)
    {
        var bytes = new byte[length];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            name = string.Empty;
            return false;
        }
        var terminator = Array.IndexOf(bytes, (byte)0);
        name = Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
        return true;
    }

    private static void WriteName(Span<byte> output, string name)
    {
        output.Clear();
        var bytes = Encoding.UTF8.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, output.Length - 1)).CopyTo(output);
    }

    private static bool TryWriteOptionalUInt32(CpuContext ctx, ulong address, uint value) =>
        address == 0 || ctx.TryWriteUInt32(address, value);

    private static bool TryClearGuestBuffer(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < length;)
        {
            var chunkSize = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunkSize]))
            {
                return false;
            }
            offset += (uint)chunkSize;
        }
        return true;
    }

    private static int ValidateSystem(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Systems.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidSystemHandle);
        }
    }

    private static int ValidateRack(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Racks.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidRackHandle);
        }
    }

    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
        }
    }

    private static uint RackType(uint rackId) => rackId switch
    {
        0x1000 => 1,
        0x2000 => 2,
        0x2001 => 3,
        0x3000 => 4,
        0x4001 => 5,
        0x4002 => 6,
        _ => 0,
    };

    private static bool ShouldTrace() =>
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"), "1", StringComparison.Ordinal);
}
