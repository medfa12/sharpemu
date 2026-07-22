// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Videodec;

public static class VideodecExports
{
    private const int ErrorStructSize = unchecked((int)0x80C10002);
    private const int ErrorHandle = unchecked((int)0x80C10003);
    private const int ErrorArgumentPointer = unchecked((int)0x80C1000F);
    private const int ConfigInfoSize = 0x28;
    private const int ResourceInfoSize = 0x38;
    private const int ControlSize = 0x18;
    private const int InputDataSize = 0x30;
    private const int FrameBufferSize = 0x18;
    private const int PictureInfoSize = 0x70;
    private const ulong FallbackMemorySize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, DecoderState> Decoders = new();
    private static long _nextHandle = 0x10000;

    private sealed class DecoderState
    {
        public uint CodecType { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
    }

    [SysAbiExport(
        Nid = "qkgRiwHyheU",
        ExportName = "sceVideodecCreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecCreateDecoder(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var resourceAddress = ctx[CpuRegister.Rsi];
        var controlAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || resourceAddress == 0 || controlAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(configAddress, out var configSize) ||
            !ctx.TryReadUInt64(resourceAddress, out var resourceSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (configSize != ConfigInfoSize || resourceSize != ResourceInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt32(configAddress + 0x08, out var codecType) ||
            !ctx.TryReadUInt32(configAddress + 0x14, out var width) ||
            !ctx.TryReadUInt32(configAddress + 0x18, out var height))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong handle;
        lock (StateGate)
        {
            handle = unchecked((ulong)Interlocked.Increment(ref _nextHandle));
            Decoders.Add(handle, new DecoderState
            {
                CodecType = codecType,
                Width = width,
                Height = height,
            });
        }

        Span<byte> control = stackalloc byte[ControlSize];
        control.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(control, ControlSize);
        BinaryPrimitives.WriteUInt64LittleEndian(control[0x08..], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(control[0x10..], 1);
        if (!ctx.Memory.TryWrite(controlAddress, control))
        {
            lock (StateGate)
            {
                Decoders.Remove(handle);
            }
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "q0W5GJMovMs",
        ExportName = "sceVideodecDecode",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecDecode(CpuContext ctx)
    {
        var controlAddress = ctx[CpuRegister.Rdi];
        var inputAddress = ctx[CpuRegister.Rsi];
        var frameBufferAddress = ctx[CpuRegister.Rdx];
        var pictureInfoAddress = ctx[CpuRegister.Rcx];
        if (controlAddress == 0 || inputAddress == 0 || frameBufferAddress == 0 || pictureInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!TryGetDecoder(ctx, controlAddress, out var decoder, out var error))
        {
            return ctx.SetReturn(error);
        }

        if (!ctx.TryReadUInt64(inputAddress, out var inputSize) ||
            !ctx.TryReadUInt64(frameBufferAddress, out var frameSize) ||
            !ctx.TryReadUInt64(pictureInfoAddress, out var pictureSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (inputSize != InputDataSize || frameSize != FrameBufferSize || pictureSize != PictureInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt64(inputAddress + 0x18, out var pts) ||
            !ctx.TryReadUInt64(inputAddress + 0x28, out var attachedData))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return WriteEmptyPictureInfo(ctx, pictureInfoAddress, decoder, pts, attachedData);
    }

    [SysAbiExport(
        Nid = "U0kpGF1cl90",
        ExportName = "sceVideodecDeleteDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecDeleteDecoder(CpuContext ctx)
    {
        var controlAddress = ctx[CpuRegister.Rdi];
        if (controlAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(controlAddress + 0x08, out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            if (handle == 0 || !Decoders.Remove(handle))
            {
                return ctx.SetReturn(ErrorHandle);
            }
        }

        return ctx.TryWriteUInt64(controlAddress + 0x08, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "jeigLlKdp5I",
        ExportName = "sceVideodecFlush",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecFlush(CpuContext ctx)
    {
        var controlAddress = ctx[CpuRegister.Rdi];
        var frameBufferAddress = ctx[CpuRegister.Rsi];
        var pictureInfoAddress = ctx[CpuRegister.Rdx];
        if (controlAddress == 0 || frameBufferAddress == 0 || pictureInfoAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!TryGetDecoder(ctx, controlAddress, out var decoder, out var error))
        {
            return ctx.SetReturn(error);
        }

        if (!ctx.TryReadUInt64(frameBufferAddress, out var frameSize) ||
            !ctx.TryReadUInt64(pictureInfoAddress, out var pictureSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (frameSize != FrameBufferSize || pictureSize != PictureInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        return WriteEmptyPictureInfo(ctx, pictureInfoAddress, decoder, 0, 0);
    }

    [SysAbiExport(
        Nid = "kg+lH0V61hM",
        ExportName = "sceVideodecMapMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecMapMemory(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "leCAscipfFY",
        ExportName = "sceVideodecQueryResourceInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecQueryResourceInfo(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var resourceAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || resourceAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(configAddress, out var configSize) ||
            !ctx.TryReadUInt64(resourceAddress, out var resourceSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (configSize != ConfigInfoSize || resourceSize != ResourceInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!ctx.TryReadUInt32(configAddress + 0x10, out var maxLevel) ||
            !ctx.TryReadUInt32(configAddress + 0x14, out var widthRaw) ||
            !ctx.TryReadUInt32(configAddress + 0x18, out var heightRaw) ||
            !ctx.TryReadUInt32(configAddress + 0x1C, out var dpbRaw))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var width = unchecked((int)widthRaw);
        var height = unchecked((int)heightRaw);
        if (width <= 0 || height <= 0)
        {
            width = maxLevel >= 150 ? 3840 : 1920;
            height = maxLevel >= 150 ? 2160 : 1080;
        }

        var alignedWidth = AlignUp(unchecked((uint)width), 256);
        var alignedHeight = AlignUp(unchecked((uint)height), 16);
        var frameSize = ((ulong)alignedWidth * alignedHeight * 3) / 2;
        var paddedFrameSize = AlignUp(frameSize, 256) + 0x4000;
        var dpbCount = unchecked((int)dpbRaw) > 0 ? unchecked((uint)dpbRaw) : 8;

        Span<byte> resource = stackalloc byte[ResourceInfoSize];
        resource.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(resource, ResourceInfoSize);
        BinaryPrimitives.WriteUInt64LittleEndian(resource[0x08..], FallbackMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(resource[0x18..], paddedFrameSize * (dpbCount + 2) + (8 * 1024 * 1024));
        BinaryPrimitives.WriteUInt64LittleEndian(resource[0x28..], paddedFrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(resource[0x30..], 0x100);
        return ctx.Memory.TryWrite(resourceAddress, resource)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "f8AgDv-1X8A",
        ExportName = "sceVideodecReset",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec")]
    public static int VideodecReset(CpuContext ctx)
    {
        return TryGetDecoder(ctx, ctx[CpuRegister.Rdi], out _, out var error)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(error);
    }

    private static bool TryGetDecoder(CpuContext ctx, ulong controlAddress, out DecoderState decoder, out int error)
    {
        decoder = null!;
        if (!ctx.TryReadUInt64(controlAddress, out var controlSize) ||
            !ctx.TryReadUInt64(controlAddress + 0x08, out var handle))
        {
            error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        if (controlSize != ControlSize)
        {
            error = ErrorStructSize;
            return false;
        }

        lock (StateGate)
        {
            if (!Decoders.TryGetValue(handle, out decoder!))
            {
                error = ErrorHandle;
                return false;
            }
        }

        error = 0;
        return true;
    }

    private static int WriteEmptyPictureInfo(
        CpuContext ctx,
        ulong address,
        DecoderState decoder,
        ulong pts,
        ulong attachedData)
    {
        Span<byte> picture = stackalloc byte[PictureInfoSize];
        picture.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(picture, PictureInfoSize);
        BinaryPrimitives.WriteUInt32LittleEndian(picture[0x0C..], decoder.CodecType);
        BinaryPrimitives.WriteUInt32LittleEndian(picture[0x10..], decoder.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(picture[0x14..], AlignUp(decoder.Width, 256));
        BinaryPrimitives.WriteUInt32LittleEndian(picture[0x18..], decoder.Height);
        BinaryPrimitives.WriteUInt64LittleEndian(picture[0x20..], pts);
        BinaryPrimitives.WriteUInt64LittleEndian(picture[0x28..], attachedData);
        return ctx.Memory.TryWrite(address, picture)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
