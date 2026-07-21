// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private const int MaxGuestStringLength = 1024;
    private const int MaxPostedEvents = 256;
    private static readonly object StateGate = new();
    private static readonly Dictionary<int, bool> Contexts = [];
    private static readonly HashSet<int> Handles = [];
    private static readonly Dictionary<ulong, Dictionary<string, object?>> PropertyObjects = [];
    private static readonly Dictionary<ulong, List<object?>> PropertyArrays = [];
    private static readonly Dictionary<ulong, EventState> Events = [];
    private static readonly Queue<PostedEvent> PostedEvents = [];
    private static int _nextContext = 1;
    private static int _nextHandle = 1;
    private static long _nextObject = 1;
    private static ulong _poolSize;
    private static bool _initialized;
    private static ulong _lostEvents;

    internal sealed record PostedEvent(int Context, string Name, string Json, ulong Options);

    private sealed record EventState(string Name, ulong PropertyObject);

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0 ||
            !ctx.TryReadUInt64(parameterAddress, out var size) ||
            !ctx.TryReadUInt64(parameterAddress + 8, out var poolSize))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        if (size < 16 || poolSize == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            _poolSize = poolSize;
            _initialized = true;
            Trace($"initialize pool={_poolSize} active={_initialized}");
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "47UAEuQl+iI",
        ExportName = "sceNpUniversalDataSystemTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemTerminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            _initialized = false;
            Contexts.Clear();
            Handles.Clear();
            Events.Clear();
            PropertyObjects.Clear();
            PropertyArrays.Clear();
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0 || ctx[CpuRegister.Rcx] != 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var id = _nextContext++;
            Contexts[id] = false;
            if (!ctx.TryWriteInt32(outAddress, id, checkNil: true))
            {
                Contexts.Remove(id);
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "wB7IWzGp2v0",
        ExportName = "sceNpUniversalDataSystemDestroyContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyContext(CpuContext ctx)
    {
        lock (StateGate)
        {
            Contexts.Remove(unchecked((int)ctx[CpuRegister.Rdi]));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var id = _nextHandle++;
            Handles.Add(id);
            if (!ctx.TryWriteInt32(outAddress, id, checkNil: true))
            {
                Handles.Remove(id);
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx)
    {
        lock (StateGate)
        {
            Handles.Remove(unchecked((int)ctx[CpuRegister.Rdi]));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "jZCqWFgMehE",
        ExportName = "sceNpUniversalDataSystemAbortHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemAbortHandle(CpuContext ctx) => ctx.SetReturn(0, typeof(long));

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            if (!Contexts.ContainsKey(contextId) || !Handles.Contains(handleId))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            Contexts[contextId] = true;
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "s6W4Zl4Slgk",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyObject(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var id = NextObjectId();
            PropertyObjects[id] = [];
            if (!ctx.TryWriteUInt64(outAddress, id))
            {
                PropertyObjects.Remove(id);
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "kKUH0Viib3c",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyObject(CpuContext ctx)
    {
        lock (StateGate)
        {
            PropertyObjects.Remove(ctx[CpuRegister.Rdi]);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "Hm7qubT3b70",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyArray(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var id = NextObjectId();
            PropertyArrays[id] = [];
            if (!ctx.TryWriteUInt64(outAddress, id))
            {
                PropertyArrays.Remove(id);
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "W-0xwY0ZMjw",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyArray(CpuContext ctx)
    {
        lock (StateGate)
        {
            PropertyArrays.Remove(ctx[CpuRegister.Rdi]);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        if (!TryReadPropertyKey(ctx, out var properties, out var key) ||
            ctx[CpuRegister.Rdx] == 0 ||
            !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rdx], MaxGuestStringLength, out var value))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            properties![key!] = value;
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "YE4dbtbz6OE",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetInt32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetInt32(CpuContext ctx)
    {
        if (!TryReadPropertyKey(ctx, out var properties, out var key))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            properties![key!] = unchecked((int)ctx[CpuRegister.Rdx]);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx)
    {
        if (!TryReadPropertyKey(ctx, out var properties, out var key))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var arrayId = ctx[CpuRegister.Rdx];
            if (arrayId == 0)
            {
                arrayId = NextObjectId();
                PropertyArrays[arrayId] = [];
            }
            else if (!PropertyArrays.ContainsKey(arrayId))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            if (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], arrayId))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }

            properties![key!] = new PropertyReference(arrayId, IsArray: true);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        var eventNameAddress = ctx[CpuRegister.Rdi];
        var newEventAddress = ctx[CpuRegister.Rdx];
        if (eventNameAddress == 0 || newEventAddress == 0 ||
            !ctx.TryReadNullTerminatedUtf8(eventNameAddress, MaxGuestStringLength, out var eventName))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        lock (StateGate)
        {
            var propertyId = ctx[CpuRegister.Rsi];
            if (propertyId == 0)
            {
                propertyId = NextObjectId();
                PropertyObjects[propertyId] = [];
            }
            else if (!PropertyObjects.ContainsKey(propertyId))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            var eventId = NextObjectId();
            Events[eventId] = new EventState(eventName, propertyId);
            if (!ctx.TryWriteUInt64(newEventAddress, eventId) ||
                (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], propertyId)))
            {
                Events.Remove(eventId);
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx)
    {
        lock (StateGate)
        {
            Events.Remove(ctx[CpuRegister.Rdi]);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "+s14jq-KGYw",
        ExportName = "sceNpUniversalDataSystemEventEstimateSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventEstimateSize(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Events.TryGetValue(ctx[CpuRegister.Rdi], out var eventState) || ctx[CpuRegister.Rsi] == 0)
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            var size = checked((ulong)Encoding.UTF8.GetByteCount(SerializeEvent(eventState)) + 1);
            return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], size)
                ? ctx.SetReturn(0, typeof(long))
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }
    }

    [SysAbiExport(
        Nid = "vj6CQGWtEBg",
        ExportName = "sceNpUniversalDataSystemEventToString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventToString(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Events.TryGetValue(ctx[CpuRegister.Rdi], out var eventState))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            var jsonBytes = Encoding.UTF8.GetBytes(SerializeEvent(eventState));
            var requiredSize = checked((ulong)jsonBytes.Length + 1);
            if (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], requiredSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }

            var bufferAddress = ctx[CpuRegister.Rsi];
            var bufferSize = ctx[CpuRegister.Rdx];
            if (bufferAddress != 0 && bufferSize != 0)
            {
                var output = new byte[checked((int)Math.Min(bufferSize, (ulong)jsonBytes.Length + 1))];
                jsonBytes.AsSpan(0, Math.Min(jsonBytes.Length, output.Length - 1)).CopyTo(output);
                if (!ctx.Memory.TryWrite(bufferAddress, output))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
                }
            }

            return ctx.SetReturn(0, typeof(long));
        }
    }

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out var registered) || !registered ||
                !Handles.Contains(handleId) || !Events.TryGetValue(ctx[CpuRegister.Rdx], out var eventState))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
            }

            if (PostedEvents.Count == MaxPostedEvents)
            {
                PostedEvents.Dequeue();
                _lostEvents++;
            }

            var json = SerializeEvent(eventState);
            PostedEvents.Enqueue(new PostedEvent(contextId, eventState.Name, json, ctx[CpuRegister.Rcx]));
            Trace($"post name='{eventState.Name}' context={contextId} bytes={Encoding.UTF8.GetByteCount(json)}");
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "su7jW3VDDb4",
        ExportName = "sceNpUniversalDataSystemGetMemoryStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemGetMemoryStat(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> stat = stackalloc byte[24];
        stat.Clear();
        lock (StateGate)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(stat, _poolSize);
            BinaryPrimitives.WriteUInt64LittleEndian(stat[8..], EstimateCurrentMemory());
            BinaryPrimitives.WriteUInt64LittleEndian(stat[16..], EstimateCurrentMemory());
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], stat)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "KmN62tT4U8A",
        ExportName = "sceNpUniversalDataSystemGetStorageStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemGetStorageStat(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> stat = stackalloc byte[56];
        stat.Clear();
        lock (StateGate)
        {
            var used = EstimateCurrentMemory();
            BinaryPrimitives.WriteUInt64LittleEndian(stat, checked((ulong)PostedEvents.Count));
            BinaryPrimitives.WriteUInt64LittleEndian(stat[16..], _lostEvents);
            BinaryPrimitives.WriteUInt64LittleEndian(stat[24..], used);
            BinaryPrimitives.WriteUInt64LittleEndian(stat[32..], checked((ulong)PostedEvents.Count));
            BinaryPrimitives.WriteUInt64LittleEndian(stat[40..], used);
            BinaryPrimitives.WriteUInt64LittleEndian(stat[48..], _poolSize > used ? _poolSize - used : 0);
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], stat)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    internal static IReadOnlyList<PostedEvent> GetPostedEventsForTests()
    {
        lock (StateGate)
        {
            return PostedEvents.ToArray();
        }
    }

    private sealed record PropertyReference(ulong Id, bool IsArray);

    private static bool TryReadPropertyKey(
        CpuContext ctx,
        out Dictionary<string, object?>? properties,
        out string? key)
    {
        key = null;
        lock (StateGate)
        {
            PropertyObjects.TryGetValue(ctx[CpuRegister.Rdi], out properties);
        }

        return properties != null && ctx[CpuRegister.Rsi] != 0 &&
               ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], MaxGuestStringLength, out key);
    }

    private static ulong NextObjectId() => 0x1000_0000_0000_0000UL | checked((ulong)Interlocked.Increment(ref _nextObject));

    private static string SerializeEvent(EventState eventState)
    {
        PropertyObjects.TryGetValue(eventState.PropertyObject, out var properties);
        var resolved = properties?.ToDictionary(pair => pair.Key, pair => ResolveProperty(pair.Value)) ?? [];
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = eventState.Name,
            ["properties"] = resolved
        });
    }

    private static object? ResolveProperty(object? value)
    {
        if (value is not PropertyReference reference)
        {
            return value;
        }

        return reference.IsArray && PropertyArrays.TryGetValue(reference.Id, out var array)
            ? array.Select(ResolveProperty).ToArray()
            : null;
    }

    private static ulong EstimateCurrentMemory() => checked((ulong)(
        PropertyObjects.Count * 64 + PropertyArrays.Count * 32 + Events.Count * 64 + PostedEvents.Count * 128));

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] np.uds.{message}");
        }
    }
}
