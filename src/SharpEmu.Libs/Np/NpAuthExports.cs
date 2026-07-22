// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpAuthExports
{
    private const int RequestIdOffset = 0x10000000;
    private const int RequestLimit = 16;
    private const int ErrorInvalidArgument = unchecked((int)0x80550301);
    private const int ErrorInvalidSize = unchecked((int)0x80550302);
    private const int ErrorAborted = unchecked((int)0x80550304);
    private const int ErrorRequestMax = unchecked((int)0x80550305);
    private const int ErrorRequestNotFound = unchecked((int)0x80550306);
    private const int ErrorInvalidId = unchecked((int)0x80550307);
    private const int ErrorSignedOut = unchecked((int)0x80550006);
    private const int ErrorUserNotFound = unchecked((int)0x80550007);

    private static int _nextRequestIndex;
    private static readonly ConcurrentDictionary<int, RequestState> Requests = new();

    private enum RequestStatus
    {
        Ready,
        Aborted,
        Complete
    }

    private sealed class RequestState(bool asynchronous)
    {
        public bool Asynchronous { get; } = asynchronous;
        public RequestStatus Status { get; set; } = RequestStatus.Ready;
        public int Result { get; set; }
    }

    [SysAbiExport(Nid = "6bwFkosYRQg", ExportName = "sceNpAuthCreateRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateRequest(CpuContext ctx) => CreateRequest(ctx, asynchronous: false);

    [SysAbiExport(Nid = "N+mr7GjTvr8", ExportName = "sceNpAuthCreateAsyncRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateAsyncRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return size == 24 ? CreateRequest(ctx, asynchronous: true) : ctx.SetReturn(ErrorInvalidSize);
    }

    [SysAbiExport(Nid = "KxGkOrQJTqY", ExportName = "sceNpAuthGetAuthorizationCode", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCode(CpuContext ctx) => GetAuthorizationCode(ctx, userVariant: false);
    [SysAbiExport(Nid = "qAUXQ9GdWp8", ExportName = "sceNpAuthGetAuthorizationCodeA", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeA(CpuContext ctx) => GetAuthorizationCode(ctx, userVariant: true);
    [SysAbiExport(Nid = "KI4dHLlTNl0", ExportName = "sceNpAuthGetAuthorizationCodeV3", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeV3(CpuContext ctx) => GetAuthorizationCode(ctx, userVariant: true);

    [SysAbiExport(Nid = "uaB-LoJqHis", ExportName = "sceNpAuthGetIdToken", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdToken(CpuContext ctx) => GetIdToken(ctx, userVariant: false);
    [SysAbiExport(Nid = "CocbHVIKPE8", ExportName = "sceNpAuthGetIdTokenA", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdTokenA(CpuContext ctx) => GetIdToken(ctx, userVariant: true);
    [SysAbiExport(Nid = "RdsFVsgSpZY", ExportName = "sceNpAuthGetIdTokenV3", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdTokenV3(CpuContext ctx) => GetIdToken(ctx, userVariant: true);

    [SysAbiExport(Nid = "cE7wIsqXdZ8", ExportName = "sceNpAuthAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthAbortRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorRequestNotFound);
        }
        if (request.Status != RequestStatus.Complete)
        {
            request.Status = RequestStatus.Aborted;
            request.Result = ErrorAborted;
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "SK-S7daqJSE", ExportName = "sceNpAuthWaitAsync", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthWaitAsync(CpuContext ctx) => GetAsyncResult(ctx);
    [SysAbiExport(Nid = "gjSyfzSsDcE", ExportName = "sceNpAuthPollAsync", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthPollAsync(CpuContext ctx) => GetAsyncResult(ctx);

    [SysAbiExport(Nid = "H8wG9Bk-nPc", ExportName = "sceNpAuthDeleteRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthDeleteRequest(CpuContext ctx) => ctx.SetReturn(
        Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _) ? 0 : ErrorRequestNotFound);

    [SysAbiExport(Nid = "PM3IZCw-7m0", ExportName = "sceNpAuthSetTimeout", Target = Generation.Gen5, LibraryName = "libSceNpAuth")]
    public static int NpAuthSetTimeout(CpuContext ctx) => ctx.SetReturn(0);

    internal static void ResetForTests()
    {
        _nextRequestIndex = 0;
        Requests.Clear();
    }

    private static int CreateRequest(CpuContext ctx, bool asynchronous)
    {
        if (Requests.Count >= RequestLimit)
        {
            return ctx.SetReturn(ErrorRequestMax);
        }
        var requestId = RequestIdOffset + Interlocked.Increment(ref _nextRequestIndex);
        Requests[requestId] = new RequestState(asynchronous);
        return ctx.SetReturn(requestId);
    }

    private static int GetAuthorizationCode(CpuContext ctx, bool userVariant)
    {
        var parameterAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (parameterAddress == 0 || outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (size != 32)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }
        if (!ValidateAuthorizationParameters(ctx, parameterAddress, userVariant))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!TryClear(ctx, outputAddress, 136) ||
            (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteInt32(ctx[CpuRegister.Rcx], 0)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return userVariant ? CompleteOfflineRequest(ctx) : ctx.SetReturn(ErrorUserNotFound);
    }

    private static int GetIdToken(CpuContext ctx, bool userVariant)
    {
        var parameterAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (parameterAddress == 0 || outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (size != 40)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }
        if (!ValidateTokenParameters(ctx, parameterAddress, userVariant))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!TryClear(ctx, outputAddress, 4104))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return userVariant ? CompleteOfflineRequest(ctx) : ctx.SetReturn(ErrorUserNotFound);
    }

    private static int CompleteOfflineRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorRequestNotFound);
        }
        if (request.Status == RequestStatus.Complete)
        {
            request.Result = ErrorInvalidArgument;
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (request.Status == RequestStatus.Aborted)
        {
            return ctx.SetReturn(ErrorAborted);
        }
        request.Status = RequestStatus.Complete;
        request.Result = ErrorSignedOut;
        return ctx.SetReturn(request.Asynchronous ? 0 : ErrorSignedOut);
    }

    private static int GetAsyncResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorRequestNotFound);
        }
        if (!request.Asynchronous || request.Status == RequestStatus.Ready)
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        return ctx.TryWriteInt32(resultAddress, request.Result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool ValidateAuthorizationParameters(CpuContext ctx, ulong address, bool userVariant)
    {
        if (!ctx.TryReadUInt64(address + 8, out var first) ||
            !ctx.TryReadUInt64(address + 16, out var clientId) ||
            !ctx.TryReadUInt64(address + 24, out var scope))
        {
            return false;
        }
        return (userVariant ? unchecked((int)first) != -1 : first != 0) && clientId != 0 && scope != 0;
    }

    private static bool ValidateTokenParameters(CpuContext ctx, ulong address, bool userVariant)
    {
        if (!ctx.TryReadUInt64(address + 8, out var first) ||
            !ctx.TryReadUInt64(address + 16, out var clientId) ||
            !ctx.TryReadUInt64(address + 24, out var clientSecret) ||
            !ctx.TryReadUInt64(address + 32, out var scope))
        {
            return false;
        }
        return (userVariant ? unchecked((int)first) != -1 : first != 0) && clientId != 0 && clientSecret != 0 && scope != 0;
    }

    private static bool TryClear(CpuContext ctx, ulong address, int size) => ctx.Memory.TryWrite(address, new byte[size]);
}
