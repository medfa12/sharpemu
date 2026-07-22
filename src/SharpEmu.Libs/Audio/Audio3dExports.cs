// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Audio;

public static class Audio3dExports
{
    private const int InvalidPort = unchecked((int)0x80EA0002);
    private const int InvalidObject = unchecked((int)0x80EA0003);
    private const int InvalidParameter = unchecked((int)0x80EA0004);
    private const int OutOfResources = unchecked((int)0x80EA0006);
    private const int NotReady = unchecked((int)0x80EA0007);
    private const int NotSupported = unchecked((int)0x80EA0008);
    private const uint InvalidId = uint.MaxValue;
    private const int MaximumPorts = 4;
    private static readonly object StateGate = new();
    private static readonly ConcurrentDictionary<uint, Port> Ports = new();
    private static readonly ConcurrentDictionary<int, uint> AudioOutPorts = new();
    private static readonly ConcurrentDictionary<uint, byte> SpeakerArrays = new();
    private static int _initialized;
    private static int _nextAudioOutHandle = 0x3000;
    private static uint _nextSpeakerArray;

    private sealed class Port
    {
        public required uint Id { get; init; }
        public required uint Granularity { get; init; }
        public required uint MaxObjects { get; init; }
        public required uint QueueDepth { get; init; }
        public required uint BufferMode { get; init; }
        public required uint NumBeds { get; init; }
        public readonly ConcurrentDictionary<uint, byte> Objects = new();
        public uint NextObject;
        public uint PendingBeds;
        public uint Queued;
        public ulong AdvanceCount;
        public ulong PushCount;
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);

