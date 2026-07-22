// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpScoreExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80550704);
    private const int ErrorTooManyObjects = unchecked((int)0x80550706);
    private const int ErrorAborted = unchecked((int)0x80550707);
    private const int ErrorInsufficientArgument = unchecked((int)0x8055070C);
    private const int ErrorInvalidId = unchecked((int)0x8055070E);
    private const int MaximumContexts = 32;

    private static int _nextTitleContextId;
    private static int _nextRequestId;
    private static readonly ConcurrentDictionary<int, TitleContext> TitleContexts = new();
    private static readonly ConcurrentDictionary<int, RequestState> Requests = new();

    private sealed record TitleContext(uint ServiceLabel, int UserId, int PlayerCharacterId);

    private sealed class RequestState(int titleContextId)
    {
        public int TitleContextId { get; } = titleContextId;
        public int Result { get; set; }
    }

    [SysAbiExport(Nid = "KnNA1TEgtBI", ExportName = "sceNpScoreCreateNpTitleCtx", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCreateNpTitleCtx(CpuContext ctx)
    {
        if ((uint)ctx[CpuRegister.Rdi] == uint.MaxValue)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInsufficientArgument);
        }
        return CreateTitleContext(ctx, (uint)ctx[CpuRegister.Rdi], -1);
    }

    [SysAbiExport(Nid = "GWnWQNXZH5M", ExportName = "sceNpScoreCreateNpTitleCtxA", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCreateNpTitleCtxA(CpuContext ctx) =>
        (uint)ctx[CpuRegister.Rdi] == uint.MaxValue
            ? ctx.SetReturn(ErrorInvalidArgument)
            : CreateTitleContext(ctx, (uint)ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(Nid = "qW9M0bQ-Zx0", ExportName = "sceNpScoreCreateTitleCtx", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCreateTitleCtx(CpuContext ctx) => CreateTitleContext(ctx, 0, -1);

    [SysAbiExport(Nid = "G0pE+RNCwfk", ExportName = "sceNpScoreDeleteNpTitleCtx", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreDeleteNpTitleCtx(CpuContext ctx)
    {
        var titleContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TitleContexts.TryRemove(titleContextId, out _))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        foreach (var request in Requests.Where(pair => pair.Value.TitleContextId == titleContextId))
        {
            Requests.TryRemove(request.Key, out _);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "gW8qyjYrUbk", ExportName = "sceNpScoreCreateRequest", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCreateRequest(CpuContext ctx)
    {
        var titleContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TitleContexts.ContainsKey(titleContextId))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        if (Requests.Count >= MaximumContexts)
        {
            return ctx.SetReturn(ErrorTooManyObjects);
        }
        var requestId = Interlocked.Increment(ref _nextRequestId);
        Requests[requestId] = new RequestState(titleContextId);
        return ctx.SetReturn(requestId);
    }

    [SysAbiExport(Nid = "dK8-SgYf6r4", ExportName = "sceNpScoreDeleteRequest", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreDeleteRequest(CpuContext ctx) =>
        ctx.SetReturn(Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _) ? 0 : ErrorInvalidId);

    [SysAbiExport(Nid = "1i7kmKbX6hk", ExportName = "sceNpScoreAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreAbortRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        request.Result = ErrorAborted;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "m1DfNRstkSQ", ExportName = "sceNpScorePollAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScorePollAsync(CpuContext ctx) => CompleteAsync(ctx);

    [SysAbiExport(Nid = "fqk8SC63p1U", ExportName = "sceNpScoreWaitAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreWaitAsync(CpuContext ctx) => CompleteAsync(ctx);

    [SysAbiExport(Nid = "zT0XBtgtOSI", ExportName = "sceNpScoreRecordScore", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreRecordScore(CpuContext ctx) => RecordScore(ctx);

    [SysAbiExport(Nid = "ANJssPz3mY0", ExportName = "sceNpScoreRecordScoreAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreRecordScoreAsync(CpuContext ctx) => RecordScore(ctx);

    [SysAbiExport(Nid = "bcoVwcBjQ9E", ExportName = "sceNpScoreRecordGameData", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreRecordGameData(CpuContext ctx) => RequestCall(ctx);

    [SysAbiExport(Nid = "1gL5PwYzrrw", ExportName = "sceNpScoreRecordGameDataAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreRecordGameDataAsync(CpuContext ctx) => RequestCall(ctx);

    [SysAbiExport(Nid = "LoVMVrijVOk", ExportName = "sceNpScoreGetBoardInfo", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetBoardInfo(CpuContext ctx) => BoardInfo(ctx);

    [SysAbiExport(Nid = "Q0Avi9kebsY", ExportName = "sceNpScoreGetBoardInfoAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetBoardInfoAsync(CpuContext ctx) => BoardInfo(ctx);

    [SysAbiExport(Nid = "zKoVok6FFEI", ExportName = "sceNpScoreGetGameData", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetGameData(CpuContext ctx) => GameData(ctx);

    [SysAbiExport(Nid = "JjOFRVPdQWc", ExportName = "sceNpScoreGetGameDataAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetGameDataAsync(CpuContext ctx) => GameData(ctx);

    [SysAbiExport(Nid = "Lmtc9GljeUA", ExportName = "sceNpScoreGetGameDataByAccountId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetGameDataByAccountId(CpuContext ctx) => GameData(ctx);

    [SysAbiExport(Nid = "PP9jx8s0574", ExportName = "sceNpScoreGetGameDataByAccountIdAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetGameDataByAccountIdAsync(CpuContext ctx) => GameData(ctx);

    [SysAbiExport(Nid = "2b3TI0mDYiI", ExportName = "sceNpScoreCensorComment", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCensorComment(CpuContext ctx) => CommentCall(ctx, false);

    [SysAbiExport(Nid = "4eOvDyN-aZc", ExportName = "sceNpScoreCensorCommentAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreCensorCommentAsync(CpuContext ctx) => CommentCall(ctx, false);

    [SysAbiExport(Nid = "r4oAo9in0TA", ExportName = "sceNpScoreSanitizeComment", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreSanitizeComment(CpuContext ctx) => CommentCall(ctx, true);

    [SysAbiExport(Nid = "3UVqGJeDf30", ExportName = "sceNpScoreSanitizeCommentAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreSanitizeCommentAsync(CpuContext ctx) => CommentCall(ctx, true);

    [SysAbiExport(Nid = "bygbKdHmjn4", ExportName = "sceNpScoreSetPlayerCharacterId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreSetPlayerCharacterId(CpuContext ctx)
    {
        var titleContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TitleContexts.TryGetValue(titleContextId, out var titleContext))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        TitleContexts[titleContextId] = titleContext with { PlayerCharacterId = unchecked((int)ctx[CpuRegister.Rsi]) };
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "dTXC+YcePtM", ExportName = "sceNpScoreChangeModeForOtherSaveDataOwners", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreChangeModeForOtherSaveDataOwners(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "yxK68584JAU", ExportName = "sceNpScoreSetThreadParam", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreSetThreadParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "S3xZj35v8Z8", ExportName = "sceNpScoreSetTimeout", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreSetTimeout(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "KBHxDjyk-jA", ExportName = "sceNpScoreGetRankingByRange", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRange(CpuContext ctx) => RangeQuery(ctx);
    [SysAbiExport(Nid = "MA9vSt7JImY", ExportName = "sceNpScoreGetRankingByRangeA", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRangeA(CpuContext ctx) => RangeQuery(ctx);
    [SysAbiExport(Nid = "y5ja7WI05rs", ExportName = "sceNpScoreGetRankingByRangeAAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRangeAAsync(CpuContext ctx) => RangeQuery(ctx);
    [SysAbiExport(Nid = "rShmqXHwoQE", ExportName = "sceNpScoreGetRankingByRangeAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRangeAsync(CpuContext ctx) => RangeQuery(ctx);
    [SysAbiExport(Nid = "nRoYV2yeUuw", ExportName = "sceNpScoreGetRankingByRangeForCrossSave", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRangeForCrossSave(CpuContext ctx) => RangeQuery(ctx);
    [SysAbiExport(Nid = "AZ4eAlGDy-Q", ExportName = "sceNpScoreGetRankingByRangeForCrossSaveAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByRangeForCrossSaveAsync(CpuContext ctx) => RangeQuery(ctx);

    [SysAbiExport(Nid = "9mZEgoiEq6Y", ExportName = "sceNpScoreGetRankingByNpId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByNpId(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "Rd27dqUFZV8", ExportName = "sceNpScoreGetRankingByNpIdAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByNpIdAsync(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "ETS-uM-vH9Q", ExportName = "sceNpScoreGetRankingByNpIdPcId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByNpIdPcId(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "FsouSN0ykN8", ExportName = "sceNpScoreGetRankingByNpIdPcIdAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByNpIdPcIdAsync(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "K9tlODTQx3c", ExportName = "sceNpScoreGetRankingByAccountId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountId(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "dRszNNyGWkw", ExportName = "sceNpScoreGetRankingByAccountIdAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdAsync(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "3Ybj4E1qNtY", ExportName = "sceNpScoreGetRankingByAccountIdForCrossSave", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdForCrossSave(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "Kc+3QK84AKM", ExportName = "sceNpScoreGetRankingByAccountIdForCrossSaveAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdForCrossSaveAsync(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "wJPWycVGzrs", ExportName = "sceNpScoreGetRankingByAccountIdPcId", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdPcId(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "bFVjDgxFapc", ExportName = "sceNpScoreGetRankingByAccountIdPcIdAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdPcIdAsync(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "oXjVieH6ZGQ", ExportName = "sceNpScoreGetRankingByAccountIdPcIdForCrossSave", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdPcIdForCrossSave(CpuContext ctx) => IdQuery(ctx);
    [SysAbiExport(Nid = "nXaF1Bxb-Nw", ExportName = "sceNpScoreGetRankingByAccountIdPcIdForCrossSaveAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetRankingByAccountIdPcIdForCrossSaveAsync(CpuContext ctx) => IdQuery(ctx);

    [SysAbiExport(Nid = "8kuIzUw6utQ", ExportName = "sceNpScoreGetFriendsRanking", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRanking(CpuContext ctx) => FriendsQuery(ctx);
    [SysAbiExport(Nid = "gMbOn+-6eXA", ExportName = "sceNpScoreGetFriendsRankingA", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRankingA(CpuContext ctx) => FriendsQuery(ctx);
    [SysAbiExport(Nid = "6-G9OxL5DKg", ExportName = "sceNpScoreGetFriendsRankingAAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRankingAAsync(CpuContext ctx) => FriendsQuery(ctx);
    [SysAbiExport(Nid = "7SuMUlN7Q6I", ExportName = "sceNpScoreGetFriendsRankingAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRankingAsync(CpuContext ctx) => FriendsQuery(ctx);
    [SysAbiExport(Nid = "AgcxgceaH8k", ExportName = "sceNpScoreGetFriendsRankingForCrossSave", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRankingForCrossSave(CpuContext ctx) => FriendsQuery(ctx);
    [SysAbiExport(Nid = "m6F7sE1HQZU", ExportName = "sceNpScoreGetFriendsRankingForCrossSaveAsync", Target = Generation.Gen5, LibraryName = "libSceNpScore")]
    public static int NpScoreGetFriendsRankingForCrossSaveAsync(CpuContext ctx) => FriendsQuery(ctx);

    private static int CreateTitleContext(CpuContext ctx, uint serviceLabel, int userId)
    {
        if (TitleContexts.Count >= MaximumContexts)
        {
            return ctx.SetReturn(ErrorTooManyObjects);
        }
        var id = Interlocked.Increment(ref _nextTitleContextId);
        TitleContexts[id] = new TitleContext(serviceLabel, userId, 0);
        return ctx.SetReturn(id);
    }

    private static int CompleteAsync(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress != 0 && !ctx.TryWriteInt32(resultAddress, request.Result))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    private static int RequestCall(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(ErrorInvalidId);
        }
        if (request.Result == ErrorAborted)
        {
            return ctx.SetReturn(ErrorAborted);
        }
        request.Result = 0;
        return ctx.SetReturn(0);
    }

    private static int RecordScore(CpuContext ctx)
    {
        var result = RequestCall(ctx);
        if (result != 0)
        {
            return result;
        }
        var temporaryRankAddress = ctx[CpuRegister.R9];
        return temporaryRankAddress == 0 || ctx.TryWriteUInt32(temporaryRankAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int BoardInfo(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(ErrorInsufficientArgument);
        }
        var result = RequestCall(ctx);
        return result != 0 ? result : Clear(ctx, ctx[CpuRegister.Rdx], 24);
    }

    private static int GameData(CpuContext ctx)
    {
        var result = RequestCall(ctx);
        if (result != 0)
        {
            return result;
        }
        if (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return Clear(ctx, ctx[CpuRegister.R9], ctx[CpuRegister.R8]);
    }

    private static int CommentCall(CpuContext ctx, bool sanitize)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInsufficientArgument);
        }
        var result = RequestCall(ctx);
        if (result != 0 || !sanitize)
        {
            return result;
        }
        if (ctx[CpuRegister.Rdx] == 0 || !ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rsi], 256, out var comment))
        {
            return ctx.SetReturn(ErrorInsufficientArgument);
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(comment);
        var output = new byte[256];
        bytes.AsSpan(0, Math.Min(bytes.Length, 255)).CopyTo(output);
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int RangeQuery(CpuContext ctx) => Query(ctx, 3);
    private static int FriendsQuery(CpuContext ctx) => Query(ctx, 3);
    private static int IdQuery(CpuContext ctx) => Query(ctx, 4);

    private static int Query(CpuContext ctx, int rankPointerIndex)
    {
        var result = RequestCall(ctx);
        if (result != 0)
        {
            return result;
        }
        var rankAddress = GetArgument(ctx, rankPointerIndex);
        var rankSize = GetArgument(ctx, rankPointerIndex + 1);
        var commentAddress = GetArgument(ctx, rankPointerIndex + 2);
        var commentSize = GetArgument(ctx, rankPointerIndex + 3);
        var infoAddress = GetArgument(ctx, rankPointerIndex + 4);
        var infoSize = GetArgument(ctx, rankPointerIndex + 5);
        var lastSortAddress = GetArgument(ctx, rankPointerIndex + 7);
        var totalRecordAddress = GetArgument(ctx, rankPointerIndex + 8);
        if (!TryClear(ctx, rankAddress, rankSize) || !TryClear(ctx, commentAddress, commentSize) ||
            !TryClear(ctx, infoAddress, infoSize) ||
            (lastSortAddress != 0 && !ctx.TryWriteUInt64(lastSortAddress, 0)) ||
            (totalRecordAddress != 0 && !ctx.TryWriteUInt32(totalRecordAddress, 0)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    private static ulong GetArgument(CpuContext ctx, int index)
    {
        if (index < 6)
        {
            return index switch
            {
                0 => ctx[CpuRegister.Rdi],
                1 => ctx[CpuRegister.Rsi],
                2 => ctx[CpuRegister.Rdx],
                3 => ctx[CpuRegister.Rcx],
                4 => ctx[CpuRegister.R8],
                _ => ctx[CpuRegister.R9],
            };
        }
        return ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 8UL + ((ulong)(index - 6) * 8UL), out var value) ? value : 0;
    }

    private static int Clear(CpuContext ctx, ulong address, ulong size) =>
        TryClear(ctx, address, size)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

    private static bool TryClear(CpuContext ctx, ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return true;
        }
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        while (size != 0)
        {
            var count = (int)Math.Min(size, (ulong)zeros.Length);
            if (!ctx.Memory.TryWrite(address, zeros[..count]))
            {
                return false;
            }
            address += (ulong)count;
            size -= (ulong)count;
        }
        return true;
    }

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _nextTitleContextId, 0);
        Interlocked.Exchange(ref _nextRequestId, 0);
        TitleContexts.Clear();
        Requests.Clear();
    }
}
