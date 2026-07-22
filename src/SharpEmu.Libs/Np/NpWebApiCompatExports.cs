// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApiCompatExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80552902);
    private const int ErrorInvalidLibraryContext = unchecked((int)0x80552903);
    private const int ErrorFilterNotFound = unchecked((int)0x8055291C);
    private const int ErrorCallbackNotFound = unchecked((int)0x8055291D);
    private static int _nextUserContext;
    private static int _nextFilter;
    private static int _nextCallback;
    private static readonly ConcurrentDictionary<int, int> UserContexts = new();
    private static readonly ConcurrentDictionary<int, int> Filters = new();
    private static readonly ConcurrentDictionary<int, int> Callbacks = new();

    [SysAbiExport(Nid = "x1Y7yiYSk7c", ExportName = "sceNpWebApiCreateContext", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiCreateContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (libraryContextId is <= 0 or >= 0x8000)
        {
            return ctx.SetReturn(ErrorInvalidLibraryContext);
        }
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var userContextId = Interlocked.Increment(ref _nextUserContext);
        UserContexts[userContextId] = libraryContextId;
        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(Nid = "y5Ta5JCzQHY", ExportName = "sceNpWebApiCreatePushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiCreatePushEventFilter(CpuContext ctx) => CreateFilter(
        ctx,
        unchecked((int)ctx[CpuRegister.Rdi]),
        ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0);

    [SysAbiExport(Nid = "sIFx734+xys", ExportName = "sceNpWebApiCreateServicePushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiCreateServicePushEventFilter(CpuContext ctx) => CreateFilter(
        ctx,
        unchecked((int)ctx[CpuRegister.Rdi]),
        ctx[CpuRegister.Rdx] != 0 && ctx[CpuRegister.R8] != 0 && ctx[CpuRegister.R9] != 0);

    [SysAbiExport(Nid = "zE+R6Rcx3W0", ExportName = "sceNpWebApiDeletePushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiDeletePushEventFilter(CpuContext ctx) => DeleteFilter(ctx);

    [SysAbiExport(Nid = "PfQ+f6ws764", ExportName = "sceNpWebApiDeleteServicePushEventFilter", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiDeleteServicePushEventFilter(CpuContext ctx) => DeleteFilter(ctx);

    [SysAbiExport(Nid = "vrM02A5Gy1M", ExportName = "sceNpWebApiRegisterExtdPushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiRegisterExtdPushEventCallback(CpuContext ctx) => RegisterFilteredCallback(ctx);

    [SysAbiExport(Nid = "PfSTDCgNMgc", ExportName = "sceNpWebApiRegisterPushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiRegisterPushEventCallback(CpuContext ctx) => RegisterFilteredCallback(ctx);

    [SysAbiExport(Nid = "kJQJE0uKm5w", ExportName = "sceNpWebApiRegisterServicePushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiRegisterServicePushEventCallback(CpuContext ctx) => RegisterFilteredCallback(ctx);

    [SysAbiExport(Nid = "HVgWmGIOKdk", ExportName = "sceNpWebApiRegisterNotificationCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiRegisterNotificationCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        return RegisterCallback(ctx, userContextId);
    }

    [SysAbiExport(Nid = "wjYEvo4xbcA", ExportName = "sceNpWebApiUnregisterNotificationCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiUnregisterNotificationCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        foreach (var callback in Callbacks.Where(pair => pair.Value == userContextId))
        {
            Callbacks.TryRemove(callback.Key, out _);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "qK4o2656W4w", ExportName = "sceNpWebApiUnregisterPushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiUnregisterPushEventCallback(CpuContext ctx) => UnregisterCallback(ctx);

    [SysAbiExport(Nid = "2edrkr0c-wg", ExportName = "sceNpWebApiUnregisterServicePushEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpWebApiCompat")]
    public static int NpWebApiUnregisterServicePushEventCallback(CpuContext ctx) => UnregisterCallback(ctx);

    private static int CreateFilter(CpuContext ctx, int libraryContextId, bool validArguments)
    {
        if (libraryContextId is <= 0 or >= 0x8000)
        {
            return ctx.SetReturn(ErrorInvalidLibraryContext);
        }
        if (!validArguments)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var filterId = Interlocked.Increment(ref _nextFilter);
        Filters[filterId] = libraryContextId;
        return ctx.SetReturn(filterId);
    }

    private static int DeleteFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(Filters.TryGetValue(filterId, out var owner) && owner == libraryContextId &&
                             Filters.TryRemove(filterId, out _)
            ? 0
            : ErrorFilterNotFound);
    }

    private static int RegisterFilteredCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!Filters.ContainsKey(filterId))
        {
            return ctx.SetReturn(ErrorFilterNotFound);
        }
        return RegisterCallback(ctx, userContextId);
    }

    private static int RegisterCallback(CpuContext ctx, int userContextId)
    {
        var callbackId = Interlocked.Increment(ref _nextCallback);
        Callbacks[callbackId] = userContextId;
        return ctx.SetReturn(callbackId);
    }

    private static int UnregisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var callbackId = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(Callbacks.TryGetValue(callbackId, out var owner) && owner == userContextId &&
                             Callbacks.TryRemove(callbackId, out _)
            ? 0
            : ErrorCallbackNotFound);
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _nextUserContext, 0);
        Interlocked.Exchange(ref _nextFilter, 0);
        Interlocked.Exchange(ref _nextCallback, 0);
        UserContexts.Clear();
        Filters.Clear();
        Callbacks.Clear();
    }
}
