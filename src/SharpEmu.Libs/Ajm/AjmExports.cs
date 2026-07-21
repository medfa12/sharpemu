// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ajm;

public static class AjmExports
{
    private const int Ok = 0;
    private const int ErrorInvalidContext = unchecked((int)0x80930002);
    private const int ErrorInvalidInstance = unchecked((int)0x80930003);
    private const int ErrorInvalidBatch = unchecked((int)0x80930004);
    private const int ErrorInvalidParameter = unchecked((int)0x80930005);
    private const int ErrorOutOfResources = unchecked((int)0x80930007);
    private const int ErrorCodecNotSupported = unchecked((int)0x80930008);
    private const int ErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int ErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int ErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const int ErrorBadPriority = unchecked((int)0x8093000E);
    private const int ErrorMalformedBatch = unchecked((int)0x80930011);
    private const int ErrorInvalidAddress = unchecked((int)0x80930016);

    private const int ResultNotInitialized = 0x00000001;
    private const int ResultInvalidData = 0x00000002;
    private const int ResultInvalidParameter = 0x00000004;
    private const int ResultPartialInput = 0x00000008;
    private const int ResultNotEnoughRoom = 0x00000010;
    private const int ResultSidebandTruncated = 0x00000100;

    private const uint StatisticsInstance = 0x80000;
    private const uint InstanceIndexMask = 0x3FFF;
    private const uint CodecMp3 = 0;
    private const uint CodecAt9 = 1;
    private const uint CodecAac = 2;
    private const uint CodecLpcm = 23;
    private const uint CodecOpus = 24;
    private const uint MaximumBatchSize = 16 * 1024 * 1024;

    private const int ChunkJob = 0;
    private const int ChunkInputRunBuffer = 1;
    private const int ChunkInputControlBuffer = 2;
    private const int ChunkControlFlags = 3;
    private const int ChunkRunFlags = 4;
    private const int ChunkReturnAddress = 6;
    private const int ChunkInlineBuffer = 7;
    private const int ChunkOutputRunBuffer = 17;
    private const int ChunkOutputControlBuffer = 18;

    private const ulong RunGetCodecInfo = 1UL << 11;
    private const ulong RunMultipleFrames = 1UL << 12;
    private const ulong ControlReset = 1UL << 13;
    private const ulong ControlInitialize = 1UL << 14;
    private const ulong ControlResample = 1UL << 15;
    private const ulong SidebandGapless = 1UL << 45;
    private const ulong SidebandFormat = 1UL << 46;
    private const ulong SidebandStream = 1UL << 47;

    private static readonly object StateGate = new();
    private static readonly Dictionary<uint, ContextState> Contexts = new();
    private static int _nextContextId;
    private static int _nextInstanceIndex;
    private static int _nextBatchId;

    private sealed class ContextState
    {
        public HashSet<uint> RegisteredCodecs { get; } = new();
        public Dictionary<uint, InstanceState> Instances { get; } = new();
        public HashSet<uint> CompletedBatches { get; } = new();
    }

    private sealed class InstanceState(uint codec, ulong flags, uint channels, uint encoding)
    {
        public uint Codec { get; } = codec;
        public ulong Flags { get; } = flags;
        public uint Channels { get; set; } = channels;
        public uint Encoding { get; } = encoding;
        public uint SampleRate { get; set; } = 48000;
        public uint Bitrate { get; set; }
        public ulong TotalDecodedSamples { get; set; }
        public uint GaplessTotalSamples { get; set; }
        public ushort GaplessSkipSamples { get; set; }
        public ushort GaplessSkippedSamples { get; set; }
        public bool Initialized { get; set; } = codec is CodecMp3 or CodecLpcm;
        public uint FrameBytes { get; set; }
        public uint FrameSamples { get; set; } = codec == CodecOpus ? 960u : 1024u;
        public uint FramesPerSuperframe { get; set; } = 1;
        public uint SuperframeBytes { get; set; }
        public uint At9FrameIndex { get; set; }
        public uint LastMp3Header { get; set; }
    }

    private readonly record struct BufferRef(ulong Address, uint Size);

    private sealed class ParsedJob
    {
        public ulong Flags;
        public BufferRef? InputControl;
        public BufferRef? OutputControl;
        public List<BufferRef> Inputs { get; } = new();
        public List<BufferRef> Outputs { get; } = new();
    }

    private readonly record struct DecodeResult(
        int Result,
        int InternalResult,
        int InputConsumed,
        int OutputWritten,
        uint Frames);

