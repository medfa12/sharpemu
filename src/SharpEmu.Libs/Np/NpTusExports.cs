// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

internal static class NpTusState
{
    internal const int DataStatusSize = 0x1F0;
    internal const int DataStatusASize = 0x210;
    internal const int TssDataStatusSize = 0x18;
    internal const int VariableSize = 0x80;
    internal const int InvalidArgument = unchecked((int)0x80550704);
    internal const int TooManyObjects = unchecked((int)0x80550706);
    internal const int Aborted = unchecked((int)0x80550707);
    internal const int InsufficientArgument = unchecked((int)0x8055070C);
    internal const int InvalidId = unchecked((int)0x8055070E);
    internal const int InvalidAlignment = unchecked((int)0x80550714);
    internal const int TooManySlotIds = unchecked((int)0x80550718);

    private const int MaxContexts = 32;
    private const int MaxRequestsPerContext = 32;
    private const ulong MaximumClearSize = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, ContextState> Contexts = [];

    private sealed class ContextState
    {
        internal readonly Dictionary<int, RequestState> Requests = [];
    }

    private sealed class RequestState
    {
        internal bool Started;
        internal int Result;
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Contexts.Clear();
        }
    }

    internal static int CreateNpTitleContext(CpuContext ctx, bool accountBased)
    {
        var serviceLabel = unchecked((uint)ctx[CpuRegister.Rdi]);
        var identity = ctx[CpuRegister.Rsi];
        if (serviceLabel == uint.MaxValue)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (!accountBased && identity == 0)
        {
            return ctx.SetReturn(InsufficientArgument);
        }

        lock (Gate)
        {
            for (var id = 1; id <= MaxContexts; id++)
            {
                if (Contexts.ContainsKey(id))
                {
                    continue;
                }

                Contexts[id] = new ContextState();
                return ctx.SetReturn(id);
            }
        }

        return ctx.SetReturn(TooManyObjects);
    }

    internal static int CreateTitleContext(CpuContext ctx) => CreateNpTitleContext(ctx, accountBased: true);

    internal static int DeleteTitleContext(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            return ctx.SetReturn(Contexts.Remove(contextId) ? 0 : InvalidId);
        }
    }

    internal static int CreateRequest(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            if (!Contexts.TryGetValue(contextId, out var context))
            {
                return ctx.SetReturn(InvalidId);
            }

            for (var requestId = 1; requestId <= MaxRequestsPerContext; requestId++)
            {
                if (context.Requests.ContainsKey(requestId))
                {
                    continue;
                }

                context.Requests[requestId] = new RequestState();
                return ctx.SetReturn((contextId << 16) | requestId);
            }
        }

        return ctx.SetReturn(TooManyObjects);
    }

    internal static int DeleteRequest(CpuContext ctx)
    {
        var packedId = unchecked((int)ctx[CpuRegister.Rdi]);
        var contextId = packedId >> 16;
        var requestId = packedId & 0xFFFF;
        lock (Gate)
        {
            if (!Contexts.TryGetValue(contextId, out var context) || !context.Requests.Remove(requestId))
            {
                return ctx.SetReturn(InvalidId);
            }
        }

        return ctx.SetReturn(0);
    }

    internal static int AbortRequest(CpuContext ctx)
    {
        lock (Gate)
        {
            if (!TryGetRequestLocked(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(InvalidId);
            }

            request.Started = true;
            request.Result = Aborted;
        }

        return ctx.SetReturn(0);
    }

    internal static int Poll(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        lock (Gate)
        {
            if (!TryGetRequestLocked(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(InvalidId);
            }

            if (!request.Started)
            {
                return ctx.SetReturn(1);
            }

            if (resultAddress != 0 && !ctx.TryWriteInt32(resultAddress, request.Result))
            {
                return ctx.SetReturn(InvalidArgument);
            }
        }

        return ctx.SetReturn(0);
    }

    internal static int Wait(CpuContext ctx) => Poll(ctx);

    internal static int Complete(CpuContext ctx)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        return ctx.SetReturn(0);
    }

    internal static int CompleteSingleVariable(CpuContext ctx, bool tryAndSet = false)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        var outputAddress = ctx[tryAndSet ? CpuRegister.R9 : CpuRegister.R8];
        return ClearOptional(ctx, outputAddress, VariableSize);
    }

    internal static int CompleteVariableArray(CpuContext ctx)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        return ClearOptional(ctx, ctx[CpuRegister.Rcx], ctx[CpuRegister.R8]);
    }

    internal static int CompleteStatusArray(CpuContext ctx)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        return ClearOptional(ctx, ctx[CpuRegister.Rcx], ctx[CpuRegister.R8]);
    }

    internal static int CompleteFriendsArray(CpuContext ctx)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        return ClearOptional(ctx, ctx[CpuRegister.R8], ctx[CpuRegister.R9]);
    }

    internal static int GetData(CpuContext ctx, int expectedStatusSize)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        var statusAddress = ctx[CpuRegister.Rcx];
        var statusSize = ctx[CpuRegister.R8];
        if (statusAddress != 0 && statusSize != (ulong)expectedStatusSize)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (!ClearMemory(ctx, statusAddress, statusAddress == 0 ? 0 : statusSize))
        {
            return ctx.SetReturn(InvalidArgument);
        }

        var dataAddress = ctx[CpuRegister.R9];
        var dataSize = StackArgument(ctx, 0);
        return ClearOptional(ctx, dataAddress, dataSize);
    }

    internal static int GetTssData(CpuContext ctx)
    {
        var slotId = unchecked((int)ctx[CpuRegister.Rsi]);
        var statusAddress = ctx[CpuRegister.Rdx];
        var statusSize = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dataSize = ctx[CpuRegister.R9];
        var optionAddress = StackArgument(ctx, 0);

        if (slotId is < 0 or > 15)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (statusAddress != 0 && statusSize != TssDataStatusSize)
        {
            return ctx.SetReturn(InvalidAlignment);
        }

        if (optionAddress != 0 &&
            (!ctx.TryReadUInt64(optionAddress, out var optionSize) || optionSize != 0x20))
        {
            return ctx.SetReturn(InvalidAlignment);
        }

        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        if (!ClearMemory(ctx, statusAddress, statusAddress == 0 ? 0UL : TssDataStatusSize) ||
            !ClearMemory(ctx, dataAddress, dataAddress == 0 ? 0 : dataSize))
        {
            return ctx.SetReturn(InvalidArgument);
        }

        return ctx.SetReturn(0);
    }

    internal static int GetTssStorage(CpuContext ctx)
    {
        if (!StartRequest(ctx, 0))
        {
            return ctx.SetReturn(InvalidId);
        }

        return ClearOptional(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
    }

    internal static int SetMultiSlotVariable(CpuContext ctx)
    {
        var slotIds = ctx[CpuRegister.Rdx];
        var variables = ctx[CpuRegister.Rcx];
        var arrayLength = unchecked((int)ctx[CpuRegister.R8]);
        var option = ctx[CpuRegister.R9];
        if (slotIds == 0 || variables == 0)
        {
            return ctx.SetReturn(InsufficientArgument);
        }

        if (arrayLength < 1 || option != 0)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (arrayLength > 64)
        {
            return ctx.SetReturn(TooManySlotIds);
        }

        for (var index = 0; index < arrayLength; index++)
        {
            if (!ctx.TryReadInt32(slotIds + (ulong)(index * sizeof(int)), out var slotId) || slotId < 0)
            {
                return ctx.SetReturn(InvalidArgument);
            }
        }

        return Complete(ctx);
    }

    internal static int GetMultiSlotDataStatusA(CpuContext ctx)
    {
        var slotIds = ctx[CpuRegister.Rdx];
        var statuses = ctx[CpuRegister.Rcx];
        var statusesSize = ctx[CpuRegister.R8];
        var arrayLength = unchecked((int)ctx[CpuRegister.R9]);
        var option = StackArgument(ctx, 0);
        if (slotIds == 0 || statuses == 0)
        {
            return ctx.SetReturn(InsufficientArgument);
        }

        if (arrayLength < 1 || option != 0)
        {
            return ctx.SetReturn(InvalidArgument);
        }

        if (arrayLength > 64)
        {
            return ctx.SetReturn(TooManySlotIds);
        }

        if (statusesSize != (ulong)arrayLength * DataStatusASize)
        {
            return ctx.SetReturn(InvalidAlignment);
        }

        for (var index = 0; index < arrayLength; index++)
        {
            if (!ctx.TryReadInt32(slotIds + (ulong)(index * sizeof(int)), out var slotId) || slotId < 0)
            {
                return ctx.SetReturn(InvalidArgument);
            }
        }

        return CompleteStatusArray(ctx);
    }

    internal static int Success(CpuContext ctx) => ctx.SetReturn(0);

    private static int ClearOptional(CpuContext ctx, ulong address, ulong size)
    {
        return ClearMemory(ctx, address, address == 0 ? 0 : size)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(InvalidArgument);
    }

    private static bool ClearMemory(CpuContext ctx, ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return true;
        }

        if (size > MaximumClearSize)
        {
            return false;
        }

        Span<byte> zeroes = stackalloc byte[256];
        zeroes.Clear();
        for (ulong offset = 0; offset < size; offset += (ulong)zeroes.Length)
        {
            var count = (int)Math.Min((ulong)zeroes.Length, size - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..count]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartRequest(CpuContext ctx, int result)
    {
        lock (Gate)
        {
            if (!TryGetRequestLocked(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return false;
            }

            request.Started = true;
            request.Result = result;
            return true;
        }
    }

    private static bool TryGetRequestLocked(int packedId, out RequestState request)
    {
        var contextId = packedId >> 16;
        var requestId = packedId & 0xFFFF;
        if (contextId > 0 && requestId > 0 &&
            Contexts.TryGetValue(contextId, out var context) &&
            context.Requests.TryGetValue(requestId, out var found))
        {
            request = found;
            return true;
        }

        request = null!;
        return false;
    }

    private static ulong StackArgument(CpuContext ctx, int index)
    {
        var stackPointer = ctx[CpuRegister.Rsp];
        return stackPointer != 0 &&
               ctx.TryReadUInt64(stackPointer + sizeof(ulong) + (ulong)(index * sizeof(ulong)), out var value)
            ? value
            : 0;
    }
}

public static class NpTusExports
{
    internal static void ResetForTests() => NpTusState.ResetForTests();

    [SysAbiExport(Nid = "lBtrk+7lk14", ExportName = "sceNpTssCreateNpTitleCtxA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssCreateNpTitleCtxA(CpuContext ctx) => NpTusState.CreateNpTitleContext(ctx, accountBased: true);
    [SysAbiExport(Nid = "-SUR+UoLS6c", ExportName = "sceNpTssGetData", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetData(CpuContext ctx) => NpTusState.GetTssData(ctx);
    [SysAbiExport(Nid = "DS2yu3Sjj1o", ExportName = "sceNpTssGetDataAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetDataAsync(CpuContext ctx) => NpTusState.GetTssData(ctx);
    [SysAbiExport(Nid = "lL+Z3zCKNTs", ExportName = "sceNpTssGetSmallStorage", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetSmallStorage(CpuContext ctx) => NpTusState.GetTssStorage(ctx);
    [SysAbiExport(Nid = "f2Pe4LGS2II", ExportName = "sceNpTssGetSmallStorageAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetSmallStorageAsync(CpuContext ctx) => NpTusState.GetTssStorage(ctx);
    [SysAbiExport(Nid = "IVSbAEOxJ6I", ExportName = "sceNpTssGetStorage", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetStorage(CpuContext ctx) => NpTusState.GetTssStorage(ctx);
    [SysAbiExport(Nid = "k5NZIzggbuk", ExportName = "sceNpTssGetStorageAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTssGetStorageAsync(CpuContext ctx) => NpTusState.GetTssStorage(ctx);
    [SysAbiExport(Nid = "2eq1bMwgZYo", ExportName = "sceNpTusAbortRequest", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAbortRequest(CpuContext ctx) => NpTusState.AbortRequest(ctx);

    [SysAbiExport(Nid = "wPFah4-5Xec", ExportName = "sceNpTusAddAndGetVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableA(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "2dB427dT3Iw", ExportName = "sceNpTusAddAndGetVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableAAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "Nt1runsPVJc", ExportName = "sceNpTusAddAndGetVariableAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableAVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "GjlEgLCh4DY", ExportName = "sceNpTusAddAndGetVariableAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableAVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "EPeq43CQKxY", ExportName = "sceNpTusAddAndGetVariableForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableForCrossSave(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "mXZi1D2xwZE", ExportName = "sceNpTusAddAndGetVariableForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "4VLlu7EIjzk", ExportName = "sceNpTusAddAndGetVariableForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "6Lu9geO5TiA", ExportName = "sceNpTusAddAndGetVariableForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "wjNhItL2wzg", ExportName = "sceNpTusChangeModeForOtherSaveDataOwners", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusChangeModeForOtherSaveDataOwners(CpuContext ctx) => NpTusState.Success(ctx);

    [SysAbiExport(Nid = "1n-dGukBgnY", ExportName = "sceNpTusCreateNpTitleCtxA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusCreateNpTitleCtxA(CpuContext ctx) => NpTusState.CreateNpTitleContext(ctx, accountBased: true);
    [SysAbiExport(Nid = "3bh2aBvvmvM", ExportName = "sceNpTusCreateRequest", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusCreateRequest(CpuContext ctx) => NpTusState.CreateRequest(ctx);
    [SysAbiExport(Nid = "hhy8+oecGac", ExportName = "sceNpTusCreateTitleCtx", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusCreateTitleCtx(CpuContext ctx) => NpTusState.CreateTitleContext(ctx);

    [SysAbiExport(Nid = "iXzUOM9sXU0", ExportName = "sceNpTusDeleteMultiSlotDataA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotDataA(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "6-+Yqc-NppQ", ExportName = "sceNpTusDeleteMultiSlotDataAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotDataAAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "xutwCvsydkk", ExportName = "sceNpTusDeleteMultiSlotDataVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotDataVUser(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "zDeH4tr+0cQ", ExportName = "sceNpTusDeleteMultiSlotDataVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotDataVUserAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "pwnE9Oa1uF8", ExportName = "sceNpTusDeleteMultiSlotVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariableA(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "NQIw7tzo0Ow", ExportName = "sceNpTusDeleteMultiSlotVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariableAAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "o02Mtf8G6V0", ExportName = "sceNpTusDeleteMultiSlotVariableVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariableVUser(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "WCzd3cxhubo", ExportName = "sceNpTusDeleteMultiSlotVariableVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariableVUserAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "H3uq7x0sZOI", ExportName = "sceNpTusDeleteNpTitleCtx", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteNpTitleCtx(CpuContext ctx) => NpTusState.DeleteTitleContext(ctx);
    [SysAbiExport(Nid = "CcIH40dYS88", ExportName = "sceNpTusDeleteRequest", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusDeleteRequest(CpuContext ctx) => NpTusState.DeleteRequest(ctx);

    [SysAbiExport(Nid = "yWEHUFkY1qI", ExportName = "sceNpTusGetDataA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataA(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "xzG8mG9YlKY", ExportName = "sceNpTusGetDataAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataAAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "iaH+Sxlw32k", ExportName = "sceNpTusGetDataAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataAVUser(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "uoFvgzwawAY", ExportName = "sceNpTusGetDataAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataAVUserAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "1TE3OvH61qo", ExportName = "sceNpTusGetDataForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataForCrossSave(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "CFPx3eyaT34", ExportName = "sceNpTusGetDataForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataForCrossSaveAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "-LxFGYCJwww", ExportName = "sceNpTusGetDataForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataForCrossSaveVUser(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);
    [SysAbiExport(Nid = "B7rBR0CoYLI", ExportName = "sceNpTusGetDataForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetDataForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusASize);

    [SysAbiExport(Nid = "yixh7HDKWfk", ExportName = "sceNpTusGetFriendsDataStatusA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatusA(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "OheijxY5RYE", ExportName = "sceNpTusGetFriendsDataStatusAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatusAAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "TDoqRD+CE+M", ExportName = "sceNpTusGetFriendsDataStatusForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatusForCrossSave(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "68B6XDgSANk", ExportName = "sceNpTusGetFriendsDataStatusForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatusForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "C8TY-UnQoXg", ExportName = "sceNpTusGetFriendsVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariableA(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "wrImtTqUSGM", ExportName = "sceNpTusGetFriendsVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariableAAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "mD6s8HtMdpk", ExportName = "sceNpTusGetFriendsVariableForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariableForCrossSave(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "FabW3QpY3gQ", ExportName = "sceNpTusGetFriendsVariableForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariableForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);

    [SysAbiExport(Nid = "833Y2TnyonE", ExportName = "sceNpTusGetMultiSlotDataStatusA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusA(CpuContext ctx) => NpTusState.GetMultiSlotDataStatusA(ctx);
    [SysAbiExport(Nid = "7uLPqiNvNLc", ExportName = "sceNpTusGetMultiSlotDataStatusAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusAAsync(CpuContext ctx) => NpTusState.GetMultiSlotDataStatusA(ctx);
    [SysAbiExport(Nid = "azmjx3jBAZA", ExportName = "sceNpTusGetMultiSlotDataStatusAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusAVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "668Ij9MYKEU", ExportName = "sceNpTusGetMultiSlotDataStatusAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusAVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "DgpRToHWN40", ExportName = "sceNpTusGetMultiSlotDataStatusForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusForCrossSave(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "LQ6CoHcp+ug", ExportName = "sceNpTusGetMultiSlotDataStatusForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "KBfBmtxCdmI", ExportName = "sceNpTusGetMultiSlotDataStatusForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "4UF2uu2eDCo", ExportName = "sceNpTusGetMultiSlotDataStatusForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);

    [SysAbiExport(Nid = "GDXlRTxgd+M", ExportName = "sceNpTusGetMultiSlotVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableA(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "2BnPSY1Oxd8", ExportName = "sceNpTusGetMultiSlotVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableAAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "AsziNQ9X2uk", ExportName = "sceNpTusGetMultiSlotVariableAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableAVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "y-DJK+d+leg", ExportName = "sceNpTusGetMultiSlotVariableAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableAVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "m9XZnxw9AmE", ExportName = "sceNpTusGetMultiSlotVariableForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableForCrossSave(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "DFlBYT+Lm2I", ExportName = "sceNpTusGetMultiSlotVariableForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "wTuuw4-6HI8", ExportName = "sceNpTusGetMultiSlotVariableForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "DPcu0qWsd7Q", ExportName = "sceNpTusGetMultiSlotVariableForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);

    [SysAbiExport(Nid = "lxNDPDnWfMc", ExportName = "sceNpTusGetMultiUserDataStatusA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusA(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "kt+k6jegYZ8", ExportName = "sceNpTusGetMultiUserDataStatusAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusAAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "fJU2TZId210", ExportName = "sceNpTusGetMultiUserDataStatusAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusAVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "WBh3zfrjS38", ExportName = "sceNpTusGetMultiUserDataStatusAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusAVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "cVeBif6zdZ4", ExportName = "sceNpTusGetMultiUserDataStatusForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusForCrossSave(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "lq0Anwhj0wY", ExportName = "sceNpTusGetMultiUserDataStatusForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "w-c7U0MW2KY", ExportName = "sceNpTusGetMultiUserDataStatusForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "H6sQJ99usfE", ExportName = "sceNpTusGetMultiUserDataStatusForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);

    [SysAbiExport(Nid = "Gjixv5hqRVY", ExportName = "sceNpTusGetMultiUserVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableA(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "eGunerNP9n0", ExportName = "sceNpTusGetMultiUserVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableAAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "fVvocpq4mG4", ExportName = "sceNpTusGetMultiUserVariableAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableAVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "V8ZA3hHrAbw", ExportName = "sceNpTusGetMultiUserVariableAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableAVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "Q5uQeScvTPE", ExportName = "sceNpTusGetMultiUserVariableForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableForCrossSave(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "oZ8DMeTU-50", ExportName = "sceNpTusGetMultiUserVariableForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "Djuj2+1VNL0", ExportName = "sceNpTusGetMultiUserVariableForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "82RP7itI-zI", ExportName = "sceNpTusGetMultiUserVariableForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);

    [SysAbiExport(Nid = "t7b6dmpQNiI", ExportName = "sceNpTusPollAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusPollAsync(CpuContext ctx) => NpTusState.Poll(ctx);
    [SysAbiExport(Nid = "VzxN3tOouj8", ExportName = "sceNpTusSetDataA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetDataA(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "4u58d6g6uwU", ExportName = "sceNpTusSetDataAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetDataAAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "kbWqOt3QjKU", ExportName = "sceNpTusSetDataAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetDataAVUser(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "Fmx4tapJGzo", ExportName = "sceNpTusSetDataAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetDataAVUserAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "cf-WMA0jYCc", ExportName = "sceNpTusSetMultiSlotVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariableA(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "ypMObSwfcns", ExportName = "sceNpTusSetMultiSlotVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariableAAsync(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "1Cz0hTJFyh4", ExportName = "sceNpTusSetMultiSlotVariableVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariableVUser(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "CJAxTxQdwHM", ExportName = "sceNpTusSetMultiSlotVariableVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariableVUserAsync(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "6GKDdRCFx8c", ExportName = "sceNpTusSetThreadParam", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetThreadParam(CpuContext ctx) => NpTusState.Success(ctx);
    [SysAbiExport(Nid = "KMlHj+tgfdQ", ExportName = "sceNpTusSetTimeout", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusSetTimeout(CpuContext ctx) => NpTusState.Success(ctx);

    [SysAbiExport(Nid = "0up4MP1wNtc", ExportName = "sceNpTusTryAndSetVariableA", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableA(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "bGTjTkHPHTE", ExportName = "sceNpTusTryAndSetVariableAAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableAAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "oGIcxlUabSA", ExportName = "sceNpTusTryAndSetVariableAVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableAVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "uf77muc5Bog", ExportName = "sceNpTusTryAndSetVariableAVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableAVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "MGvSJEHwyL8", ExportName = "sceNpTusTryAndSetVariableForCrossSave", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableForCrossSave(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "JKGYZ2F1yT8", ExportName = "sceNpTusTryAndSetVariableForCrossSaveAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableForCrossSaveAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "fcCwKpi4CbU", ExportName = "sceNpTusTryAndSetVariableForCrossSaveVUser", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableForCrossSaveVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "CjVIpztpTNc", ExportName = "sceNpTusTryAndSetVariableForCrossSaveVUserAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableForCrossSaveVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "hYPJFWzFPjA", ExportName = "sceNpTusWaitAsync", LibraryName = "libSceNpTus", Target = Generation.Gen5)]
    public static int sceNpTusWaitAsync(CpuContext ctx) => NpTusState.Wait(ctx);
}
