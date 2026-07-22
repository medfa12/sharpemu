// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

public static class JsonExports
{
    private const int ValueObjectSize = 0x20;
    private const int ValuePayloadOffset = 0x10;
    private const int Gen4ValueTypeOffset = 0x18;
    private const int Gen5ValueTypeOffset = 0x1C;
    private const int InitParameter2Size = 0x28;
    private const int MaximumStringLength = 0x10000;
    private const int MaximumJsonBufferSize = 16 * 1024 * 1024;
    private const int JsonErrorParseInvalidCharacter = unchecked((int)0x80848101);
    private const int JsonErrorInvalidArgument = unchecked((int)0x80848120);

    private enum HostValueKind : uint
    {
        Null,
        Boolean,
        Integer,
        UInteger,
        Real,
        String,
        Array,
        Object,
    }

    private sealed class HostValue
    {
        public HostValueKind Kind;
        public bool Boolean;
        public long Integer;
        public ulong UInteger;
        public double Real;
        public string String = string.Empty;
        public List<HostValue>? Array;
        public Dictionary<string, HostValue>? Object;

        public static HostValue Null() => new();

        public static HostValue FromBoolean(bool value) => new()
        {
            Kind = HostValueKind.Boolean,
            Boolean = value,
        };

        public static HostValue FromInteger(long value) => new()
        {
            Kind = HostValueKind.Integer,
            Integer = value,
        };

        public static HostValue FromUInteger(ulong value) => new()
        {
            Kind = HostValueKind.UInteger,
            UInteger = value,
        };

        public static HostValue FromReal(double value) => new()
        {
            Kind = HostValueKind.Real,
            Real = value,
        };

        public static HostValue FromString(string value) => new()
        {
            Kind = HostValueKind.String,
            String = value,
        };

        public static HostValue FromType(uint type) => type switch
        {
            (uint)HostValueKind.Boolean => FromBoolean(false),
            (uint)HostValueKind.Integer => FromInteger(0),
            (uint)HostValueKind.UInteger => FromUInteger(0),
            (uint)HostValueKind.Real => FromReal(0),
            (uint)HostValueKind.String => FromString(string.Empty),
            (uint)HostValueKind.Array => new HostValue
            {
                Kind = HostValueKind.Array,
                Array = [],
            },
            (uint)HostValueKind.Object => new HostValue
            {
                Kind = HostValueKind.Object,
                Object = new Dictionary<string, HostValue>(StringComparer.Ordinal),
            },
            0 => Null(),
            _ => new HostValue { Kind = (HostValueKind)type },
        };

        public HostValue Clone()
        {
            var copy = new HostValue
            {
                Kind = Kind,
                Boolean = Boolean,
                Integer = Integer,
                UInteger = UInteger,
                Real = Real,
                String = String,
            };
            if (Array is not null)
            {
                copy.Array = Array.Select(value => value.Clone()).ToList();
            }

            if (Object is not null)
            {
                copy.Object = Object.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal);
            }

            return copy;
        }

