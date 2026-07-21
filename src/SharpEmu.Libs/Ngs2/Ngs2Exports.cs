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
    private const int ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int ErrorInvalidOutSize = unchecked((int)0x804A0054);
    private const int ErrorInvalidOptionAddress = unchecked((int)0x804A0080);
    private const int ErrorInvalidOptionSize = unchecked((int)0x804A0081);
    private const int ErrorInvalidMaxVoices = unchecked((int)0x804A0103);
    private const int ErrorInvalidHandle = unchecked((int)0x804A0200);
    private const int ErrorInvalidSampleRate = unchecked((int)0x804A0201);
    private const int ErrorInvalidBufferInfo = unchecked((int)0x804A0206);
    private const int ErrorInvalidBufferAllocator = unchecked((int)0x804A020A);
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
    private const int ErrorInvalidVoiceStateSize = unchecked((int)0x804A0A06);

    private const ulong HandleStorageSize = 0x1000;
    private const int RenderBufferInfoSize = 0x18;
    private const int ContextBufferInfoSize = 0x40;
    private const int SystemInfoSize = 0x88;
    private const int RackInfoSize = 0xA8;
    private const int WaveformFormatSize = 0x18;
    private const int WaveformBlockSize = 0x20;
    private const int WaveformInfoSize = 0xC8;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;
    private const uint DefaultMaximumGrainSamples = 512;
    private const uint DefaultGrainSamples = 256;
    private const uint DefaultSampleRate = 48000;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
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
        Resume,
    }

    private sealed class VoiceState
    {
        public ulong RackHandle;
        public uint VoiceIndex;
        public VoicePhase Phase;
        public VoiceEvent PendingEvent;
        public ulong DecodedSamples;
        public ulong DecodedBytes;
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

        var size = ctx.TargetGeneration == Generation.Gen5 ? 0x90 : 0x40;
        var option = new byte[size];
        BinaryPrimitives.WriteUInt64LittleEndian(option, (ulong)size);
        var commonOffset = ctx.TargetGeneration == Generation.Gen5 ? 0x68 : 0x18;
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(commonOffset + 4), DefaultMaximumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(commonOffset + 8), DefaultGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(commonOffset + 12), DefaultSampleRate);
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
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryValidateAllocator(ctx, allocatorAddress))
        {
            return ctx.SetReturn(ErrorInvalidBufferAllocator);
        }
        if (!TryReadSystemOption(ctx, optionAddress, out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }

        return CreateSystem(ctx, option, outHandleAddress);
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
        if (!TryReadSystemOption(ctx, ctx[CpuRegister.Rdi], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        if (!ctx.TryReadUInt64(bufferInfoAddress, out var hostBuffer) ||
            !ctx.TryReadUInt64(bufferInfoAddress + 8, out var hostBufferSize) ||
            hostBuffer == 0 || hostBufferSize < HandleStorageSize)
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }
        return CreateSystem(ctx, option, ctx[CpuRegister.Rdx]);
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
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(88), system.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(92), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(96), system.MaximumGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(100), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(104), rackCount);
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
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        if (!TryValidateAllocator(ctx, ctx[CpuRegister.Rcx]))
        {
            return ctx.SetReturn(ErrorInvalidBufferAllocator);
        }
        if (!TryReadRackOption(ctx, rackId, ctx[CpuRegister.Rdx], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        return CreateRack(ctx, systemHandle, rackId, option, ctx[CpuRegister.R8]);
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
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(ErrorInvalidSystemHandle);
            }
        }
        if (bufferInfoAddress == 0 ||
            !ctx.TryReadUInt64(bufferInfoAddress, out var hostBuffer) ||
            !ctx.TryReadUInt64(bufferInfoAddress + 8, out var hostBufferSize) ||
            hostBuffer == 0 || hostBufferSize == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }
        if (!TryReadRackOption(ctx, rackId, ctx[CpuRegister.Rdx], out var option, out var optionError))
        {
            return ctx.SetReturn(optionError);
        }
        return CreateRack(ctx, systemHandle, rackId, option, ctx[CpuRegister.R8]);
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
        if (stateSize < 4 || stateSize > 0x400)
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
        if (state.Length >= 48)
        {
            BinaryPrimitives.WriteSingleLittleEndian(state.AsSpan(4), voice.Phase == VoicePhase.Playing ? 1.0f : 0.0f);
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(16), voice.DecodedSamples);
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(24), voice.DecodedBytes);
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
        if (bufferInfoCount != 0 && bufferInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidBufferInfo);
        }

        ulong renderedBytes = 0;
        for (uint index = 0; index < bufferInfoCount; index++)
        {
            var entryAddress = bufferInfoAddress + index * RenderBufferInfoSize;
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            if (bufferSize > MaximumRenderBufferSize)
            {
                return ctx.SetReturn(ErrorInvalidOutSize);
            }
            if (bufferAddress != 0 && bufferSize != 0 && !TryClearGuestBuffer(ctx, bufferAddress, bufferSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            renderedBytes += bufferSize;
        }

        lock (StateGate)
        {
            system.RenderCount++;
            foreach (var pair in Racks.Where(pair => pair.Value.SystemHandle == systemHandle))
            {
                pair.Value.RenderCount++;
                foreach (var voice in Voices.Values.Where(value => value.RackHandle == pair.Key))
                {
                    ApplyPendingEvent(voice);
                    if (voice.Phase == VoicePhase.Playing)
                    {
                        voice.DecodedSamples += system.GrainSamples;
                        voice.DecodedBytes += renderedBytes;
                    }
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
            if (samples == 0 || samples > system.MaximumGrainSamples)
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
            if (sampleRate is < 8000 or > 192000)
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
        if (dataSize == 0 || dataSize > MaximumRenderBufferSize)
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

    private static int CreateSystem(CpuContext ctx, SystemOption option, ulong outHandleAddress)
    {
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryCreateHandle(ctx, 1, 0, out var handle) || !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
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
            };
        }
        return ctx.SetReturn(0);
    }

    private static int CreateRack(CpuContext ctx, ulong systemHandle, uint rackId, RackOption option, ulong outHandleAddress)
    {
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidOutAddress);
        }
        if (!TryCreateHandle(ctx, 2, systemHandle, out var handle) || !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
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
        var expectedSize = ctx.TargetGeneration == Generation.Gen5 ? 0x90UL : 0x40UL;
        if (size != expectedSize)
        {
            error = ErrorInvalidOptionSize;
            return false;
        }
        var commonOffset = ctx.TargetGeneration == Generation.Gen5 ? 0x68UL : 0x18UL;
        if (!ctx.TryReadUInt32(address + commonOffset + 4, out var maximumGrainSamples) ||
            !ctx.TryReadUInt32(address + commonOffset + 8, out var grainSamples) ||
            !ctx.TryReadUInt32(address + commonOffset + 12, out var sampleRate))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        if (maximumGrainSamples == 0)
        {
            error = ErrorInvalidMaxGrainSamples;
            return false;
        }
        if (grainSamples == 0 || grainSamples > maximumGrainSamples)
        {
            error = ErrorInvalidNumGrainSamples;
            return false;
        }
        if (sampleRate is < 8000 or > 192000)
        {
            error = ErrorInvalidSampleRate;
            return false;
        }
        var nameLength = ctx.TargetGeneration == Generation.Gen5 ? 64 : 16;
        if (!TryReadName(ctx, address + 8, nameLength, out var name))
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
        var expected = ExpectedRackOptionSize(ctx.TargetGeneration, rackId);
        if (expected == 0 || size != expected)
        {
            error = expected == 0 ? ErrorInvalidRackId : ErrorInvalidOptionSize;
            return false;
        }
        var fieldOffset = ctx.TargetGeneration == Generation.Gen5 ? 0x4CUL : 0x1CUL;
        if (!ctx.TryReadUInt32(address + fieldOffset, out var maximumGrainSamples) ||
            !ctx.TryReadUInt32(address + fieldOffset + 4, out var maximumVoices) ||
            !ctx.TryReadUInt32(address + fieldOffset + 12, out var maximumMatrices) ||
            !ctx.TryReadUInt32(address + fieldOffset + 16, out var maximumPorts))
        {
            error = ErrorInvalidOptionAddress;
            return false;
        }
        if (maximumGrainSamples == 0)
        {
            error = ErrorInvalidMaxGrainSamples;
            return false;
        }
        if (maximumVoices == 0 || maximumVoices > 4096)
        {
            error = ErrorInvalidMaxVoices;
            return false;
        }
        var nameLength = ctx.TargetGeneration == Generation.Gen5 ? 64 : 16;
        if (!TryReadName(ctx, address + 8, nameLength, out var name))
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
        _ => default,
    };

    private static ulong ExpectedRackOptionSize(Generation generation, uint rackId)
    {
        if (generation == Generation.Gen5)
        {
            return rackId switch
            {
                0x1000 => 0xD4,
                0x2000 => 0xC4,
                0x2001 => 0xB8,
                0x3000 => 0xB8,
                0x4001 => 0x518,
                0x4002 => 0x508,
                _ => 0,
            };
        }
        return rackId switch
        {
            0x1000 => 0xA4,
            0x2000 => 0x94,
            0x2001 => 0x88,
            0x3000 => 0x88,
            _ => 0,
        };
    }

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
                if (size < requiredSize)
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
                        0x0001 => VoiceEvent.Play,
                        0x0002 => VoiceEvent.Stop,
                        0x0004 => VoiceEvent.StopImmediate,
                        0x0008 => VoiceEvent.Kill,
                        0x0010 => VoiceEvent.Pause,
                        0x0020 => VoiceEvent.Resume,
                        _ => VoiceEvent.None,
                    };
                    if (voice.PendingEvent == VoiceEvent.None)
                    {
                        return ErrorInvalidEventType;
                    }
                }
            }
            else if (rackId == rack.RackId || rackId is 0x4000 or 0x4001 or 0x4002)
            {
                if (voice.Phase == VoicePhase.Empty)
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
            case VoiceEvent.Kill:
                voice.Phase = VoicePhase.Empty;
                voice.DecodedSamples = 0;
                voice.DecodedBytes = 0;
                break;
            case VoiceEvent.Pause when voice.Phase == VoicePhase.Playing:
                voice.Phase = VoicePhase.Paused;
                break;
            case VoiceEvent.Resume when voice.Phase == VoicePhase.Paused:
                voice.Phase = VoicePhase.Playing;
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
        VoicePhase.Stopped => 0xB,
        _ => 0,
    };

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
