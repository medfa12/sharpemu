// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Compression;

public static class PngEncExports
{
    private const int ErrorInvalidAddress = unchecked((int)0x80690101);
    private const int ErrorInvalidSize = unchecked((int)0x80690102);
    private const int ErrorInvalidParameter = unchecked((int)0x80690103);
    private const int ErrorInvalidHandle = unchecked((int)0x80690104);
    private const int ErrorDataOverflow = unchecked((int)0x80690110);
    private const int WorkMemorySize = 16;
    private static readonly ConcurrentDictionary<ulong, byte> Handles = new();

    [SysAbiExport(Nid = "7aGTPfrqT9s", ExportName = "scePngEncCreate", Target = Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Create(CpuContext ctx)
    {
        var validation = ValidateCreateParameter(ctx, ctx[CpuRegister.Rdi]);
        if (validation != 0) return ctx.SetReturn(validation);
        var memory = ctx[CpuRegister.Rsi];
        if (memory == 0 || ctx[CpuRegister.Rcx] == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (ctx[CpuRegister.Rdx] < WorkMemorySize) return ctx.SetReturn(ErrorInvalidSize);
        Handles[memory] = 0;
        return ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], memory) ? ctx.SetReturn(0) : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "RUrWdwTWZy8", ExportName = "scePngEncDelete", Target = Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Delete(CpuContext ctx) => ctx.SetReturn(Handles.TryRemove(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorInvalidHandle);

    [SysAbiExport(Nid = "xgDjJKpcyHo", ExportName = "scePngEncEncode", Target = Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Encode(CpuContext ctx)
    {
        if (!Handles.ContainsKey(ctx[CpuRegister.Rdi])) return ctx.SetReturn(ErrorInvalidHandle);
        var parameter = ctx[CpuRegister.Rsi];
        if (parameter == 0) return ctx.SetReturn(ErrorInvalidParameter);
        Span<byte> values = stackalloc byte[48];
        if (!ctx.Memory.TryRead(parameter, values)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var imageAddress = BinaryPrimitives.ReadUInt64LittleEndian(values);
        var pngAddress = BinaryPrimitives.ReadUInt64LittleEndian(values[8..]);
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(values[16..]);
        var pngSize = BinaryPrimitives.ReadUInt32LittleEndian(values[20..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(values[24..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(values[28..]);
        var pitch = BinaryPrimitives.ReadUInt32LittleEndian(values[32..]);
        var pixelFormat = BinaryPrimitives.ReadUInt16LittleEndian(values[36..]);
        var colorSpace = BinaryPrimitives.ReadUInt16LittleEndian(values[38..]);
        var bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(values[40..]);
        var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(values[46..]);
        if (imageAddress == 0 || pngAddress == 0) return ctx.SetReturn(ErrorInvalidAddress);
        var minimumPitch = (long)width * 4;
        if (width == 0 || height == 0 || bitDepth != 8 || minimumPitch > uint.MaxValue || pitch < minimumPitch ||
            (long)pitch * height > imageSize || imageSize > 64 * 1024 * 1024)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }
        if (pixelFormat > 1 || (colorSpace != 3 && colorSpace != 19)) return ctx.SetReturn(ErrorInvalidParameter);
        var pixels = new byte[imageSize];
        if (!ctx.Memory.TryRead(imageAddress, pixels)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        byte[] png;
        try
        {
            png = PngCodec.Encode(pixels, width, height, pitch, colorSpace == 19, pixelFormat == 1, Math.Min(compressionLevel, (ushort)9));
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }
        if (png.Length > pngSize) return ctx.SetReturn(ErrorDataOverflow);
        if (!ctx.Memory.TryWrite(pngAddress, png)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var output = ctx[CpuRegister.Rdx];
        if (output != 0)
        {
            Span<byte> info = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], height);
            if (!ctx.Memory.TryWrite(output, info)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(png.Length);
    }

    [SysAbiExport(Nid = "9030RnBDoh4", ExportName = "scePngEncQueryMemorySize", Target = Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int QueryMemorySize(CpuContext ctx)
    {
        var validation = ValidateCreateParameter(ctx, ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(validation == 0 ? WorkMemorySize : validation);
    }

    internal static void ResetForTests() => Handles.Clear();

    private static int ValidateCreateParameter(CpuContext ctx, ulong address)
    {
        if (address == 0) return ErrorInvalidAddress;
        if (!ctx.TryReadUInt32(address + 4, out var attribute) || !ctx.TryReadUInt32(address + 8, out var maxWidth) || !ctx.TryReadUInt32(address + 12, out var maxFilters)) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        if (attribute != 0 || maxFilters > 5) return ErrorInvalidParameter;
        return maxWidth == 0 || maxWidth > 1_000_000 ? ErrorInvalidSize : 0;
    }
}
