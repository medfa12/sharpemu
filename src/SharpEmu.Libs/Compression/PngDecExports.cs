// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Compression;

public static class PngDecExports
{
    private const int ErrorInvalidAddress = unchecked((int)0x80690001);
    private const int ErrorInvalidSize = unchecked((int)0x80690002);
    private const int ErrorInvalidParameter = unchecked((int)0x80690003);
    private const int ErrorInvalidHandle = unchecked((int)0x80690004);
    private const int ErrorInvalidData = unchecked((int)0x80690010);
    private const int WorkMemorySize = 16;
    private static readonly ConcurrentDictionary<ulong, byte> Handles = new();

    [SysAbiExport(Nid = "m0uW+8pFyaw", ExportName = "scePngDecCreate", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int Create(CpuContext ctx)
    {
        var parameter = ctx[CpuRegister.Rdi];
        var memory = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var output = ctx[CpuRegister.Rcx];
        var validation = ValidateCreateParameter(ctx, parameter);
        if (validation != 0) return ctx.SetReturn(validation);
        if (memory == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (memorySize < WorkMemorySize) return ctx.SetReturn(ErrorInvalidSize);
        if (output == 0) return ctx.SetReturn(ErrorInvalidAddress);
        Handles[memory] = 0;
        return ctx.TryWriteUInt64(output, memory) ? ctx.SetReturn(0) : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "WC216DD3El4", ExportName = "scePngDecDecode", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int Decode(CpuContext ctx)
    {
        if (!Handles.ContainsKey(ctx[CpuRegister.Rdi])) return ctx.SetReturn(ErrorInvalidHandle);
        var parameter = ctx[CpuRegister.Rsi];
        if (parameter == 0) return ctx.SetReturn(ErrorInvalidParameter);
        Span<byte> values = stackalloc byte[32];
        if (!ctx.Memory.TryRead(parameter, values)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var pngAddress = BinaryPrimitives.ReadUInt64LittleEndian(values);
        var imageAddress = BinaryPrimitives.ReadUInt64LittleEndian(values[8..]);
        var pngSize = BinaryPrimitives.ReadUInt32LittleEndian(values[16..]);
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(values[20..]);
        var pixelFormat = BinaryPrimitives.ReadUInt16LittleEndian(values[24..]);
        var alpha = BinaryPrimitives.ReadUInt16LittleEndian(values[26..]);
        var pitch = BinaryPrimitives.ReadUInt32LittleEndian(values[28..]);
        if (pngAddress == 0 || imageAddress == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (pngSize == 0 || pngSize > 64 * 1024 * 1024) return ctx.SetReturn(ErrorInvalidSize);
        var png = new byte[(int)pngSize];
        if (!ctx.Memory.TryRead(pngAddress, png)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (!PngCodec.TryDecodeRgba(png, out var metadata, out var rgba)) return ctx.SetReturn(ErrorInvalidData);
        var rowBytes = checked((int)metadata.Width * 4);
        if (pitch > int.MaxValue) return ctx.SetReturn(ErrorInvalidSize);
        var stride = pitch == 0 ? rowBytes : (int)pitch;
        if (stride < rowBytes || (long)stride * metadata.Height > imageSize) return ctx.SetReturn(ErrorInvalidSize);
        var row = new byte[stride];
        for (var y = 0; y < (int)metadata.Height; y++)
        {
            rgba.AsSpan(y * rowBytes, rowBytes).CopyTo(row);
            if (metadata.ColorType is 0 or 2 or 3)
            {
                for (var x = 3; x < rowBytes; x += 4) row[x] = (byte)alpha;
            }
            if (pixelFormat == 1)
            {
                for (var x = 0; x < rowBytes; x += 4) (row[x], row[x + 2]) = (row[x + 2], row[x]);
            }
            if (!ctx.Memory.TryWrite(imageAddress + (ulong)(y * stride), row)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            row.AsSpan().Clear();
        }
        if (ctx[CpuRegister.Rdx] != 0 && !WriteImageInfo(ctx, ctx[CpuRegister.Rdx], metadata, false)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var packed = metadata.Width > 32767 || metadata.Height > 32767 ? 0 : unchecked((int)((metadata.Width << 16) | metadata.Height));
        return ctx.SetReturn(packed);
    }

    [SysAbiExport(Nid = "cJ--1xAbj-I", ExportName = "scePngDecDecodeWithInputControl", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int DecodeWithInputControl(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "QbD+eENEwo8", ExportName = "scePngDecDelete", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int Delete(CpuContext ctx) => ctx.SetReturn(Handles.TryRemove(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorInvalidHandle);

    [SysAbiExport(Nid = "U6h4e5JRPaQ", ExportName = "scePngDecParseHeader", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int ParseHeader(CpuContext ctx)
    {
        var parameter = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        if (parameter == 0) return ctx.SetReturn(ErrorInvalidParameter);
        if (output == 0) return ctx.SetReturn(ErrorInvalidAddress);
        Span<byte> values = stackalloc byte[16];
        if (!ctx.Memory.TryRead(parameter, values)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var pngAddress = BinaryPrimitives.ReadUInt64LittleEndian(values);
        var pngSize = BinaryPrimitives.ReadUInt32LittleEndian(values[8..]);
        if (pngAddress == 0) return ctx.SetReturn(ErrorInvalidAddress);
        if (pngSize < 33 || pngSize > 64 * 1024 * 1024) return ctx.SetReturn(ErrorInvalidSize);
        var png = new byte[(int)pngSize];
        if (!ctx.Memory.TryRead(pngAddress, png)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (!PngCodec.TryReadMetadata(png, out var metadata)) return ctx.SetReturn(ErrorInvalidData);
        return ctx.SetReturn(WriteImageInfo(ctx, output, metadata, false) ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "-6srIGbLTIU", ExportName = "scePngDecQueryMemorySize", Target = Generation.Gen5, LibraryName = "libScePngDec")]
    public static int QueryMemorySize(CpuContext ctx)
    {
        var validation = ValidateCreateParameter(ctx, ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(validation == 0 ? WorkMemorySize : validation);
    }

    internal static void ResetForTests() => Handles.Clear();

    private static int ValidateCreateParameter(CpuContext ctx, ulong address)
    {
        if (address == 0) return ErrorInvalidParameter;
        if (!ctx.TryReadUInt32(address + 4, out var attribute) || !ctx.TryReadUInt32(address + 8, out var maxWidth)) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        if (attribute > 1) return ErrorInvalidAddress;
        return maxWidth == 0 || maxWidth > 1_000_000 ? ErrorInvalidSize : 0;
    }

    private static bool WriteImageInfo(CpuContext ctx, ulong address, PngCodec.Metadata metadata, bool transparency)
    {
        Span<byte> info = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(info, metadata.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(info[4..], metadata.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(info[8..], metadata.SceColorSpace);
        BinaryPrimitives.WriteUInt16LittleEndian(info[10..], metadata.BitDepth);
        var flags = (metadata.Interlace == 1 ? 1u : 0u) | (transparency ? 2u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(info[12..], flags);
        return ctx.Memory.TryWrite(address, info);
    }
}
