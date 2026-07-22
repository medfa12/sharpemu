// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Videodec;

public static class Videodec2Exports
{
    private const int ErrorStructSize = unchecked((int)0x811D0101);
    private const int ErrorArgumentPointer = unchecked((int)0x811D0102);
    private const int ErrorDecoderInstance = unchecked((int)0x811D0103);
    private const int ErrorMemoryPointer = unchecked((int)0x811D0105);
    private const int ErrorConfigInfo = unchecked((int)0x811D0200);
    private const int ErrorComputePipeId = unchecked((int)0x811D0201);
    private const int ErrorComputeQueueId = unchecked((int)0x811D0202);
    private const int DecoderConfigSize = 0x48;
    private const int DecoderMemoryInfoSize = 0x48;
    private const int InputDataSize = 0x30;
    private const int OutputInfoSize = 0x38;
    private const int FrameBufferSize = 0x20;
    private const int ComputeMemoryInfoSize = 0x18;
    private const int ComputeConfigInfoSize = 0x10;
    private const ulong MinimumMemorySize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, DecoderState> Decoders = new();
    private static readonly HashSet<ulong> ComputeQueues = new();
    private static long _nextHandle = 0x20000;

    private sealed class DecoderState
    {
        public uint CodecType { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
    }

    [SysAbiExport(Nid = "eD+X2SmxUt4", ExportName = "sceVideodec2AllocateComputeQueue", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2AllocateComputeQueue(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var queueAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryAddress == 0 || queueAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(configAddress, out var configSize) ||
            !ctx.TryReadUInt64(memoryAddress, out var memorySize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (configSize != ComputeConfigInfoSize || memorySize != ComputeMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!TryReadUInt16(ctx, configAddress + 0x08, out var pipeId) ||
            !TryReadUInt16(ctx, configAddress + 0x0A, out var queueId) ||
            !ctx.TryReadByte(configAddress + 0x0D, out var reserved0) ||
            !TryReadUInt16(ctx, configAddress + 0x0E, out var reserved1) ||
            !ctx.TryReadUInt64(memoryAddress + 0x10, out var memoryPointer))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (reserved0 != 0 || reserved1 != 0)
        {
            return ctx.SetReturn(ErrorConfigInfo);
        }
        if (pipeId > 4)
        {
            return ctx.SetReturn(ErrorComputePipeId);
        }
        if (queueId > 7)
        {
            return ctx.SetReturn(ErrorComputeQueueId);
        }
        if (memoryPointer == 0)
        {
            return ctx.SetReturn(ErrorMemoryPointer);
        }

        lock (StateGate)
        {
            ComputeQueues.Add(memoryPointer);
        }
        return ctx.TryWriteUInt64(queueAddress, memoryPointer)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "CNNRoRYd8XI", ExportName = "sceVideodec2CreateDecoder", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2CreateDecoder(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var decoderAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryAddress == 0 || decoderAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }

        if (!ctx.TryReadUInt64(configAddress, out var configSize) ||
            !ctx.TryReadUInt64(memoryAddress, out var memorySize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (configSize != DecoderConfigSize || memorySize != DecoderMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }
        if (!ctx.TryReadUInt32(configAddress + 0x0C, out var codecType) ||
            !ctx.TryReadUInt32(configAddress + 0x18, out var width) ||
            !ctx.TryReadUInt32(configAddress + 0x1C, out var height))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong handle;
        lock (StateGate)
        {
            handle = unchecked((ulong)Interlocked.Increment(ref _nextHandle));
            Decoders.Add(handle, new DecoderState { CodecType = codecType, Width = width, Height = height });
        }
        if (!ctx.TryWriteUInt64(decoderAddress, handle))
        {
            lock (StateGate)
            {
                Decoders.Remove(handle);
            }
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "852F5+q6+iM", ExportName = "sceVideodec2Decode", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2Decode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var inputAddress = ctx[CpuRegister.Rsi];
        var frameAddress = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (!TryGetDecoder(handle, out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }
        if (inputAddress == 0 || frameAddress == 0 || outputAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }
        if (!ctx.TryReadUInt64(inputAddress, out var inputSize) ||
            !ctx.TryReadUInt64(frameAddress, out var frameSize) ||
            !ctx.TryReadUInt64(outputAddress, out var outputSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (inputSize != InputDataSize || frameSize != FrameBufferSize || !IsOutputInfoSize(outputSize))
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        return WriteEmptyOutput(ctx, frameAddress, outputAddress, outputSize, decoder);
    }

    [SysAbiExport(Nid = "jwImxXRGSKA", ExportName = "sceVideodec2DeleteDecoder", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2DeleteDecoder(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Decoders.Remove(ctx[CpuRegister.Rdi]) ? 0 : ErrorDecoderInstance);
        }
    }

    [SysAbiExport(Nid = "l1hXwscLuCY", ExportName = "sceVideodec2Flush", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2Flush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var frameAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (!TryGetDecoder(handle, out var decoder))
        {
            return ctx.SetReturn(ErrorDecoderInstance);
        }
        if (frameAddress == 0 || outputAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }
        if (!ctx.TryReadUInt64(frameAddress, out var frameSize) ||
            !ctx.TryReadUInt64(outputAddress, out var outputSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (frameSize != FrameBufferSize || !IsOutputInfoSize(outputSize))
        {
            return ctx.SetReturn(ErrorStructSize);
        }
        return WriteEmptyOutput(ctx, frameAddress, outputAddress, outputSize, decoder);
    }

    [SysAbiExport(Nid = "kjrLbcyhEiw", ExportName = "sceVideodec2GetAvcPictureInfo", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2GetAvcPictureInfo(CpuContext ctx) => GetPictureInfo(ctx);

    [SysAbiExport(Nid = "NtXRa3dRzU0", ExportName = "sceVideodec2GetPictureInfo", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2GetPictureInfo(CpuContext ctx) => GetPictureInfo(ctx);

    [SysAbiExport(Nid = "RnDibcGCPKw", ExportName = "sceVideodec2QueryComputeMemoryInfo", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryComputeMemoryInfo(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }
        if (!ctx.TryReadUInt64(address, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (size != ComputeMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        Span<byte> info = stackalloc byte[ComputeMemoryInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, ComputeMemoryInfoSize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], MinimumMemorySize);
        return ctx.Memory.TryWrite(address, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "qqMCwlULR+E", ExportName = "sceVideodec2QueryDecoderMemoryInfo", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryDecoderMemoryInfo(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }
        if (!ctx.TryReadUInt64(configAddress, out var configSize) ||
            !ctx.TryReadUInt64(infoAddress, out var infoSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (configSize != DecoderConfigSize || infoSize != DecoderMemoryInfoSize)
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        Span<byte> info = stackalloc byte[DecoderMemoryInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, DecoderMemoryInfoSize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], MinimumMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x18..], MinimumMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x28..], MinimumMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x38..], MinimumMemorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x40..], 0x100);
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "UvtA3FAiF4Y", ExportName = "sceVideodec2ReleaseComputeQueue", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2ReleaseComputeQueue(CpuContext ctx)
    {
        lock (StateGate)
        {
            ComputeQueues.Remove(ctx[CpuRegister.Rdi]);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "wJXikG6QFN8", ExportName = "sceVideodec2Reset", Target = Generation.Gen5, LibraryName = "libSceVideodec2")]
    public static int Videodec2Reset(CpuContext ctx) =>
        ctx.SetReturn(TryGetDecoder(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorDecoderInstance);

    private static int GetPictureInfo(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(ErrorArgumentPointer);
        }
        if (!ctx.TryReadUInt64(outputAddress, out var outputSize) ||
            !ctx.TryReadByte(outputAddress + 0x0A, out var pictureCount))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (!IsOutputInfoSize(outputSize))
        {
            return ctx.SetReturn(ErrorStructSize);
        }

        if (!TryClearSizedOutput(ctx, ctx[CpuRegister.Rsi]) ||
            (pictureCount > 1 && !TryClearSizedOutput(ctx, ctx[CpuRegister.Rdx])))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    private static int WriteEmptyOutput(CpuContext ctx, ulong frameAddress, ulong outputAddress, ulong outputSize, DecoderState decoder)
    {
        Span<byte> notAccepted = stackalloc byte[1];
        notAccepted.Clear();
        if (!ctx.Memory.TryWrite(frameAddress + 0x18, notAccepted))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> output = stackalloc byte[OutputInfoSize];
        output.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(output, outputSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x0C..], decoder.CodecType);
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x10..], decoder.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x14..], AlignUp(decoder.Width, 256));
        BinaryPrimitives.WriteUInt32LittleEndian(output[0x18..], decoder.Height);
        return ctx.Memory.TryWrite(outputAddress, output[..checked((int)outputSize)])
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryGetDecoder(ulong handle, out DecoderState decoder)
    {
        lock (StateGate)
        {
            return Decoders.TryGetValue(handle, out decoder!);
        }
    }

    private static bool TryClearSizedOutput(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return true;
        }
        if (!ctx.TryReadUInt64(address, out var size) || size < 8 || size > 0x400)
        {
            return false;
        }
        var bytes = new byte[checked((int)size)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, size);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    private static bool IsOutputInfoSize(ulong size) => (size | 8) == OutputInfoSize;

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) & ~(alignment - 1);
}
