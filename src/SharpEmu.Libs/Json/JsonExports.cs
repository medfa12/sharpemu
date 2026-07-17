// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

// libSceJson initialization plus the parser read side. Parser::parse decodes the guest buffer with
// System.Text.Json and shadows the cloned root element host-side keyed by the guest Value address;
// the accessors below translate that element back through the sce::Json C++ ABI. A small mirror of
// the scalar payload is written into the guest Value object because getBoolean/getInteger/
// getUInteger/getReal return a pointer to the value storage inside the object (this + 0x10), which
// the guest then dereferences directly.
public static class JsonExports
{
    // sce::Json::Value occupies 0x20 bytes; the type tag sits at 0x18 (Gen4) / 0x1C (Gen5) and the
    // scalar payload union at 0x10, per the reference layout verified against ASTRO's title loader.
    private const int ValueObjectSize = 0x20;
    private const int ValuePayloadOffset = 0x10;
    private const int MaxKeyLength = 4096;
    private const ulong MaximumJsonBufferSize = 16 * 1024 * 1024;
    private const int SceJsonParserErrorInvalidToken = unchecked((int)0x80920101);
    private const int SceJsonParserErrorEmptyBuffer = unchecked((int)0x80920105);

    // sce::Json::String shadow. c_str() marshals the text into a guest-visible buffer and reuses it
    // while the capacity still fits, so repeated calls return a stable pointer.
    private sealed record JsonStringShadow(
        string Value,
        ulong GuestBufferAddress = 0,
        int GuestBufferCapacity = 0);

    // Identity key for a child Value handed out by operator[] / getValue / referValue. ASTRO calls
    // referValue repeatedly for the same member and relies on getting the same Value address back,
    // so child storage is allocated once per (parent, member) and only its content is refreshed.
    private readonly record struct JsonChildKey(ulong ValueAddress, ulong Index, string? Name);

    private static readonly ConcurrentDictionary<ulong, JsonElement> _parsedValues = new();
    private static readonly ConcurrentDictionary<ulong, JsonStringShadow> _strings = new();
    private static readonly ConcurrentDictionary<JsonChildKey, ulong> _childReferences = new();
    private static readonly JsonElement _nullElement = CreateNullElement();