    [SysAbiExport(
        Nid = "dl+4eHSzUu4",
        ExportName = "sceAjmInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int Initialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outContextAddress = ctx[CpuRegister.Rsi];
        if (reserved != 0 || outContextAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        uint contextId;
        lock (StateGate)
        {
            do
            {
                contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
            }
            while (contextId == 0 || Contexts.ContainsKey(contextId));

            Contexts.Add(contextId, new ContextState());
        }

        if (!ctx.TryWriteUInt32(outContextAddress, contextId))
        {
            lock (StateGate)
            {
                Contexts.Remove(contextId);
            }

            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return ctx.SetReturn(Ok);
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int Finalize(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Contexts.Remove((uint)ctx[CpuRegister.Rdi]) ? Ok : ErrorInvalidContext);
        }
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int ModuleRegister(CpuContext ctx)
    {
        var contextId = (uint)ctx[CpuRegister.Rdi];
        var codec = (uint)ctx[CpuRegister.Rsi];
        if (ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }

            if (!IsKnownCodec(codec))
            {
                return ctx.SetReturn(ErrorCodecNotSupported);
            }

            return ctx.SetReturn(context.RegisteredCodecs.Add(codec) ? Ok : ErrorCodecAlreadyRegistered);
        }
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int ModuleUnregister(CpuContext ctx)
    {
        var contextId = (uint)ctx[CpuRegister.Rdi];
        var codec = (uint)ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }

            if (context.Instances.Values.Any(instance => instance.Codec == codec))
            {
                return ctx.SetReturn(ErrorOutOfResources);
            }

            return ctx.SetReturn(context.RegisteredCodecs.Remove(codec) ? Ok : ErrorCodecNotRegistered);
        }
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int InstanceCreate(CpuContext ctx)
    {
        var contextId = (uint)ctx[CpuRegister.Rdi];
        var codec = (uint)ctx[CpuRegister.Rsi];
        var flags = ctx[CpuRegister.Rdx];
        var outInstanceAddress = ctx[CpuRegister.Rcx];
        if (outInstanceAddress == 0 || !IsKnownCodec(codec))
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        var revision = (uint)(flags & 7);
        if (ctx.TargetGeneration == Generation.Gen4 && revision == 0)
        {
            return ctx.SetReturn(ErrorWrongRevisionFlag);
        }

        var encoding = (uint)((flags >> 7) & 7);
        if (encoding > 2)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        var channels = revision == 0
            ? (uint)(flags & 0x7F)
            : (uint)((flags >> 3) & 0xF);
        if (channels == 0)
        {
            channels = 2;
        }
        if (channels > 16)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        uint instanceId;
        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }
            if (!context.RegisteredCodecs.Contains(codec))
            {
                return ctx.SetReturn(ErrorCodecNotRegistered);
            }

            var attempts = 0;
            do
            {
                var index = unchecked((uint)Interlocked.Increment(ref _nextInstanceIndex)) & InstanceIndexMask;
                if (index == 0)
                {
                    continue;
                }
                instanceId = index | (codec << 14);
                if (!context.Instances.ContainsKey(instanceId))
                {
                    context.Instances.Add(instanceId, new InstanceState(codec, flags, channels, encoding));
                    goto Created;
                }
            }
            while (++attempts <= InstanceIndexMask);

            return ctx.SetReturn(ErrorOutOfResources);
        }

    Created:
        if (!ctx.TryWriteUInt32(outInstanceAddress, instanceId))
        {
            lock (StateGate)
            {
                if (Contexts.TryGetValue(contextId, out var context))
                {
                    context.Instances.Remove(instanceId);
                }
            }
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return ctx.SetReturn(Ok);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int InstanceDestroy(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Contexts.TryGetValue((uint)ctx[CpuRegister.Rdi], out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }

            return ctx.SetReturn(context.Instances.Remove((uint)ctx[CpuRegister.Rsi]) ? Ok : ErrorInvalidInstance);
        }
    }

    [SysAbiExport(
        Nid = "diXjQNiMu-s",
        ExportName = "sceAjmInstanceCodecType",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int InstanceCodecType(CpuContext ctx) =>
        ctx.SetReturn((int)(((uint)ctx[CpuRegister.Rdi] >> 14) & 0x1F));

    [SysAbiExport(
        Nid = "bkRHEYG6lEM",
        ExportName = "sceAjmMemoryRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int MemoryRegister(CpuContext ctx) => ValidateMemoryOperation(ctx, requirePages: true);

    [SysAbiExport(
        Nid = "pIpGiaYkHkM",
        ExportName = "sceAjmMemoryUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int MemoryUnregister(CpuContext ctx) => ValidateMemoryOperation(ctx, requirePages: false);

    [SysAbiExport(
        Nid = "fFFkk0xfGWs",
        ExportName = "sceAjmBatchStartBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchStartBuffer(CpuContext ctx)
    {
        var contextId = (uint)ctx[CpuRegister.Rdi];
        var batchAddress = ctx[CpuRegister.Rsi];
        var batchSize = (uint)ctx[CpuRegister.Rdx];
        var priority = unchecked((int)(uint)ctx[CpuRegister.Rcx]);
        var errorAddress = ctx[CpuRegister.R8];
        var outBatchAddress = ctx[CpuRegister.R9];
        if (batchAddress == 0 || outBatchAddress == 0 || batchSize > MaximumBatchSize)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }
        if ((batchSize & 7) != 0)
        {
            return ctx.SetReturn(ErrorMalformedBatch);
        }
        if (priority is < -1 or > 255)
        {
            return ctx.SetReturn(ErrorBadPriority);
        }

        ContextState context;
        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out context!))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }
        }

        if (errorAddress != 0 && !TryClear(ctx, errorAddress, 32))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        var parseResult = ParseAndExecuteBatch(ctx, context, batchAddress, batchSize, errorAddress);
        if (parseResult != Ok)
        {
            return ctx.SetReturn(parseResult);
        }

        var batchId = unchecked((uint)Interlocked.Increment(ref _nextBatchId));
        lock (StateGate)
        {
            context.CompletedBatches.Add(batchId);
        }
        if (!ctx.TryWriteUInt32(outBatchAddress, batchId))
        {
            lock (StateGate)
            {
                context.CompletedBatches.Remove(batchId);
            }
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        return ctx.SetReturn(Ok);
    }

    [SysAbiExport(
        Nid = "-qLsfDAywIY",
        ExportName = "sceAjmBatchWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchWait(CpuContext ctx)
    {
        var contextId = (uint)ctx[CpuRegister.Rdi];
        var batchId = (uint)ctx[CpuRegister.Rsi];
        var errorAddress = ctx[CpuRegister.Rcx];
        lock (StateGate)
        {
            if (!Contexts.TryGetValue(contextId, out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }
            if (!context.CompletedBatches.Remove(batchId))
            {
                return ctx.SetReturn(ErrorInvalidBatch);
            }
        }

        if (errorAddress != 0 && !TryClear(ctx, errorAddress, 32))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }
        return ctx.SetReturn(Ok);
    }

    [SysAbiExport(
        Nid = "NVDXiUesSbA",
        ExportName = "sceAjmBatchCancel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchCancel(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Contexts.TryGetValue((uint)ctx[CpuRegister.Rdi], out var context))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }
            return ctx.SetReturn(context.CompletedBatches.Contains((uint)ctx[CpuRegister.Rsi]) ? Ok : ErrorInvalidBatch);
        }
    }

    [SysAbiExport(
        Nid = "WfAiBW8Wcek",
        ExportName = "sceAjmBatchErrorDump",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchErrorDump(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "YDFR0dDVGAg",
        ExportName = "sceAjmInstanceExtend",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int InstanceExtend(CpuContext ctx) => ctx.SetReturn(ErrorCodecNotSupported);

    [SysAbiExport(
        Nid = "rgLjmfdXocI",
        ExportName = "sceAjmInstanceSwitch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int InstanceSwitch(CpuContext ctx) => ctx.SetReturn(ErrorCodecNotSupported);

    [SysAbiExport(
        Nid = "AxhcqVv5AYU",
        ExportName = "sceAjmStrError",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int StrError(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "dmDybN--Fn8",
        ExportName = "sceAjmBatchJobControlBufferRa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchJobControlBufferRa(CpuContext ctx)
    {
        if (!TryReadStackArgument(ctx, 0, out var outputSize) ||
            !TryReadStackArgument(ctx, 1, out var returnAddress))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        var cursor = ctx[CpuRegister.Rdi];
        if (cursor == 0)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }
        var jobStart = cursor;
        cursor += 8;
        if (returnAddress != 0 && !WriteBufferChunk(ctx, ref cursor, ChunkReturnAddress, 0, returnAddress) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkInputControlBuffer, (uint)ctx[CpuRegister.R8], ctx[CpuRegister.Rcx]) ||
            !WriteFlagsChunk(ctx, ref cursor, ChunkControlFlags, ctx[CpuRegister.Rdx]) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkOutputControlBuffer, (uint)outputSize, ctx[CpuRegister.R9]) ||
            !WriteJobHeader(ctx, jobStart, (uint)ctx[CpuRegister.Rsi], (uint)(cursor - jobStart - 8)))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        ctx[CpuRegister.Rax] = cursor;
        return Ok;
    }

    [SysAbiExport(
        Nid = "ElslOCpOIns",
        ExportName = "sceAjmBatchJobRunBufferRa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchJobRunBufferRa(CpuContext ctx)
    {
        if (!TryReadStackArgument(ctx, 0, out var outputSize) ||
            !TryReadStackArgument(ctx, 1, out var sidebandAddress) ||
            !TryReadStackArgument(ctx, 2, out var sidebandSize) ||
            !TryReadStackArgument(ctx, 3, out var returnAddress))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        var cursor = ctx[CpuRegister.Rdi];
        if (cursor == 0)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }
        var jobStart = cursor;
        cursor += 8;
        if (returnAddress != 0 && !WriteBufferChunk(ctx, ref cursor, ChunkReturnAddress, 0, returnAddress) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkInputRunBuffer, (uint)ctx[CpuRegister.R8], ctx[CpuRegister.Rcx]) ||
            !WriteFlagsChunk(ctx, ref cursor, ChunkRunFlags, ctx[CpuRegister.Rdx]) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkOutputRunBuffer, (uint)outputSize, ctx[CpuRegister.R9]) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkOutputControlBuffer, (uint)sidebandSize, sidebandAddress) ||
            !WriteJobHeader(ctx, jobStart, (uint)ctx[CpuRegister.Rsi], (uint)(cursor - jobStart - 8)))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        ctx[CpuRegister.Rax] = cursor;
        return Ok;
    }

    [SysAbiExport(
        Nid = "7jdAXK+2fMo",
        ExportName = "sceAjmBatchJobRunSplitBufferRa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchJobRunSplitBufferRa(CpuContext ctx)
    {
        if (!TryReadStackArgument(ctx, 0, out var outputCountRaw) ||
            !TryReadStackArgument(ctx, 1, out var sidebandAddress) ||
            !TryReadStackArgument(ctx, 2, out var sidebandSize) ||
            !TryReadStackArgument(ctx, 3, out var returnAddress))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        var cursor = ctx[CpuRegister.Rdi];
        var inputArray = ctx[CpuRegister.Rcx];
        var inputCount = ctx[CpuRegister.R8];
        var outputArray = ctx[CpuRegister.R9];
        var outputCount = outputCountRaw;
        if (cursor == 0 || inputCount > 1024 || outputCount > 1024)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }
        var jobStart = cursor;
        cursor += 8;
        if (returnAddress != 0 && !WriteBufferChunk(ctx, ref cursor, ChunkReturnAddress, 0, returnAddress))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }
        if (!CopyBufferArrayToChunks(ctx, ref cursor, inputArray, inputCount, ChunkInputRunBuffer) ||
            !WriteFlagsChunk(ctx, ref cursor, ChunkRunFlags, ctx[CpuRegister.Rdx]) ||
            !CopyBufferArrayToChunks(ctx, ref cursor, outputArray, outputCount, ChunkOutputRunBuffer) ||
            !WriteBufferChunk(ctx, ref cursor, ChunkOutputControlBuffer, (uint)sidebandSize, sidebandAddress) ||
            !WriteJobHeader(ctx, jobStart, (uint)ctx[CpuRegister.Rsi], (uint)(cursor - jobStart - 8)))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        ctx[CpuRegister.Rax] = cursor;
        return Ok;
    }

    [SysAbiExport(
        Nid = "stlghnic3Jc",
        ExportName = "sceAjmBatchJobInlineBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int BatchJobInlineBuffer(CpuContext ctx)
    {
        var buffer = ctx[CpuRegister.Rdi];
        var data = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var outBatchAddress = ctx[CpuRegister.Rcx];
        if (buffer == 0 || (size != 0 && data == 0) || outBatchAddress == 0 || size > MaximumBatchSize)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        var alignedSize = (size + 7) & ~7UL;
        if (!WriteJobHeader(ctx, buffer, 0, (uint)alignedSize, ChunkInlineBuffer) ||
            !ctx.TryWriteUInt64(outBatchAddress, buffer + 8))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }
        if (size != 0)
        {
            var bytes = new byte[(int)size];
            if (!ctx.Memory.TryRead(data, bytes) || !ctx.Memory.TryWrite(buffer + 8, bytes))
            {
                return ctx.SetReturn(ErrorInvalidAddress);
            }
        }
        if (alignedSize != size && !TryClear(ctx, buffer + 8 + size, alignedSize - size))
        {
            return ctx.SetReturn(ErrorInvalidAddress);
        }

        ctx[CpuRegister.Rax] = buffer + 8 + alignedSize;
        return Ok;
    }

    [SysAbiExport(
        Nid = "1t3ixYNXyuc",
        ExportName = "sceAjmDecAt9ParseConfigData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int DecAt9ParseConfigData(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var outInfoAddress = ctx[CpuRegister.Rsi];
        Span<byte> config = stackalloc byte[4];
        if (configAddress == 0 || outInfoAddress == 0 || !ctx.Memory.TryRead(configAddress, config) ||
            !TryParseAt9Config(config, out var sampleRate, out var channels, out var frameSamples,
                out var framesPerSuperframe, out var superframeBytes))
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        Span<byte> info = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(info[0..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(info[4..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(info[8..], frameSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(info[12..], frameSamples * framesPerSuperframe);
        BinaryPrimitives.WriteUInt32LittleEndian(info[16..], superframeBytes);
        return ctx.Memory.TryWrite(outInfoAddress, info)
            ? ctx.SetReturn(Ok)
            : ctx.SetReturn(ErrorInvalidAddress);
    }

    [SysAbiExport(
        Nid = "eDFeTyi+G3Y",
        ExportName = "sceAjmDecMp3ParseFrame",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int DecMp3ParseFrame(CpuContext ctx)
    {
        var streamAddress = ctx[CpuRegister.Rdi];
        var streamSize = (uint)ctx[CpuRegister.Rsi];
        var outFrameAddress = ctx[CpuRegister.Rcx];
        Span<byte> header = stackalloc byte[4];
        if (streamAddress == 0 || streamSize < 4 || outFrameAddress == 0 ||
            !ctx.Memory.TryRead(streamAddress, header) || !TryParseMp3Header(header, out var info))
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }

        Span<byte> frame = stackalloc byte[48];
        frame.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(frame[0..], info.FrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], info.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], info.Samples);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[16..], info.Bitrate);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], info.SampleRate);
        return ctx.Memory.TryWrite(outFrameAddress, frame)
            ? ctx.SetReturn(Ok)
            : ctx.SetReturn(ErrorInvalidAddress);
    }

    private static int ValidateMemoryOperation(CpuContext ctx, bool requirePages)
    {
        lock (StateGate)
        {
            if (!Contexts.ContainsKey((uint)ctx[CpuRegister.Rdi]))
            {
                return ctx.SetReturn(ErrorInvalidContext);
            }
        }

        if (ctx[CpuRegister.Rsi] == 0 || requirePages && ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(ErrorInvalidParameter);
        }
        return ctx.SetReturn(Ok);
    }

    private static int ParseAndExecuteBatch(
        CpuContext ctx,
        ContextState context,
        ulong batchAddress,
        uint batchSize,
        ulong errorAddress)
    {
        uint offset = 0;
        while (offset < batchSize)
        {
            if (batchSize - offset < 8 ||
                !ctx.TryReadUInt32(batchAddress + offset, out var header) ||
                !ctx.TryReadUInt32(batchAddress + offset + 4, out var jobSize))
            {
                return WriteBatchError(ctx, errorAddress, ErrorMalformedBatch, batchAddress + offset, offset);
            }

            var ident = (int)(header & 0x3F);
            var instanceId = (header >> 6) & 0xFFFFF;
            if (jobSize > batchSize - offset - 8)
            {
                return WriteBatchError(ctx, errorAddress, ErrorMalformedBatch, batchAddress + offset, offset);
            }

            if (ident == ChunkInlineBuffer)
            {
                offset += 8 + jobSize;
                continue;
            }
            if (ident != ChunkJob || !TryParseJob(ctx, batchAddress + offset + 8, jobSize, out var job))
            {
                return WriteBatchError(ctx, errorAddress, ErrorMalformedBatch, batchAddress + offset, offset);
            }

            int result;
            if (instanceId == StatisticsInstance)
            {
                result = ExecuteStatisticsJob(ctx, context, job);
            }
            else
            {
                lock (StateGate)
                {
                    if (!context.Instances.TryGetValue(instanceId, out var instance))
                    {
                        return WriteBatchError(ctx, errorAddress, ErrorInvalidInstance, batchAddress + offset, offset);
                    }
                    result = ExecuteCodecJob(ctx, instance, job);
                }
            }

            if (result != Ok)
            {
                return WriteBatchError(ctx, errorAddress, result, batchAddress + offset, offset);
            }
            offset += 8 + jobSize;
        }

        return offset == batchSize ? Ok : ErrorMalformedBatch;
    }

    private static bool TryParseJob(CpuContext ctx, ulong address, uint size, out ParsedJob job)
    {
        job = new ParsedJob();
        uint offset = 0;
        var foundFlags = false;
        while (offset < size)
        {
            if (size - offset < 4 || !ctx.TryReadUInt32(address + offset, out var header))
            {
                return false;
            }
            var ident = (int)(header & 0x3F);
            switch (ident)
            {
                case ChunkControlFlags:
                case ChunkRunFlags:
                    if (foundFlags || size - offset < 8 || !ctx.TryReadUInt32(address + offset + 4, out var lowFlags))
                    {
                        return false;
                    }
                    job.Flags = ((ulong)((header >> 6) & 0xFFFFF) << 32) | lowFlags;
                    foundFlags = true;
                    offset += 8;
                    break;
                case ChunkInputRunBuffer:
                case ChunkInputControlBuffer:
                case ChunkReturnAddress:
                case ChunkOutputRunBuffer:
                case ChunkOutputControlBuffer:
                    if (size - offset < 16 ||
                        !ctx.TryReadUInt32(address + offset + 4, out var bufferSize) ||
                        !ctx.TryReadUInt64(address + offset + 8, out var bufferAddress))
                    {
                        return false;
                    }
                    var buffer = new BufferRef(bufferAddress, bufferSize);
                    if (ident == ChunkInputRunBuffer)
                    {
                        job.Inputs.Add(buffer);
                    }
                    else if (ident == ChunkOutputRunBuffer)
                    {
                        job.Outputs.Add(buffer);
                    }
                    else if (ident == ChunkInputControlBuffer)
                    {
                        if (job.InputControl.HasValue)
                        {
                            return false;
                        }
                        job.InputControl = buffer;
                    }
                    else if (ident == ChunkOutputControlBuffer)
                    {
                        if (job.OutputControl.HasValue)
                        {
                            return false;
                        }
                        job.OutputControl = buffer;
                    }
                    offset += 16;
                    break;
                default:
                    return false;
            }
        }

        return offset == size && foundFlags;
    }

    private static int ExecuteCodecJob(CpuContext ctx, InstanceState instance, ParsedJob job)
    {
        var resultBits = 0;
        var internalResult = 0;
        if ((job.Flags & ControlReset) != 0)
        {
            ResetInstance(instance);
        }

        var inputControlOffset = 0u;
        if ((job.Flags & SidebandFormat) != 0 && job.InputControl is { } formatInput)
        {
            if (formatInput.Size < 24 ||
                !ctx.TryReadUInt32(formatInput.Address, out var channels) ||
                !ctx.TryReadUInt32(formatInput.Address + 8, out var sampleRate))
            {
                resultBits |= ResultInvalidParameter;
            }
            else
            {
                if (channels != 0)
                {
                    instance.Channels = channels;
                }
                if (sampleRate != 0)
                {
                    instance.SampleRate = sampleRate;
                }
                inputControlOffset += 24;
            }
        }
        if ((job.Flags & SidebandGapless) != 0 && job.InputControl is { } gaplessInput &&
            gaplessInput.Size >= inputControlOffset + 8)
        {
            if (!ctx.TryReadUInt32(gaplessInput.Address + inputControlOffset, out var totalSamples) ||
                !ctx.TryReadUInt16(gaplessInput.Address + inputControlOffset + 4, out var skipSamples))
            {
                resultBits |= ResultInvalidParameter;
            }
            else
            {
                instance.GaplessTotalSamples = totalSamples;
                instance.GaplessSkipSamples = skipSamples;
                instance.GaplessSkippedSamples = 0;
                inputControlOffset += 8;
            }
        }
        if ((job.Flags & ControlResample) != 0)
        {
            inputControlOffset += 8;
        }
        if ((job.Flags & ControlInitialize) != 0)
        {
            if (job.InputControl is not { } initInput || initInput.Size < inputControlOffset + 8)
            {
                resultBits |= ResultInvalidParameter;
            }
            else
            {
                Span<byte> init = stackalloc byte[8];
                if (!ctx.Memory.TryRead(initInput.Address + inputControlOffset, init))
                {
                    return ErrorInvalidAddress;
                }
                resultBits |= InitializeCodec(instance, init);
            }
        }

        var decode = new DecodeResult(resultBits, internalResult, 0, 0, 0);
        if (job.Inputs.Count != 0)
        {
            if (!TryGatherInput(ctx, job.Inputs, out var input))
            {
                return ErrorInvalidAddress;
            }
            decode = Decode(ctx, instance, job.Outputs, input, (job.Flags & RunMultipleFrames) != 0);
            decode = decode with { Result = decode.Result | resultBits };
        }

        return WriteJobSideband(ctx, instance, job, decode);
    }

    private static int InitializeCodec(InstanceState instance, ReadOnlySpan<byte> parameters)
    {
        if (instance.Codec == CodecAt9)
        {
            if (!TryParseAt9Config(parameters[..4], out var sampleRate, out var channels,
                    out var frameSamples, out var framesPerSuperframe, out var superframeBytes))
            {
                instance.Initialized = false;
                return ResultInvalidParameter;
            }
            instance.SampleRate = sampleRate;
            instance.Channels = channels;
            instance.FrameSamples = frameSamples;
            instance.FramesPerSuperframe = framesPerSuperframe;
            instance.SuperframeBytes = superframeBytes;
            instance.FrameBytes = superframeBytes / framesPerSuperframe;
            instance.At9FrameIndex = 0;
            instance.Initialized = true;
        }
        else if (instance.Codec == CodecAac)
        {
            var configType = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
            var frequencyIndex = BinaryPrimitives.ReadUInt32LittleEndian(parameters[4..]);
            if (configType is < 1 or > 3 || frequencyIndex > 11)
            {
                instance.Initialized = false;
                return ResultInvalidParameter;
            }
            if (configType == 2)
            {
                instance.SampleRate = AacSampleRates[frequencyIndex];
            }
            instance.Initialized = true;
        }
        else if (instance.Codec == CodecOpus)
        {
            instance.Initialized = true;
        }
        else
        {
            instance.Initialized = true;
        }
        return 0;
    }

    private static DecodeResult Decode(
        CpuContext ctx,
        InstanceState instance,
        IReadOnlyList<BufferRef> outputs,
        byte[] input,
        bool multipleFrames)
    {
        if (!instance.Initialized)
        {
            return new DecodeResult(ResultNotInitialized, 0, 0, 0, 0);
        }
        if (input.Length == 0)
        {
            return new DecodeResult(ResultPartialInput, 0, 0, 0, 0);
        }

        var outputCapacity = outputs.Aggregate<BufferRef, ulong>(0, (total, buffer) => total + buffer.Size);
        if (instance.Codec == CodecLpcm)
        {
            var written = (int)Math.Min((ulong)input.Length, outputCapacity);
            if (!TryScatter(ctx, outputs, input.AsSpan(0, written), clearRemainder: false))
            {
                return new DecodeResult(ResultInvalidParameter, 0, 0, 0, 0);
            }
            var samples = (uint)(written / Math.Max(1u, instance.Channels * BytesPerSample(instance.Encoding)));
            instance.TotalDecodedSamples += samples;
            return new DecodeResult(
                written < input.Length ? ResultNotEnoughRoom : 0,
                0,
                written,
                written,
                written == 0 ? 0u : 1u);
        }

        var consumed = 0;
        var outputWritten = 0;
        uint frames = 0;
        var result = 0;
        do
        {
            if (!TryGetCompressedFrame(instance, input.AsSpan(consumed), out var frameBytes, out var frameSamples))
            {
                result |= input.Length - consumed < MinimumInputBytes(instance.Codec)
                    ? ResultPartialInput
                    : ResultInvalidData;
                break;
            }
            if (input.Length - consumed < frameBytes)
            {
                result |= ResultPartialInput;
                break;
            }

            var skipped = Math.Min((uint)instance.GaplessSkipSamples, frameSamples);
            var samples = frameSamples - skipped;
            if (instance.GaplessTotalSamples != 0)
            {
                samples = Math.Min(samples, instance.GaplessTotalSamples);
            }
            var frameOutputBytes = checked((ulong)samples * instance.Channels * BytesPerSample(instance.Encoding));
            if ((ulong)outputWritten + frameOutputBytes > outputCapacity)
            {
                result |= ResultNotEnoughRoom;
                break;
            }

            consumed += frameBytes;
            outputWritten += (int)frameOutputBytes;
            frames++;
            if (instance.Codec == CodecAt9)
            {
                instance.At9FrameIndex = (instance.At9FrameIndex + 1) % instance.FramesPerSuperframe;
            }
            instance.GaplessSkipSamples -= (ushort)skipped;
            instance.GaplessSkippedSamples = (ushort)Math.Min(
                ushort.MaxValue,
                instance.GaplessSkippedSamples + frameSamples - samples);
            if (instance.GaplessTotalSamples != 0)
            {
                instance.GaplessTotalSamples -= samples;
            }
            instance.TotalDecodedSamples += samples;
        }
        while (multipleFrames && consumed < input.Length);

        if (outputWritten != 0 && !TryScatterZeroes(ctx, outputs, (uint)outputWritten))
        {
            return new DecodeResult(ResultInvalidParameter, 0, 0, 0, 0);
        }
        return new DecodeResult(result, 0, consumed, outputWritten, frames);
    }

    private static bool TryGetCompressedFrame(
        InstanceState instance,
        ReadOnlySpan<byte> input,
        out int frameBytes,
        out uint frameSamples)
    {
        frameBytes = 0;
        frameSamples = instance.FrameSamples;
        switch (instance.Codec)
        {
            case CodecMp3:
                if (!TryParseMp3Header(input, out var mp3))
                {
                    return false;
                }
                frameBytes = (int)mp3.FrameSize;
                frameSamples = mp3.Samples;
                instance.SampleRate = mp3.SampleRate;
                instance.Channels = mp3.Channels;
                instance.Bitrate = mp3.Bitrate;
                instance.FrameBytes = (uint)frameBytes;
                instance.FrameSamples = frameSamples;
                instance.LastMp3Header = BinaryPrimitives.ReadUInt32BigEndian(input);
                return frameBytes > 0;
            case CodecAt9:
                frameBytes = (int)(instance.At9FrameIndex + 1 == instance.FramesPerSuperframe
                    ? instance.SuperframeBytes - instance.FrameBytes * instance.At9FrameIndex
                    : instance.FrameBytes);
                return frameBytes > 0;
            case CodecAac:
                if (TryParseAdts(input, out frameBytes, out frameSamples, out var sampleRate, out var channels))
                {
                    instance.SampleRate = sampleRate;
                    instance.Channels = channels;
                    return true;
                }
                frameBytes = input.Length;
                frameSamples = 1024;
                return frameBytes > 0;
            case CodecOpus:
                frameBytes = input.Length;
                frameSamples = 960;
                return frameBytes > 0;
            default:
                return false;
        }
    }

    private static int WriteJobSideband(
        CpuContext ctx,
        InstanceState instance,
        ParsedJob job,
        DecodeResult decode)
    {
        if (job.OutputControl is not { } output || output.Address == 0 || output.Size == 0)
        {
            return Ok;
        }

        var required = 8u;
        if ((job.Flags & SidebandStream) != 0) required += 16;
        if ((job.Flags & SidebandFormat) != 0) required += 24;
        if ((job.Flags & SidebandGapless) != 0) required += 8;
        if ((job.Flags & RunMultipleFrames) != 0) required += 8;
        if ((job.Flags & RunGetCodecInfo) != 0) required += CodecInfoSize(instance.Codec);
        var resultBits = decode.Result | (output.Size < required ? ResultSidebandTruncated : 0);

        var bytes = new byte[output.Size];
        var offset = 0;
        if (TryReserve(bytes, ref offset, 8, out var result))
        {
            BinaryPrimitives.WriteInt32LittleEndian(result, resultBits);
            BinaryPrimitives.WriteInt32LittleEndian(result[4..], decode.InternalResult);
        }
        if ((job.Flags & SidebandStream) != 0 && TryReserve(bytes, ref offset, 16, out var stream))
        {
            BinaryPrimitives.WriteInt32LittleEndian(stream, decode.InputConsumed);
            BinaryPrimitives.WriteInt32LittleEndian(stream[4..], decode.OutputWritten);
            BinaryPrimitives.WriteUInt64LittleEndian(stream[8..], instance.TotalDecodedSamples);
        }
        if ((job.Flags & SidebandFormat) != 0 && TryReserve(bytes, ref offset, 24, out var format))
        {
            WriteFormat(format, instance);
        }
        if ((job.Flags & SidebandGapless) != 0 && TryReserve(bytes, ref offset, 8, out var gapless))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(gapless, instance.GaplessTotalSamples);
            BinaryPrimitives.WriteUInt16LittleEndian(gapless[4..], instance.GaplessSkipSamples);
            BinaryPrimitives.WriteUInt16LittleEndian(gapless[6..], instance.GaplessSkippedSamples);
        }
        if ((job.Flags & RunMultipleFrames) != 0 && TryReserve(bytes, ref offset, 8, out var multiframe))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(multiframe, decode.Frames);
        }
        if ((job.Flags & RunGetCodecInfo) != 0 && TryReserve(bytes, ref offset, CodecInfoSize(instance.Codec), out var codecInfo))
        {
            WriteCodecInfo(codecInfo, instance);
        }

        return ctx.Memory.TryWrite(output.Address, bytes) ? Ok : ErrorInvalidAddress;
    }

    private static int ExecuteStatisticsJob(CpuContext ctx, ContextState context, ParsedJob job)
    {
        if (job.OutputControl is not { } output || output.Address == 0 || output.Size == 0)
        {
            return Ok;
        }

        var flags = job.Flags;
        var required = 8u;
        if ((flags & (1UL << 31)) != 0) required += 16;
        if ((flags & (1UL << 30)) != 0) required += 16;
        if ((flags & (1UL << 15)) != 0) required += 24;
        var bytes = new byte[output.Size];
        var offset = 0;
        if (TryReserve(bytes, ref offset, 8, out var result))
        {
            BinaryPrimitives.WriteInt32LittleEndian(result, output.Size < required ? ResultSidebandTruncated : 0);
        }
        if ((flags & (1UL << 31)) != 0 && TryReserve(bytes, ref offset, 16, out var engine))
        {
            BinaryPrimitives.WriteSingleLittleEndian(engine, 0.05f);
            BinaryPrimitives.WriteSingleLittleEndian(engine[4..], 0.01f);
            BinaryPrimitives.WriteSingleLittleEndian(engine[8..], 0.01f);
            BinaryPrimitives.WriteSingleLittleEndian(engine[12..], 0.01f);
        }
        if ((flags & (1UL << 30)) != 0 && TryReserve(bytes, ref offset, 16, out var codecs))
        {
            var activeCodecs = context.Instances.Values.Select(value => value.Codec).Distinct().Take(3).ToArray();
            codecs[0] = (byte)activeCodecs.Length;
            for (var index = 0; index < activeCodecs.Length; index++)
            {
                codecs[1 + index] = (byte)activeCodecs[index];
                BinaryPrimitives.WriteSingleLittleEndian(codecs[(4 + index * 4)..], 0.01f);
            }
        }
        if ((flags & (1UL << 15)) != 0 && TryReserve(bytes, ref offset, 24, out var memory))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(memory, (uint)Math.Max(0, 0x4000 - context.Instances.Count));
            BinaryPrimitives.WriteUInt32LittleEndian(memory[4..], 0x400000);
            BinaryPrimitives.WriteUInt32LittleEndian(memory[8..], 0x4200);
            BinaryPrimitives.WriteUInt32LittleEndian(memory[12..], 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(memory[16..], 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(memory[20..], 0x400);
        }
        return ctx.Memory.TryWrite(output.Address, bytes) ? Ok : ErrorInvalidAddress;
    }

    private static int WriteBatchError(CpuContext ctx, ulong address, int error, ulong jobAddress, uint offset)
    {
        if (address != 0)
        {
            Span<byte> data = stackalloc byte[32];
            data.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(data, error);
            BinaryPrimitives.WriteUInt64LittleEndian(data[8..], jobAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(data[16..], offset);
            if (!ctx.Memory.TryWrite(address, data))
            {
                return ErrorInvalidAddress;
            }
        }
        return error;
    }

    private static void ResetInstance(InstanceState instance)
    {
        instance.TotalDecodedSamples = 0;
        instance.GaplessSkippedSamples = 0;
        instance.At9FrameIndex = 0;
        instance.LastMp3Header = 0;
    }

    private static bool TryGatherInput(CpuContext ctx, IReadOnlyList<BufferRef> inputs, out byte[] data)
    {
        var size = inputs.Aggregate<BufferRef, ulong>(0, (total, buffer) => total + buffer.Size);
        if (size > MaximumBatchSize)
        {
            data = Array.Empty<byte>();
            return false;
        }
        data = new byte[(int)size];
        var offset = 0;
        foreach (var input in inputs)
        {
            if (input.Size != 0 && (input.Address == 0 || !ctx.Memory.TryRead(input.Address, data.AsSpan(offset, (int)input.Size))))
            {
                return false;
            }
            offset += (int)input.Size;
        }
        return true;
    }

    private static bool TryScatter(CpuContext ctx, IReadOnlyList<BufferRef> outputs, ReadOnlySpan<byte> data, bool clearRemainder)
    {
        var offset = 0;
        foreach (var output in outputs)
        {
            var count = Math.Min((int)output.Size, data.Length - offset);
            if (count > 0 && (output.Address == 0 || !ctx.Memory.TryWrite(output.Address, data.Slice(offset, count))))
            {
                return false;
            }
            if (clearRemainder && output.Size > count && !TryClear(ctx, output.Address + (uint)count, output.Size - (uint)count))
            {
                return false;
            }
            offset += count;
            if (offset == data.Length)
            {
                break;
            }
        }
        return offset == data.Length;
    }

    private static bool TryScatterZeroes(CpuContext ctx, IReadOnlyList<BufferRef> outputs, uint size)
    {
        var remaining = size;
        foreach (var output in outputs)
        {
            var count = Math.Min(output.Size, remaining);
            if (count != 0 && (output.Address == 0 || !TryClear(ctx, output.Address, count)))
            {
                return false;
            }
            remaining -= count;
            if (remaining == 0)
            {
                break;
            }
        }
        return remaining == 0;
    }

    private static bool TryClear(CpuContext ctx, ulong address, ulong size)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < size;)
        {
            var count = (int)Math.Min((ulong)zeroes.Length, size - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..count]))
            {
                return false;
            }
            offset += (uint)count;
        }
        return true;
    }

    private static bool TryReserve(byte[] bytes, ref int offset, uint size, out Span<byte> result)
    {
        if ((ulong)offset + size > (ulong)bytes.Length)
        {
            result = default;
            offset = bytes.Length;
            return false;
        }
        result = bytes.AsSpan(offset, (int)size);
        offset += (int)size;
        return true;
    }

    private static void WriteFormat(Span<byte> output, InstanceState instance)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output, instance.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], ChannelMask(instance.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(output[8..], instance.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(output[12..], instance.Encoding);
        BinaryPrimitives.WriteUInt32LittleEndian(output[16..], instance.Bitrate);
    }

    private static void WriteCodecInfo(Span<byte> output, InstanceState instance)
    {
        output.Clear();
        switch (instance.Codec)
        {
            case CodecMp3 when output.Length >= 16:
                BinaryPrimitives.WriteUInt32LittleEndian(output, BinaryPrimitives.ReverseEndianness(instance.LastMp3Header));
                if (instance.LastMp3Header != 0)
                {
                    output[4] = (byte)(((instance.LastMp3Header >> 16) & 1) == 0 ? 1 : 0);
                    output[5] = (byte)((instance.LastMp3Header >> 6) & 3);
                    output[6] = (byte)((instance.LastMp3Header >> 4) & 3);
                    output[7] = (byte)((instance.LastMp3Header >> 3) & 1);
                    output[8] = (byte)((instance.LastMp3Header >> 2) & 1);
                    output[9] = (byte)(instance.LastMp3Header & 3);
                }
                break;
            case CodecAt9 when output.Length >= 16:
                BinaryPrimitives.WriteUInt32LittleEndian(output, instance.SuperframeBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(output[4..], instance.FramesPerSuperframe);
                var consumedInSuperframe = instance.FrameBytes * instance.At9FrameIndex;
                BinaryPrimitives.WriteUInt32LittleEndian(output[8..], instance.SuperframeBytes - consumedInSuperframe);
                BinaryPrimitives.WriteUInt32LittleEndian(output[12..], instance.FrameSamples);
                break;
            case CodecAac when output.Length >= 8:
                BinaryPrimitives.WriteUInt32LittleEndian(output, (instance.Flags & (1UL << 32)) != 0 ? 1u : 0u);
                break;
            case CodecOpus when output.Length >= 4:
                BinaryPrimitives.WriteUInt32LittleEndian(output, 1);
                break;
        }
    }

    private static uint CodecInfoSize(uint codec) => codec switch
    {
        CodecMp3 => 16,
        CodecAt9 => 16,
        CodecAac => 8,
        CodecOpus => 4,
        _ => 0,
    };

    private static bool TryParseAt9Config(
        ReadOnlySpan<byte> data,
        out uint sampleRate,
        out uint channels,
        out uint frameSamples,
        out uint framesPerSuperframe,
        out uint superframeBytes)
    {
        sampleRate = channels = frameSamples = framesPerSuperframe = superframeBytes = 0;
        if (data.Length < 4 || data[0] != 0xFE)
        {
            return false;
        }
        var bits = BinaryPrimitives.ReadUInt32BigEndian(data);
        var sampleRateIndex = (int)((bits >> 20) & 0xF);
        var channelConfigIndex = (int)((bits >> 17) & 7);
        var validationBit = (bits >> 16) & 1;
        if (validationBit != 0 || channelConfigIndex >= At9ChannelCounts.Length)
        {
            return false;
        }
        var frameBytes = ((bits >> 5) & 0x7FF) + 1;
        var superframeIndex = (int)((bits >> 3) & 3);
        sampleRate = At9SampleRates[sampleRateIndex];
        channels = At9ChannelCounts[channelConfigIndex];
        frameSamples = 1u << At9FrameSamplePowers[sampleRateIndex];
        framesPerSuperframe = 1u << superframeIndex;
        superframeBytes = frameBytes * framesPerSuperframe;
        return true;
    }

    private readonly record struct Mp3Info(ulong FrameSize, uint Channels, uint Samples, uint Bitrate, uint SampleRate);

    private static bool TryParseMp3Header(ReadOnlySpan<byte> data, out Mp3Info info)
    {
        info = default;
        if (data.Length < 4)
        {
            return false;
        }
        var header = BinaryPrimitives.ReadUInt32BigEndian(data);
        if ((header & 0xFFE00000) != 0xFFE00000)
        {
            return false;
        }
        var version = (int)((header >> 19) & 3);
        var layer = (int)((header >> 17) & 3);
        var bitrateIndex = (int)((header >> 12) & 0xF);
        var sampleRateIndex = (int)((header >> 10) & 3);
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return false;
        }
        var sampleRate = (uint)Mp3SampleRates[version, sampleRateIndex];
        var bitrateKbps = version == 3 ? Mp3V1Bitrates[bitrateIndex] : Mp3V2Bitrates[bitrateIndex];
        if (sampleRate == 0 || bitrateKbps == 0)
        {
            return false;
        }
        var bitrate = (uint)bitrateKbps * 1000;
        var padding = (header >> 9) & 1;
        var samples = version == 3 ? 1152u : 576u;
        var frameSize = ((version == 3 ? 144UL : 72UL) * bitrate / sampleRate) + padding;
        var channels = ((header >> 6) & 3) == 3 ? 1u : 2u;
        info = new Mp3Info(frameSize, channels, samples, bitrate, sampleRate);
        return frameSize >= 4;
    }

    private static bool TryParseAdts(
        ReadOnlySpan<byte> data,
        out int frameBytes,
        out uint samples,
        out uint sampleRate,
        out uint channels)
    {
        frameBytes = 0;
        samples = sampleRate = channels = 0;
        if (data.Length < 7 || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
        {
            return false;
        }
        var sampleRateIndex = (data[2] >> 2) & 0xF;
        if (sampleRateIndex >= AacSampleRates.Length)
        {
            return false;
        }
        channels = (uint)(((data[2] & 1) << 2) | (data[3] >> 6));
        if (channels == 0)
        {
            channels = 2;
        }
        frameBytes = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        samples = (uint)(data[6] & 3) + 1u;
        samples *= 1024;
        sampleRate = AacSampleRates[sampleRateIndex];
        return frameBytes >= 7;
    }

    private static bool WriteJobHeader(CpuContext ctx, ulong address, uint instanceId, uint size, int ident = ChunkJob)
    {
        var header = ((instanceId & 0xFFFFF) << 6) | (uint)ident;
        return ctx.TryWriteUInt32(address, header) && ctx.TryWriteUInt32(address + 4, size);
    }

    private static bool WriteFlagsChunk(CpuContext ctx, ref ulong cursor, int ident, ulong flags)
    {
        var header = (uint)ident | (((uint)(flags >> 32) & 0xFFFFF) << 6);
        if (!ctx.TryWriteUInt32(cursor, header) || !ctx.TryWriteUInt32(cursor + 4, (uint)flags))
        {
            return false;
        }
        cursor += 8;
        return true;
    }

    private static bool WriteBufferChunk(CpuContext ctx, ref ulong cursor, int ident, uint size, ulong address)
    {
        if (!ctx.TryWriteUInt32(cursor, (uint)ident) ||
            !ctx.TryWriteUInt32(cursor + 4, size) ||
            !ctx.TryWriteUInt64(cursor + 8, address))
        {
            return false;
        }
        cursor += 16;
        return true;
    }

    private static bool CopyBufferArrayToChunks(
        CpuContext ctx,
        ref ulong cursor,
        ulong arrayAddress,
        ulong count,
        int ident)
    {
        if (count != 0 && arrayAddress == 0)
        {
            return false;
        }
        for (ulong index = 0; index < count; index++)
        {
            if (!ctx.TryReadUInt64(arrayAddress + index * 16, out var address) ||
                !ctx.TryReadUInt64(arrayAddress + index * 16 + 8, out var size) ||
                size > uint.MaxValue || !WriteBufferChunk(ctx, ref cursor, ident, (uint)size, address))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadStackArgument(CpuContext ctx, uint index, out ulong value) =>
        ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 8 + index * 8, out value);

    private static bool IsKnownCodec(uint codec) => codec is CodecMp3 or CodecAt9 or CodecAac or CodecLpcm or CodecOpus;

    private static uint BytesPerSample(uint encoding) => encoding == 0 ? 2u : 4u;

    private static int MinimumInputBytes(uint codec) => codec switch
    {
        CodecMp3 => 4,
        CodecAt9 => 1,
        CodecAac => 7,
        CodecOpus => 1,
        _ => 1,
    };

    private static uint ChannelMask(uint channels) => channels switch
    {
        1 => 0x0004,
        2 => 0x0003,
        3 => 0x0007,
        4 => 0x0033,
        5 => 0x0607,
        6 => 0x060F,
        7 => 0x070F,
        8 => 0x063F,
        _ => 0,
    };

    private static readonly uint[] At9SampleRates =
    [
        11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
        44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000,
    ];

    private static readonly byte[] At9FrameSamplePowers = [6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8];
    private static readonly uint[] At9ChannelCounts = [1, 2, 2, 6, 8, 4];
    private static readonly uint[] AacSampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000];
    private static readonly int[,] Mp3SampleRates =
    {
        { 11025, 12000, 8000 },
        { 0, 0, 0 },
        { 22050, 24000, 16000 },
        { 44100, 48000, 32000 },
    };
    private static readonly int[] Mp3V1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mp3V2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
}
