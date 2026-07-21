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