    [SysAbiExport(
        Nid = "-hJRce8wn1U",
        ExportName = "_ZN3sce4Json12MemAllocatorC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("MemAllocator.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OcAgPxcq5Vk",
        ExportName = "_ZN3sce4Json12MemAllocatorD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorDestructor(CpuContext ctx)
    {
        TraceJson("MemAllocator.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cK6bYHf-Q5E",
        ExportName = "_ZN3sce4Json11InitializerC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("Initializer.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RujUxbr3haM",
        ExportName = "_ZN3sce4Json11InitializerD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerDestructor(CpuContext ctx)
    {
        TraceJson("Initializer.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Cxwy7wHq4J0",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerInitialize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        if (thisAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        TraceJson("Initializer.initialize", thisAddress, initParameterAddress);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // sce::Json::Initializer::setGlobalNullAccessCallback(const Value& (*)(ValueType, const Value*, void*), void*)
    // Registers the guest hook invoked when a Value is accessed as the wrong type. Quake calls it
    // during kexPSNWebAPI::Initialize and treats a non-zero return as a fatal init failure.
    [SysAbiExport(
        Nid = "+drDFyAS6u4",
        ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallback(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        JsonObjectHeap.GlobalNullAccessCallback = ctx[CpuRegister.Rsi];
        JsonObjectHeap.GlobalNullAccessCallbackContext = ctx[CpuRegister.Rdx];
        TraceJson("Initializer.setGlobalNullAccessCallback", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WSOuge5IsCg",
        ExportName = "_ZN3sce4Json14InitParameter2C1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.ctor", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I2QC8PYhJWY",
        ExportName = "_ZN3sce4Json14InitParameter212setAllocatorEPNS0_12MemAllocatorEPv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2SetAllocator(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.setAllocator", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Eu95jmqn5Rw",
        ExportName = "_ZN3sce4Json14InitParameter217setFileBufferSizeEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2SetFileBufferSize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.setFileBufferSize", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "IXW-z8pggfg",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int Initializer2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("Initializer2.ctor", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- sce::Json::Value base-object ctor/dtor (the C1/D1 variants live in JsonValueExports) ----

    [SysAbiExport(
        Nid = "-wa17B7TGnw",
        ExportName = "_ZN3sce4Json5ValueC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseConstructor(CpuContext ctx) => ConstructValue(ctx);

    [SysAbiExport(
        Nid = "0eUrW9JAxM0",
        ExportName = "_ZN3sce4Json5ValueD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseDestructor(CpuContext ctx) => DestroyValue(ctx);

    // ---- sce::Json::Parser ----

    [SysAbiExport(
        Nid = "S5JxQnoGF3E",
        ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ParserParseBuffer(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        if (valueAddress == 0 || bufferAddress == 0 || bufferSize == 0)
        {
            return ctx.SetReturn(SceJsonParserErrorEmptyBuffer);
        }

        if (bufferSize > MaximumJsonBufferSize)
        {
            return ctx.SetReturn(SceJsonParserErrorInvalidToken);
        }

        var buffer = new byte[(int)bufferSize];
        if (!ctx.Memory.TryRead(bufferAddress, buffer))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Titles routinely pass strlen()+1 style lengths; System.Text.Json rejects the trailing
        // NUL as an invalid token, so trim it before parsing.
        var text = buffer.AsMemory();
        while (text.Length > 0 && text.Span[^1] == 0)
        {
            text = text[..^1];
        }

        if (text.IsEmpty)
        {
            return ctx.SetReturn(SceJsonParserErrorEmptyBuffer);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            StoreValue(ctx, valueAddress, document.RootElement);
            TraceJsonText("Parser.parse", valueAddress, Encoding.UTF8.GetString(text.Span));
            return ctx.SetReturn(0);
        }
        catch (JsonException)
        {
            TraceJsonText("Parser.parse.invalid", valueAddress, Encoding.UTF8.GetString(text.Span));
            return ctx.SetReturn(SceJsonParserErrorInvalidToken);
        }
    }

    // ---- sce::Json::Value accessors ----

    [SysAbiExport(
        Nid = "SHtAad20YYM",
        ExportName = "_ZNK3sce4Json5Value7getTypeEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetType(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (ulong)GetValueType(GetParsedValue(ctx[CpuRegister.Rdi]));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RBw+4NukeGQ",
        ExportName = "_ZNK3sce4Json5Value5countEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueCount(CpuContext ctx)
    {
        var element = GetParsedValue(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Array => (ulong)element.GetArrayLength(),
            System.Text.Json.JsonValueKind.Object => (ulong)element.EnumerateObject().Count(),
            _ => 0,
        };
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // The scalar getters return a pointer to the value storage inside the guest Value object; the
    // caller dereferences it, so the mirror written by StoreValue must already hold the payload.

    [SysAbiExport(
        Nid = "zTwZdI8AZ5Y",
        ExportName = "_ZNK3sce4Json5Value10getBooleanEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetBoolean(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "DIxvoy7Ngvk",
        ExportName = "_ZNK3sce4Json5Value10getIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "sn4HNCtNRzY",
        ExportName = "_ZNK3sce4Json5Value11getUIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetUnsignedInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "3qrge7L-AU4",
        ExportName = "_ZNK3sce4Json5Value7getRealEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetReal(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "HwDt5lD9Bfo",
        ExportName = "_ZNK3sce4Json5ValueixEPKc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexCString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], MaxKeyLength, out var key) ||
            !TryGetOrAllocateChild(ctx, new JsonChildKey(valueAddress, 0, key), out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var parent = GetParsedValue(valueAddress);
        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
            parent.TryGetProperty(key, out var property)
            ? property
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        TraceJsonText("Value.index", valueAddress, key);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Unlike operator[], referValue reports a missing member as a null reference instead of
    // materializing a null child.
    [SysAbiExport(
        Nid = "wLsJlmgEIaI",
        ExportName = "_ZN3sce4Json5Value10referValueERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueReferValueByString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        if (!TryGetStringValue(ctx, ctx[CpuRegister.Rsi], out var key))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var parent = GetParsedValue(valueAddress);
        if (parent.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !parent.TryGetProperty(key, out var property) ||
            !TryGetOrAllocateChild(ctx, new JsonChildKey(valueAddress, 0, key), out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            TraceJsonText("Value.referValue.miss", valueAddress, key);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        StoreValue(ctx, childAddress, property);
        ctx[CpuRegister.Rax] = childAddress;
        TraceJsonText("Value.referValue", valueAddress, key);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "XlWbvieLj2M",
        ExportName = "_ZNK3sce4Json5ValueixEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "0YqYAoO-+Uo",
        ExportName = "_ZNK3sce4Json5Value8getValueEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "4zrm6VrgIAw",
        ExportName = "_ZN3sce4Json5ValueaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueAssignment(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        if (destinationAddress != 0)
        {
            StoreValue(ctx, destinationAddress, GetParsedValue(ctx[CpuRegister.Rsi]));
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- sce::Json::String base-object ctor/dtor and read side ----

    [SysAbiExport(
        Nid = "eG9E9M6XvTM",
        ExportName = "_ZN3sce4Json6StringC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseConstructor(CpuContext ctx) => ConstructString(ctx);

    [SysAbiExport(
        Nid = "Ui7YFnSTCBw",
        ExportName = "_ZN3sce4Json6StringD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseDestructor(CpuContext ctx) => DestroyString(ctx);

    [SysAbiExport(
        Nid = "Ncel8t2Rrpc",
        ExportName = "_ZNK3sce4Json5Value8toStringERNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueToString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var stringAddress = ctx[CpuRegister.Rsi];
        if (stringAddress != 0)
        {
            var element = GetParsedValue(valueAddress);
            var text = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
            _strings[stringAddress] = new JsonStringShadow(text);
            JsonObjectHeap.SetString(stringAddress, text);
            TraceJsonText("Value.toString", valueAddress, text);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "L1KAkYWml-M",
        ExportName = "_ZNK3sce4Json6String5c_strEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringCStr(CpuContext ctx)
    {
        var stringAddress = ctx[CpuRegister.Rdi];
        if (!_strings.TryGetValue(stringAddress, out var shadow))
        {
            shadow = new JsonStringShadow(JsonObjectHeap.GetStringOrEmpty(stringAddress));
        }

        var bytes = Encoding.UTF8.GetBytes(shadow.Value + '\0');
        var bufferAddress = shadow.GuestBufferAddress;
        if ((bufferAddress == 0 || shadow.GuestBufferCapacity < bytes.Length) &&
            !TryAllocateGuestObject(ctx, bytes.Length, out bufferAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!ctx.Memory.TryWrite(bufferAddress, bytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        _strings[stringAddress] = shadow with
        {
            GuestBufferAddress = bufferAddress,
            GuestBufferCapacity = Math.Max(shadow.GuestBufferCapacity, bytes.Length),
        };
        ctx.TryWriteUInt64(stringAddress, bufferAddress);
        ctx[CpuRegister.Rax] = bufferAddress;
        TraceJsonText("String.c_str", stringAddress, shadow.Value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- helpers ----

    private static int ConstructValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        StoreValue(ctx, thisAddress, _nullElement);
        TraceJson("Value.ctor2", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DestroyValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        RemoveValueShadow(thisAddress);
        TraceJson("Value.dtor", thisAddress, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ConstructString(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        _strings[thisAddress] = new JsonStringShadow(string.Empty);
        ctx.TryWriteUInt64(thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DestroyString(CpuContext ctx)
    {
        RemoveStringShadow(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnIndexedValue(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var position = ctx[CpuRegister.Rsi];
        if (!TryGetOrAllocateChild(ctx, new JsonChildKey(valueAddress, position, null), out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var parent = GetParsedValue(valueAddress);
        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Array &&
            position < (ulong)parent.GetArrayLength()
            ? parent[(int)position]
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnValueStorage(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = thisAddress == 0 ? 0 : thisAddress + ValuePayloadOffset;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static JsonElement GetParsedValue(ulong address) =>
        address != 0 && _parsedValues.TryGetValue(address, out var element)
            ? element
            : _nullElement;

    // Shadows the element host-side and mirrors the type tag plus the scalar payload into the
    // guest object so the pointer handed out by the scalar getters dereferences correctly.
    private static void StoreValue(CpuContext ctx, ulong address, JsonElement element)
    {
        if (address == 0)
        {
            return;
        }

        var clone = element.Clone();
        _parsedValues[address] = clone;

        Span<byte> mirror = stackalloc byte[ValueObjectSize];
        mirror.Clear();
        var typeOffset = ctx.TargetGeneration == Generation.Gen4 ? 0x18 : 0x1C;
        BinaryPrimitives.WriteInt32LittleEndian(mirror[typeOffset..], GetValueType(clone));
        switch (clone.ValueKind)
        {
            case System.Text.Json.JsonValueKind.True:
                mirror[ValuePayloadOffset] = 1;
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetInt64(out var integer):
                BinaryPrimitives.WriteInt64LittleEndian(mirror[ValuePayloadOffset..], integer);
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetUInt64(out var unsignedInteger):
                BinaryPrimitives.WriteUInt64LittleEndian(mirror[ValuePayloadOffset..], unsignedInteger);
                break;
            case System.Text.Json.JsonValueKind.Number:
                BinaryPrimitives.WriteInt64LittleEndian(
                    mirror[ValuePayloadOffset..],
                    BitConverter.DoubleToInt64Bits(clone.GetDouble()));
                break;
        }

        ctx.Memory.TryWrite(address, mirror);
    }

    // SceJson ValueType: 0 null, 1 boolean, 2 integer, 3 uinteger, 4 real, 5 string, 6 array,
    // 7 object.
    private static int GetValueType(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => 1,
        System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out _) => 2,
        System.Text.Json.JsonValueKind.Number when element.TryGetUInt64(out _) => 3,
        System.Text.Json.JsonValueKind.Number => 4,
        System.Text.Json.JsonValueKind.String => 5,
        System.Text.Json.JsonValueKind.Array => 6,
        System.Text.Json.JsonValueKind.Object => 7,
        _ => 0,
    };

    private static bool TryGetOrAllocateChild(CpuContext ctx, JsonChildKey key, out ulong address)
    {
        if (_childReferences.TryGetValue(key, out address))
        {
            return true;
        }

        if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var allocated))
        {
            address = 0;
            return false;
        }

        // The guest allocation arena cannot free; losing this race leaks one 0x20-byte block,
        // which is acceptable for the single-threaded guest JSON traffic observed so far.
        address = _childReferences.GetOrAdd(key, allocated);
        return true;
    }

    private static bool TryAllocateGuestObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        return size > 0 &&
            ctx.Memory is IGuestMemoryAllocator allocator &&
            allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address);
    }

    // referValue takes a sce::Json::String; resolve it from our read-side shadow first, then the
    // build-side JsonObjectHeap shadow, and finally by chasing the object's own c_str pointer.
    private static bool TryGetStringValue(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        if (_strings.TryGetValue(address, out var shadow))
        {
            value = shadow.Value;
            return true;
        }

        if (JsonObjectHeap.Strings.TryGetValue(address, out var heapText))
        {
            value = heapText;
            return true;
        }

        return ctx.TryReadUInt64(address, out var bufferAddress) &&
            ctx.TryReadNullTerminatedUtf8(bufferAddress, MaxKeyLength, out value);
    }

    // Destructor path shared with JsonValueExports: drops the element shadow plus every child
    // Value the destroyed parent handed out (their guest storage stays behind in the arena).
    internal static void RemoveValueShadow(ulong address)
    {
        _parsedValues.TryRemove(address, out _);
        foreach (var reference in _childReferences.Where(entry => entry.Key.ValueAddress == address).ToArray())
        {
            if (_childReferences.TryRemove(reference.Key, out var childAddress))
            {
                _parsedValues.TryRemove(childAddress, out _);
            }
        }
    }

    internal static void RemoveStringShadow(ulong address) => _strings.TryRemove(address, out _);

    internal static void ResetForTests()
    {
        _parsedValues.Clear();
        _strings.Clear();
        _childReferences.Clear();
    }

    private static void TraceJsonText(string operation, ulong thisAddress, string value)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var preview = value.Length <= 128 ? value : value[..128];
        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} value={preview}");
    }

    private static void TraceJson(string operation, ulong thisAddress, ulong argument)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} arg=0x{argument:X16}");
    }
}
