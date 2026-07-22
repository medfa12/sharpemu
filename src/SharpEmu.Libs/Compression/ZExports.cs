// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.IO.Compression;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Compression;

public static class ZExports
{
    private const int ErrorNotFound = unchecked((int)0x81120002);
    private const int ErrorInvalid = unchecked((int)0x81120016);
    private const int ErrorNoSpace = unchecked((int)0x8112001C);
    private const int ErrorTimedOut = unchecked((int)0x81120027);
    private const int ErrorNotInitialized = unchecked((int)0x81120032);
    private const int ErrorAlreadyInitialized = unchecked((int)0x81120033);
    private const int ErrorFatal = unchecked((int)0x811200FF);
    private static readonly ConcurrentDictionary<ulong, InflateResult> Results = new();
    private static readonly ConcurrentQueue<ulong> Completed = new();
    private static long _nextRequestId;
    private static int _initialized;

    private readonly record struct InflateResult(uint Length, int Status);

    [SysAbiExport(Nid = "m1YErdIXCp4", ExportName = "sceZlibInitialize", Target = Generation.Gen5, LibraryName = "libSceZlib")]
    public static int Initialize(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return ctx.SetReturn(ErrorAlreadyInitialized);
        ResetQueues();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "TLar1HULv1Q", ExportName = "sceZlibInflate", Target = Generation.Gen5, LibraryName = "libSceZlib")]
    public static int Inflate(CpuContext ctx)
    {
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(ErrorNotInitialized);
        var sourceAddress = ctx[CpuRegister.Rdi];
        var sourceLength = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationLength = ctx[CpuRegister.Rcx];
        var requestIdAddress = ctx[CpuRegister.R8];
        if (sourceAddress == 0 || sourceLength == 0 || sourceLength > int.MaxValue ||
            destinationAddress == 0 || destinationLength == 0 || destinationLength > 64 * 1024 ||
            destinationLength % 2048 != 0 || requestIdAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalid);
        }

        var source = new byte[(int)sourceLength];
        if (!ctx.Memory.TryRead(sourceAddress, source)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

        var requestId = unchecked((ulong)Interlocked.Increment(ref _nextRequestId));
        uint written = 0;
        var status = 0;
        try
        {
            using var input = new MemoryStream(source, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)destinationLength);
            var buffer = new byte[8192];
            while (true)
            {
                var count = zlib.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                if (output.Length + count > (long)destinationLength)
                {
                    status = ErrorNoSpace;
                    break;
                }
                output.Write(buffer, 0, count);
            }

            if (status == 0)
            {
                var data = output.ToArray();
                written = (uint)data.Length;
                if (!ctx.Memory.TryWrite(destinationAddress, data)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }
        catch (InvalidDataException)
        {
            status = ErrorFatal;
        }

        Results[requestId] = new InflateResult(written, status);
        Completed.Enqueue(requestId);
        return ctx.TryWriteUInt64(requestIdAddress, requestId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "uB8VlDD4e0s", ExportName = "sceZlibWaitForDone", Target = Generation.Gen5, LibraryName = "libSceZlib")]
    public static int WaitForDone(CpuContext ctx)
    {
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(ErrorNotInitialized);
        var output = ctx[CpuRegister.Rdi];
        if (output == 0) return ctx.SetReturn(ErrorInvalid);
        if (!Completed.TryDequeue(out var requestId)) return ctx.SetReturn(ErrorTimedOut);
        return ctx.TryWriteUInt64(output, requestId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "2eDcGHC0YaM", ExportName = "sceZlibGetResult", Target = Generation.Gen5, LibraryName = "libSceZlib")]
    public static int GetResult(CpuContext ctx)
    {
        if (Volatile.Read(ref _initialized) == 0) return ctx.SetReturn(ErrorNotInitialized);
        var requestId = ctx[CpuRegister.Rdi];
        var lengthAddress = ctx[CpuRegister.Rsi];
        var statusAddress = ctx[CpuRegister.Rdx];
        if (lengthAddress == 0 || statusAddress == 0) return ctx.SetReturn(ErrorInvalid);
        if (!Results.TryGetValue(requestId, out var result)) return ctx.SetReturn(ErrorNotFound);
        if (!ctx.TryWriteUInt32(lengthAddress, result.Length) || !ctx.TryWriteInt32(statusAddress, result.Status))
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "6na+Sa-B83w", ExportName = "sceZlibFinalize", Target = Generation.Gen5, LibraryName = "libSceZlib")]
    public static int FinalizeLibrary(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return ctx.SetReturn(ErrorNotInitialized);
        ResetQueues();
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Volatile.Write(ref _initialized, 0);
        ResetQueues();
    }

    private static void ResetQueues()
    {
        Results.Clear();
        while (Completed.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _nextRequestId, 0);
    }
}
