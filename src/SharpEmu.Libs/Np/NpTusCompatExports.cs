// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpTusCompatExports
{
    [SysAbiExport(Nid = "sRVb2Cf0GHg", ExportName = "sceNpTssCreateNpTitleCtx", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTssCreateNpTitleCtx(CpuContext ctx) => NpTusState.CreateNpTitleContext(ctx, accountBased: false);
    [SysAbiExport(Nid = "cRVmNrJDbG8", ExportName = "sceNpTusAddAndGetVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariable(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "Q2UmHdK04c8", ExportName = "sceNpTusAddAndGetVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "ukr6FBSrkJw", ExportName = "sceNpTusAddAndGetVariableVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "lliK9T6ylJg", ExportName = "sceNpTusAddAndGetVariableVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusAddAndGetVariableVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx);
    [SysAbiExport(Nid = "BIkMmUfNKWM", ExportName = "sceNpTusCreateNpTitleCtx", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusCreateNpTitleCtx(CpuContext ctx) => NpTusState.CreateNpTitleContext(ctx, accountBased: false);

    [SysAbiExport(Nid = "0DT5bP6YzBo", ExportName = "sceNpTusDeleteMultiSlotData", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotData(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "OCozl1ZtxRY", ExportName = "sceNpTusDeleteMultiSlotDataAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotDataAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "mYhbiRtkE1Y", ExportName = "sceNpTusDeleteMultiSlotVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariable(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "0nDVqcYECoM", ExportName = "sceNpTusDeleteMultiSlotVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusDeleteMultiSlotVariableAsync(CpuContext ctx) => NpTusState.Complete(ctx);

    [SysAbiExport(Nid = "XOzszO4ONWU", ExportName = "sceNpTusGetData", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetData(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusSize);
    [SysAbiExport(Nid = "uHtKS5V1T5k", ExportName = "sceNpTusGetDataAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetDataAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusSize);
    [SysAbiExport(Nid = "GQHCksS7aLs", ExportName = "sceNpTusGetDataVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetDataVUser(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusSize);
    [SysAbiExport(Nid = "5R6kI-8f+Hk", ExportName = "sceNpTusGetDataVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetDataVUserAsync(CpuContext ctx) => NpTusState.GetData(ctx, NpTusState.DataStatusSize);

    [SysAbiExport(Nid = "DXigwIBTjWE", ExportName = "sceNpTusGetFriendsDataStatus", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatus(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "LUwvy0MOSqw", ExportName = "sceNpTusGetFriendsDataStatusAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsDataStatusAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "cy+pAALkHp8", ExportName = "sceNpTusGetFriendsVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariable(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);
    [SysAbiExport(Nid = "YFYWOwYI6DY", ExportName = "sceNpTusGetFriendsVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetFriendsVariableAsync(CpuContext ctx) => NpTusState.CompleteFriendsArray(ctx);

    [SysAbiExport(Nid = "pgcNwFHoOL4", ExportName = "sceNpTusGetMultiSlotDataStatus", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatus(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "Qyek420uZmM", ExportName = "sceNpTusGetMultiSlotDataStatusAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "NGCeFUl5ckM", ExportName = "sceNpTusGetMultiSlotDataStatusVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "bHWFSg6jvXc", ExportName = "sceNpTusGetMultiSlotDataStatusVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotDataStatusVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "F+eQlfcka98", ExportName = "sceNpTusGetMultiSlotVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariable(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "bcPB2rnhQqo", ExportName = "sceNpTusGetMultiSlotVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "uFxVYJEkcmc", ExportName = "sceNpTusGetMultiSlotVariableVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "qp-rTrq1klk", ExportName = "sceNpTusGetMultiSlotVariableVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiSlotVariableVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);

    [SysAbiExport(Nid = "NvHjFkx2rnU", ExportName = "sceNpTusGetMultiUserDataStatus", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatus(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "0zkr0T+NYvI", ExportName = "sceNpTusGetMultiUserDataStatusAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "xwJIlK0bHgA", ExportName = "sceNpTusGetMultiUserDataStatusVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusVUser(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "I5dlIKkHNkQ", ExportName = "sceNpTusGetMultiUserDataStatusVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserDataStatusVUserAsync(CpuContext ctx) => NpTusState.CompleteStatusArray(ctx);
    [SysAbiExport(Nid = "6G9+4eIb+cY", ExportName = "sceNpTusGetMultiUserVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariable(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "YRje5yEXS0U", ExportName = "sceNpTusGetMultiUserVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "zB0vaHTzA6g", ExportName = "sceNpTusGetMultiUserVariableVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableVUser(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);
    [SysAbiExport(Nid = "xZXQuNSTC6o", ExportName = "sceNpTusGetMultiUserVariableVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusGetMultiUserVariableVUserAsync(CpuContext ctx) => NpTusState.CompleteVariableArray(ctx);

    [SysAbiExport(Nid = "4NrufkNCkiE", ExportName = "sceNpTusSetData", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetData(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "G68xdfQuiyU", ExportName = "sceNpTusSetDataAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetDataAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "+RhzSuuXwxo", ExportName = "sceNpTusSetDataVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetDataVUser(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "E4BCVfx-YfM", ExportName = "sceNpTusSetDataVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetDataVUserAsync(CpuContext ctx) => NpTusState.Complete(ctx);
    [SysAbiExport(Nid = "c6aYoa47YgI", ExportName = "sceNpTusSetMultiSlotVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariable(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "5J9GGMludxY", ExportName = "sceNpTusSetMultiSlotVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusSetMultiSlotVariableAsync(CpuContext ctx) => NpTusState.SetMultiSlotVariable(ctx);
    [SysAbiExport(Nid = "ukC55HsotJ4", ExportName = "sceNpTusTryAndSetVariable", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariable(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "xQfR51i4kck", ExportName = "sceNpTusTryAndSetVariableAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "ZbitD262GhY", ExportName = "sceNpTusTryAndSetVariableVUser", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableVUser(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
    [SysAbiExport(Nid = "trZ6QGW6jHs", ExportName = "sceNpTusTryAndSetVariableVUserAsync", LibraryName = "libSceNpTusCompat", Target = Generation.Gen5)]
    public static int sceNpTusTryAndSetVariableVUserAsync(CpuContext ctx) => NpTusState.CompleteSingleVariable(ctx, tryAndSet: true);
}
