// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.GameUpdate;

public static class GameUpdateExports
{
    private const int ErrorNotInitialized = unchecked((int)0x80412801);
    private const int ErrorInvalidArgument = unchecked((int)0x80412803);
    private const int ErrorInvalidSize = unchecked((int)0x80412804);
    private const int ErrorRequestNotFound = unchecked((int)0x80412805);
    private const int CheckParamSize = 48;
    private const int CheckResultSize = 48;
    private const int AddcontVersionInfoSize = 48;

    private static readonly object Sync = new();
    private static readonly HashSet<int> Requests = [];
    private static bool _initialized;
    private static int _nextRequestId = 1;

    [SysAbiExport(
        Nid = "YJtKLttI9fM",
        ExportName = "sceGameUpdateInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateInitialize(CpuContext ctx)
    {
        lock (Sync)
        {
            _initialized = true;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "NSH-C-OmoNI",
        ExportName = "sceGameUpdateTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateTerminate(CpuContext ctx)
    {
        lock (Sync)
        {
            _initialized = false;
            Requests.Clear();
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "UvcvKaFvupA",
        ExportName = "sceGameUpdateCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateCreateRequest(CpuContext ctx)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var requestId = _nextRequestId++;
            Requests.Add(requestId);
            return ctx.SetReturn(requestId);
        }
    }

    [SysAbiExport(
        Nid = "LYVV9z8+owM",
        ExportName = "sceGameUpdateCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateCheck(CpuContext ctx)
    {
        var requestId = unchecked((int)(uint)ctx[CpuRegister.Rdi]);
        var paramAddress = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];

        lock (Sync)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            if (!Requests.Contains(requestId))
            {
                return ctx.SetReturn(ErrorRequestNotFound);
            }
        }

        if (paramAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (!ctx.TryReadUInt64(paramAddress, out var paramSize) ||
            !ctx.TryReadUInt64(resultAddress, out var resultSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (paramSize < CheckParamSize || resultSize < CheckResultSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        Span<byte> result = stackalloc byte[CheckResultSize];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result, resultSize);
        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "d1CNGEOaK28",
        ExportName = "sceGameUpdateAbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateAbortRequest(CpuContext ctx) => CompleteRequestOperation(ctx, remove: false);

    [SysAbiExport(
        Nid = "bcCyjHN5sn0",
        ExportName = "sceGameUpdateDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateDeleteRequest(CpuContext ctx) => CompleteRequestOperation(ctx, remove: true);

    [SysAbiExport(
        Nid = "0g0+Oq9xcI0",
        ExportName = "sceGameUpdateGetAddcontLatestVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateGetAddcontLatestVersion(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (Sync)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }
        }

        if (infoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (!ctx.TryReadUInt64(infoAddress, out var infoSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (infoSize < AddcontVersionInfoSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        Span<byte> info = stackalloc byte[AddcontVersionInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, infoSize);
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int CompleteRequestOperation(CpuContext ctx, bool remove)
    {
        var requestId = unchecked((int)(uint)ctx[CpuRegister.Rdi]);
        lock (Sync)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var found = remove ? Requests.Remove(requestId) : Requests.Contains(requestId);
            return ctx.SetReturn(found ? 0 : ErrorRequestNotFound);
        }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _initialized = false;
            _nextRequestId = 1;
            Requests.Clear();
        }
    }
}
