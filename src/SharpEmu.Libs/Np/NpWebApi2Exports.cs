// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);
    private const int NpWebApi2ErrorInvalidLibContextId = unchecked((int)0x80553403);
    private const int NpWebApi2ErrorUserContextNotFound = unchecked((int)0x80553405);
    private const int NpWebApi2ErrorRequestNotFound = unchecked((int)0x80553406);
    private const int NpWebApi2ErrorNotSignedIn = unchecked((int)0x80553407);
    private const int NpWebApi2ErrorHandleNotFound = unchecked((int)0x8055340D);

    private static int _initialized;
    private static int _lastLibraryContextId;
    private static int _lastPushEventHandleId;
    private static int _lastUserContextId;
    private static long _lastRequestId;
    private static readonly ConcurrentDictionary<int, byte> LibraryContexts = new();
    private static readonly ConcurrentDictionary<int, int> UserContexts = new();
    private static readonly ConcurrentDictionary<int, int> PushEventHandles = new();
    private static readonly ConcurrentDictionary<long, RequestState> Requests = new();

    private sealed record RequestState(int UserContextId);

    // SHARPEMU_NP_FAKE_USERCTX=1: hand sceNpWebApi2CreateUserContext a synthetic
    // valid user-context id instead of NOT_SIGNED_IN. Some titles (Astro Bot's
    // online-init) do NOT treat NOT_SIGNED_IN as terminal and retry the context
    // creation every frame, gating menu progression; a fake success lets that
    // state machine advance. Off by default (keeps the offline-refusal behavior).
    private static readonly bool _fakeUserContext =
        Environment.GetEnvironmentVariable("SHARPEMU_NP_FAKE_USERCTX") == "1";

    // sceNpWebApi2Initialize returns the library context id, not plain success. Handing back 0
    // makes titles carry libCtxId=0 into sceNpWebApi2CreateUserContext, whose invalid-argument
    // failure they treat as a transient bug and retry every frame (observed with ASTRO's
    // online-init loop).
    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        Interlocked.Exchange(ref _initialized, 1);
        var libraryContextId = Interlocked.Increment(ref _lastLibraryContextId);
        LibraryContexts[libraryContextId] = 0;
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsKnownLibraryContext(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidLibContextId);
        }

        var handleId = Interlocked.Increment(ref _lastPushEventHandleId);
        PushEventHandles[handleId] = libraryContextId;
        TraceNpWebApi2("push-event-create-handle", libraryContextId, (ulong)handleId);
        return ctx.SetReturn(handleId);
    }

    // No PSN backend: report the local user as signed out. Unlike invalid-argument, titles treat
    // NOT_SIGNED_IN as a terminal offline condition and stop retrying context creation.
    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceNpWebApi2("create-user-context", libraryContextId, ctx[CpuRegister.Rsi]);
        if (!IsKnownLibraryContext(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidLibContextId);
        }

        if (_fakeUserContext)
        {
            var userContextId = Interlocked.Increment(ref _lastUserContextId);
            UserContexts[userContextId] = libraryContextId;
            TraceNpWebApi2("create-user-context-fake", libraryContextId, (ulong)userContextId);
            return ctx.SetReturn(userContextId);
        }

        return ctx.SetReturn(NpWebApi2ErrorNotSignedIn);
    }

    [SysAbiExport(
        Nid = "9X9+cneTGUU",
        ExportName = "sceNpWebApi2DeleteUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2DeleteUserContext(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!UserContexts.TryRemove(userContextId, out _))
        {
            return ctx.SetReturn(NpWebApi2ErrorUserContextNotFound);
        }

        foreach (var request in Requests.Where(pair => pair.Value.UserContextId == userContextId))
        {
            Requests.TryRemove(request.Key, out _);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "3EI-OSJ65Xc",
        ExportName = "sceNpWebApi2CreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateRequest(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var requestIdAddress = ctx[CpuRegister.R9];
        if (!UserContexts.ContainsKey(userContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorUserContextNotFound);
        }

        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 ||
            ctx[CpuRegister.Rcx] == 0 || requestIdAddress == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var requestId = Interlocked.Increment(ref _lastRequestId);
        Requests[requestId] = new RequestState(userContextId);
        if (!ctx.TryWriteUInt64(requestIdAddress, unchecked((ulong)requestId)))
        {
            Requests.TryRemove(requestId, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "vvzWO-DvG1s",
        ExportName = "sceNpWebApi2DeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2DeleteRequest(CpuContext ctx)
    {
        Requests.TryRemove(unchecked((long)ctx[CpuRegister.Rdi]), out _);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "zpiPsH7dbFQ",
        ExportName = "sceNpWebApi2AbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AbortRequest(CpuContext ctx) =>
        ctx.SetReturn(Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? 0
            : NpWebApi2ErrorRequestNotFound);

    [SysAbiExport(
        Nid = "lQOCF84lvzw",
        ExportName = "sceNpWebApi2SendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2SendRequest(CpuContext ctx)
    {
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        if (!Requests.ContainsKey(requestId))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }

        var responseInfoAddress = ctx[CpuRegister.Rcx];
        if (responseInfoAddress != 0)
        {
            Span<byte> responseInfo = stackalloc byte[32];
            responseInfo.Clear();
            if (!ctx.Memory.TryWrite(responseInfoAddress, responseInfo))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(NpWebApi2ErrorNotSignedIn);
    }

    [SysAbiExport(
        Nid = "OOY9+ObfKec",
        ExportName = "sceNpWebApi2ReadData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2ReadData(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        return ctx.SetReturn(Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? 0
            : NpWebApi2ErrorRequestNotFound);
    }

    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(PushEventHandles.TryGetValue(handleId, out var owner) && owner == libraryContextId &&
                             PushEventHandles.TryRemove(handleId, out _)
            ? 0
            : NpWebApi2ErrorHandleNotFound);
    }

    [SysAbiExport(
        Nid = "1OLgvahaSco",
        ExportName = "sceNpWebApi2PushEventAbortHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventAbortHandle(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(PushEventHandles.TryGetValue(handleId, out var owner) && owner == libraryContextId
            ? 0
            : NpWebApi2ErrorHandleNotFound);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        LibraryContexts.TryRemove(libraryContextId, out _);
        foreach (var user in UserContexts.Where(pair => pair.Value == libraryContextId))
        {
            UserContexts.TryRemove(user.Key, out _);
        }

        if (LibraryContexts.IsEmpty)
        {
            Interlocked.Exchange(ref _initialized, 0);
        }
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static int _lastFilterId;
    private static int _lastCallbackId;
    private static long _lastPushContextId;
    private static readonly ConcurrentDictionary<int, (int LibraryContextId, int HandleId)> PushEventFilters = new();
    private static readonly ConcurrentDictionary<int, int> PushEventCallbacks = new();
    private static readonly ConcurrentDictionary<string, int> PushContexts = new();
    private static readonly ConcurrentDictionary<int, ulong> LibraryPoolSizes = new();

    [SysAbiExport(Nid = "egOOvrnF6mI", ExportName = "sceNpWebApi2AddHttpRequestHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AddHttpRequestHeader(CpuContext ctx) =>
        RequestArgumentCall(ctx, ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0);

    [SysAbiExport(Nid = "Io7kh1LHDoM", ExportName = "sceNpWebApi2AddMultipartPart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AddMultipartPart(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "MgsTa76wlEk", ExportName = "sceNpWebApi2AddWebTraceTag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AddWebTraceTag(CpuContext ctx) =>
        RequestArgumentCall(ctx, ctx[CpuRegister.Rsi] != 0);

    [SysAbiExport(Nid = "3Tt9zL3tkoc", ExportName = "sceNpWebApi2CheckTimeout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CheckTimeout(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "+nz1Vq-NrDA", ExportName = "sceNpWebApi2CreateMultipartRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateMultipartRequest(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.R8];
        if (!UserContexts.ContainsKey(userContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorUserContextNotFound);
        }
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        var requestId = Interlocked.Increment(ref _lastRequestId);
        Requests[requestId] = new RequestState(userContextId);
        if (outputAddress != 0 && !ctx.TryWriteUInt64(outputAddress, unchecked((ulong)requestId)))
        {
            Requests.TryRemove(requestId, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "hksbskNToEA", ExportName = "sceNpWebApi2GetHttpResponseHeaderValue", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2GetHttpResponseHeaderValue(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }
        return ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "HwP3aM+c85c", ExportName = "sceNpWebApi2GetHttpResponseHeaderValueLength", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2GetHttpResponseHeaderValueLength(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], new byte[] { 0 })
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "Xweb+naPZ8Y", ExportName = "sceNpWebApi2GetMemoryPoolStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2GetMemoryPoolStats(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!LibraryContexts.ContainsKey(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidLibContextId);
        }
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(0);
        }
        Span<byte> stats = stackalloc byte[32];
        stats.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            stats,
            LibraryPoolSizes.TryGetValue(libraryContextId, out var poolSize) ? poolSize : 0);
        return ctx.Memory.TryWrite(outputAddress, stats)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "dowMWFgowXY", ExportName = "sceNpWebApi2InitializeForPresence", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeForPresence(CpuContext ctx) => InitializeExtended(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "qmINYLuqzaA", ExportName = "sceNpWebApi2IntCreateRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntCreateRequest(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "zXaFo7euxsQ", ExportName = "sceNpWebApi2IntInitialize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntInitialize(CpuContext ctx) => InitializeFromArguments(ctx, 32);

    [SysAbiExport(Nid = "9KSGFMRnp3k", ExportName = "sceNpWebApi2IntInitialize2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntInitialize2(CpuContext ctx) => InitializeFromArguments(ctx, 40);

    [SysAbiExport(Nid = "2hlBNB96saE", ExportName = "sceNpWebApi2IntPushEventCreateCtxIndFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntPushEventCreateCtxIndFilter(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "MsaFhR+lPE4", ExportName = "sceNpWebApi2PushEventCreateFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.R8] == 0 || ctx[CpuRegister.R9] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!PushEventHandles.TryGetValue(handleId, out var owner) || owner != libraryContextId)
        {
            return ctx.SetReturn(NpWebApi2ErrorHandleNotFound);
        }
        var filterId = Interlocked.Increment(ref _lastFilterId);
        PushEventFilters[filterId] = (libraryContextId, handleId);
        return ctx.SetReturn(filterId);
    }

    [SysAbiExport(Nid = "KJdPcOGmK58", ExportName = "sceNpWebApi2PushEventDeleteFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(PushEventFilters.TryGetValue(filterId, out var filter) &&
                             filter.LibraryContextId == libraryContextId && PushEventFilters.TryRemove(filterId, out _)
            ? 0
            : NpWebApi2ErrorHandleNotFound);
    }

    [SysAbiExport(Nid = "NNVf18SlbT8", ExportName = "sceNpWebApi2PushEventCreatePushContext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreatePushContext(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!UserContexts.ContainsKey(userContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorUserContextNotFound);
        }
        var id = Interlocked.Increment(ref _lastPushContextId);
        var value = $"00000000-0000-0000-0000-{id:000000000000}";
        var bytes = System.Text.Encoding.ASCII.GetBytes(value + '\0');
        if (!ctx.Memory.TryWrite(outputAddress, bytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        PushContexts[value] = userContextId;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "QafxeZM3WK4", ExportName = "sceNpWebApi2PushEventDeletePushContext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeletePushContext(CpuContext ctx) => PushContextCall(ctx, true);

    [SysAbiExport(Nid = "AAj9X+4aGYA", ExportName = "sceNpWebApi2PushEventStartPushContextCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventStartPushContextCallback(CpuContext ctx) => PushContextCall(ctx, false);

    [SysAbiExport(Nid = "fY3QqeNkF8k", ExportName = "sceNpWebApi2PushEventRegisterCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterCallback(CpuContext ctx) => RegisterCallback(ctx);

    [SysAbiExport(Nid = "lxtHJMwBsaU", ExportName = "sceNpWebApi2PushEventRegisterPushContextCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterPushContextCallback(CpuContext ctx) => RegisterCallback(ctx);

    [SysAbiExport(Nid = "hOnIlcGrO6g", ExportName = "sceNpWebApi2PushEventUnregisterCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventUnregisterCallback(CpuContext ctx) => UnregisterCallback(ctx);

    [SysAbiExport(Nid = "PmyrbbJSFz0", ExportName = "sceNpWebApi2PushEventUnregisterPushContextCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventUnregisterPushContextCallback(CpuContext ctx) => UnregisterCallback(ctx);

    [SysAbiExport(Nid = "KWkc6Q3tjXc", ExportName = "sceNpWebApi2PushEventSetHandleTimeout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventSetHandleTimeout(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(PushEventHandles.TryGetValue(handleId, out var owner) && owner == libraryContextId
            ? 0
            : NpWebApi2ErrorHandleNotFound);
    }

    [SysAbiExport(Nid = "NKCwS8+5Fx8", ExportName = "sceNpWebApi2SendMultipartRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2SendMultipartRequest(CpuContext ctx)
    {
        if (!Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }
        if (unchecked((int)ctx[CpuRegister.Rsi]) <= 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        var responseAddress = ctx[CpuRegister.R8];
        if (responseAddress != 0)
        {
            Span<byte> response = stackalloc byte[32];
            response.Clear();
            if (!ctx.Memory.TryWrite(responseAddress, response))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }
        return ctx.SetReturn(NpWebApi2ErrorNotSignedIn);
    }

    [SysAbiExport(Nid = "bltDCAskmfE", ExportName = "sceNpWebApi2SetMultipartContentType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2SetMultipartContentType(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "TjAutbrkr60", ExportName = "sceNpWebApi2SetRequestTimeout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2SetRequestTimeout(CpuContext ctx) =>
        ctx.SetReturn(Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi])) ? 0 : NpWebApi2ErrorRequestNotFound);

    private static int RequestArgumentCall(CpuContext ctx, bool validArguments)
    {
        if (!validArguments)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        return ctx.SetReturn(Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? 0
            : NpWebApi2ErrorRequestNotFound);
    }

    private static int InitializeExtended(CpuContext ctx, int httpContextId, ulong poolSize)
    {
        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        Interlocked.Exchange(ref _initialized, 1);
        var libraryContextId = Interlocked.Increment(ref _lastLibraryContextId);
        LibraryContexts[libraryContextId] = 0;
        LibraryPoolSizes[libraryContextId] = poolSize;
        return ctx.SetReturn(libraryContextId);
    }

    private static int InitializeFromArguments(CpuContext ctx, ulong expectedSize)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0 || !ctx.TryReadInt32(address, out var httpContextId) ||
            !ctx.TryReadUInt64(address + 8, out var poolSize) ||
            !ctx.TryReadUInt64(address + expectedSize - 8, out var size) || size != expectedSize)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        return InitializeExtended(ctx, httpContextId, poolSize);
    }

    private static int RegisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!UserContexts.ContainsKey(userContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorUserContextNotFound);
        }
        if (!PushEventFilters.ContainsKey(filterId))
        {
            return ctx.SetReturn(NpWebApi2ErrorHandleNotFound);
        }
        var callbackId = Interlocked.Increment(ref _lastCallbackId);
        PushEventCallbacks[callbackId] = userContextId;
        return ctx.SetReturn(callbackId);
    }

    private static int UnregisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var callbackId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(PushEventCallbacks.TryGetValue(callbackId, out var owner) && owner == userContextId &&
                             PushEventCallbacks.TryRemove(callbackId, out _)
            ? 0
            : NpWebApi2ErrorHandleNotFound);
    }

    private static int PushContextCall(CpuContext ctx, bool remove)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], 37, out var id))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }
        if (!PushContexts.TryGetValue(id, out var owner) || owner != userContextId)
        {
            return ctx.SetReturn(NpWebApi2ErrorHandleNotFound);
        }
        if (remove)
        {
            PushContexts.TryRemove(id, out _);
        }
        return ctx.SetReturn(0);
    }

    private static bool IsKnownLibraryContext(int libraryContextId) =>
        LibraryContexts.ContainsKey(libraryContextId);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _lastLibraryContextId, 0);
        Interlocked.Exchange(ref _lastPushEventHandleId, 0);
        Interlocked.Exchange(ref _lastUserContextId, 0);
        Interlocked.Exchange(ref _lastRequestId, 0);
        LibraryContexts.Clear();
        UserContexts.Clear();
        PushEventHandles.Clear();
        Requests.Clear();
        Interlocked.Exchange(ref _lastFilterId, 0);
        Interlocked.Exchange(ref _lastCallbackId, 0);
        Interlocked.Exchange(ref _lastPushContextId, 0);
        PushEventFilters.Clear();
        PushEventCallbacks.Clear();
        PushContexts.Clear();
        LibraryPoolSizes.Clear();
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