        public void ReplaceWith(HostValue value)
        {
            Kind = value.Kind;
            Boolean = value.Boolean;
            Integer = value.Integer;
            UInteger = value.UInteger;
            Real = value.Real;
            String = value.String;
            Array = value.Array;
            Object = value.Object;
        }
    }

    private sealed record JsonStringShadow(
        string Value,
        ulong GuestBufferAddress = 0,
        int GuestBufferCapacity = 0);

    private sealed class StaticStorage
    {
        public readonly object SyncRoot = new();
        public ulong NullValue;
        public ulong EmptyString;
        public ulong EmptyArray;
        public ulong EmptyObject;
        public ulong Zero;
    }

    private readonly record struct ChildKey(ulong ParentAddress, ulong Index, string? Name);
    private readonly record struct CompoundKey(ulong ValueAddress, HostValueKind Kind);

    private static readonly ConcurrentDictionary<ulong, HostValue> _values = new();
    private static readonly ConcurrentDictionary<ulong, JsonStringShadow> _strings = new();
    private static readonly ConcurrentDictionary<ulong, List<HostValue>> _arrays = new();
    private static readonly ConcurrentDictionary<ulong, Dictionary<string, HostValue>> _objects = new();
    private static readonly ConcurrentDictionary<ChildKey, ulong> _childReferences = new();
    private static readonly ConcurrentDictionary<CompoundKey, ulong> _compoundReferences = new();
    private static ConditionalWeakTable<ICpuMemory, StaticStorage> _staticStorage = new();

    [SysAbiExport(Nid = "-hJRce8wn1U", ExportName = "_ZN3sce4Json12MemAllocatorC2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int MemAllocatorConstructor(CpuContext ctx) => ReturnThis(ctx);

    [SysAbiExport(Nid = "OcAgPxcq5Vk", ExportName = "_ZN3sce4Json12MemAllocatorD2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int MemAllocatorDestructor(CpuContext ctx) => ReturnVoid(ctx);

    [SysAbiExport(Nid = "cK6bYHf-Q5E", ExportName = "_ZN3sce4Json11InitializerC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerConstructor(CpuContext ctx) => ReturnThis(ctx);

    [SysAbiExport(Nid = "RujUxbr3haM", ExportName = "_ZN3sce4Json11InitializerD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerDestructor(CpuContext ctx) => ReturnVoid(ctx);

    [SysAbiExport(Nid = "Cxwy7wHq4J0", ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerInitialize(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(JsonErrorInvalidArgument)
            : ctx.SetReturn(0);

    [SysAbiExport(Nid = "IXW-z8pggfg", ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson2")]
    public static int InitializerInitialize2(CpuContext ctx) => InitializerInitialize(ctx);

    [SysAbiExport(Nid = "PR5k1penBLM", ExportName = "_ZN3sce4Json11Initializer9terminateEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerTerminate(CpuContext ctx) => ReturnVoid(ctx);

    [SysAbiExport(Nid = "+drDFyAS6u4", ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallback(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        JsonObjectHeap.GlobalNullAccessCallback = ctx[CpuRegister.Rsi];
        JsonObjectHeap.GlobalNullAccessCallbackContext = ctx[CpuRegister.Rdx];
        return ctx.SetReturn(0);
    }

    // Title-captured alias NID for the same callback setter (not in ps5_names.txt).
    [SysAbiExport(Nid = "00oCq0RwSAY", ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallbackAlt(CpuContext ctx) =>
        InitializerSetGlobalNullAccessCallback(ctx);

    [SysAbiExport(Nid = "WSOuge5IsCg", ExportName = "_ZN3sce4Json14InitParameter2C1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson2")]
    public static int InitParameter2Constructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        Span<byte> parameter = stackalloc byte[InitParameter2Size];
        parameter.Clear();
        if (!ctx.Memory.TryWrite(address, parameter))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    [SysAbiExport(Nid = "I2QC8PYhJWY", ExportName = "_ZN3sce4Json14InitParameter212setAllocatorEPNS0_12MemAllocatorEPv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson2")]
    public static int InitParameter2SetAllocator(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0 ||
            !ctx.TryWriteUInt64(address, ctx[CpuRegister.Rsi]) ||
            !ctx.TryWriteUInt64(address + 8, ctx[CpuRegister.Rdx]))
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "Eu95jmqn5Rw", ExportName = "_ZN3sce4Json14InitParameter217setFileBufferSizeEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson2")]
    public static int InitParameter2SetFileBufferSize(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 ||
            !ctx.TryWriteUInt64(ctx[CpuRegister.Rdi] + 0x10, ctx[CpuRegister.Rsi]))
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "WVZBP4IyM+E", ExportName = "_ZN3sce4Json14InitParameter225setSpecialFloatFormatTypeENS0_22SpecialFloatFormatTypeE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson2")]
    public static int InitParameter2SetSpecialFloatFormatType(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 ||
            !ctx.TryWriteUInt32(ctx[CpuRegister.Rdi] + 0x18, unchecked((uint)ctx[CpuRegister.Rsi])))
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "-wa17B7TGnw", ExportName = "_ZN3sce4Json5ValueC2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueBaseConstructor(CpuContext ctx)
    {
        SetValue(ctx, ctx[CpuRegister.Rdi], HostValue.Null());
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "0eUrW9JAxM0", ExportName = "_ZN3sce4Json5ValueD2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueBaseDestructor(CpuContext ctx)
    {
        RemoveValueShadow(ctx[CpuRegister.Rdi]);
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "S5JxQnoGF3E", ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ParserParseBuffer(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        if (destination == 0 || bufferAddress == 0)
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        if (size > MaximumJsonBufferSize)
        {
            return ctx.SetReturn(JsonErrorParseInvalidCharacter);
        }

        var buffer = new byte[(int)size];
        if (!ctx.Memory.TryRead(bufferAddress, buffer))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ParseBytes(ctx, destination, buffer);
    }

    [SysAbiExport(Nid = "LB3jxppxyKU", ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ParserParseCString(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 ||
            !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], MaximumJsonBufferSize, out var text))
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        return ParseBytes(ctx, ctx[CpuRegister.Rdi], Encoding.UTF8.GetBytes(text));
    }

    [SysAbiExport(Nid = "itqj2YmuAa8", ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPFiRcPvES5_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ParserParseProvider(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var provider = ctx[CpuRegister.Rsi];
        var userData = ctx[CpuRegister.Rdx];
        var scheduler = GuestThreadExecution.Scheduler;
        if (destination == 0 || provider == 0 || scheduler is null ||
            !TryAllocateGuestObject(ctx, 1, out var characterAddress))
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        var bytes = new List<byte>(4096);
        while (bytes.Count < MaximumJsonBufferSize)
        {
            if (!ctx.Memory.TryWrite(characterAddress, [0]) ||
                !scheduler.TryCallGuestFunction(
                    ctx,
                    provider,
                    characterAddress,
                    userData,
                    0,
                    0,
                    0,
                    "json_input_provider",
                    out var callbackResult,
                    out _))
            {
                return ctx.SetReturn(JsonErrorInvalidArgument);
            }

            if (unchecked((int)callbackResult) != 0)
            {
                return ParseBytes(ctx, destination, bytes.ToArray());
            }

            if (!ctx.TryReadByte(characterAddress, out var character))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            bytes.Add(character);
        }

        return ctx.SetReturn(JsonErrorParseInvalidCharacter);
    }

    [SysAbiExport(Nid = "SHtAad20YYM", ExportName = "_ZNK3sce4Json5Value7getTypeEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetType(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (uint)GetValue(ctx[CpuRegister.Rdi]).Kind;
        return 0;
    }

    [SysAbiExport(Nid = "RBw+4NukeGQ", ExportName = "_ZNK3sce4Json5Value5countEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueCount(CpuContext ctx)
    {
        var value = GetValue(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = value.Kind switch
        {
            HostValueKind.Array => (ulong)(value.Array?.Count ?? 0),
            HostValueKind.Object => (ulong)(value.Object?.Count ?? 0),
            _ => 0,
        };
        return 0;
    }

    [SysAbiExport(Nid = "zTwZdI8AZ5Y", ExportName = "_ZNK3sce4Json5Value10getBooleanEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetBoolean(CpuContext ctx) => ReturnScalarStorage(ctx, HostValueKind.Boolean);

    [SysAbiExport(Nid = "DIxvoy7Ngvk", ExportName = "_ZNK3sce4Json5Value10getIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetInteger(CpuContext ctx) => ReturnScalarStorage(ctx, HostValueKind.Integer);

    [SysAbiExport(Nid = "sn4HNCtNRzY", ExportName = "_ZNK3sce4Json5Value11getUIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetUnsignedInteger(CpuContext ctx) => ReturnScalarStorage(ctx, HostValueKind.UInteger);

    [SysAbiExport(Nid = "3qrge7L-AU4", ExportName = "_ZNK3sce4Json5Value7getRealEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetReal(CpuContext ctx) => ReturnScalarStorage(ctx, HostValueKind.Real);

    [SysAbiExport(Nid = "epJ6x2LV0kU", ExportName = "_ZNK3sce4Json5Value9getStringEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetString(CpuContext ctx) => ReturnString(ctx, requireMatch: false);

    [SysAbiExport(Nid = "ONT8As5R1ug", ExportName = "_ZNK3sce4Json5Value8getArrayEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetArray(CpuContext ctx) => ReturnArray(ctx, requireMatch: false);

    [SysAbiExport(Nid = "IlsmvBtMkak", ExportName = "_ZNK3sce4Json5Value9getObjectEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetObject(CpuContext ctx) => ReturnObject(ctx, requireMatch: false);

    [SysAbiExport(Nid = "nM5XqdeXFPw", ExportName = "_ZN3sce4Json5Value10referArrayEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueReferArray(CpuContext ctx) => ReturnArray(ctx, requireMatch: true);

    [SysAbiExport(Nid = "-NxEk7XLkDY", ExportName = "_ZN3sce4Json5Value11referObjectEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueReferObject(CpuContext ctx) => ReturnObject(ctx, requireMatch: true);

    [SysAbiExport(Nid = "HwDt5lD9Bfo", ExportName = "_ZNK3sce4Json5ValueixEPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueIndexCString(CpuContext ctx)
    {
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], MaximumStringLength, out var key))
        {
            return ReturnStaticNull(ctx);
        }

        return ReturnObjectMember(ctx, ctx[CpuRegister.Rdi], key, create: false, nullOnMissing: false);
    }

    [SysAbiExport(Nid = "clF7J7N9xXE", ExportName = "_ZNK3sce4Json5ValueixERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueIndexString(CpuContext ctx) => ReturnValueMemberFromString(ctx, false, false);

    [SysAbiExport(Nid = "MsMOdxWfbwQ", ExportName = "_ZNK3sce4Json5Value8getValueERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetValueByString(CpuContext ctx) => ReturnValueMemberFromString(ctx, false, false);

    [SysAbiExport(Nid = "wLsJlmgEIaI", ExportName = "_ZN3sce4Json5Value10referValueERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueReferValueByString(CpuContext ctx) => ReturnValueMemberFromString(ctx, false, true);

    [SysAbiExport(Nid = "XlWbvieLj2M", ExportName = "_ZNK3sce4Json5ValueixEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueIndexPosition(CpuContext ctx) => ReturnArrayElement(ctx, false);

    [SysAbiExport(Nid = "0YqYAoO-+Uo", ExportName = "_ZNK3sce4Json5Value8getValueEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueGetPosition(CpuContext ctx) => ReturnArrayElement(ctx, false);

    [SysAbiExport(Nid = "gLzCc67aTbw", ExportName = "_ZN3sce4Json5Value10referValueEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueReferPosition(CpuContext ctx) => ReturnArrayElement(ctx, true);

    [SysAbiExport(Nid = "4zrm6VrgIAw", ExportName = "_ZN3sce4Json5ValueaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueAssignment(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        if (destination != 0)
        {
            SetValue(ctx, destination, GetValue(ctx[CpuRegister.Rsi]).Clone());
        }

        ctx[CpuRegister.Rax] = destination;
        return 0;
    }

    [SysAbiExport(Nid = "R7FDWtcN6f8", ExportName = "_ZN3sce4Json5Value9serializeERNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSerialize(CpuContext ctx)
    {
        var stringAddress = ctx[CpuRegister.Rsi];
        if (stringAddress == 0)
        {
            return ctx.SetReturn(JsonErrorInvalidArgument);
        }

        SetString(ctx, stringAddress, Serialize(GetValue(ctx[CpuRegister.Rdi])));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Ncel8t2Rrpc", ExportName = "_ZNK3sce4Json5Value8toStringERNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueToString(CpuContext ctx)
    {
        var value = GetValue(ctx[CpuRegister.Rdi]);
        var text = value.Kind == HostValueKind.String ? value.String : Serialize(value);
        SetString(ctx, ctx[CpuRegister.Rsi], text);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "eG9E9M6XvTM", ExportName = "_ZN3sce4Json6StringC2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringBaseConstructor(CpuContext ctx)
    {
        SetString(ctx, ctx[CpuRegister.Rdi], string.Empty);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "Ui7YFnSTCBw", ExportName = "_ZN3sce4Json6StringD2Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringBaseDestructor(CpuContext ctx)
    {
        RemoveStringShadow(ctx[CpuRegister.Rdi]);
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "cn9svYGWKDQ", ExportName = "_ZN3sce4Json6StringaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringAssignment(CpuContext ctx)
    {
        SetString(ctx, ctx[CpuRegister.Rdi], GetString(ctx, ctx[CpuRegister.Rsi]));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "L1KAkYWml-M", ExportName = "_ZNK3sce4Json6String5c_strEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringCStr(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var shadow = _strings.GetOrAdd(address, _ => new JsonStringShadow(string.Empty));
        var bytes = Encoding.UTF8.GetBytes(shadow.Value + '\0');
        var bufferAddress = shadow.GuestBufferAddress;
        if ((bufferAddress == 0 || shadow.GuestBufferCapacity < bytes.Length) &&
            !TryAllocateGuestObject(ctx, bytes.Length, out bufferAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (!ctx.Memory.TryWrite(bufferAddress, bytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _strings[address] = shadow with
        {
            GuestBufferAddress = bufferAddress,
            GuestBufferCapacity = Math.Max(shadow.GuestBufferCapacity, bytes.Length),
        };
        ctx.TryWriteUInt64(address, bufferAddress);
        ctx[CpuRegister.Rax] = bufferAddress;
        return 0;
    }

    [SysAbiExport(Nid = "EUH+EmT-v9E", ExportName = "_ZNK3sce4Json6String6lengthEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringLength(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (ulong)Encoding.UTF8.GetByteCount(GetString(ctx, ctx[CpuRegister.Rdi]));
        return 0;
    }

    [SysAbiExport(Nid = "JP-PtKMiI1E", ExportName = "_ZN3sce4Json5ArrayC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayConstructor(CpuContext ctx)
    {
        _arrays[ctx[CpuRegister.Rdi]] = [];
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "bI5AGFMydrA", ExportName = "_ZN3sce4Json5ArrayC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayCopyConstructor(CpuContext ctx)
    {
        _arrays[ctx[CpuRegister.Rdi]] = GetArray(ctx[CpuRegister.Rsi]).Select(value => value.Clone()).ToList();
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "HJ8GpRT1aiw", ExportName = "_ZN3sce4Json5ArrayD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayDestructor(CpuContext ctx)
    {
        _arrays.TryRemove(ctx[CpuRegister.Rdi], out _);
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "w5UbvPLGye0", ExportName = "_ZN3sce4Json5ArrayaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayAssignment(CpuContext ctx) => ArrayCopyConstructor(ctx);

    [SysAbiExport(Nid = "zQtLRTqceMY", ExportName = "_ZN3sce4Json5Array9push_backERKNS0_5ValueE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayPushBack(CpuContext ctx)
    {
        GetArray(ctx[CpuRegister.Rdi]).Add(GetValue(ctx[CpuRegister.Rsi]).Clone());
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "qVOSuDRHCpA", ExportName = "_ZN3sce4Json5Array5clearEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayClear(CpuContext ctx)
    {
        GetArray(ctx[CpuRegister.Rdi]).Clear();
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "rQGJeNjOuUk", ExportName = "_ZNK3sce4Json5Array4sizeEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArraySize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (ulong)GetArray(ctx[CpuRegister.Rdi]).Count;
        return 0;
    }

    [SysAbiExport(Nid = "bAM9Qwofus0", ExportName = "_ZNK3sce4Json5Array4backEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ArrayBack(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var array = GetArray(address);
        if (array.Count == 0)
        {
            return ReturnStaticNull(ctx);
        }

        return ReturnChild(ctx, new ChildKey(address, (ulong)(array.Count - 1), null), array[^1]);
    }

    [SysAbiExport(Nid = "OJPTonqdg0I", ExportName = "_ZN3sce4Json6ObjectC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectConstructor(CpuContext ctx)
    {
        _objects[ctx[CpuRegister.Rdi]] = new Dictionary<string, HostValue>(StringComparer.Ordinal);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "a+W7HHlwpBs", ExportName = "_ZN3sce4Json6ObjectC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectCopyConstructor(CpuContext ctx)
    {
        _objects[ctx[CpuRegister.Rdi]] = GetObject(ctx[CpuRegister.Rsi]).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "5JmzZt8twAo", ExportName = "_ZN3sce4Json6ObjectD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectDestructor(CpuContext ctx)
    {
        _objects.TryRemove(ctx[CpuRegister.Rdi], out _);
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "urOpESTBZmo", ExportName = "_ZN3sce4Json6ObjectaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectAssignment(CpuContext ctx) => ObjectCopyConstructor(ctx);

    [SysAbiExport(Nid = "ERuf9y0DY84", ExportName = "_ZN3sce4Json6ObjectixERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectIndex(CpuContext ctx)
    {
        if (!TryGetString(ctx, ctx[CpuRegister.Rsi], out var key))
        {
            return ReturnStaticNull(ctx);
        }

        var address = ctx[CpuRegister.Rdi];
        var values = GetObject(address);
        if (!values.TryGetValue(key, out var value))
        {
            value = HostValue.Null();
            values[key] = value;
        }

        return ReturnChild(ctx, new ChildKey(address, 0, key), value);
    }

    [SysAbiExport(Nid = "oH8aBmLU+fc", ExportName = "_ZN3sce4Json6Object5clearEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectClear(CpuContext ctx)
    {
        GetObject(ctx[CpuRegister.Rdi]).Clear();
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "fSGHm9RjN5U", ExportName = "_ZNK3sce4Json6Object4sizeEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ObjectSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (ulong)GetObject(ctx[CpuRegister.Rdi]).Count;
        return 0;
    }

    internal static void CopyValue(CpuContext ctx, ulong address, ulong sourceAddress) =>
        SetValue(ctx, address, GetValue(sourceAddress).Clone());

    internal static void SetArrayValue(CpuContext ctx, ulong address, ulong arrayAddress) =>
        SetValue(ctx, address, new HostValue
        {
            Kind = HostValueKind.Array,
            Array = GetArray(arrayAddress).Select(value => value.Clone()).ToList(),
        });

    internal static void SetObjectValue(CpuContext ctx, ulong address, ulong objectAddress) =>
        SetValue(ctx, address, new HostValue
        {
            Kind = HostValueKind.Object,
            Object = GetObject(objectAddress).ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
        });

    internal static void SetNull(CpuContext ctx, ulong address) => SetValue(ctx, address, HostValue.Null());

    internal static void SetBoolean(CpuContext ctx, ulong address, bool value) =>
        SetValue(ctx, address, HostValue.FromBoolean(value));

    internal static void SetInteger(CpuContext ctx, ulong address, long value) =>
        SetValue(ctx, address, HostValue.FromInteger(value));

    internal static void SetUInteger(CpuContext ctx, ulong address, ulong value) =>
        SetValue(ctx, address, HostValue.FromUInteger(value));

    internal static void SetReal(CpuContext ctx, ulong address, double value) =>
        SetValue(ctx, address, HostValue.FromReal(value));

    internal static void SetStringValue(CpuContext ctx, ulong address, string value) =>
        SetValue(ctx, address, HostValue.FromString(value));

    internal static void SetExplicitType(CpuContext ctx, ulong address, uint type) =>
        SetValue(ctx, address, HostValue.FromType(type));

    internal static string GetStringValue(CpuContext ctx, ulong address) => GetString(ctx, address);

    internal static void ConstructString(CpuContext ctx, ulong address, string value) => SetString(ctx, address, value);

    internal static void RemoveValueShadow(ulong address)
    {
        _values.TryRemove(address, out _);
        RemoveOwnedReferences(address);
    }

    private static void RemoveOwnedReferences(ulong address)
    {
        foreach (var reference in _childReferences.Where(pair => pair.Key.ParentAddress == address).ToArray())
        {
            if (_childReferences.TryRemove(reference.Key, out var childAddress))
            {
                _values.TryRemove(childAddress, out _);
            }
        }

        foreach (var reference in _compoundReferences.Where(pair => pair.Key.ValueAddress == address).ToArray())
        {
            if (!_compoundReferences.TryRemove(reference.Key, out var compoundAddress))
            {
                continue;
            }

            _strings.TryRemove(compoundAddress, out _);
            _arrays.TryRemove(compoundAddress, out _);
            _objects.TryRemove(compoundAddress, out _);
        }
    }

    internal static void RemoveStringShadow(ulong address) => _strings.TryRemove(address, out _);

    internal static void ResetForTests()
    {
        _values.Clear();
        _strings.Clear();
        _arrays.Clear();
        _objects.Clear();
        _childReferences.Clear();
        _compoundReferences.Clear();
        _staticStorage = new ConditionalWeakTable<ICpuMemory, StaticStorage>();
    }

    private static int ParseBytes(CpuContext ctx, ulong destination, byte[] buffer)
    {
        var length = buffer.Length;
        while (length > 0 && buffer[length - 1] == 0)
        {
            length--;
        }

        if (length == 0)
        {
            return ctx.SetReturn(JsonErrorParseInvalidCharacter);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            SetValue(ctx, destination, Materialize(document.RootElement));
            return ctx.SetReturn(0);
        }
        catch (JsonException)
        {
            return ctx.SetReturn(JsonErrorParseInvalidCharacter);
        }
    }

    private static HostValue Materialize(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null => HostValue.Null(),
        System.Text.Json.JsonValueKind.False => HostValue.FromBoolean(false),
        System.Text.Json.JsonValueKind.True => HostValue.FromBoolean(true),
        System.Text.Json.JsonValueKind.String => HostValue.FromString(element.GetString() ?? string.Empty),
        System.Text.Json.JsonValueKind.Array => new HostValue
        {
            Kind = HostValueKind.Array,
            Array = element.EnumerateArray().Select(Materialize).ToList(),
        },
        System.Text.Json.JsonValueKind.Object => new HostValue
        {
            Kind = HostValueKind.Object,
            Object = MaterializeObject(element),
        },
        System.Text.Json.JsonValueKind.Number when element.GetRawText().StartsWith("-", StringComparison.Ordinal) &&
            element.TryGetInt64(out var integer) => HostValue.FromInteger(integer),
        System.Text.Json.JsonValueKind.Number when element.TryGetUInt64(out var uinteger) =>
            HostValue.FromUInteger(uinteger),
        System.Text.Json.JsonValueKind.Number => HostValue.FromReal(element.GetDouble()),
        _ => HostValue.Null(),
    };

    private static Dictionary<string, HostValue> MaterializeObject(JsonElement element)
    {
        var values = new Dictionary<string, HostValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = Materialize(property.Value);
        }

        return values;
    }

    private static HostValue GetValue(ulong address) =>
        address != 0 && _values.TryGetValue(address, out var value) ? value : HostValue.Null();

    private static void SetValue(CpuContext ctx, ulong address, HostValue value)
    {
        if (address == 0)
        {
            return;
        }

        RemoveOwnedReferences(address);
        if (_values.TryGetValue(address, out var existing))
        {
            existing.ReplaceWith(value);
            MirrorValue(ctx, address, existing);
            return;
        }

        _values[address] = value;
        MirrorValue(ctx, address, value);
    }

    private static void BindValue(CpuContext ctx, ulong address, HostValue value)
    {
        _values[address] = value;
        MirrorValue(ctx, address, value);
    }

    private static void MirrorValue(CpuContext ctx, ulong address, HostValue value)
    {
        Span<byte> mirror = stackalloc byte[ValueObjectSize];
        mirror.Clear();
        var typeOffset = ctx.TargetGeneration == Generation.Gen4 ? Gen4ValueTypeOffset : Gen5ValueTypeOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(mirror[typeOffset..], (uint)value.Kind);
        switch (value.Kind)
        {
            case HostValueKind.Boolean:
                mirror[ValuePayloadOffset] = value.Boolean ? (byte)1 : (byte)0;
                break;
            case HostValueKind.Integer:
                BinaryPrimitives.WriteInt64LittleEndian(mirror[ValuePayloadOffset..], value.Integer);
                break;
            case HostValueKind.UInteger:
                BinaryPrimitives.WriteUInt64LittleEndian(mirror[ValuePayloadOffset..], value.UInteger);
                break;
            case HostValueKind.Real:
                BinaryPrimitives.WriteInt64LittleEndian(
                    mirror[ValuePayloadOffset..],
                    BitConverter.DoubleToInt64Bits(value.Real));
                break;
        }

        ctx.Memory.TryWrite(address, mirror);
    }

    private static int ReturnScalarStorage(CpuContext ctx, HostValueKind expectedKind)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address != 0 && GetValue(address).Kind == expectedKind)
        {
            ctx[CpuRegister.Rax] = address + ValuePayloadOffset;
            return 0;
        }

        ctx[CpuRegister.Rax] = GetStaticAddress(ctx, HostValueKind.Null, scalarZero: true);
        return 0;
    }

    private static int ReturnString(CpuContext ctx, bool requireMatch)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var value = GetValue(valueAddress);
        if (value.Kind != HostValueKind.String)
        {
            ctx[CpuRegister.Rax] = requireMatch
                ? 0
                : GetStaticAddress(ctx, HostValueKind.String, scalarZero: false);
            return 0;
        }

        if (!TryGetOrAllocateCompound(ctx, valueAddress, HostValueKind.String, out var address))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var old = _strings.GetOrAdd(address, _ => new JsonStringShadow(value.String));
        _strings[address] = old.Value == value.String ? old : new JsonStringShadow(value.String);
        ctx.TryWriteUInt64(valueAddress + ValuePayloadOffset, address);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    private static int ReturnArray(CpuContext ctx, bool requireMatch)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var value = GetValue(valueAddress);
        if (value.Kind != HostValueKind.Array || value.Array is null)
        {
            ctx[CpuRegister.Rax] = requireMatch
                ? 0
                : GetStaticAddress(ctx, HostValueKind.Array, scalarZero: false);
            return 0;
        }

        if (!TryGetOrAllocateCompound(ctx, valueAddress, HostValueKind.Array, out var address))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _arrays[address] = value.Array;
        ctx.TryWriteUInt64(valueAddress + ValuePayloadOffset, address);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    private static int ReturnObject(CpuContext ctx, bool requireMatch)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var value = GetValue(valueAddress);
        if (value.Kind != HostValueKind.Object || value.Object is null)
        {
            ctx[CpuRegister.Rax] = requireMatch
                ? 0
                : GetStaticAddress(ctx, HostValueKind.Object, scalarZero: false);
            return 0;
        }

        if (!TryGetOrAllocateCompound(ctx, valueAddress, HostValueKind.Object, out var address))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _objects[address] = value.Object;
        ctx.TryWriteUInt64(valueAddress + ValuePayloadOffset, address);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    private static int ReturnValueMemberFromString(CpuContext ctx, bool create, bool nullOnMissing)
    {
        if (!TryGetString(ctx, ctx[CpuRegister.Rsi], out var key))
        {
            return nullOnMissing ? ReturnNullPointer(ctx) : ReturnStaticNull(ctx);
        }

        return ReturnObjectMember(ctx, ctx[CpuRegister.Rdi], key, create, nullOnMissing);
    }

    private static int ReturnObjectMember(
        CpuContext ctx,
        ulong valueAddress,
        string key,
        bool create,
        bool nullOnMissing)
    {
        var value = GetValue(valueAddress);
        if (value.Kind != HostValueKind.Object || value.Object is null)
        {
            return nullOnMissing ? ReturnNullPointer(ctx) : ReturnStaticNull(ctx);
        }

        if (!value.Object.TryGetValue(key, out var child))
        {
            if (!create)
            {
                return nullOnMissing ? ReturnNullPointer(ctx) : ReturnStaticNull(ctx);
            }

            child = HostValue.Null();
            value.Object[key] = child;
        }

        return ReturnChild(ctx, new ChildKey(valueAddress, 0, key), child);
    }

    private static int ReturnArrayElement(CpuContext ctx, bool nullOnMissing)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var position = ctx[CpuRegister.Rsi];
        var value = GetValue(valueAddress);
        if (value.Kind != HostValueKind.Array || value.Array is null || position >= (ulong)value.Array.Count)
        {
            return nullOnMissing ? ReturnNullPointer(ctx) : ReturnStaticNull(ctx);
        }

        return ReturnChild(ctx, new ChildKey(valueAddress, position, null), value.Array[(int)position]);
    }

    private static int ReturnChild(CpuContext ctx, ChildKey key, HostValue value)
    {
        if (!_childReferences.TryGetValue(key, out var address))
        {
            if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var allocated))
            {
                ctx[CpuRegister.Rax] = 0;
                return 0;
            }

            address = _childReferences.GetOrAdd(key, allocated);
        }

        BindValue(ctx, address, value);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    private static int ReturnStaticNull(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = GetStaticAddress(ctx, HostValueKind.Null, scalarZero: false);
        return 0;
    }

    private static int ReturnNullPointer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static ulong GetStaticAddress(CpuContext ctx, HostValueKind kind, bool scalarZero)
    {
        var storage = _staticStorage.GetValue(ctx.Memory, _ => new StaticStorage());
        lock (storage.SyncRoot)
        {
            var address = scalarZero
                ? storage.Zero
                : kind switch
                {
                    HostValueKind.String => storage.EmptyString,
                    HostValueKind.Array => storage.EmptyArray,
                    HostValueKind.Object => storage.EmptyObject,
                    _ => storage.NullValue,
                };
            if (address != 0)
            {
                return address;
            }

            var size = scalarZero ? sizeof(ulong) : kind == HostValueKind.Null ? ValueObjectSize : sizeof(ulong);
            if (!TryAllocateGuestObject(ctx, size, out address))
            {
                return 0;
            }

            if (scalarZero)
            {
                storage.Zero = address;
            }
            else
            {
                switch (kind)
                {
                    case HostValueKind.String:
                        storage.EmptyString = address;
                        break;
                    case HostValueKind.Array:
                        storage.EmptyArray = address;
                        break;
                    case HostValueKind.Object:
                        storage.EmptyObject = address;
                        break;
                    default:
                        storage.NullValue = address;
                        break;
                }
            }

            if (scalarZero)
            {
                ctx.TryWriteUInt64(address, 0);
            }
            else if (kind == HostValueKind.Null)
            {
                BindValue(ctx, address, HostValue.Null());
            }
            else if (kind == HostValueKind.String)
            {
                SetString(ctx, address, string.Empty);
            }
            else if (kind == HostValueKind.Array)
            {
                _arrays[address] = [];
            }
            else
            {
                _objects[address] = new Dictionary<string, HostValue>(StringComparer.Ordinal);
            }

            return address;
        }
    }

    private static bool TryGetOrAllocateCompound(
        CpuContext ctx,
        ulong valueAddress,
        HostValueKind kind,
        out ulong address)
    {
        var key = new CompoundKey(valueAddress, kind);
        if (_compoundReferences.TryGetValue(key, out address))
        {
            return true;
        }

        if (!TryAllocateGuestObject(ctx, sizeof(ulong), out var allocated))
        {
            address = 0;
            return false;
        }

        address = _compoundReferences.GetOrAdd(key, allocated);
        return true;
    }

    private static List<HostValue> GetArray(ulong address) =>
        _arrays.GetOrAdd(address, _ => []);

    private static Dictionary<string, HostValue> GetObject(ulong address) =>
        _objects.GetOrAdd(address, _ => new Dictionary<string, HostValue>(StringComparer.Ordinal));

    private static void SetString(CpuContext ctx, ulong address, string value)
    {
        if (address == 0)
        {
            return;
        }

        _strings[address] = new JsonStringShadow(value);
        JsonObjectHeap.SetString(address, value);
        ctx.TryWriteUInt64(address, 0);
    }

    private static string GetString(CpuContext ctx, ulong address)
    {
        if (_strings.TryGetValue(address, out var shadow))
        {
            return shadow.Value;
        }

        if (JsonObjectHeap.Strings.TryGetValue(address, out var value))
        {
            return value;
        }

        return ctx.TryReadUInt64(address, out var bufferAddress) &&
            ctx.TryReadNullTerminatedUtf8(bufferAddress, MaximumStringLength, out value)
                ? value
                : string.Empty;
    }

    private static bool TryGetString(CpuContext ctx, ulong address, out string value)
    {
        value = GetString(ctx, address);
        return address != 0;
    }

    private static string Serialize(HostValue value) =>
        JsonSerializer.Serialize(ToSerializableObject(value));

    private static object? ToSerializableObject(HostValue value) => value.Kind switch
    {
        HostValueKind.Null => null,
        HostValueKind.Boolean => value.Boolean,
        HostValueKind.Integer => value.Integer,
        HostValueKind.UInteger => value.UInteger,
        HostValueKind.Real => value.Real,
        HostValueKind.String => value.String,
        HostValueKind.Array => value.Array?.Select(ToSerializableObject).ToArray() ?? [],
        HostValueKind.Object => value.Object?.ToDictionary(
            pair => pair.Key,
            pair => ToSerializableObject(pair.Value),
            StringComparer.Ordinal),
        _ => null,
    };

    private static bool TryAllocateGuestObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        return size > 0 &&
            ctx.Memory is IGuestMemoryAllocator allocator &&
            allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address);
    }

    private static int ReturnThis(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        return 0;
    }

    private static int ReturnVoid(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
