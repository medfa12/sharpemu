// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpTrophyExports
{
    internal const int GameDetailsSize = 0x4A0;
    internal const int GameDataSize = 0x20;
    internal const int GroupDetailsSize = 0x4A0;
    internal const int GroupDataSize = 0x28;
    internal const int TrophyDetailsSize = 0x498;
    internal const int TrophyDataSize = 0x18;

    private const int ErrorInvalidArgument = unchecked((int)0x80551604);
    private const int ErrorInvalidTrophyId = unchecked((int)0x8055160A);
    private const int ErrorInvalidGroupId = unchecked((int)0x8055160B);
    private const int ErrorIconFileNotFound = unchecked((int)0x80551614);
    private const int BaseGameGroupId = -1;

    [SysAbiExport(Nid = "aTnHs7W-9Uk", ExportName = "sceNpTrophyAbortHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyAbortHandle(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2AbortHandle(ctx));

    [SysAbiExport(Nid = "XbkjbobZlCY", ExportName = "sceNpTrophyCreateContext", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyCreateContext(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2CreateContext(ctx));

    [SysAbiExport(Nid = "q7U6tEAQf7c", ExportName = "sceNpTrophyCreateHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyCreateHandle(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2CreateHandle(ctx));

    [SysAbiExport(Nid = "E1Wrwd07Lr8", ExportName = "sceNpTrophyDestroyContext", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyDestroyContext(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2DestroyContext(ctx));

    [SysAbiExport(Nid = "GNcF4oidY0Y", ExportName = "sceNpTrophyDestroyHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyDestroyHandle(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2DestroyHandle(ctx));

    [SysAbiExport(Nid = "TJCAxto9SEU", ExportName = "sceNpTrophyRegisterContext", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyRegisterContext(CpuContext ctx) => MapLegacyResult(ctx, NpTrophy2Exports.NpTrophy2RegisterContext(ctx));

    [SysAbiExport(Nid = "HLwz1fRIycA", ExportName = "sceNpTrophyGetGameIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetGameIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.Rcx]);

    [SysAbiExport(Nid = "w4uMPmErD4I", ExportName = "sceNpTrophyGetGroupIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetGroupIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(Nid = "eBL+l6HG9xk", ExportName = "sceNpTrophyGetTrophyIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetTrophyIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(Nid = "YYP3f2W09og", ExportName = "sceNpTrophyGetGameInfo", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetGameInfo(CpuContext ctx)
    {
        var result = GetSnapshot(ctx, out var titleId, out var trophies);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        var detailsAddress = ctx[CpuRegister.Rdx];
        var dataAddress = ctx[CpuRegister.Rcx];
        if (detailsAddress == 0 || dataAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        return ctx.Memory.TryWrite(detailsAddress, PackGameDetails(titleId, trophies)) &&
               ctx.Memory.TryWrite(dataAddress, PackGameData(trophies))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "wTUwGfspKic", ExportName = "sceNpTrophyGetGroupInfo", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetGroupInfo(CpuContext ctx)
    {
        var result = GetSnapshot(ctx, out _, out var trophies);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        var groupId = unchecked((int)ctx[CpuRegister.Rdx]);
        var detailsAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        if (detailsAddress == 0 || dataAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (groupId != BaseGameGroupId && !trophies.Any(trophy => trophy.GroupId == groupId))
        {
            return ctx.SetReturn(ErrorInvalidGroupId);
        }

        var groupTrophies = trophies.Where(trophy => NormalizeGroupId(trophy.GroupId) == groupId).ToArray();
        return ctx.Memory.TryWrite(detailsAddress, PackGroupDetails(groupId, groupTrophies)) &&
               ctx.Memory.TryWrite(dataAddress, PackGroupData(groupId, groupTrophies))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "qqUVGDgQBm0", ExportName = "sceNpTrophyGetTrophyInfo", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetTrophyInfo(CpuContext ctx)
    {
        var result = GetSnapshot(ctx, out _, out var trophies);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        var trophyId = unchecked((int)ctx[CpuRegister.Rdx]);
        var trophy = trophies.FirstOrDefault(item => item.Id == trophyId);
        if (trophy is null)
        {
            return ctx.SetReturn(ErrorInvalidTrophyId);
        }

        var detailsAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        if (detailsAddress == 0 || dataAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        return ctx.Memory.TryWrite(detailsAddress, PackTrophyDetails(trophy)) &&
               ctx.Memory.TryWrite(dataAddress, PackTrophyData(trophy))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "LHuSmO3SLd8", ExportName = "sceNpTrophyGetTrophyUnlockState", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetTrophyUnlockState(CpuContext ctx)
    {
        var result = GetSnapshot(ctx, out _, out var trophies);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        var flagsAddress = ctx[CpuRegister.Rdx];
        var countAddress = ctx[CpuRegister.Rcx];
        if (flagsAddress == 0 || countAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        Span<byte> flags = stackalloc byte[16];
        foreach (var trophy in trophies.Where(trophy => trophy.Unlocked && trophy.Id is >= 0 and < 128))
        {
            var wordOffset = trophy.Id / 32 * sizeof(uint);
            var bit = 1U << (trophy.Id % 32);
            var word = BinaryPrimitives.ReadUInt32LittleEndian(flags[wordOffset..]);
            BinaryPrimitives.WriteUInt32LittleEndian(flags[wordOffset..], word | bit);
        }

        return ctx.Memory.TryWrite(flagsAddress, flags) &&
               ctx.TryWriteUInt32(countAddress, checked((uint)trophies.Length))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "d9jpdPz5f-8", ExportName = "sceNpTrophyShowTrophyList", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyShowTrophyList(CpuContext ctx) => ctx.SetReturn(GetSnapshot(ctx, out _, out _));

    [SysAbiExport(Nid = "cqGkYAN-gRw", ExportName = "sceNpTrophyCaptureScreenshot", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyCaptureScreenshot(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "lhE4XS9OJXs", ExportName = "sceNpTrophyConfigGetTrophyDetails", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophyDetails(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "qJ3IvrOoXg0", ExportName = "sceNpTrophyConfigGetTrophyFlagArray", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophyFlagArray(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "zDjF2G+6tI0", ExportName = "sceNpTrophyConfigGetTrophyGroupArray", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophyGroupArray(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "7Kh86vJqtxw", ExportName = "sceNpTrophyConfigGetTrophyGroupDetails", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophyGroupDetails(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ndLeNWExeZE", ExportName = "sceNpTrophyConfigGetTrophySetInfo", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophySetInfo(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "6EOfS5SDgoo", ExportName = "sceNpTrophyConfigGetTrophySetInfoInGroup", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophySetInfoInGroup(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "MW5ygoZqEBs", ExportName = "sceNpTrophyConfigGetTrophySetVersion", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophySetVersion(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "3tWKpNKn5+I", ExportName = "sceNpTrophyConfigGetTrophyTitleDetails", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigGetTrophyTitleDetails(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "iqYfxC12sak", ExportName = "sceNpTrophyConfigHasGroupFeature", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyConfigHasGroupFeature(CpuContext ctx) => Success(ctx);

    [SysAbiExport(Nid = "Ht6MNTl-je4", ExportName = "sceNpTrophyGroupArrayGetNum", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGroupArrayGetNum(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "u9plkqa2e0k", ExportName = "sceNpTrophyIntAbortHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntAbortHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "pE5yhroy9m0", ExportName = "sceNpTrophyIntCheckNetSyncTitles", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntCheckNetSyncTitles(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "edPIOFpEAvU", ExportName = "sceNpTrophyIntCreateHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntCreateHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "DSh3EXpqAQ4", ExportName = "sceNpTrophyIntDestroyHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntDestroyHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "sng98qULzPA", ExportName = "sceNpTrophyIntGetLocalTrophySummary", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntGetLocalTrophySummary(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "t3CQzag7-zs", ExportName = "sceNpTrophyIntGetProgress", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntGetProgress(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "jF-mCgGuvbQ", ExportName = "sceNpTrophyIntGetRunningTitle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntGetRunningTitle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "PeAyBjC5kp8", ExportName = "sceNpTrophyIntGetRunningTitles", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntGetRunningTitles(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "PEo09Dkqv0o", ExportName = "sceNpTrophyIntGetTrpIconByUri", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntGetTrpIconByUri(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "kF9zjnlAzIA", ExportName = "sceNpTrophyIntNetSyncTitle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntNetSyncTitle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "UXiyfabxFNQ", ExportName = "sceNpTrophyIntNetSyncTitles", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyIntNetSyncTitles(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "hvdThnVvwdY", ExportName = "sceNpTrophyNumInfoGetTotal", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophyNumInfoGetTotal(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ITUmvpBPaG0", ExportName = "sceNpTrophySetInfoGetTrophyFlagArray", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySetInfoGetTrophyFlagArray(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "BSoSgiMVHnY", ExportName = "sceNpTrophySetInfoGetTrophyNum", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySetInfoGetTrophyNum(CpuContext ctx) => Success(ctx);

    [SysAbiExport(Nid = "JzJdh-JLtu0", ExportName = "sceNpTrophySystemAbortHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemAbortHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "z8RCP536GOM", ExportName = "sceNpTrophySystemBuildGroupIconUri", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemBuildGroupIconUri(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Rd2FBOQE094", ExportName = "sceNpTrophySystemBuildNetTrophyIconUri", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemBuildNetTrophyIconUri(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Q182x0rT75I", ExportName = "sceNpTrophySystemBuildTitleIconUri", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemBuildTitleIconUri(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "lGnm5Kg-zpA", ExportName = "sceNpTrophySystemBuildTrophyIconUri", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemBuildTrophyIconUri(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "20wAMbXP-u0", ExportName = "sceNpTrophySystemCheckNetSyncTitles", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemCheckNetSyncTitles(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "sKGFFY59ksY", ExportName = "sceNpTrophySystemCheckRecoveryRequired", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemCheckRecoveryRequired(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "JMSapEtDH9Q", ExportName = "sceNpTrophySystemCloseStorage", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemCloseStorage(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "dk27olS4CEE", ExportName = "sceNpTrophySystemCreateContext", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemCreateContext(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "cBzXEdzVzvs", ExportName = "sceNpTrophySystemCreateHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemCreateHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "8aLlLHKP+No", ExportName = "sceNpTrophySystemDbgCtl", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDbgCtl(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "NobVwD8qcQY", ExportName = "sceNpTrophySystemDebugLockTrophy", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDebugLockTrophy(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "yXJlgXljItk", ExportName = "sceNpTrophySystemDebugUnlockTrophy", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDebugUnlockTrophy(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "U0TOSinfuvw", ExportName = "sceNpTrophySystemDestroyContext", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDestroyContext(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "-LC9hudmD+Y", ExportName = "sceNpTrophySystemDestroyHandle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDestroyHandle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "q6eAMucXIEM", ExportName = "sceNpTrophySystemDestroyTrophyConfig", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemDestroyTrophyConfig(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "WdCUUJLQodM", ExportName = "sceNpTrophySystemGetDbgParam", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetDbgParam(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "4QYFwC7tn4U", ExportName = "sceNpTrophySystemGetDbgParamInt", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetDbgParamInt(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "OcllHFFcQkI", ExportName = "sceNpTrophySystemGetGroupIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetGroupIcon(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "tQ3tXfVZreU", ExportName = "sceNpTrophySystemGetLocalTrophySummary", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetLocalTrophySummary(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "g0dxBNTspC0", ExportName = "sceNpTrophySystemGetNextTitleFileEntryStatus", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetNextTitleFileEntryStatus(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "sJSDnJRJHhI", ExportName = "sceNpTrophySystemGetProgress", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetProgress(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "X47s4AamPGg", ExportName = "sceNpTrophySystemGetTitleFileStatus", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTitleFileStatus(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "7WPj4KCF3D8", ExportName = "sceNpTrophySystemGetTitleIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTitleIcon(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "pzL+aAk0tQA", ExportName = "sceNpTrophySystemGetTitleSyncStatus", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTitleSyncStatus(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Ro4sI9xgYl4", ExportName = "sceNpTrophySystemGetTrophyConfig", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyConfig(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "7+OR1TU5QOA", ExportName = "sceNpTrophySystemGetTrophyData", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyData(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "aXhvf2OmbiE", ExportName = "sceNpTrophySystemGetTrophyGroupData", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyGroupData(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Rkt0bVyaa4Y", ExportName = "sceNpTrophySystemGetTrophyIcon", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyIcon(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "nXr5Rho8Bqk", ExportName = "sceNpTrophySystemGetTrophyTitleData", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyTitleData(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "eV1rtLr+eys", ExportName = "sceNpTrophySystemGetTrophyTitleIds", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetTrophyTitleIds(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "SsGLKTfWfm0", ExportName = "sceNpTrophySystemGetUserFileInfo", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetUserFileInfo(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "XqLLsvl48kA", ExportName = "sceNpTrophySystemGetUserFileStatus", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemGetUserFileStatus(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "-qjm2fFE64M", ExportName = "sceNpTrophySystemIsServerAvailable", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemIsServerAvailable(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "50BvYYzPTsY", ExportName = "sceNpTrophySystemNetSyncTitle", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemNetSyncTitle(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "yDJ-r-8f4S4", ExportName = "sceNpTrophySystemNetSyncTitles", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemNetSyncTitles(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "mWtsnHY8JZg", ExportName = "sceNpTrophySystemOpenStorage", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemOpenStorage(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "tAxnXpzDgFw", ExportName = "sceNpTrophySystemPerformRecovery", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemPerformRecovery(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "tV18n8OcheI", ExportName = "sceNpTrophySystemRemoveAll", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemRemoveAll(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "kV4DP0OTMNo", ExportName = "sceNpTrophySystemRemoveTitleData", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemRemoveTitleData(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "lZSZoN8BstI", ExportName = "sceNpTrophySystemRemoveUserData", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemRemoveUserData(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "nytN-3-pdvI", ExportName = "sceNpTrophySystemSetDbgParam", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemSetDbgParam(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "JsRnDKRzvRw", ExportName = "sceNpTrophySystemSetDbgParamInt", Target = Generation.Gen5, LibraryName = "libSceNpTrophy")]
    public static int NpTrophySystemSetDbgParamInt(CpuContext ctx) => Success(ctx);

    internal static byte[] PackTrophyDetails(NpTrophy2Exports.TrophyRecord trophy)
    {
        var bytes = new byte[TrophyDetailsSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, TrophyDetailsSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08), trophy.Id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x0C), trophy.Grade);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), NormalizeGroupId(trophy.GroupId));
        bytes[0x14] = trophy.Hidden ? (byte)1 : (byte)0;
        WriteFixedUtf8(bytes.AsSpan(0x18, 128), trophy.Name);
        WriteFixedUtf8(bytes.AsSpan(0x98, 1024), trophy.Description);
        return bytes;
    }

    internal static byte[] PackTrophyData(NpTrophy2Exports.TrophyRecord trophy)
    {
        var bytes = new byte[TrophyDataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, TrophyDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08), trophy.Id);
        bytes[0x0C] = trophy.Unlocked ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x10), trophy.TimestampTick);
        return bytes;
    }

    private static byte[] PackGameDetails(string titleId, NpTrophy2Exports.TrophyRecord[] trophies)
    {
        var bytes = new byte[GameDetailsSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, GameDetailsSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08), checked((uint)trophies.Select(trophy => NormalizeGroupId(trophy.GroupId)).Where(id => id >= 0).Distinct().Count()));
        WriteGradeCounts(bytes, 0x0C, trophies);
        WriteFixedUtf8(bytes.AsSpan(0x24, 128), titleId);
        return bytes;
    }

    private static byte[] PackGameData(NpTrophy2Exports.TrophyRecord[] trophies)
    {
        var bytes = new byte[GameDataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, GameDataSize);
        var unlocked = trophies.Where(trophy => trophy.Unlocked).ToArray();
        WriteUnlockedCounts(bytes, 0x08, unlocked);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C), trophies.Length == 0 ? 0U : checked((uint)(unlocked.Length * 100 / trophies.Length)));
        return bytes;
    }

    private static byte[] PackGroupDetails(int groupId, NpTrophy2Exports.TrophyRecord[] trophies)
    {
        var bytes = new byte[GroupDetailsSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, GroupDetailsSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08), groupId);
        WriteGradeCounts(bytes, 0x0C, trophies);
        WriteFixedUtf8(bytes.AsSpan(0x24, 128), groupId == BaseGameGroupId ? "Base Game" : $"Group {groupId}");
        return bytes;
    }

    private static byte[] PackGroupData(int groupId, NpTrophy2Exports.TrophyRecord[] trophies)
    {
        var bytes = new byte[GroupDataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, GroupDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08), groupId);
        var unlocked = trophies.Where(trophy => trophy.Unlocked).ToArray();
        WriteUnlockedCounts(bytes, 0x0C, unlocked);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x20), trophies.Length == 0 ? 0U : checked((uint)(unlocked.Length * 100 / trophies.Length)));
        return bytes;
    }

    private static void WriteGradeCounts(Span<byte> bytes, int offset, IReadOnlyCollection<NpTrophy2Exports.TrophyRecord> trophies)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], checked((uint)trophies.Count));
        for (var grade = 1; grade <= 4; grade++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + grade * sizeof(uint))..], checked((uint)trophies.Count(trophy => trophy.Grade == grade)));
        }
    }

    private static void WriteUnlockedCounts(Span<byte> bytes, int offset, IReadOnlyCollection<NpTrophy2Exports.TrophyRecord> trophies)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], checked((uint)trophies.Count));
        for (var grade = 1; grade <= 4; grade++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + grade * sizeof(uint))..], checked((uint)trophies.Count(trophy => trophy.Grade == grade)));
        }
    }

    private static int GetSnapshot(CpuContext ctx, out string titleId, out NpTrophy2Exports.TrophyRecord[] trophies) =>
        NpTrophy2Exports.GetLegacyRegistrySnapshot(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((int)ctx[CpuRegister.Rsi]),
            out titleId,
            out trophies);

    private static int MapLegacyResult(CpuContext ctx, int result)
    {
        var legacyResult = result is >= unchecked((int)0x80553900) and <= unchecked((int)0x805539FF)
            ? result - 0x2300
            : result;
        return ctx.SetReturn(legacyResult);
    }

    private static int ReturnMissingIcon(CpuContext ctx, ulong sizeAddress)
    {
        if (sizeAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        return ctx.TryWriteUInt64(sizeAddress, 0)
            ? ctx.SetReturn(ErrorIconFileNotFound)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int Success(CpuContext ctx) => ctx.SetReturn(0);

    private static int NormalizeGroupId(int groupId) => groupId <= 0 ? BaseGameGroupId : groupId;

    private static void WriteFixedUtf8(Span<byte> destination, string value)
    {
        destination.Clear();
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Encoding.UTF8.GetBytes(value, destination[..Math.Min(byteCount, destination.Length - 1)]);
    }
}