    private static bool TryGetPort(CpuContext ctx, out Port port)
    {
        return Ports.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out port!);
    }

    private static int WriteDefaultParameters(CpuContext ctx, ulong address)
    {
        if (address == 0) return Ok(ctx);
        Span<byte> parameters = stackalloc byte[0x20];
        parameters.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[8..], 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[16..], 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[20..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[24..], 2);
        return ctx.Memory.TryWrite(address, parameters) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }

    private static int OpenPort(CpuContext ctx, ulong parameterAddress, ulong outputAddress)
    {
        if (parameterAddress == 0 || outputAddress == 0) return ctx.SetReturn(InvalidParameter);
        if (!ctx.TryWriteUInt32(outputAddress, InvalidId)) return ctx.SetReturn(InvalidParameter);
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(NotReady);
        if (!ctx.TryReadUInt64(parameterAddress, out var size) ||
            !ctx.TryReadUInt32(parameterAddress + 8, out var granularity) ||
            !ctx.TryReadUInt32(parameterAddress + 12, out var rate)) return ctx.SetReturn(InvalidParameter);

        var normalizedSize = size & ~7UL;
        if (normalizedSize is not (0x10 or 0x18 or 0x20 or 0x28) || rate != 0 || granularity < 0x100 || (granularity & 0xFF) != 0)
            return ctx.SetReturn(InvalidParameter);

        uint maxObjects = 0x200;
        uint queueDepth = 2;
        uint bufferMode = 0;
        uint numBeds = 2;
        if (normalizedSize >= 0x18 &&
            (!ctx.TryReadUInt32(parameterAddress + 16, out maxObjects) || !ctx.TryReadUInt32(parameterAddress + 20, out queueDepth)))
            return ctx.SetReturn(InvalidParameter);
        if (normalizedSize == 0x18) bufferMode = 1;
        if (normalizedSize >= 0x20 && !ctx.TryReadUInt32(parameterAddress + 24, out bufferMode)) return ctx.SetReturn(InvalidParameter);
        if (normalizedSize >= 0x28 && !ctx.TryReadUInt32(parameterAddress + 32, out numBeds)) return ctx.SetReturn(InvalidParameter);
        if (maxObjects == 0 || queueDepth == 0 || bufferMode > 2 || (numBeds & 0xFFFFFFFEu) != 2) return ctx.SetReturn(InvalidParameter);
        maxObjects = Math.Min(maxObjects, 0x200);

        lock (StateGate)
        {
            if (Ports.Count >= MaximumPorts) return ctx.SetReturn(OutOfResources);
            for (uint id = 0; id < MaximumPorts; id++)
            {
                var port = new Port { Id = id, Granularity = granularity, MaxObjects = maxObjects, QueueDepth = queueDepth, BufferMode = bufferMode, NumBeds = numBeds };
                if (Ports.TryAdd(id, port))
                {
                    return ctx.TryWriteUInt32(outputAddress, id) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
                }
            }
        }
        return ctx.SetReturn(OutOfResources);
    }

    [SysAbiExport(Nid = "pZlOm1aF3aA", ExportName = "sceAudio3dAudioOutClose", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dAudioOutClose(CpuContext ctx) => ctx.SetReturn(AudioOutPorts.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _) ? 0 : InvalidPort);
    [SysAbiExport(Nid = "ucEsi62soTo", ExportName = "sceAudio3dAudioOutOpen", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dAudioOutOpen(CpuContext ctx)
    {
        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(portId, out var port) || unchecked((uint)ctx[CpuRegister.R8]) != port.Granularity) return ctx.SetReturn(InvalidPort);
        var handle = Interlocked.Increment(ref _nextAudioOutHandle);
        AudioOutPorts[handle] = portId;
        return ctx.SetReturn(handle);
    }
    [SysAbiExport(Nid = "7NYEzJ9SJbM", ExportName = "sceAudio3dAudioOutOutput", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dAudioOutOutput(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0) return ctx.SetReturn(InvalidParameter);
        return AudioOutPorts.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var portId) && Ports.TryGetValue(portId, out var port)
            ? ctx.SetReturn(unchecked((int)(port.Granularity * 2)))
            : ctx.SetReturn(InvalidPort);
    }
    [SysAbiExport(Nid = "HbxYY27lK6E", ExportName = "sceAudio3dAudioOutOutputs", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dAudioOutOutputs(CpuContext ctx)
    {
        var entries = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (entries == 0 || count == 0) return ctx.SetReturn(InvalidParameter);
        for (uint i = 0; i < count; i++)
        {
            if (!ctx.TryReadUInt32(entries + i * 16, out var handle) || !ctx.TryReadUInt64(entries + i * 16 + 8, out var pointer)) return ctx.SetReturn(InvalidParameter);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = pointer;
            var result = sceAudio3dAudioOutOutput(ctx);
            if (result < 0) return result;
        }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "9tEwE0GV0qo", ExportName = "sceAudio3dBedWrite", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dBedWrite(CpuContext ctx) => BedWrite(ctx);
    [SysAbiExport(Nid = "xH4Q9UILL3o", ExportName = "sceAudio3dBedWrite2", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dBedWrite2(CpuContext ctx) => BedWrite(ctx);

    private static int BedWrite(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        var channels = unchecked((uint)ctx[CpuRegister.Rsi]);
        var format = unchecked((uint)ctx[CpuRegister.Rdx]);
        var buffer = ctx[CpuRegister.Rcx];
        var samples = unchecked((uint)ctx[CpuRegister.R8]);
        if (channels is not (2 or 6 or 8) || format > 1 || buffer == 0 || samples == 0 || (buffer & (format == 1 ? 3UL : 1UL)) != 0) return ctx.SetReturn(InvalidParameter);
        lock (port)
        {
            if (port.PendingBeds >= port.QueueDepth) return ctx.SetReturn(NotReady);
            port.PendingBeds++;
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "lvWMW6vEqFU", ExportName = "sceAudio3dCreateSpeakerArray", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dCreateSpeakerArray(CpuContext ctx)
    {
        var handle = unchecked(++_nextSpeakerArray);
        SpeakerArrays[handle] = 0;
        return ctx.SetReturn(unchecked((int)handle));
    }
    [SysAbiExport(Nid = "8hm6YdoQgwg", ExportName = "sceAudio3dDeleteSpeakerArray", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dDeleteSpeakerArray(CpuContext ctx) => ctx.SetReturn(SpeakerArrays.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _) ? 0 : InvalidParameter);
    [SysAbiExport(Nid = "Im+jOoa5WAI", ExportName = "sceAudio3dGetDefaultOpenParameters", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dGetDefaultOpenParameters(CpuContext ctx) => WriteDefaultParameters(ctx, ctx[CpuRegister.Rdi]);
    [SysAbiExport(Nid = "kEqqyDkmgdI", ExportName = "sceAudio3dGetSpeakerArrayMemorySize", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dGetSpeakerArrayMemorySize(CpuContext ctx) => ctx[CpuRegister.Rsi] == 0 || ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], 0x1000) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    [SysAbiExport(Nid = "-R1DukFq7Dk", ExportName = "sceAudio3dGetSpeakerArrayMixCoefficients", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dGetSpeakerArrayMixCoefficients(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdx], 0x20);
    [SysAbiExport(Nid = "-Re+pCWvwjQ", ExportName = "sceAudio3dGetSpeakerArrayMixCoefficients2", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dGetSpeakerArrayMixCoefficients2(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdx], 0x20);
    [SysAbiExport(Nid = "UmCvjSmuZIw", ExportName = "sceAudio3dInitialize", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dInitialize(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] != 0) return ctx.SetReturn(InvalidParameter);
        return Interlocked.CompareExchange(ref _initialized, 1, 0) == 0 ? Ok(ctx) : ctx.SetReturn(NotReady);
    }
    [SysAbiExport(Nid = "jO2tec4dJ2M", ExportName = "sceAudio3dObjectReserve", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dObjectReserve(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return ctx.SetReturn(InvalidParameter);
        if (!ctx.TryWriteUInt32(output, InvalidId)) return ctx.SetReturn(InvalidParameter);
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        lock (port)
        {
            if (port.Objects.Count >= port.MaxObjects) return ctx.SetReturn(OutOfResources);
            uint id;
            do id = ++port.NextObject; while (id == 0 || id == InvalidId || port.Objects.ContainsKey(id));
            port.Objects[id] = 0;
            return ctx.TryWriteUInt32(output, id) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
        }
    }
    [SysAbiExport(Nid = "4uyHN9q4ZeU", ExportName = "sceAudio3dObjectSetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dObjectSetAttributes(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        var objectId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!port.Objects.ContainsKey(objectId)) return ctx.SetReturn(InvalidObject);
        return ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] != 0 ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }
    [SysAbiExport(Nid = "1HXxo-+1qCw", ExportName = "sceAudio3dObjectUnreserve", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dObjectUnreserve(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        return ctx.SetReturn(port.Objects.TryRemove(unchecked((uint)ctx[CpuRegister.Rsi]), out _) ? 0 : InvalidObject);
    }
    [SysAbiExport(Nid = "lw0qrdSjZt8", ExportName = "sceAudio3dPortAdvance", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortAdvance(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        lock (port)
        {
            port.AdvanceCount++;
            if (port.Queued >= port.QueueDepth) return ctx.SetReturn(NotReady);
            port.Queued++;
            port.PendingBeds = 0;
        }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "OyVqOeVNtSk", ExportName = "sceAudio3dPortClose", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortClose(CpuContext ctx)
    {
        var id = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!Ports.TryRemove(id, out _)) return ctx.SetReturn(InvalidPort);
        foreach (var pair in AudioOutPorts.Where(pair => pair.Value == id)) AudioOutPorts.TryRemove(pair.Key, out _);
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "UHFOgVNz0kk", ExportName = "sceAudio3dPortCreate", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortCreate(CpuContext ctx)
    {
        var granularity = unchecked((uint)ctx[CpuRegister.Rdi]);
        var rate = unchecked((uint)ctx[CpuRegister.Rsi]);
        var output = ctx[CpuRegister.Rcx];
        if (ctx[CpuRegister.Rdx] != 0 || output == 0 || rate != 0 || granularity < 0x100 || (granularity & 0xFF) != 0)
            return ctx.SetReturn(InvalidParameter);
        if (!ctx.TryWriteUInt32(output, InvalidId)) return ctx.SetReturn(InvalidParameter);
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(NotReady);

        lock (StateGate)
        {
            if (Ports.Count >= MaximumPorts) return ctx.SetReturn(OutOfResources);
            for (uint id = 0; id < MaximumPorts; id++)
            {
                var port = new Port { Id = id, Granularity = granularity, MaxObjects = 0x200, QueueDepth = 2, BufferMode = 0, NumBeds = 2 };
                if (Ports.TryAdd(id, port)) return ctx.TryWriteUInt32(output, id) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
            }
        }
        return ctx.SetReturn(OutOfResources);
    }
    [SysAbiExport(Nid = "Mw9mRQtWepY", ExportName = "sceAudio3dPortDestroy", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortDestroy(CpuContext ctx) => sceAudio3dPortClose(ctx);
    [SysAbiExport(Nid = "ZOGrxWLgQzE", ExportName = "sceAudio3dPortFlush", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortFlush(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        lock (port) { port.PendingBeds = 0; port.Queued = 0; }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "uJ0VhGcxCTQ", ExportName = "sceAudio3dPortFreeState", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortFreeState(CpuContext ctx) => TryGetPort(ctx, out _) ? Ok(ctx) : ctx.SetReturn(InvalidPort);
    [SysAbiExport(Nid = "9ZA23Ia46Po", ExportName = "sceAudio3dPortGetAttributesSupported", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetAttributesSupported(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out _)) return ctx.SetReturn(InvalidPort);
        var capabilities = ctx[CpuRegister.Rsi];
        var countAddress = ctx[CpuRegister.Rdx];
        if (countAddress == 0 || !ctx.TryReadUInt32(countAddress, out var capacity)) return ctx.SetReturn(InvalidParameter);
        var count = capabilities == 0 ? 3u : Math.Min(capacity, 3u);
        if (capabilities != 0)
            for (uint i = 0; i < count; i++) if (!ctx.TryWriteUInt32(capabilities + i * 4, i == 0 ? 1u : i == 1 ? 3u : 9u)) return ctx.SetReturn(InvalidParameter);
        return ctx.TryWriteUInt32(countAddress, count) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }
    [SysAbiExport(Nid = "SEggctIeTcI", ExportName = "sceAudio3dPortGetList", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetList(CpuContext ctx)
    {
        var list = ctx[CpuRegister.Rdi];
        var countAddress = ctx[CpuRegister.Rsi];
        if (countAddress == 0) return ctx.SetReturn(InvalidParameter);
        var ids = Ports.Keys.Order().ToArray();
        if (list != 0 && ctx.TryReadUInt32(countAddress, out var capacity))
            for (var i = 0; i < Math.Min(ids.Length, capacity); i++) if (!ctx.TryWriteUInt32(list + (ulong)i * 4, ids[i])) return ctx.SetReturn(InvalidParameter);
        return ctx.TryWriteUInt32(countAddress, unchecked((uint)ids.Length)) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }
    [SysAbiExport(Nid = "flPcUaXVXcw", ExportName = "sceAudio3dPortGetParameters", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetParameters(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return ctx.SetReturn(InvalidParameter);
        Span<byte> parameters = stackalloc byte[0x28]; parameters.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, 0x28); BinaryPrimitives.WriteUInt32LittleEndian(parameters[8..], port.Granularity);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[16..], port.MaxObjects); BinaryPrimitives.WriteUInt32LittleEndian(parameters[20..], port.QueueDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[24..], port.BufferMode); BinaryPrimitives.WriteUInt32LittleEndian(parameters[32..], port.NumBeds);
        return ctx.Memory.TryWrite(output, parameters) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }
    [SysAbiExport(Nid = "YaaDbDwKpFM", ExportName = "sceAudio3dPortGetQueueLevel", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetQueueLevel(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        var level = ctx[CpuRegister.Rsi]; var available = ctx[CpuRegister.Rdx];
        if (level == 0 && available == 0) return ctx.SetReturn(InvalidParameter);
        lock (port)
        {
            if (level != 0 && !ctx.TryWriteUInt32(level, port.Queued)) return ctx.SetReturn(InvalidParameter);
            if (available != 0 && !ctx.TryWriteUInt32(available, port.QueueDepth - Math.Min(port.QueueDepth, port.Queued))) return ctx.SetReturn(InvalidParameter);
        }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "CKHlRW2E9dA", ExportName = "sceAudio3dPortGetState", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetState(CpuContext ctx) => WritePortCounters(ctx);
    [SysAbiExport(Nid = "iRX6GJs9tvE", ExportName = "sceAudio3dPortGetStatus", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortGetStatus(CpuContext ctx) => WritePortCounters(ctx);
    [SysAbiExport(Nid = "XeDDK0xJWQA", ExportName = "sceAudio3dPortOpen", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortOpen(CpuContext ctx)
    {
        if (unchecked((uint)ctx[CpuRegister.Rdi]) != 0xFF) return ctx.SetReturn(InvalidParameter);
        return OpenPort(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }
    [SysAbiExport(Nid = "VEVhZ9qd4ZY", ExportName = "sceAudio3dPortPush", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortPush(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        if (port.BufferMode != 2) return ctx.SetReturn(NotSupported);
        lock (port) { port.PushCount++; if (port.Queued > 0) port.Queued--; }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "-pzYDZozm+M", ExportName = "sceAudio3dPortQueryDebug", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortQueryDebug(CpuContext ctx) => WritePortCounters(ctx);
    [SysAbiExport(Nid = "Yq9bfUQ0uJg", ExportName = "sceAudio3dPortSetAttribute", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dPortSetAttribute(CpuContext ctx) => TryGetPort(ctx, out _) && ctx[CpuRegister.Rdx] != 0 ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    [SysAbiExport(Nid = "QfNXBrKZeI0", ExportName = "sceAudio3dReportRegisterHandler", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dReportRegisterHandler(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "psv2gbihC1A", ExportName = "sceAudio3dReportUnregisterHandler", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dReportUnregisterHandler(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "yEYXcbAGK14", ExportName = "sceAudio3dSetGpuRenderer", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dSetGpuRenderer(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Aacl5qkRU6U", ExportName = "sceAudio3dStrError", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dStrError(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "WW1TS2iz5yc", ExportName = "sceAudio3dTerminate", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int sceAudio3dTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return ctx.SetReturn(NotReady);
        Ports.Clear(); AudioOutPorts.Clear(); SpeakerArrays.Clear();
        return Ok(ctx);
    }

    private static int WritePortCounters(CpuContext ctx)
    {
        if (!TryGetPort(ctx, out var port)) return ctx.SetReturn(InvalidPort);
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return Ok(ctx);
        Span<byte> state = stackalloc byte[0x20]; state.Clear();
        lock (port)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(state, port.Queued);
            BinaryPrimitives.WriteUInt32LittleEndian(state[4..], unchecked((uint)port.Objects.Count));
            BinaryPrimitives.WriteUInt64LittleEndian(state[8..], port.AdvanceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(state[16..], port.PushCount);
        }
        return ctx.Memory.TryWrite(output, state) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);
    }

    private static int ClearOptional(CpuContext ctx, ulong address, int size) =>
        address == 0 || ctx.Memory.TryWrite(address, new byte[size]) ? Ok(ctx) : ctx.SetReturn(InvalidParameter);

    internal static void ResetForTests()
    {
        Ports.Clear(); AudioOutPorts.Clear(); SpeakerArrays.Clear();
        _initialized = 0; _nextAudioOutHandle = 0x3000; _nextSpeakerArray = 0;
    }
}
