// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Compression;

public static class JpegEncExports
{
    private const int ErrorInvalidAddress = unchecked((int)0x80650101);
    private const int ErrorInvalidSize = unchecked((int)0x80650102);
    private const int ErrorInvalidParameter = unchecked((int)0x80650103);
    private const int ErrorInvalidHandle = unchecked((int)0x80650104);
    private const int WorkMemorySize = 0x800;
    private static readonly ConcurrentDictionary<ulong, byte> Handles = new();

    [SysAbiExport(Nid = "K+rocojkr-I", ExportName = "sceJpegEncCreate", Target = Generation.Gen5, LibraryName = "libSceJpegEnc")]
    public static int Create(CpuContext ctx)
    {
        var validation = ValidateCreateParameter(ctx, ctx[CpuRegister.Rdi]);
        if (validation != 0) return ctx.SetReturn(validation);
        var memory = ctx[CpuRegister.Rsi];
        if (memory == 0 || ctx[CpuRegister.Rcx] == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (ctx[CpuRegister.Rdx] < WorkMemorySize) return ctx.SetReturn(ErrorInvalidSize);
        var handle = (memory + 31) & ~31UL;
        if (!ctx.TryWriteUInt64(handle, handle) || !ctx.TryWriteUInt32(handle + 8, sizeof(ulong))) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        Handles[handle] = 0;
        return ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], handle) ? ctx.SetReturn(0) : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "j1LyMdaM+C0", ExportName = "sceJpegEncDelete", Target = Generation.Gen5, LibraryName = "libSceJpegEnc")]
    public static int Delete(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!Handles.TryRemove(handle, out _)) return ctx.SetReturn(ErrorInvalidHandle);
        return ctx.TryWriteUInt64(handle, 0) ? ctx.SetReturn(0) : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "QbrU0cUghEM", ExportName = "sceJpegEncEncode", Target = Generation.Gen5, LibraryName = "libSceJpegEnc")]
    public static int Encode(CpuContext ctx)
    {
        if (!Handles.ContainsKey(ctx[CpuRegister.Rdi])) return ctx.SetReturn(ErrorInvalidHandle);
        var parameter = ctx[CpuRegister.Rsi];
        if (parameter == 0) return ctx.SetReturn(ErrorInvalidAddress);
        Span<byte> values = stackalloc byte[48];
        if (!ctx.Memory.TryRead(parameter, values)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var image = BinaryPrimitives.ReadUInt64LittleEndian(values);
        var jpeg = BinaryPrimitives.ReadUInt64LittleEndian(values[8..]);
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(values[16..]);
        var jpegSize = BinaryPrimitives.ReadUInt32LittleEndian(values[20..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(values[24..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(values[28..]);
        var pitch = BinaryPrimitives.ReadUInt32LittleEndian(values[32..]);
        if (image == 0 || jpeg == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (imageSize == 0 || jpegSize == 0) return ctx.SetReturn(ErrorInvalidSize);
        if (width > ushort.MaxValue || height > ushort.MaxValue || pitch == 0 || (long)pitch * height > imageSize) return ctx.SetReturn(ErrorInvalidParameter);
        var output = ctx[CpuRegister.Rdx];
        if (output != 0)
        {
            Span<byte> info = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(info, jpegSize);
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], height);
            if (!ctx.Memory.TryWrite(output, info)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "o6ZgXfFdWXQ", ExportName = "sceJpegEncQueryMemorySize", Target = Generation.Gen5, LibraryName = "libSceJpegEnc")]
    public static int QueryMemorySize(CpuContext ctx)
    {
        var validation = ValidateCreateParameter(ctx, ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(validation == 0 ? WorkMemorySize : validation);
    }

    internal static void ResetForTests() => Handles.Clear();

    private static int ValidateCreateParameter(CpuContext ctx, ulong address)
    {
        if (address == 0) return ErrorInvalidAddress;
        if (!ctx.TryReadUInt32(address, out var size) || !ctx.TryReadUInt32(address + 4, out var attribute)) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        if (size != 8) return ErrorInvalidSize;
        return attribute == 0 ? 0 : ErrorInvalidParameter;
    }
}
