// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApiExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80552902);
    private const int ErrorInvalidLibraryContext = unchecked((int)0x80552903);
    private const int ErrorLibraryContextNotFound = unchecked((int)0x80552904);
    private const int ErrorUserContextNotFound = unchecked((int)0x80552905);
    private const int ErrorRequestNotFound = unchecked((int)0x80552906);
    private const int ErrorNotSignedIn = unchecked((int)0x80552907);
    private const int ErrorAborted = unchecked((int)0x80552909);
    private const int ErrorFilterNotFound = unchecked((int)0x8055291C);
    private const int ErrorCallbackNotFound = unchecked((int)0x8055291D);
    private const int ErrorHandleNotFound = unchecked((int)0x8055290D);

    private static int _nextLibraryContext;
    private static int _nextUserContext;
    private static int _nextHandle;
    private static int _nextFilter;
    private static int _nextCallback;
    private static long _nextRequest;
    private static int _lastError;
    private static readonly ConcurrentDictionary<int, LibraryContext> LibraryContexts = new();
    private static readonly ConcurrentDictionary<int, int> UserContexts = new();
    private static readonly ConcurrentDictionary<int, int> Handles = new();
    private static readonly ConcurrentDictionary<int, int> Filters = new();
    private static readonly ConcurrentDictionary<int, int> Callbacks = new();
    private static readonly ConcurrentDictionary<long, RequestState> Requests = new();

    private sealed record LibraryContext(ulong PoolSize);
    private sealed class RequestState(int userContextId)
    {
        public int UserContextId { get; } = userContextId;
        public bool Aborted { get; set; }
        public int MultipartParts { get; set; }
    }

    [SysAbiExport(Nid = "G3AnLNdRBjE", ExportName = "sceNpWebApiInitialize", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiInitialize(CpuContext ctx) => Initialize(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]);
    [SysAbiExport(Nid = "FkuwsD64zoQ", ExportName = "sceNpWebApiInitializeForPresence", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiInitializeForPresence(CpuContext ctx) => Initialize(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]);
    [SysAbiExport(Nid = "uRsskUhAfnM", ExportName = "sceNpWebApiVshInitialize", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiVshInitialize(CpuContext ctx) => Initialize(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "8Vjplhyyc44", ExportName = "sceNpWebApiIntInitialize", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntInitialize(CpuContext ctx)
    {
        var argsAddress = ctx[CpuRegister.Rdi];
        if (argsAddress == 0 || !ctx.TryReadUInt32(argsAddress, out var httpContextId) ||
            !ctx.TryReadUInt64(argsAddress + 8, out var poolSize) ||
            !ctx.TryReadUInt64(argsAddress + 24, out var structSize))
        {
            return SetError(ctx, ErrorInvalidArgument);
        }

        return structSize == 32
            ? Initialize(ctx, unchecked((int)httpContextId), poolSize)
            : SetError(ctx, ErrorInvalidArgument);
    }

    [SysAbiExport(Nid = "asz3TtIqGF8", ExportName = "sceNpWebApiTerminate", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiTerminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!LibraryContexts.TryRemove(libraryContextId, out _))
        {
            return SetError(ctx, ErrorLibraryContextNotFound);
        }

        foreach (var userContext in UserContexts.Where(pair => pair.Value == libraryContextId))
        {
            DeleteUserContext(userContext.Key);
        }
        foreach (var handle in Handles.Where(pair => pair.Value == libraryContextId))
        {
            Handles.TryRemove(handle.Key, out _);
        }
        foreach (var filter in Filters.Where(pair => pair.Value == libraryContextId))
        {
            Filters.TryRemove(filter.Key, out _);
        }

        return SetError(ctx, 0);
    }

    [SysAbiExport(Nid = "zk6c65xoyO0", ExportName = "sceNpWebApiCreateContextA", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCreateContextA(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (libraryContextId >= 0x8000)
        {
            return SetError(ctx, ErrorInvalidLibraryContext);
        }
        if (userId == -1)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!LibraryContexts.ContainsKey(libraryContextId))
        {
            return SetError(ctx, ErrorLibraryContextNotFound);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContext);
        UserContexts[userContextId] = libraryContextId;
        return SetError(ctx, userContextId);
    }

    [SysAbiExport(Nid = "XUjdsSTTZ3U", ExportName = "sceNpWebApiDeleteContext", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiDeleteContext(CpuContext ctx) => SetError(
        ctx,
        DeleteUserContext(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : ErrorUserContextNotFound);

    [SysAbiExport(Nid = "79M-JqvvGo0", ExportName = "sceNpWebApiCreateHandle", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCreateHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!LibraryContexts.ContainsKey(libraryContextId))
        {
            return SetError(ctx, ErrorLibraryContextNotFound);
        }

        var handleId = Interlocked.Increment(ref _nextHandle);
        Handles[handleId] = libraryContextId;
        return SetError(ctx, handleId);
    }

    [SysAbiExport(Nid = "5Mn7TYwpl30", ExportName = "sceNpWebApiDeleteHandle", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiDeleteHandle(CpuContext ctx) => DeleteHandle(ctx, remove: true);
    [SysAbiExport(Nid = "WKcm4PeyJww", ExportName = "sceNpWebApiAbortHandle", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiAbortHandle(CpuContext ctx) => DeleteHandle(ctx, remove: false);

    [SysAbiExport(Nid = "rdgs5Z1MyFw", ExportName = "sceNpWebApiCreateRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCreateRequest(CpuContext ctx) => CreateRequest(ctx, ctx[CpuRegister.R9]);
    [SysAbiExport(Nid = "KBxgeNpoRIQ", ExportName = "sceNpWebApiCreateMultipartRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCreateMultipartRequest(CpuContext ctx) => CreateRequest(ctx, ctx[CpuRegister.R8]);
    [SysAbiExport(Nid = "N2Jbx4tIaQ4", ExportName = "sceNpWebApiIntCreateRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntCreateRequest(CpuContext ctx)
    {
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var requestIdAddress))
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return CreateRequest(ctx, requestIdAddress);
    }

    [SysAbiExport(Nid = "noQgleu+KLE", ExportName = "sceNpWebApiDeleteRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiDeleteRequest(CpuContext ctx) => SetError(
        ctx,
        Requests.TryRemove(unchecked((long)ctx[CpuRegister.Rdi]), out _) ? 0 : ErrorRequestNotFound);

    [SysAbiExport(Nid = "JzhYTP2fG18", ExportName = "sceNpWebApiAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiAbortRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((long)ctx[CpuRegister.Rdi]), out var request))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }
        request.Aborted = true;
        return SetError(ctx, 0);
    }

    [SysAbiExport(Nid = "joRjtRXTFoc", ExportName = "sceNpWebApiAddHttpRequestHeader", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiAddHttpRequestHeader(CpuContext ctx) => RequestArgumentCall(ctx, ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0);
    [SysAbiExport(Nid = "19KgfJXgM+U", ExportName = "sceNpWebApiAddMultipartPart", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiAddMultipartPart(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!Requests.TryGetValue(unchecked((long)ctx[CpuRegister.Rdi]), out var request))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }

        var index = ++request.MultipartParts;
        var indexAddress = ctx[CpuRegister.Rdx];
        return indexAddress == 0 || ctx.TryWriteInt32(indexAddress, index)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "i0dr6grIZyc", ExportName = "sceNpWebApiSetMultipartContentType", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSetMultipartContentType(CpuContext ctx) => RequestArgumentCall(ctx, ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0);
    [SysAbiExport(Nid = "qWcbJkBj1Lg", ExportName = "sceNpWebApiSetRequestTimeout", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSetRequestTimeout(CpuContext ctx) => RequestArgumentCall(ctx, ctx[CpuRegister.Rsi] != 0);

    [SysAbiExport(Nid = "kVbL4hL3K7w", ExportName = "sceNpWebApiSendRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSendRequest(CpuContext ctx) => SendRequest(ctx, 0);
    [SysAbiExport(Nid = "KjNeZ-29ysQ", ExportName = "sceNpWebApiSendRequest2", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSendRequest2(CpuContext ctx) => SendRequest(ctx, ctx[CpuRegister.Rcx]);
    [SysAbiExport(Nid = "KCItz6QkeGs", ExportName = "sceNpWebApiSendMultipartRequest", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSendMultipartRequest(CpuContext ctx) => SendMultipartRequest(ctx, 0);
    [SysAbiExport(Nid = "DsPOTEvSe7M", ExportName = "sceNpWebApiSendMultipartRequest2", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSendMultipartRequest2(CpuContext ctx) => SendMultipartRequest(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(Nid = "CQtPRSF6Ds8", ExportName = "sceNpWebApiReadData", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiReadData(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? SetError(ctx, 0)
            : SetError(ctx, ErrorRequestNotFound);
    }

    [SysAbiExport(Nid = "k210oKgP80Y", ExportName = "sceNpWebApiGetHttpStatusCode", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetHttpStatusCode(CpuContext ctx) => WriteRequestInt32(ctx, ctx[CpuRegister.Rsi], 0);
    [SysAbiExport(Nid = "743ZzEBzlV8", ExportName = "sceNpWebApiGetHttpResponseHeaderValueLength", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetHttpResponseHeaderValueLength(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return WriteRequestUInt64(ctx, ctx[CpuRegister.Rdx], 0);
    }
    [SysAbiExport(Nid = "VwJ5L0Higg0", ExportName = "sceNpWebApiGetHttpResponseHeaderValue", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetHttpResponseHeaderValue(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }
        Span<byte> terminator = stackalloc byte[1];
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], terminator)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "UJ8H+7kVQUE", ExportName = "sceNpWebApiGetConnectionStats", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetConnectionStats(CpuContext ctx) => ClearStructure(ctx, ctx[CpuRegister.Rdx], 24);
    [SysAbiExport(Nid = "3OnubUs02UM", ExportName = "sceNpWebApiGetMemoryPoolStats", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetMemoryPoolStats(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var statsAddress = ctx[CpuRegister.Rsi];
        if (!LibraryContexts.TryGetValue(libraryContextId, out var libraryContext))
        {
            return SetError(ctx, ErrorLibraryContextNotFound);
        }
        if (statsAddress == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        Span<byte> stats = stackalloc byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(stats, libraryContext.PoolSize);
        return ctx.Memory.TryWrite(statsAddress, stats)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "2qSZ0DgwTsc", ExportName = "sceNpWebApiGetErrorCode", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiGetErrorCode(CpuContext ctx) => ctx.SetReturn(Volatile.Read(ref _lastError));
    [SysAbiExport(Nid = "gVNNyxf-1Sg", ExportName = "sceNpWebApiCheckTimeout", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCheckTimeout(CpuContext ctx) => SetError(ctx, 0);
    [SysAbiExport(Nid = "KQIkDGf80PQ", ExportName = "sceNpWebApiClearAllUnusedConnection", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiClearAllUnusedConnection(CpuContext ctx) => SetError(ctx, 0);
    [SysAbiExport(Nid = "f-pgaNSd1zc", ExportName = "sceNpWebApiClearUnusedConnection", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiClearUnusedConnection(CpuContext ctx) => SetError(ctx, 0);
    [SysAbiExport(Nid = "gRiilVCvfAI", ExportName = "sceNpWebApiSetMaxConnection", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSetMaxConnection(CpuContext ctx) => SetError(ctx, 0);
    [SysAbiExport(Nid = "6g6q-g1i4XU", ExportName = "sceNpWebApiSetHandleTimeout", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiSetHandleTimeout(CpuContext ctx) => ValidateHandle(ctx);

    [SysAbiExport(Nid = "M2BUB+DNEGE", ExportName = "sceNpWebApiCreateExtdPushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiCreateExtdPushEventFilter(CpuContext ctx) => CreateFilter(ctx, ctx[CpuRegister.R8] != 0 && ctx[CpuRegister.R9] != 0);
    [SysAbiExport(Nid = "c1pKoztonB8", ExportName = "sceNpWebApiIntCreateCtxIndExtdPushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntCreateCtxIndExtdPushEventFilter(CpuContext ctx) => CreateFilter(ctx, ctx[CpuRegister.Rdx] != 0 && ctx[CpuRegister.Rcx] != 0);
    [SysAbiExport(Nid = "TZSep4xB4EY", ExportName = "sceNpWebApiIntCreateServicePushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntCreateServicePushEventFilter(CpuContext ctx) => CreateFilter(ctx, ctx[CpuRegister.R8] != 0 && ctx[CpuRegister.R9] != 0);
    [SysAbiExport(Nid = "pfaJtb7SQ80", ExportName = "sceNpWebApiDeleteExtdPushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiDeleteExtdPushEventFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        return Filters.TryGetValue(filterId, out var owner) && owner == libraryContextId && Filters.TryRemove(filterId, out _)
            ? SetError(ctx, 0)
            : SetError(ctx, ErrorFilterNotFound);
    }

    [SysAbiExport(Nid = "jhXKGQJ4egI", ExportName = "sceNpWebApiRegisterExtdPushEventCallbackA", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiRegisterExtdPushEventCallbackA(CpuContext ctx) => RegisterCallback(ctx);
    [SysAbiExport(Nid = "VjVukb2EWPc", ExportName = "sceNpWebApiIntRegisterServicePushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntRegisterServicePushEventCallback(CpuContext ctx) => RegisterCallback(ctx);
    [SysAbiExport(Nid = "sfq23ZVHVEw", ExportName = "sceNpWebApiIntRegisterServicePushEventCallbackA", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiIntRegisterServicePushEventCallbackA(CpuContext ctx) => RegisterCallback(ctx);
    [SysAbiExport(Nid = "PqCY25FMzPs", ExportName = "sceNpWebApiUnregisterExtdPushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiUnregisterExtdPushEventCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var callbackId = unchecked((int)ctx[CpuRegister.Rsi]);
        return Callbacks.TryGetValue(callbackId, out var owner) && owner == userContextId && Callbacks.TryRemove(callbackId, out _)
            ? SetError(ctx, 0)
            : SetError(ctx, ErrorCallbackNotFound);
    }

    [SysAbiExport(Nid = "or0e885BlXo", ExportName = "sceNpWebApiUtilityParseNpId", Target = Generation.Gen5, LibraryName = "libSceNpWebApi")]
    public static int NpWebApiUtilityParseNpId(CpuContext ctx)
    {
        var encodedAddress = ctx[CpuRegister.Rdi];
        var npIdAddress = ctx[CpuRegister.Rsi];
        if (encodedAddress == 0 || !ctx.TryReadNullTerminatedUtf8(encodedAddress, 4096, out var encoded))
        {
            return SetError(ctx, ErrorInvalidArgument);
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }

        var text = Encoding.UTF8.GetString(decoded);
        var separator = text.IndexOf('@');
        var handle = separator < 0 ? text : text[..separator];
        var option = separator < 0 ? string.Empty : text[(separator + 1)..].Replace("/", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal);
        Span<byte> npId = stackalloc byte[36];
        Encoding.UTF8.GetBytes(handle.AsSpan(0, Math.Min(handle.Length, 16)), npId[..16]);
        Encoding.UTF8.GetBytes(option.AsSpan(0, Math.Min(option.Length, 8)), npId.Slice(20, 8));
        npId[28] = 1;
        return npIdAddress == 0 || ctx.Memory.TryWrite(npIdAddress, npId)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    internal static void ResetForTests()
    {
        _nextLibraryContext = 0;
        _nextUserContext = 0;
        _nextHandle = 0;
        _nextFilter = 0;
        _nextCallback = 0;
        _nextRequest = 0;
        _lastError = 0;
        LibraryContexts.Clear();
        UserContexts.Clear();
        Handles.Clear();
        Filters.Clear();
        Callbacks.Clear();
        Requests.Clear();
    }

    private static int Initialize(CpuContext ctx, int httpContextId, ulong poolSize)
    {
        if (httpContextId <= 0 || poolSize == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        var libraryContextId = Interlocked.Increment(ref _nextLibraryContext);
        LibraryContexts[libraryContextId] = new LibraryContext(poolSize);
        return SetError(ctx, libraryContextId);
    }

    private static int CreateRequest(CpuContext ctx, ulong requestIdAddress)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!UserContexts.ContainsKey(userContextId))
        {
            return SetError(ctx, ErrorUserContextNotFound);
        }
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || requestIdAddress == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }

        var requestId = Interlocked.Increment(ref _nextRequest);
        Requests[requestId] = new RequestState(userContextId);
        if (!ctx.TryWriteUInt64(requestIdAddress, unchecked((ulong)requestId)))
        {
            Requests.TryRemove(requestId, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return SetError(ctx, 0);
    }

    private static int SendRequest(CpuContext ctx, ulong responseInfoAddress)
    {
        if (!Requests.TryGetValue(unchecked((long)ctx[CpuRegister.Rdi]), out var request))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }
        if (responseInfoAddress != 0 && !TryClear(ctx, responseInfoAddress, 32))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return SetError(ctx, request.Aborted ? ErrorAborted : ErrorNotSignedIn);
    }

    private static int SendMultipartRequest(CpuContext ctx, ulong responseInfoAddress)
    {
        if (unchecked((int)ctx[CpuRegister.Rsi]) <= 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return SendRequest(ctx, responseInfoAddress);
    }

    private static int RequestArgumentCall(CpuContext ctx, bool validArguments)
    {
        if (!validArguments)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? SetError(ctx, 0)
            : SetError(ctx, ErrorRequestNotFound);
    }

    private static int WriteRequestInt32(CpuContext ctx, ulong address, int value)
    {
        if (address == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }
        return ctx.TryWriteInt32(address, value)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteRequestUInt64(CpuContext ctx, ulong address, ulong value)
    {
        if (address == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return SetError(ctx, ErrorRequestNotFound);
        }
        return ctx.TryWriteUInt64(address, value)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ClearStructure(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        return TryClear(ctx, address, size)
            ? SetError(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int CreateFilter(CpuContext ctx, bool validArguments)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!validArguments)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!LibraryContexts.ContainsKey(libraryContextId))
        {
            return SetError(ctx, ErrorLibraryContextNotFound);
        }
        if (!Handles.TryGetValue(handleId, out var owner) || owner != libraryContextId)
        {
            return SetError(ctx, ErrorHandleNotFound);
        }
        var filterId = Interlocked.Increment(ref _nextFilter);
        Filters[filterId] = libraryContextId;
        return SetError(ctx, filterId);
    }

    private static int RegisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return SetError(ctx, ErrorInvalidArgument);
        }
        if (!UserContexts.TryGetValue(userContextId, out var libraryContextId))
        {
            return SetError(ctx, ErrorUserContextNotFound);
        }
        if (!Filters.TryGetValue(filterId, out var filterOwner) || filterOwner != libraryContextId)
        {
            return SetError(ctx, ErrorFilterNotFound);
        }
        var callbackId = Interlocked.Increment(ref _nextCallback);
        Callbacks[callbackId] = userContextId;
        return SetError(ctx, callbackId);
    }

    private static int ValidateHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        return Handles.TryGetValue(handleId, out var owner) && owner == libraryContextId
            ? SetError(ctx, 0)
            : SetError(ctx, ErrorHandleNotFound);
    }

    private static int DeleteHandle(CpuContext ctx, bool remove)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!Handles.TryGetValue(handleId, out var owner) || owner != libraryContextId)
        {
            return SetError(ctx, ErrorHandleNotFound);
        }
        if (remove)
        {
            Handles.TryRemove(handleId, out _);
        }
        return SetError(ctx, 0);
    }

    private static bool DeleteUserContext(int userContextId)
    {
        if (!UserContexts.TryRemove(userContextId, out _))
        {
            return false;
        }
        foreach (var request in Requests.Where(pair => pair.Value.UserContextId == userContextId))
        {
            Requests.TryRemove(request.Key, out _);
        }
        foreach (var callback in Callbacks.Where(pair => pair.Value == userContextId))
        {
            Callbacks.TryRemove(callback.Key, out _);
        }
        return true;
    }

    private static bool TryClear(CpuContext ctx, ulong address, int size)
    {
        var bytes = new byte[size];
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetError(CpuContext ctx, int result)
    {
        Volatile.Write(ref _lastError, result < 0 ? result : 0);
        return ctx.SetReturn(result);
    }
}
