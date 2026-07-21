// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

public static class JsonValueExports
{
    private const int MaximumStringLength = 0x10000;

    [SysAbiExport(Nid = "qBMjqyBn3OM", ExportName = "_ZN3sce4Json5ValueC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueDefaultConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        JsonExports.SetNull(ctx, address);
        JsonObjectHeap.SetValue(address, JsonValueState.Null);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "UeuWT+yNdCQ", ExportName = "_ZN3sce4Json5ValueC1Eb",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueBooleanConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = (ctx[CpuRegister.Rsi] & 0xFF) != 0;
        JsonExports.SetBoolean(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromBoolean(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "0lLK8+kDqmE", ExportName = "_ZN3sce4Json5ValueC1El",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueIntegerConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = unchecked((long)ctx[CpuRegister.Rsi]);
        JsonExports.SetInteger(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromInteger(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "x4AUdbhpRB0", ExportName = "_ZN3sce4Json5ValueC1Em",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueUnsignedConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ctx[CpuRegister.Rsi];
        JsonExports.SetUInteger(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromUnsignedInteger(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "sOmU4vnx3s0", ExportName = "_ZN3sce4Json5ValueC1Ed",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueRealConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ReadDoubleArgument(ctx);
        JsonExports.SetReal(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromReal(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "b9V6fmppLXY", ExportName = "_ZN3sce4Json5ValueC1EPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueCStringConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ReadCString(ctx, ctx[CpuRegister.Rsi]);
        JsonExports.SetStringValue(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromString(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "CbrT3dwDILo", ExportName = "_ZN3sce4Json5ValueC1ENS0_9ValueTypeE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueTypeConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var type = unchecked((uint)ctx[CpuRegister.Rsi]);
        JsonExports.SetExplicitType(ctx, address, type);
        JsonObjectHeap.SetValue(address, JsonValueState.FromExplicitType(type));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "sZIoMRGO+jk", ExportName = "_ZN3sce4Json5ValueC1ERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueStringConstructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = JsonExports.GetStringValue(ctx, ctx[CpuRegister.Rsi]);
        JsonExports.SetStringValue(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromString(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "iZeYfOxtMRg", ExportName = "_ZN3sce4Json5ValueC1ERKNS0_5ArrayE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueArrayConstructor(CpuContext ctx)
    {
        JsonExports.SetArrayValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "3xUXnmUkXfo", ExportName = "_ZN3sce4Json5ValueC1ERKNS0_6ObjectE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueObjectConstructor(CpuContext ctx)
    {
        JsonExports.SetObjectValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "fSb2oQTNrgA", ExportName = "_ZN3sce4Json5ValueC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueCopyConstructor(CpuContext ctx)
    {
        JsonExports.CopyValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "WTtYf+cNnXI", ExportName = "_ZN3sce4Json5ValueD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueDestructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        JsonExports.RemoveValueShadow(address);
        JsonObjectHeap.RemoveValue(address);
        return ReturnVoid(ctx);
    }

    [SysAbiExport(Nid = "5yHuiWXo2gg", ExportName = "_ZN3sce4Json5Value3setEb",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetBoolean(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = (ctx[CpuRegister.Rsi] & 0xFF) != 0;
        JsonExports.SetBoolean(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromBoolean(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "QxVVYhP-mvg", ExportName = "_ZN3sce4Json5Value3setEl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetInteger(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = unchecked((long)ctx[CpuRegister.Rsi]);
        JsonExports.SetInteger(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromInteger(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "SIe1ZmW7e7s", ExportName = "_ZN3sce4Json5Value3setEm",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetUnsigned(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ctx[CpuRegister.Rsi];
        JsonExports.SetUInteger(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromUnsignedInteger(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "BSmWDIkV4w4", ExportName = "_ZN3sce4Json5Value3setEd",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetReal(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ReadDoubleArgument(ctx);
        JsonExports.SetReal(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromReal(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "IKQimvG9Wqs", ExportName = "_ZN3sce4Json5Value3setENS0_9ValueTypeE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetType(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var type = unchecked((uint)ctx[CpuRegister.Rsi]);
        JsonExports.SetExplicitType(ctx, address, type);
        JsonObjectHeap.SetValue(address, JsonValueState.FromExplicitType(type));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "n6FC+l9DU70", ExportName = "_ZN3sce4Json5Value3setEPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetCString(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = ReadCString(ctx, ctx[CpuRegister.Rsi]);
        JsonExports.SetStringValue(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromString(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "6l3Bv2gysNc", ExportName = "_ZN3sce4Json5Value3setERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetString(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var value = JsonExports.GetStringValue(ctx, ctx[CpuRegister.Rsi]);
        JsonExports.SetStringValue(ctx, address, value);
        JsonObjectHeap.SetValue(address, JsonValueState.FromString(value));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "195ad-jAsTU", ExportName = "_ZN3sce4Json5Value3setERKNS0_5ArrayE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetArray(CpuContext ctx)
    {
        JsonExports.SetArrayValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "dFCphqnd+a4", ExportName = "_ZN3sce4Json5Value3setERKNS0_6ObjectE",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetObject(CpuContext ctx)
    {
        JsonExports.SetObjectValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "XL8+BUqjB1w", ExportName = "_ZN3sce4Json5Value3setERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueSetValue(CpuContext ctx)
    {
        JsonExports.CopyValue(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "FIjXN2TkuTs", ExportName = "_ZN3sce4Json5Value5clearEv",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int ValueClear(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        JsonExports.SetNull(ctx, address);
        JsonObjectHeap.SetValue(address, JsonValueState.Null);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "9KUZFjI1IxA", ExportName = "_ZN3sce4Json6StringC1EPKc",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringCStringConstructor(CpuContext ctx)
    {
        JsonExports.ConstructString(ctx, ctx[CpuRegister.Rdi], ReadCString(ctx, ctx[CpuRegister.Rsi]));
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "qSmqLXXCPas", ExportName = "_ZN3sce4Json6StringC1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringDefaultConstructor(CpuContext ctx)
    {
        JsonExports.ConstructString(ctx, ctx[CpuRegister.Rdi], string.Empty);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "0CAesfH963Q", ExportName = "_ZN3sce4Json6StringC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringCopyConstructor(CpuContext ctx)
    {
        var value = JsonExports.GetStringValue(ctx, ctx[CpuRegister.Rsi]);
        JsonExports.ConstructString(ctx, ctx[CpuRegister.Rdi], value);
        return ReturnThis(ctx);
    }

    [SysAbiExport(Nid = "cG1VE2HMl6c", ExportName = "_ZN3sce4Json6StringD1Ev",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceJson")]
    public static int StringDestructor(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        JsonExports.RemoveStringShadow(address);
        JsonObjectHeap.RemoveString(address);
        return ReturnVoid(ctx);
    }

    private static string ReadCString(CpuContext ctx, ulong address) =>
        ctx.TryReadNullTerminatedUtf8(address, MaximumStringLength, out var value)
            ? value
            : string.Empty;

    private static double ReadDoubleArgument(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
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
