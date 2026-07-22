// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.UserService;

public static class UserServiceExports
{
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNoEvent = unchecked((int)0x80960007);
    private const int OrbisUserServiceErrorInvalidParameter = unchecked((int)0x80960009);
    private const int OrbisUserServiceErrorBufferTooShort = unchecked((int)0x8096000A);
    // Retail user-service IDs begin at 0x3E8.
    private const int PrimaryUserId = 1000;
    private const int InvalidUserId = -1;
    private const string PrimaryUserName = "SharpEmu";
    private static int _loginEventDelivered;
    private static int _foregroundUserId = PrimaryUserId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _settings = new();

    private static readonly bool _traceUserService =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USER_SERVICE"), "1", StringComparison.Ordinal);

    [SysAbiExport(
        Nid = "j3YMu1MVNNo",
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceInitialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CdWp0oHWGr0",
        ExportName = "sceUserServiceGetInitialUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetInitialUser(CpuContext ctx)
    {
        var userIdAddress = ctx[CpuRegister.Rdi];
        if (userIdAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var result = ctx.TryWriteInt32(userIdAddress, PrimaryUserId)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService($"get_initial_user out=0x{userIdAddress:X16} value={PrimaryUserId} result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "fPhymKNvK-A",
        ExportName = "sceUserServiceGetLoginUserIdList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetLoginUserIdList(CpuContext ctx)
    {
        var userIdListAddress = ctx[CpuRegister.Rdi];
        if (userIdListAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> userIds = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x04..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x08..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x0C..], InvalidUserId);
        return ctx.Memory.TryWrite(userIdListAddress, userIds)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "yH17Q6NWtVg",
        ExportName = "sceUserServiceGetEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetEvent(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        if (Interlocked.Exchange(ref _loginEventDelivered, 1) != 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNoEvent);
        }

        Span<byte> payload = stackalloc byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload[sizeof(int)..], PrimaryUserId);
        return ctx.Memory.TryWrite(eventAddress, payload)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1xxcMiGu2fo",
        ExportName = "sceUserServiceGetUserName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetUserName(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var nameAddress = ctx[CpuRegister.Rsi];
        var capacity = ctx[CpuRegister.Rdx];
        // Zero selects the current user.
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (nameAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var nameBytes = Encoding.UTF8.GetBytes(PrimaryUserName);
        if (capacity <= (ulong)nameBytes.Length)
        {
            return ctx.SetReturn(OrbisUserServiceErrorBufferTooShort);
        }

        Span<byte> output = stackalloc byte[nameBytes.Length + 1];
        nameBytes.CopyTo(output);
        var result = ctx.Memory.TryWrite(nameAddress, output)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService(
            $"get_user_name user={userId} out=0x{nameAddress:X16} capacity=0x{capacity:X} value='{PrimaryUserName}' result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "-sD02mFDBh4",
        ExportName = "sceUserServiceGetGamePresets",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetGamePresets(CpuContext ctx)
    {
        // Return deterministic defaults for the offline profile.
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rcx];
        Span<byte> defaults = stackalloc byte[0x18];
        if (resultAddress == 0 || !ctx.Memory.TryWrite(resultAddress, defaults))
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        TraceUserService(
            $"get_game_presets user={userId} title=0x{ctx[CpuRegister.Rsi]:X16} " +
            $"key=0x{ctx[CpuRegister.R9]:X} out=0x{resultAddress:X16} result=0x00000000");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "D-CzAxQL0XI",
        ExportName = "sceUserServiceGetPlatformPrivacySetting",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetPlatformPrivacySetting(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (parameterId != 1000)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(valueAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "bwFjS+bX9mA",
        ExportName = "sceUserServiceTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceTerminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _loginEventDelivered, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-3Y5GO+-i78",
        ExportName = "sceUserServiceGetAccessibilityTriggerEffect",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityTriggerEffect(CpuContext ctx)
    {
        return WriteAccessibilitySetting(ctx);
    }

    [SysAbiExport(
        Nid = "qWYHOFwqCxY",
        ExportName = "sceUserServiceGetAccessibilityVibration",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityVibration(CpuContext ctx)
    {
        return WriteAccessibilitySetting(ctx);
    }

    [SysAbiExport(
        Nid = "woNpu+45RLk",
        ExportName = "sceUserServiceGetAgeLevel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAgeLevel(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var ageLevelAddress = ctx[CpuRegister.Rsi];
        if (userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (ageLevelAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        // Report an adult account so titles skip parental-restriction paths.
        return ctx.TryWriteInt32(ageLevelAddress, 21)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // PS5 firmware 8.00 user-service exports.
    [SysAbiExport(Nid = "GC18r56Bp7Y", ExportName = "sceUserServiceDestroyUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceDestroyUser(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(Nid = "g6ojqW3c8Z4", ExportName = "sceUserServiceGetAccessibilityKeyremapData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityKeyremapData(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAccessibilityKeyremapData", 0);

    [SysAbiExport(Nid = "xrtki9sUopg", ExportName = "sceUserServiceGetAccessibilityKeyremapEnable", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityKeyremapEnable(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAccessibilityKeyremapEnable", 0);

    [SysAbiExport(Nid = "ZKJtxdgvzwg", ExportName = "sceUserServiceGetAccessibilityPressAndHoldDelay", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityPressAndHoldDelay(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAccessibilityPressAndHoldDelay", 0);

    [SysAbiExport(Nid = "1zDEFUmBdoo", ExportName = "sceUserServiceGetAccessibilityZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoom(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAccessibilityZoom", 0);

    [SysAbiExport(Nid = "hD-H81EN9Vg", ExportName = "sceUserServiceGetAccessibilityZoomEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoomEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAccessibilityZoomEnabled", 0);

    [SysAbiExport(Nid = "7zu3F7ykVeo", ExportName = "sceUserServiceGetAccountRemarks", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAccountRemarks(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "oJzfZxZchX4", ExportName = "sceUserServiceGetAgeVerified", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAgeVerified(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAgeVerified", 1);

    [SysAbiExport(Nid = "6r4hDyrRUGg", ExportName = "sceUserServiceGetAppearOfflineSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAppearOfflineSetting(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAppearOfflineSetting", 0);

    [SysAbiExport(Nid = "PhXZbj4wVhE", ExportName = "sceUserServiceGetAppSortOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAppSortOrder(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAppSortOrder", 0);

    [SysAbiExport(Nid = "nqDEnj7M0QE", ExportName = "sceUserServiceGetAutoLoginEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetAutoLoginEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetAutoLoginEnabled", 1);

    [SysAbiExport(Nid = "WGXOvoUwrOs", ExportName = "sceUserServiceGetCreatedVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetCreatedVersion(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetCreatedVersion", 0);

    [SysAbiExport(Nid = "5G-MA1x5utw", ExportName = "sceUserServiceGetCurrentUserGroupIndex", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetCurrentUserGroupIndex(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetCurrentUserGroupIndex", 0);

    [SysAbiExport(Nid = "1U5cFdTdso0", ExportName = "sceUserServiceGetDefaultNewUserGroupName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetDefaultNewUserGroupName(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "NiTGNLkBc-Q", ExportName = "sceUserServiceGetDeletedUserInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetDeletedUserInfo(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetDeletedUserInfo", 0);

    [SysAbiExport(Nid = "RdpmnHZ3Q9M", ExportName = "sceUserServiceGetDiscPlayerFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetDiscPlayerFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetDiscPlayerFlag", 0);

    [SysAbiExport(Nid = "zs60MvClEkc", ExportName = "sceUserServiceGetEventCalendarType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventCalendarType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventCalendarType", 0);

    [SysAbiExport(Nid = "TwELPoqW8tA", ExportName = "sceUserServiceGetEventFilterTeamEvent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventFilterTeamEvent(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventFilterTeamEvent", 0);

    [SysAbiExport(Nid = "ygVuZ1Hb-nc", ExportName = "sceUserServiceGetEventSortEvent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventSortEvent(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventSortEvent", 0);

    [SysAbiExport(Nid = "aaC3005VtY4", ExportName = "sceUserServiceGetEventSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventSortTitle(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventSortTitle", 0);

    [SysAbiExport(Nid = "kUaJUV1b+PM", ExportName = "sceUserServiceGetEventUiFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventUiFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventUiFlag", 0);

    [SysAbiExport(Nid = "3wTtZ3c2+0A", ExportName = "sceUserServiceGetEventVsh", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetEventVsh(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetEventVsh", 0);

    [SysAbiExport(Nid = "uRU0lQe+9xY", ExportName = "sceUserServiceGetFaceRecognitionDeleteCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFaceRecognitionDeleteCount(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFaceRecognitionDeleteCount", 0);

    [SysAbiExport(Nid = "fbCC0yo2pVQ", ExportName = "sceUserServiceGetFaceRecognitionRegisterCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFaceRecognitionRegisterCount(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFaceRecognitionRegisterCount", 0);

    [SysAbiExport(Nid = "k-7kxXGr+r0", ExportName = "sceUserServiceGetFileBrowserFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileBrowserFilter(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileBrowserFilter", 0);

    [SysAbiExport(Nid = "fCBpPJbELDk", ExportName = "sceUserServiceGetFileBrowserSortContent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileBrowserSortContent(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileBrowserSortContent", 0);

    [SysAbiExport(Nid = "UYR9fcPXDUE", ExportName = "sceUserServiceGetFileBrowserSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileBrowserSortTitle(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileBrowserSortTitle", 0);

    [SysAbiExport(Nid = "FsOBy3JfbrM", ExportName = "sceUserServiceGetFileSelectorFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileSelectorFilter(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileSelectorFilter", 0);

    [SysAbiExport(Nid = "IAB7wscPwio", ExportName = "sceUserServiceGetFileSelectorSortContent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileSelectorSortContent(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileSelectorSortContent", 0);

    [SysAbiExport(Nid = "6Et3d4p1u8c", ExportName = "sceUserServiceGetFileSelectorSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFileSelectorSortTitle(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFileSelectorSortTitle", 0);

    [SysAbiExport(Nid = "eNb53LQJmIM", ExportName = "sceUserServiceGetForegroundUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetForegroundUser(CpuContext ctx) => WritePrimaryUser(ctx);

    [SysAbiExport(Nid = "eMGF77hKF6U", ExportName = "sceUserServiceGetFriendCustomListLastFocus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFriendCustomListLastFocus(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFriendCustomListLastFocus", 0);

    [SysAbiExport(Nid = "wBGmrRTUC14", ExportName = "sceUserServiceGetFriendFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetFriendFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetFriendFlag", 0);

    [SysAbiExport(Nid = "64PEUYPuK98", ExportName = "sceUserServiceGetGlsAccessTokenNiconicoLive", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsAccessTokenNiconicoLive(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "8Y+aDvVGLiw", ExportName = "sceUserServiceGetGlsAccessTokenTwitch", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsAccessTokenTwitch(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "V7ZG7V+dd08", ExportName = "sceUserServiceGetGlsAccessTokenUstream", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsAccessTokenUstream(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "QqZ1A3vukFM", ExportName = "sceUserServiceGetGlsAnonymousUserId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsAnonymousUserId(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "FP4TKrdRXXM", ExportName = "sceUserServiceGetGlsBcTags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBcTags(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "yX-TpbFAYxo", ExportName = "sceUserServiceGetGlsBcTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBcTitle(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "Mm4+PSflHbM", ExportName = "sceUserServiceGetGlsBroadcastChannel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBroadcastChannel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsBroadcastChannel", 0);

    [SysAbiExport(Nid = "NpEYVDOyjRk", ExportName = "sceUserServiceGetGlsBroadcastersComment", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBroadcastersComment(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "WvM21J1SI0U", ExportName = "sceUserServiceGetGlsBroadcastersCommentColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBroadcastersCommentColor(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsBroadcastersCommentColor", 0);

    [SysAbiExport(Nid = "HxNRiCWfVFw", ExportName = "sceUserServiceGetGlsBroadcastService", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBroadcastService(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsBroadcastService", 0);

    [SysAbiExport(Nid = "6ZQ4kfhM37c", ExportName = "sceUserServiceGetGlsBroadcastUiLayout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsBroadcastUiLayout(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsBroadcastUiLayout", 0);

    [SysAbiExport(Nid = "YmmFiEoegko", ExportName = "sceUserServiceGetGlsCamCrop", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCamCrop(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCamCrop", 0);

    [SysAbiExport(Nid = "Y5U66nk0bUc", ExportName = "sceUserServiceGetGlsCameraBgFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraBgFilter(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraBgFilter", 0);

    [SysAbiExport(Nid = "LbQ-jU9jOsk", ExportName = "sceUserServiceGetGlsCameraBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraBrightness(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraBrightness", 0);

    [SysAbiExport(Nid = "91kOKRnkrhE", ExportName = "sceUserServiceGetGlsCameraChromaKeyLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraChromaKeyLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraChromaKeyLevel", 0);

    [SysAbiExport(Nid = "1ppzHkQhiNs", ExportName = "sceUserServiceGetGlsCameraContrast", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraContrast(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraContrast", 0);

    [SysAbiExport(Nid = "jIe8ZED06XI", ExportName = "sceUserServiceGetGlsCameraDepthLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraDepthLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraDepthLevel", 0);

    [SysAbiExport(Nid = "0H51EFxR3mc", ExportName = "sceUserServiceGetGlsCameraEdgeLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraEdgeLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraEdgeLevel", 0);

    [SysAbiExport(Nid = "rLEw4n5yI40", ExportName = "sceUserServiceGetGlsCameraEffect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraEffect(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraEffect", 0);

    [SysAbiExport(Nid = "+Prbx5iagl0", ExportName = "sceUserServiceGetGlsCameraEliminationLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraEliminationLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraEliminationLevel", 0);

    [SysAbiExport(Nid = "F0wuEvioQd4", ExportName = "sceUserServiceGetGlsCameraPosition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraPosition(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraPosition", 0);

    [SysAbiExport(Nid = "GkcHilidQHk", ExportName = "sceUserServiceGetGlsCameraReflection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraReflection(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraReflection", 0);

    [SysAbiExport(Nid = "zBLxX8JRMoo", ExportName = "sceUserServiceGetGlsCameraSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraSize(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraSize", 0);

    [SysAbiExport(Nid = "O1nURsxyYmk", ExportName = "sceUserServiceGetGlsCameraTransparency", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCameraTransparency(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsCameraTransparency", 0);

    [SysAbiExport(Nid = "4TOEFdmFVcI", ExportName = "sceUserServiceGetGlsCommunityId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsCommunityId(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "+29DSndZ9Oc", ExportName = "sceUserServiceGetGlsFloatingMessage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsFloatingMessage(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "ki81gh1yZDM", ExportName = "sceUserServiceGetGlsHintFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsHintFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsHintFlag", 0);

    [SysAbiExport(Nid = "zR+J2PPJgSU", ExportName = "sceUserServiceGetGlsInitSpectating", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsInitSpectating(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsInitSpectating", 0);

    [SysAbiExport(Nid = "8IqdtMmc5Uc", ExportName = "sceUserServiceGetGlsIsCameraHidden", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsCameraHidden(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsCameraHidden", 0);

    [SysAbiExport(Nid = "f5lAVp0sFNo", ExportName = "sceUserServiceGetGlsIsFacebookEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsFacebookEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsFacebookEnabled", 0);

    [SysAbiExport(Nid = "W3neFYAvZss", ExportName = "sceUserServiceGetGlsIsMuteEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsMuteEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsMuteEnabled", 0);

    [SysAbiExport(Nid = "4IXuUaBxzEg", ExportName = "sceUserServiceGetGlsIsRecDisabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsRecDisabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsRecDisabled", 0);

    [SysAbiExport(Nid = "hyW5w855fk4", ExportName = "sceUserServiceGetGlsIsRecievedMessageHidden", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsRecievedMessageHidden(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsRecievedMessageHidden", 0);

    [SysAbiExport(Nid = "Xp9Px0V0tas", ExportName = "sceUserServiceGetGlsIsTwitterEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsIsTwitterEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsIsTwitterEnabled", 0);

    [SysAbiExport(Nid = "uMkqgm70thg", ExportName = "sceUserServiceGetGlsLanguageFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLanguageFilter(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLanguageFilter", 0);

    [SysAbiExport(Nid = "LyXzCtzleAQ", ExportName = "sceUserServiceGetGlsLfpsSortOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLfpsSortOrder(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLfpsSortOrder", 0);

    [SysAbiExport(Nid = "CvwCMJtzp1I", ExportName = "sceUserServiceGetGlsLiveQuality", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLiveQuality(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLiveQuality", 0);

    [SysAbiExport(Nid = "Z+dzNaClq7w", ExportName = "sceUserServiceGetGlsLiveQuality2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLiveQuality2(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLiveQuality2", 0);

    [SysAbiExport(Nid = "X5On-7hVCs0", ExportName = "sceUserServiceGetGlsLiveQuality3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLiveQuality3(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLiveQuality3", 0);

    [SysAbiExport(Nid = "+qAE4tRMrXk", ExportName = "sceUserServiceGetGlsLiveQuality4", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLiveQuality4(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLiveQuality4", 0);

    [SysAbiExport(Nid = "4ys00CRU6V8", ExportName = "sceUserServiceGetGlsLiveQuality5", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsLiveQuality5(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsLiveQuality5", 0);

    [SysAbiExport(Nid = "75cwn1y2ffk", ExportName = "sceUserServiceGetGlsMessageFilterLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsMessageFilterLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsMessageFilterLevel", 0);

    [SysAbiExport(Nid = "+NVJMeISrM4", ExportName = "sceUserServiceGetGlsTtsFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsTtsFlags(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsTtsFlags", 0);

    [SysAbiExport(Nid = "eQrBbMmZ1Ss", ExportName = "sceUserServiceGetGlsTtsPitch", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsTtsPitch(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsTtsPitch", 0);

    [SysAbiExport(Nid = "BCDA6jn4HVY", ExportName = "sceUserServiceGetGlsTtsSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsTtsSpeed(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsTtsSpeed", 0);

    [SysAbiExport(Nid = "SBurFYk7M74", ExportName = "sceUserServiceGetGlsTtsVolume", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetGlsTtsVolume(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetGlsTtsVolume", 0);

    [SysAbiExport(Nid = "YVzw4T1fnS4", ExportName = "sceUserServiceGetHmuBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetHmuBrightness(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetHmuBrightness", 0);

    [SysAbiExport(Nid = "O8ONJV3b8jg", ExportName = "sceUserServiceGetHmuZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetHmuZoom(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetHmuZoom", 0);

    [SysAbiExport(Nid = "VjLkKY0CQew", ExportName = "sceUserServiceGetHoldAudioOutDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetHoldAudioOutDevice(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetHoldAudioOutDevice", 0);

    [SysAbiExport(Nid = "J-KEr4gUEvQ", ExportName = "sceUserServiceGetHomeDirectory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetHomeDirectory(CpuContext ctx) => WriteUserText(ctx, "/user/home/1000");

    [SysAbiExport(Nid = "yLNm3n7fgpw", ExportName = "sceUserServiceGetImeAutoCapitalEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImeAutoCapitalEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImeAutoCapitalEnabled", 0);

    [SysAbiExport(Nid = "gnViUj0ab8U", ExportName = "sceUserServiceGetImeInitFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImeInitFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImeInitFlag", 0);

    [SysAbiExport(Nid = "zru8Zhuy1UY", ExportName = "sceUserServiceGetImeInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImeInputType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImeInputType", 0);

    [SysAbiExport(Nid = "2-b8QbU+HNc", ExportName = "sceUserServiceGetImeLastUnit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImeLastUnit(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImeLastUnit", 0);

    [SysAbiExport(Nid = "NNblpSGxrY8", ExportName = "sceUserServiceGetImePointerMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImePointerMode(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImePointerMode", 0);

    [SysAbiExport(Nid = "YUhBM-ASEcA", ExportName = "sceUserServiceGetImePredictiveTextEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImePredictiveTextEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImePredictiveTextEnabled", 0);

    [SysAbiExport(Nid = "IWEla-izyTs", ExportName = "sceUserServiceGetImeRunCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetImeRunCount(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetImeRunCount", 0);

    [SysAbiExport(Nid = "PQlF4cjUz9U", ExportName = "sceUserServiceGetIPDLeft", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetIPDLeft(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetIPDLeft", 0);

    [SysAbiExport(Nid = "UDx67PTzB20", ExportName = "sceUserServiceGetIPDRight", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetIPDRight(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetIPDRight", 0);

    [SysAbiExport(Nid = "IKk3EGj+xRI", ExportName = "sceUserServiceGetIsFakePlus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetIsFakePlus(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetIsFakePlus", 0);

    [SysAbiExport(Nid = "MzVmbq2IVCo", ExportName = "sceUserServiceGetIsQuickSignup", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetIsQuickSignup(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetIsQuickSignup", 0);

    [SysAbiExport(Nid = "Lgi5A4fQwHc", ExportName = "sceUserServiceGetIsRemotePlayAllowed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetIsRemotePlayAllowed(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetIsRemotePlayAllowed", 1);

    [SysAbiExport(Nid = "u-dCVE6fQAU", ExportName = "sceUserServiceGetJapaneseInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetJapaneseInputType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetJapaneseInputType", 0);

    [SysAbiExport(Nid = "Ta52bXx5Tek", ExportName = "sceUserServiceGetKeyboardType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetKeyboardType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetKeyboardType", 0);

    [SysAbiExport(Nid = "XUT7ad-BUMc", ExportName = "sceUserServiceGetKeyRepeatSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetKeyRepeatSpeed(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetKeyRepeatSpeed", 0);

    [SysAbiExport(Nid = "iWpzXixD0UE", ExportName = "sceUserServiceGetKeyRepeatStartingTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetKeyRepeatStartingTime(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetKeyRepeatStartingTime", 0);

    [SysAbiExport(Nid = "uAPBw-7641s", ExportName = "sceUserServiceGetKratosPrimaryUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetKratosPrimaryUser(CpuContext ctx) => WritePrimaryUser(ctx);

    [SysAbiExport(Nid = "4nUbGGBcGco", ExportName = "sceUserServiceGetLastLoginOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetLastLoginOrder(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetLastLoginOrder", 0);

    [SysAbiExport(Nid = "q+7UTGELzj4", ExportName = "sceUserServiceGetLightBarBaseBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetLightBarBaseBrightness(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetLightBarBaseBrightness", 0);

    [SysAbiExport(Nid = "QNk7qD4dlD4", ExportName = "sceUserServiceGetLoginFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetLoginFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetLoginFlag", 0);

    [SysAbiExport(Nid = "YfDgKz5SolU", ExportName = "sceUserServiceGetMicLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetMicLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetMicLevel", 50);

    [SysAbiExport(Nid = "sukPd-xBDjM", ExportName = "sceUserServiceGetMouseHandType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetMouseHandType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetMouseHandType", 0);

    [SysAbiExport(Nid = "Y5zgw69ndoE", ExportName = "sceUserServiceGetMousePointerSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetMousePointerSpeed(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetMousePointerSpeed", 0);

    [SysAbiExport(Nid = "3oqgIFPVkV8", ExportName = "sceUserServiceGetNotificationBehavior", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNotificationBehavior(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNotificationBehavior", 0);

    [SysAbiExport(Nid = "5iqtUryI-hI", ExportName = "sceUserServiceGetNotificationSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNotificationSettings(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNotificationSettings", 0);

    [SysAbiExport(Nid = "6dfDreosXGY", ExportName = "sceUserServiceGetNpAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpAccountId(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpAccountId", 0);

    [SysAbiExport(Nid = "Veo1PbQZzG4", ExportName = "sceUserServiceGetNpAccountUpgradeFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpAccountUpgradeFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpAccountUpgradeFlag", 0);

    [SysAbiExport(Nid = "OySMIASmH0Y", ExportName = "sceUserServiceGetNpAge", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpAge(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpAge", 21);

    [SysAbiExport(Nid = "nlOWAiRyxkA", ExportName = "sceUserServiceGetNpAuthErrorFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpAuthErrorFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpAuthErrorFlag", 0);

    [SysAbiExport(Nid = "8vhI2SwEfes", ExportName = "sceUserServiceGetNpCountryCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpCountryCode(CpuContext ctx) => WriteUserText(ctx, "US");

    [SysAbiExport(Nid = "YyC7QCLoSxY", ExportName = "sceUserServiceGetNpDateOfBirth", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpDateOfBirth(CpuContext ctx) => WriteDateOfBirth(ctx);

    [SysAbiExport(Nid = "-YcNkLzNGmY", ExportName = "sceUserServiceGetNpEnv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpEnv(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpEnv", 0);

    [SysAbiExport(Nid = "J4ten1IOe5w", ExportName = "sceUserServiceGetNpLanguageCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpLanguageCode(CpuContext ctx) => WriteUserText(ctx, "en");

    [SysAbiExport(Nid = "ruF+U6DexT4", ExportName = "sceUserServiceGetNpLanguageCode2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpLanguageCode2(CpuContext ctx) => WriteUserText(ctx, "en-US");

    [SysAbiExport(Nid = "W5RgPUuv35Y", ExportName = "sceUserServiceGetNpLoginId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpLoginId(CpuContext ctx) => WriteUserText(ctx, "sharpemu@localhost");

    [SysAbiExport(Nid = "j-CnRJn3K+Q", ExportName = "sceUserServiceGetNpMAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpMAccountId(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpMAccountId", 0);

    [SysAbiExport(Nid = "5Ds-y6A1nAI", ExportName = "sceUserServiceGetNpNpId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpNpId(CpuContext ctx) => WriteUserText(ctx, "SharpEmu");

    [SysAbiExport(Nid = "auc64RJAcus", ExportName = "sceUserServiceGetNpOfflineAccountAdult", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpOfflineAccountAdult(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpOfflineAccountAdult", 1);

    [SysAbiExport(Nid = "fEy0EW0AR18", ExportName = "sceUserServiceGetNpOfflineAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpOfflineAccountId(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpOfflineAccountId", 0);

    [SysAbiExport(Nid = "if-BeWwY0aU", ExportName = "sceUserServiceGetNpOnlineId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpOnlineId(CpuContext ctx) => WriteUserText(ctx, "SharpEmu");

    [SysAbiExport(Nid = "wCGnkXhpRL4", ExportName = "sceUserServiceGetNpSubAccount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetNpSubAccount(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetNpSubAccount", 0);

    [SysAbiExport(Nid = "zNvCnHpkPmM", ExportName = "sceUserServiceGetPadSpeakerVolume", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPadSpeakerVolume(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPadSpeakerVolume", 100);

    [SysAbiExport(Nid = "lXKtAHMrwig", ExportName = "sceUserServiceGetParentalBdAge", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalBdAge(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalBdAge", 0);

    [SysAbiExport(Nid = "t04S4aC0LCM", ExportName = "sceUserServiceGetParentalBrowser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalBrowser(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalBrowser", 0);

    [SysAbiExport(Nid = "5vtFYXFJ7OU", ExportName = "sceUserServiceGetParentalDvd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalDvd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalDvd", 0);

    [SysAbiExport(Nid = "d9DOmIk9-y4", ExportName = "sceUserServiceGetParentalDvdRegion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalDvdRegion(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalDvdRegion", 0);

    [SysAbiExport(Nid = "OdiXSuoIK7c", ExportName = "sceUserServiceGetParentalGame", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalGame(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalGame", 0);

    [SysAbiExport(Nid = "oXARzvLAiyc", ExportName = "sceUserServiceGetParentalGameAgeLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalGameAgeLevel(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalGameAgeLevel", 0);

    [SysAbiExport(Nid = "yXvfR+AcgaY", ExportName = "sceUserServiceGetParentalMorpheus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetParentalMorpheus(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetParentalMorpheus", 0);

    [SysAbiExport(Nid = "UeIv6aNXlOw", ExportName = "sceUserServiceGetPartyMuteList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPartyMuteList(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPartyMuteList", 0);

    [SysAbiExport(Nid = "aq1jwlgyOV4", ExportName = "sceUserServiceGetPartyMuteListA", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPartyMuteListA(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPartyMuteListA", 0);

    [SysAbiExport(Nid = "yARnQeWzhdM", ExportName = "sceUserServiceGetPartySettingFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPartySettingFlags(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPartySettingFlags", 0);

    [SysAbiExport(Nid = "X5rJZNDZ2Ss", ExportName = "sceUserServiceGetPasscode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPasscode(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "m1h-E6BU6CA", ExportName = "sceUserServiceGetPbtcAdditionalTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcAdditionalTime(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcAdditionalTime", 0);

    [SysAbiExport(Nid = "HsOlaoGngDc", ExportName = "sceUserServiceGetPbtcFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcFlag", 0);

    [SysAbiExport(Nid = "3DuTkVXaj9Y", ExportName = "sceUserServiceGetPbtcFridayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcFridayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcFridayDuration", 0);

    [SysAbiExport(Nid = "5dM-i0Ox2d8", ExportName = "sceUserServiceGetPbtcFridayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcFridayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcFridayHoursEnd", 0);

    [SysAbiExport(Nid = "vcd5Kfs1QeA", ExportName = "sceUserServiceGetPbtcFridayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcFridayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcFridayHoursStart", 0);

    [SysAbiExport(Nid = "Q5Um9Yri-VA", ExportName = "sceUserServiceGetPbtcMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcMode(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcMode", 0);

    [SysAbiExport(Nid = "NnvYm9PFJiw", ExportName = "sceUserServiceGetPbtcMondayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcMondayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcMondayDuration", 0);

    [SysAbiExport(Nid = "42K0F17ml9c", ExportName = "sceUserServiceGetPbtcMondayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcMondayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcMondayHoursEnd", 0);

    [SysAbiExport(Nid = "WunW7G5bHYo", ExportName = "sceUserServiceGetPbtcMondayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcMondayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcMondayHoursStart", 0);

    [SysAbiExport(Nid = "JrFGcFUL0lg", ExportName = "sceUserServiceGetPbtcPlayTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcPlayTime(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcPlayTime", 0);

    [SysAbiExport(Nid = "R6ldE-2ON1w", ExportName = "sceUserServiceGetPbtcPlayTimeLastUpdated", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcPlayTimeLastUpdated(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcPlayTimeLastUpdated", 0);

    [SysAbiExport(Nid = "DembpCGx9DU", ExportName = "sceUserServiceGetPbtcSaturdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSaturdayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSaturdayDuration", 0);

    [SysAbiExport(Nid = "Cf8NftzheE4", ExportName = "sceUserServiceGetPbtcSaturdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSaturdayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSaturdayHoursEnd", 0);

    [SysAbiExport(Nid = "+1qj-S-k6m0", ExportName = "sceUserServiceGetPbtcSaturdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSaturdayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSaturdayHoursStart", 0);

    [SysAbiExport(Nid = "JVMIyR8vDec", ExportName = "sceUserServiceGetPbtcSundayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSundayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSundayDuration", 0);

    [SysAbiExport(Nid = "J+bKHRzY4nw", ExportName = "sceUserServiceGetPbtcSundayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSundayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSundayHoursEnd", 0);

    [SysAbiExport(Nid = "J+cECJ7CBFM", ExportName = "sceUserServiceGetPbtcSundayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcSundayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcSundayHoursStart", 0);

    [SysAbiExport(Nid = "z-hJNdfLRN0", ExportName = "sceUserServiceGetPbtcThursdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcThursdayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcThursdayDuration", 0);

    [SysAbiExport(Nid = "BkOBCo0sdLM", ExportName = "sceUserServiceGetPbtcThursdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcThursdayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcThursdayHoursEnd", 0);

    [SysAbiExport(Nid = "T70Qyzo51uw", ExportName = "sceUserServiceGetPbtcThursdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcThursdayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcThursdayHoursStart", 0);

    [SysAbiExport(Nid = "UPDgXiV1Zp0", ExportName = "sceUserServiceGetPbtcTuesdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcTuesdayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcTuesdayDuration", 0);

    [SysAbiExport(Nid = "Kpds+6CpTus", ExportName = "sceUserServiceGetPbtcTuesdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcTuesdayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcTuesdayHoursEnd", 0);

    [SysAbiExport(Nid = "azCh0Ibz8ls", ExportName = "sceUserServiceGetPbtcTuesdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcTuesdayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcTuesdayHoursStart", 0);

    [SysAbiExport(Nid = "NjEMsEjXlTY", ExportName = "sceUserServiceGetPbtcTzOffset", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcTzOffset(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcTzOffset", 0);

    [SysAbiExport(Nid = "VwF4r--aouQ", ExportName = "sceUserServiceGetPbtcWednesdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcWednesdayDuration(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcWednesdayDuration", 0);

    [SysAbiExport(Nid = "nxGZSi5FEwc", ExportName = "sceUserServiceGetPbtcWednesdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcWednesdayHoursEnd(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcWednesdayHoursEnd", 0);

    [SysAbiExport(Nid = "7Wes8MVwuoM", ExportName = "sceUserServiceGetPbtcWednesdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPbtcWednesdayHoursStart(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPbtcWednesdayHoursStart", 0);

    [SysAbiExport(Nid = "yAWUqugjPvE", ExportName = "sceUserServiceGetPlayTogetherFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPlayTogetherFlags(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetPlayTogetherFlags", 0);

    [SysAbiExport(Nid = "VSQR9qYpaCM", ExportName = "sceUserServiceGetPsnPasswordForDebug", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetPsnPasswordForDebug(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "OVdVBcejvmQ", ExportName = "sceUserServiceGetRegisteredHomeUserIdList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetRegisteredHomeUserIdList(CpuContext ctx) => WriteRegisteredUsers(ctx);

    [SysAbiExport(Nid = "5EiQCnL2G1Y", ExportName = "sceUserServiceGetRegisteredUserIdList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetRegisteredUserIdList(CpuContext ctx) => WriteRegisteredUsers(ctx);

    [SysAbiExport(Nid = "UxrSdH6jA3E", ExportName = "sceUserServiceGetSaveDataAutoUpload", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSaveDataAutoUpload(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSaveDataAutoUpload", 0);

    [SysAbiExport(Nid = "pVsEKLk5bIA", ExportName = "sceUserServiceGetSaveDataSort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSaveDataSort(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSaveDataSort", 0);

    [SysAbiExport(Nid = "88+nqBN-SQM", ExportName = "sceUserServiceGetSaveDataTutorialFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSaveDataTutorialFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSaveDataTutorialFlag", 0);

    [SysAbiExport(Nid = "xzQVBcKYoI8", ExportName = "sceUserServiceGetSecureHomeDirectory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSecureHomeDirectory(CpuContext ctx) => WriteUserText(ctx, "/user/home/1000");

    [SysAbiExport(Nid = "zsJcWtE81Rk", ExportName = "sceUserServiceGetShareButtonAssign", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetShareButtonAssign(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetShareButtonAssign", 0);

    [SysAbiExport(Nid = "NjhK36GfEGQ", ExportName = "sceUserServiceGetShareDailymotionAccessToken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetShareDailymotionAccessToken(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "t-I2Lbj8a+0", ExportName = "sceUserServiceGetShareDailymotionRefreshToken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetShareDailymotionRefreshToken(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "lrPF-kNBPro", ExportName = "sceUserServiceGetSharePlayFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSharePlayFlags(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSharePlayFlags", 0);

    [SysAbiExport(Nid = "eC88db1i-f8", ExportName = "sceUserServiceGetSharePlayFramerateHost", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSharePlayFramerateHost(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSharePlayFramerateHost", 0);

    [SysAbiExport(Nid = "ttiSviAPLXI", ExportName = "sceUserServiceGetSharePlayResolutionHost", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSharePlayResolutionHost(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSharePlayResolutionHost", 0);

    [SysAbiExport(Nid = "YnXM2saZkl4", ExportName = "sceUserServiceGetShareStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetShareStatus(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetShareStatus", 0);

    [SysAbiExport(Nid = "wMtSHLNAVj0", ExportName = "sceUserServiceGetShareStatus2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetShareStatus2(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetShareStatus2", 0);

    [SysAbiExport(Nid = "8no2rlDjl7o", ExportName = "sceUserServiceGetSystemLoggerHashedAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSystemLoggerHashedAccountId(CpuContext ctx) => WriteUserText(ctx, "0000000000000000");

    [SysAbiExport(Nid = "vW2qWKYmlvw", ExportName = "sceUserServiceGetSystemLoggerHashedAccountIdClockType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSystemLoggerHashedAccountIdClockType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSystemLoggerHashedAccountIdClockType", 0);

    [SysAbiExport(Nid = "Zr4h+Bbx0do", ExportName = "sceUserServiceGetSystemLoggerHashedAccountIdParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSystemLoggerHashedAccountIdParam(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSystemLoggerHashedAccountIdParam", 0);

    [SysAbiExport(Nid = "cf9BIMy4muY", ExportName = "sceUserServiceGetSystemLoggerHashedAccountIdTtl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetSystemLoggerHashedAccountIdTtl(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetSystemLoggerHashedAccountIdTtl", 0);

    [SysAbiExport(Nid = "AGDKupLjTZM", ExportName = "sceUserServiceGetTeamShowAboutTeam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetTeamShowAboutTeam(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetTeamShowAboutTeam", 0);

    [SysAbiExport(Nid = "EZJecX+WvJc", ExportName = "sceUserServiceGetThemeBgImageDimmer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeBgImageDimmer(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeBgImageDimmer", 0);

    [SysAbiExport(Nid = "POVfvCDcVUw", ExportName = "sceUserServiceGetThemeBgImageWaveColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeBgImageWaveColor(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeBgImageWaveColor", 0);

    [SysAbiExport(Nid = "qI2HG1pV+OA", ExportName = "sceUserServiceGetThemeBgImageZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeBgImageZoom(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeBgImageZoom", 0);

    [SysAbiExport(Nid = "x6m8P9DBPSc", ExportName = "sceUserServiceGetThemeEntitlementId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeEntitlementId(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "K8Nh6fhmYkc", ExportName = "sceUserServiceGetThemeHomeShareOwner", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeHomeShareOwner(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeHomeShareOwner", 0);

    [SysAbiExport(Nid = "EgEPXDie5XQ", ExportName = "sceUserServiceGetThemeTextShadow", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeTextShadow(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeTextShadow", 0);

    [SysAbiExport(Nid = "WaHZGp0Vn2k", ExportName = "sceUserServiceGetThemeWaveColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetThemeWaveColor(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetThemeWaveColor", 0);

    [SysAbiExport(Nid = "IxCpDYsiTX0", ExportName = "sceUserServiceGetTopMenuLimitItem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetTopMenuLimitItem(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetTopMenuLimitItem", 0);

    [SysAbiExport(Nid = "SykFcJEGvz4", ExportName = "sceUserServiceGetTopMenuNotificationFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetTopMenuNotificationFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetTopMenuNotificationFlag", 0);

    [SysAbiExport(Nid = "MG+ObGDYePw", ExportName = "sceUserServiceGetTopMenuTutorialFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetTopMenuTutorialFlag(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetTopMenuTutorialFlag", 0);

    [SysAbiExport(Nid = "oXVAQutr3Ns", ExportName = "sceUserServiceGetTraditionalChineseInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetTraditionalChineseInputType(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetTraditionalChineseInputType", 0);

    [SysAbiExport(Nid = "lUoqwTQu4Go", ExportName = "sceUserServiceGetUserColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetUserColor(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetUserColor", 0);

    [SysAbiExport(Nid = "1+nxJ4awLH8", ExportName = "sceUserServiceGetUserGroupName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetUserGroupName(CpuContext ctx) => WriteUserText(ctx, "");

    [SysAbiExport(Nid = "ga2z3AAn8XI", ExportName = "sceUserServiceGetUserGroupNameList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetUserGroupNameList(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetUserGroupNameList", 0);

    [SysAbiExport(Nid = "xzdhJrL3Hns", ExportName = "sceUserServiceGetUserGroupNum", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetUserGroupNum(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetUserGroupNum", 0);

    [SysAbiExport(Nid = "RJX7T4sjNgI", ExportName = "sceUserServiceGetUserStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetUserStatus(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetUserStatus", 1);

    [SysAbiExport(Nid = "O0mtfoE5Cek", ExportName = "sceUserServiceGetVibrationEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVibrationEnabled(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVibrationEnabled", 1);

    [SysAbiExport(Nid = "T4L2vVa0zuA", ExportName = "sceUserServiceGetVoiceRecognitionLastUsedOsk", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVoiceRecognitionLastUsedOsk(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVoiceRecognitionLastUsedOsk", 0);

    [SysAbiExport(Nid = "-jRGLt2Dbe4", ExportName = "sceUserServiceGetVoiceRecognitionTutorialState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVoiceRecognitionTutorialState(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVoiceRecognitionTutorialState", 0);

    [SysAbiExport(Nid = "ld396XJQPgM", ExportName = "sceUserServiceGetVolumeForController", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVolumeForController(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVolumeForController", 100);

    [SysAbiExport(Nid = "TEsQ0HWJ8R4", ExportName = "sceUserServiceGetVolumeForGenericUSB", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVolumeForGenericUSB(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVolumeForGenericUSB", 100);

    [SysAbiExport(Nid = "r2QuHIT8u9I", ExportName = "sceUserServiceGetVolumeForMorpheusSidetone", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVolumeForMorpheusSidetone(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVolumeForMorpheusSidetone", 50);

    [SysAbiExport(Nid = "3UZADLBXpiA", ExportName = "sceUserServiceGetVolumeForSidetone", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceGetVolumeForSidetone(CpuContext ctx) => WriteStoredSetting(ctx, "sceUserServiceGetVolumeForSidetone", 50);

    [SysAbiExport(Nid = "az-0R6eviZ0", ExportName = "sceUserServiceInitialize2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceInitialize2(CpuContext ctx) => InitializeSecondGeneration(ctx);

    [SysAbiExport(Nid = "FnWkLNOmJXw", ExportName = "sceUserServiceIsGuestUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsGuestUser(CpuContext ctx) => ReturnUserPredicate(ctx, 0);

    [SysAbiExport(Nid = "mNnB2PWMSgw", ExportName = "sceUserServiceIsKratosPrimaryUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsKratosPrimaryUser(CpuContext ctx) => ReturnUserPredicate(ctx, 1);

    [SysAbiExport(Nid = "pZL154KvMjU", ExportName = "sceUserServiceIsKratosUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsKratosUser(CpuContext ctx) => ReturnUserPredicate(ctx, 1);

    [SysAbiExport(Nid = "MZxH8029+Wg", ExportName = "sceUserServiceIsLoggedIn", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsLoggedIn(CpuContext ctx) => ReturnUserPredicate(ctx, 1);

    [SysAbiExport(Nid = "hTdcWcUUcrk", ExportName = "sceUserServiceIsLoggedInWithoutLock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsLoggedInWithoutLock(CpuContext ctx) => ReturnUserPredicate(ctx, 1);

    [SysAbiExport(Nid = "-7XgCmEwKrs", ExportName = "sceUserServiceIsSharePlayClientUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsSharePlayClientUser(CpuContext ctx) => ReturnUserPredicate(ctx, 0);

    [SysAbiExport(Nid = "TLrDgrPYTDo", ExportName = "sceUserServiceIsUserStorageAccountBound", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceIsUserStorageAccountBound(CpuContext ctx) => ReturnUserPredicate(ctx, 1);

    [SysAbiExport(Nid = "uvVR70ZxFrQ", ExportName = "sceUserServiceLogin", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceLogin(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(Nid = "3T9y5xDcfOk", ExportName = "sceUserServiceLogout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceLogout(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(Nid = "wuI7c7UNk0A", ExportName = "sceUserServiceRegisterEventCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceRegisterEventCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(Nid = "SfGVfyEN8iw", ExportName = "sceUserServiceSetAccessibilityKeyremapData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAccessibilityKeyremapData(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAccessibilityKeyremapData");

    [SysAbiExport(Nid = "ZP0ti1CRxNA", ExportName = "sceUserServiceSetAccessibilityKeyremapEnable", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAccessibilityKeyremapEnable(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAccessibilityKeyremapEnable");

    [SysAbiExport(Nid = "HKu68cVzctg", ExportName = "sceUserServiceSetAccessibilityZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAccessibilityZoom(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAccessibilityZoom");

    [SysAbiExport(Nid = "vC-uSETCFUY", ExportName = "sceUserServiceSetAccountRemarks", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAccountRemarks(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAccountRemarks");

    [SysAbiExport(Nid = "gBLMGhB6B9E", ExportName = "sceUserServiceSetAgeVerified", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAgeVerified(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAgeVerified");

    [SysAbiExport(Nid = "7IiUdURpH0k", ExportName = "sceUserServiceSetAppearOfflineSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAppearOfflineSetting(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAppearOfflineSetting");

    [SysAbiExport(Nid = "b5-tnLcyUQE", ExportName = "sceUserServiceSetAppSortOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAppSortOrder(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAppSortOrder");

    [SysAbiExport(Nid = "u-E+6d9PiP8", ExportName = "sceUserServiceSetAutoLoginEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetAutoLoginEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetAutoLoginEnabled");

    [SysAbiExport(Nid = "feqktbQD1eo", ExportName = "sceUserServiceSetCreatedVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetCreatedVersion(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetCreatedVersion");

    [SysAbiExport(Nid = "m8VtSd5I5og", ExportName = "sceUserServiceSetDiscPlayerFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetDiscPlayerFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetDiscPlayerFlag");

    [SysAbiExport(Nid = "wV3jlvsT5jA", ExportName = "sceUserServiceSetEventCalendarType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetEventCalendarType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetEventCalendarType");

    [SysAbiExport(Nid = "rez819wV7AU", ExportName = "sceUserServiceSetEventFilterTeamEvent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetEventFilterTeamEvent(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetEventFilterTeamEvent");

    [SysAbiExport(Nid = "uhwssTtt3yo", ExportName = "sceUserServiceSetEventSortEvent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetEventSortEvent(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetEventSortEvent");

    [SysAbiExport(Nid = "XEgdhGfqRpI", ExportName = "sceUserServiceSetEventSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetEventSortTitle(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetEventSortTitle");

    [SysAbiExport(Nid = "Ty9wanVDC9k", ExportName = "sceUserServiceSetEventUiFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetEventUiFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetEventUiFlag");

    [SysAbiExport(Nid = "snOzH0NQyO0", ExportName = "sceUserServiceSetFaceRecognitionDeleteCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFaceRecognitionDeleteCount(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFaceRecognitionDeleteCount");

    [SysAbiExport(Nid = "jiMNYgxzT-4", ExportName = "sceUserServiceSetFaceRecognitionRegisterCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFaceRecognitionRegisterCount(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFaceRecognitionRegisterCount");

    [SysAbiExport(Nid = "M9noOXMhlGo", ExportName = "sceUserServiceSetFileBrowserFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileBrowserFilter(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileBrowserFilter");

    [SysAbiExport(Nid = "Xy4rq8gpYHU", ExportName = "sceUserServiceSetFileBrowserSortContent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileBrowserSortContent(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileBrowserSortContent");

    [SysAbiExport(Nid = "wN5zRLw4J6A", ExportName = "sceUserServiceSetFileBrowserSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileBrowserSortTitle(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileBrowserSortTitle");

    [SysAbiExport(Nid = "hP2q9Eb5hf0", ExportName = "sceUserServiceSetFileSelectorFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileSelectorFilter(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileSelectorFilter");

    [SysAbiExport(Nid = "Fl52JeSLPyw", ExportName = "sceUserServiceSetFileSelectorSortContent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileSelectorSortContent(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileSelectorSortContent");

    [SysAbiExport(Nid = "Llv693Nx+nU", ExportName = "sceUserServiceSetFileSelectorSortTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFileSelectorSortTitle(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFileSelectorSortTitle");

    [SysAbiExport(Nid = "MgBIXUkGtpE", ExportName = "sceUserServiceSetForegroundUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetForegroundUser(CpuContext ctx) => SetForegroundUser(ctx);

    [SysAbiExport(Nid = "fK4AIM0knFQ", ExportName = "sceUserServiceSetFriendCustomListLastFocus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFriendCustomListLastFocus(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFriendCustomListLastFocus");

    [SysAbiExport(Nid = "5cK+UC54Oz4", ExportName = "sceUserServiceSetFriendFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetFriendFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetFriendFlag");

    [SysAbiExport(Nid = "VEUKQumI5B8", ExportName = "sceUserServiceSetGlsAccessTokenNiconicoLive", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsAccessTokenNiconicoLive(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsAccessTokenNiconicoLive");

    [SysAbiExport(Nid = "0D2xtHQYxII", ExportName = "sceUserServiceSetGlsAccessTokenTwitch", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsAccessTokenTwitch(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsAccessTokenTwitch");

    [SysAbiExport(Nid = "vdBd3PMBFp4", ExportName = "sceUserServiceSetGlsAccessTokenUstream", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsAccessTokenUstream(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsAccessTokenUstream");

    [SysAbiExport(Nid = "TerdSx+FXrc", ExportName = "sceUserServiceSetGlsAnonymousUserId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsAnonymousUserId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsAnonymousUserId");

    [SysAbiExport(Nid = "UdZhN1nVYfw", ExportName = "sceUserServiceSetGlsBcTags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBcTags(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBcTags");

    [SysAbiExport(Nid = "hJ5gj+Pv3-M", ExportName = "sceUserServiceSetGlsBcTitle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBcTitle(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBcTitle");

    [SysAbiExport(Nid = "OALd6SmF220", ExportName = "sceUserServiceSetGlsBroadcastChannel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBroadcastChannel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBroadcastChannel");

    [SysAbiExport(Nid = "ZopdvNlYFHc", ExportName = "sceUserServiceSetGlsBroadcastersComment", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBroadcastersComment(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBroadcastersComment");

    [SysAbiExport(Nid = "f5DDIXCTxww", ExportName = "sceUserServiceSetGlsBroadcastersCommentColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBroadcastersCommentColor(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBroadcastersCommentColor");

    [SysAbiExport(Nid = "LIBEeNNfeQo", ExportName = "sceUserServiceSetGlsBroadcastService", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBroadcastService(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBroadcastService");

    [SysAbiExport(Nid = "RdAvEmks-ZE", ExportName = "sceUserServiceSetGlsBroadcastUiLayout", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsBroadcastUiLayout(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsBroadcastUiLayout");

    [SysAbiExport(Nid = "HYMgE5B62QY", ExportName = "sceUserServiceSetGlsCamCrop", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCamCrop(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCamCrop");

    [SysAbiExport(Nid = "N-xzO5-livc", ExportName = "sceUserServiceSetGlsCameraBgFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraBgFilter(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraBgFilter");

    [SysAbiExport(Nid = "GxqMYA60BII", ExportName = "sceUserServiceSetGlsCameraBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraBrightness(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraBrightness");

    [SysAbiExport(Nid = "Di05lHWmCLU", ExportName = "sceUserServiceSetGlsCameraChromaKeyLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraChromaKeyLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraChromaKeyLevel");

    [SysAbiExport(Nid = "gGbu3TZiXeU", ExportName = "sceUserServiceSetGlsCameraContrast", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraContrast(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraContrast");

    [SysAbiExport(Nid = "8PXQIdRsZIE", ExportName = "sceUserServiceSetGlsCameraDepthLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraDepthLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraDepthLevel");

    [SysAbiExport(Nid = "56bliV+tc0Y", ExportName = "sceUserServiceSetGlsCameraEdgeLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraEdgeLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraEdgeLevel");

    [SysAbiExport(Nid = "ghjrbwjC0VE", ExportName = "sceUserServiceSetGlsCameraEffect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraEffect(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraEffect");

    [SysAbiExport(Nid = "YnBnZpr3UJg", ExportName = "sceUserServiceSetGlsCameraEliminationLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraEliminationLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraEliminationLevel");

    [SysAbiExport(Nid = "wWZzH-BwWuA", ExportName = "sceUserServiceSetGlsCameraPosition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraPosition(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraPosition");

    [SysAbiExport(Nid = "pnHR-aj9edo", ExportName = "sceUserServiceSetGlsCameraReflection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraReflection(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraReflection");

    [SysAbiExport(Nid = "rriXMS0a7BM", ExportName = "sceUserServiceSetGlsCameraSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraSize(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraSize");

    [SysAbiExport(Nid = "0e0wzFADy0I", ExportName = "sceUserServiceSetGlsCameraTransparency", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCameraTransparency(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCameraTransparency");

    [SysAbiExport(Nid = "wQDizdO49CA", ExportName = "sceUserServiceSetGlsCommunityId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsCommunityId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsCommunityId");

    [SysAbiExport(Nid = "t1oU0+93b+s", ExportName = "sceUserServiceSetGlsFloatingMessage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsFloatingMessage(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsFloatingMessage");

    [SysAbiExport(Nid = "bdJdX2bKo2E", ExportName = "sceUserServiceSetGlsHintFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsHintFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsHintFlag");

    [SysAbiExport(Nid = "vRgpAhKJJ+M", ExportName = "sceUserServiceSetGlsInitSpectating", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsInitSpectating(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsInitSpectating");

    [SysAbiExport(Nid = "EjxE+-VvuJ4", ExportName = "sceUserServiceSetGlsIsCameraHidden", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsCameraHidden(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsCameraHidden");

    [SysAbiExport(Nid = "HfQTiMSCHJk", ExportName = "sceUserServiceSetGlsIsFacebookEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsFacebookEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsFacebookEnabled");

    [SysAbiExport(Nid = "63t6w0MgG8I", ExportName = "sceUserServiceSetGlsIsMuteEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsMuteEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsMuteEnabled");

    [SysAbiExport(Nid = "6oZ3DZGzjIE", ExportName = "sceUserServiceSetGlsIsRecDisabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsRecDisabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsRecDisabled");

    [SysAbiExport(Nid = "AmJ3FJxT7r8", ExportName = "sceUserServiceSetGlsIsRecievedMessageHidden", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsRecievedMessageHidden(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsRecievedMessageHidden");

    [SysAbiExport(Nid = "lsdxBeRnEes", ExportName = "sceUserServiceSetGlsIsTwitterEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsIsTwitterEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsIsTwitterEnabled");

    [SysAbiExport(Nid = "wgVAwa31l0E", ExportName = "sceUserServiceSetGlsLanguageFilter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLanguageFilter(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLanguageFilter");

    [SysAbiExport(Nid = "rDkflpHzrRE", ExportName = "sceUserServiceSetGlsLfpsSortOrder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLfpsSortOrder(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLfpsSortOrder");

    [SysAbiExport(Nid = "qT8-eJKe+rI", ExportName = "sceUserServiceSetGlsLiveQuality", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLiveQuality(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLiveQuality");

    [SysAbiExport(Nid = "hQ72M-YRb8g", ExportName = "sceUserServiceSetGlsLiveQuality2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLiveQuality2(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLiveQuality2");

    [SysAbiExport(Nid = "ZWAUCzgSQ2Q", ExportName = "sceUserServiceSetGlsLiveQuality3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLiveQuality3(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLiveQuality3");

    [SysAbiExport(Nid = "HwFpasG4+kM", ExportName = "sceUserServiceSetGlsLiveQuality4", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLiveQuality4(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLiveQuality4");

    [SysAbiExport(Nid = "Ov8hs+c1GNY", ExportName = "sceUserServiceSetGlsLiveQuality5", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsLiveQuality5(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsLiveQuality5");

    [SysAbiExport(Nid = "fm7XpsO++lk", ExportName = "sceUserServiceSetGlsMessageFilterLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsMessageFilterLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsMessageFilterLevel");

    [SysAbiExport(Nid = "Lge4s3h8BFA", ExportName = "sceUserServiceSetGlsTtsFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsTtsFlags(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsTtsFlags");

    [SysAbiExport(Nid = "NB9-D-o3hN0", ExportName = "sceUserServiceSetGlsTtsPitch", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsTtsPitch(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsTtsPitch");

    [SysAbiExport(Nid = "2EWfAroUQE4", ExportName = "sceUserServiceSetGlsTtsSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsTtsSpeed(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsTtsSpeed");

    [SysAbiExport(Nid = "QzeIQXyavtU", ExportName = "sceUserServiceSetGlsTtsVolume", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetGlsTtsVolume(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetGlsTtsVolume");

    [SysAbiExport(Nid = "WU5s+cPzO8Y", ExportName = "sceUserServiceSetHmuBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetHmuBrightness(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetHmuBrightness");

    [SysAbiExport(Nid = "gQh8NaCbRqo", ExportName = "sceUserServiceSetHmuZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetHmuZoom(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetHmuZoom");

    [SysAbiExport(Nid = "7pif5RySi+s", ExportName = "sceUserServiceSetHoldAudioOutDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetHoldAudioOutDevice(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetHoldAudioOutDevice");

    [SysAbiExport(Nid = "8TGeI5PAabg", ExportName = "sceUserServiceSetImeAutoCapitalEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImeAutoCapitalEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImeAutoCapitalEnabled");

    [SysAbiExport(Nid = "3fcBoTACkWY", ExportName = "sceUserServiceSetImeInitFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImeInitFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImeInitFlag");

    [SysAbiExport(Nid = "Ghu0khDguq8", ExportName = "sceUserServiceSetImeInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImeInputType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImeInputType");

    [SysAbiExport(Nid = "hjlUn9UCgXg", ExportName = "sceUserServiceSetImeLastUnit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImeLastUnit(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImeLastUnit");

    [SysAbiExport(Nid = "19uCF96mfos", ExportName = "sceUserServiceSetImePointerMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImePointerMode(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImePointerMode");

    [SysAbiExport(Nid = "NiwMhCbg764", ExportName = "sceUserServiceSetImePredictiveTextEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImePredictiveTextEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImePredictiveTextEnabled");

    [SysAbiExport(Nid = "AZFXXpZJEPI", ExportName = "sceUserServiceSetImeRunCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetImeRunCount(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetImeRunCount");

    [SysAbiExport(Nid = "Izy+4XmTBB8", ExportName = "sceUserServiceSetIPDLeft", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetIPDLeft(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetIPDLeft");

    [SysAbiExport(Nid = "z-lbCrpteB4", ExportName = "sceUserServiceSetIPDRight", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetIPDRight(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetIPDRight");

    [SysAbiExport(Nid = "7SE4sjhlOCI", ExportName = "sceUserServiceSetIsFakePlus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetIsFakePlus(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetIsFakePlus");

    [SysAbiExport(Nid = "nNn8Gnn+E6Y", ExportName = "sceUserServiceSetIsQuickSignup", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetIsQuickSignup(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetIsQuickSignup");

    [SysAbiExport(Nid = "AQ680L4Sr74", ExportName = "sceUserServiceSetIsRemotePlayAllowed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetIsRemotePlayAllowed(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetIsRemotePlayAllowed");

    [SysAbiExport(Nid = "lAR1nkEoMBo", ExportName = "sceUserServiceSetJapaneseInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetJapaneseInputType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetJapaneseInputType");

    [SysAbiExport(Nid = "dCdhOJIOtR4", ExportName = "sceUserServiceSetKeyboardType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetKeyboardType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetKeyboardType");

    [SysAbiExport(Nid = "zs4i9SEHy0g", ExportName = "sceUserServiceSetKeyRepeatSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetKeyRepeatSpeed(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetKeyRepeatSpeed");

    [SysAbiExport(Nid = "FfXgMSmZLfk", ExportName = "sceUserServiceSetKeyRepeatStartingTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetKeyRepeatStartingTime(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetKeyRepeatStartingTime");

    [SysAbiExport(Nid = "dlBQfiDOklQ", ExportName = "sceUserServiceSetLightBarBaseBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetLightBarBaseBrightness(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetLightBarBaseBrightness");

    [SysAbiExport(Nid = "Zdd5gybtsi0", ExportName = "sceUserServiceSetLoginFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetLoginFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetLoginFlag");

    [SysAbiExport(Nid = "c9U2pk4Ao9w", ExportName = "sceUserServiceSetMicLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetMicLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetMicLevel");

    [SysAbiExport(Nid = "lg2I8bETiZo", ExportName = "sceUserServiceSetMouseHandType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetMouseHandType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetMouseHandType");

    [SysAbiExport(Nid = "omf6BE2-FPo", ExportName = "sceUserServiceSetMousePointerSpeed", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetMousePointerSpeed(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetMousePointerSpeed");

    [SysAbiExport(Nid = "uisYUWMn-+U", ExportName = "sceUserServiceSetNotificationBehavior", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNotificationBehavior(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNotificationBehavior");

    [SysAbiExport(Nid = "X9Jgur0QtLE", ExportName = "sceUserServiceSetNotificationSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNotificationSettings(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNotificationSettings");

    [SysAbiExport(Nid = "SkE5SnCFjQk", ExportName = "sceUserServiceSetNpAccountUpgradeFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpAccountUpgradeFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpAccountUpgradeFlag");

    [SysAbiExport(Nid = "nGacpiUONQ0", ExportName = "sceUserServiceSetNpAge", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpAge(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpAge");

    [SysAbiExport(Nid = "om4jx+pJlQo", ExportName = "sceUserServiceSetNpAuthErrorFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpAuthErrorFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpAuthErrorFlag");

    [SysAbiExport(Nid = "Z5t2LiajkAQ", ExportName = "sceUserServiceSetNpCountryCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpCountryCode(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpCountryCode");

    [SysAbiExport(Nid = "cGvpAO63abg", ExportName = "sceUserServiceSetNpDateOfBirth", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpDateOfBirth(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpDateOfBirth");

    [SysAbiExport(Nid = "JifncjTlXV8", ExportName = "sceUserServiceSetNpEnv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpEnv(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpEnv");

    [SysAbiExport(Nid = "D7lbcn6Uxho", ExportName = "sceUserServiceSetNpLanguageCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpLanguageCode(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpLanguageCode");

    [SysAbiExport(Nid = "oHRrt1cfbBI", ExportName = "sceUserServiceSetNpLanguageCode2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpLanguageCode2(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpLanguageCode2");

    [SysAbiExport(Nid = "Zgq19lM+u2U", ExportName = "sceUserServiceSetNpLoginId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpLoginId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpLoginId");

    [SysAbiExport(Nid = "8W+8vFlIPuA", ExportName = "sceUserServiceSetNpMAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpMAccountId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpMAccountId");

    [SysAbiExport(Nid = "0Xsfib8bq3M", ExportName = "sceUserServiceSetNpNpId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpNpId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpNpId");

    [SysAbiExport(Nid = "j6FgkXhxp1Y", ExportName = "sceUserServiceSetNpOfflineAccountAdult", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpOfflineAccountAdult(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpOfflineAccountAdult");

    [SysAbiExport(Nid = "pubVXAG+Juc", ExportName = "sceUserServiceSetNpOnlineId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpOnlineId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpOnlineId");

    [SysAbiExport(Nid = "ng4XlNFMiCo", ExportName = "sceUserServiceSetNpSubAccount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetNpSubAccount(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetNpSubAccount");

    [SysAbiExport(Nid = "41kc2YhzZoU", ExportName = "sceUserServiceSetPadSpeakerVolume", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPadSpeakerVolume(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPadSpeakerVolume");

    [SysAbiExport(Nid = "KJw6rahYNdQ", ExportName = "sceUserServiceSetParentalBdAge", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalBdAge(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalBdAge");

    [SysAbiExport(Nid = "6jPYBCGQgiQ", ExportName = "sceUserServiceSetParentalBrowser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalBrowser(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalBrowser");

    [SysAbiExport(Nid = "UT8+lb5fypc", ExportName = "sceUserServiceSetParentalDvd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalDvd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalDvd");

    [SysAbiExport(Nid = "NJpUvo+rezg", ExportName = "sceUserServiceSetParentalDvdRegion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalDvdRegion(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalDvdRegion");

    [SysAbiExport(Nid = "gRI+BnPA6UI", ExportName = "sceUserServiceSetParentalGame", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalGame(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalGame");

    [SysAbiExport(Nid = "BPFs-TiU+8Q", ExportName = "sceUserServiceSetParentalGameAgeLevel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalGameAgeLevel(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalGameAgeLevel");

    [SysAbiExport(Nid = "mmFgyjXMQBs", ExportName = "sceUserServiceSetParentalMorpheus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetParentalMorpheus(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetParentalMorpheus");

    [SysAbiExport(Nid = "ZsyQjvVFHnk", ExportName = "sceUserServiceSetPartyMuteList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPartyMuteList(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPartyMuteList");

    [SysAbiExport(Nid = "97ZkWubtMk0", ExportName = "sceUserServiceSetPartyMuteListA", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPartyMuteListA(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPartyMuteListA");

    [SysAbiExport(Nid = "IiwhRynrDnQ", ExportName = "sceUserServiceSetPartySettingFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPartySettingFlags(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPartySettingFlags");

    [SysAbiExport(Nid = "7LCq4lSlmw4", ExportName = "sceUserServiceSetPasscode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPasscode(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPasscode");

    [SysAbiExport(Nid = "dukLb11bY9c", ExportName = "sceUserServiceSetPbtcAdditionalTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcAdditionalTime(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcAdditionalTime");

    [SysAbiExport(Nid = "JK0fCuBEWJM", ExportName = "sceUserServiceSetPbtcFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcFlag");

    [SysAbiExport(Nid = "RUrfnne6Dds", ExportName = "sceUserServiceSetPbtcFridayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcFridayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcFridayDuration");

    [SysAbiExport(Nid = "YWmKJ8pWEkw", ExportName = "sceUserServiceSetPbtcFridayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcFridayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcFridayHoursEnd");

    [SysAbiExport(Nid = "GMLAWOO7I2Y", ExportName = "sceUserServiceSetPbtcFridayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcFridayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcFridayHoursStart");

    [SysAbiExport(Nid = "94ZcZmcnXK4", ExportName = "sceUserServiceSetPbtcMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcMode(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcMode");

    [SysAbiExport(Nid = "SoxZWGb3l0U", ExportName = "sceUserServiceSetPbtcMondayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcMondayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcMondayDuration");

    [SysAbiExport(Nid = "uBDKFasVr2c", ExportName = "sceUserServiceSetPbtcMondayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcMondayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcMondayHoursEnd");

    [SysAbiExport(Nid = "7XIlJQQZ2fg", ExportName = "sceUserServiceSetPbtcMondayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcMondayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcMondayHoursStart");

    [SysAbiExport(Nid = "ABoN0o46u8E", ExportName = "sceUserServiceSetPbtcPlayTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcPlayTime(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcPlayTime");

    [SysAbiExport(Nid = "VXdkxm-AaIg", ExportName = "sceUserServiceSetPbtcPlayTimeLastUpdated", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcPlayTimeLastUpdated(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcPlayTimeLastUpdated");

    [SysAbiExport(Nid = "RTrsbjUnFNo", ExportName = "sceUserServiceSetPbtcSaturdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSaturdayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSaturdayDuration");

    [SysAbiExport(Nid = "8wVUn7AO8mA", ExportName = "sceUserServiceSetPbtcSaturdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSaturdayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSaturdayHoursEnd");

    [SysAbiExport(Nid = "p2NKAA3BS6k", ExportName = "sceUserServiceSetPbtcSaturdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSaturdayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSaturdayHoursStart");

    [SysAbiExport(Nid = "hGnwgvLREHM", ExportName = "sceUserServiceSetPbtcSundayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSundayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSundayDuration");

    [SysAbiExport(Nid = "rp4DB+ICfcg", ExportName = "sceUserServiceSetPbtcSundayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSundayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSundayHoursEnd");

    [SysAbiExport(Nid = "cTpHiHGMWpk", ExportName = "sceUserServiceSetPbtcSundayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcSundayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcSundayHoursStart");

    [SysAbiExport(Nid = "R9vnyf-B1pU", ExportName = "sceUserServiceSetPbtcThursdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcThursdayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcThursdayDuration");

    [SysAbiExport(Nid = "W3oNrewI7bc", ExportName = "sceUserServiceSetPbtcThursdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcThursdayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcThursdayHoursEnd");

    [SysAbiExport(Nid = "JO5QXiyBcjQ", ExportName = "sceUserServiceSetPbtcThursdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcThursdayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcThursdayHoursStart");

    [SysAbiExport(Nid = "YX-64Vjk5oM", ExportName = "sceUserServiceSetPbtcTuesdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcTuesdayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcTuesdayDuration");

    [SysAbiExport(Nid = "MtE3Me0UJKc", ExportName = "sceUserServiceSetPbtcTuesdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcTuesdayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcTuesdayHoursEnd");

    [SysAbiExport(Nid = "bLfjqFmN4s4", ExportName = "sceUserServiceSetPbtcTuesdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcTuesdayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcTuesdayHoursStart");

    [SysAbiExport(Nid = "HsjvaxD7veE", ExportName = "sceUserServiceSetPbtcTzOffset", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcTzOffset(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcTzOffset");

    [SysAbiExport(Nid = "EqfGtRCryNg", ExportName = "sceUserServiceSetPbtcWednesdayDuration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcWednesdayDuration(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcWednesdayDuration");

    [SysAbiExport(Nid = "uZG5rmROeg4", ExportName = "sceUserServiceSetPbtcWednesdayHoursEnd", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcWednesdayHoursEnd(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcWednesdayHoursEnd");

    [SysAbiExport(Nid = "dDaO7svUM8w", ExportName = "sceUserServiceSetPbtcWednesdayHoursStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPbtcWednesdayHoursStart(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPbtcWednesdayHoursStart");

    [SysAbiExport(Nid = "pmW5v9hORos", ExportName = "sceUserServiceSetPlayTogetherFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPlayTogetherFlags(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPlayTogetherFlags");

    [SysAbiExport(Nid = "nCfhbtuZbk8", ExportName = "sceUserServiceSetPsnPasswordForDebug", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetPsnPasswordForDebug(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetPsnPasswordForDebug");

    [SysAbiExport(Nid = "ksUJCL0Hq20", ExportName = "sceUserServiceSetSaveDataAutoUpload", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSaveDataAutoUpload(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSaveDataAutoUpload");

    [SysAbiExport(Nid = "pfz4rzKJc6g", ExportName = "sceUserServiceSetSaveDataSort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSaveDataSort(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSaveDataSort");

    [SysAbiExport(Nid = "zq45SROKj9Q", ExportName = "sceUserServiceSetSaveDataTutorialFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSaveDataTutorialFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSaveDataTutorialFlag");

    [SysAbiExport(Nid = "bFzA3t6muvU", ExportName = "sceUserServiceSetShareButtonAssign", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetShareButtonAssign(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetShareButtonAssign");

    [SysAbiExport(Nid = "B-WW6mNtp2s", ExportName = "sceUserServiceSetShareDailymotionAccessToken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetShareDailymotionAccessToken(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetShareDailymotionAccessToken");

    [SysAbiExport(Nid = "OANH5P9lV4I", ExportName = "sceUserServiceSetShareDailymotionRefreshToken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetShareDailymotionRefreshToken(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetShareDailymotionRefreshToken");

    [SysAbiExport(Nid = "CMl8mUJvSf8", ExportName = "sceUserServiceSetSharePlayFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSharePlayFlags(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSharePlayFlags");

    [SysAbiExport(Nid = "rB70KuquYxs", ExportName = "sceUserServiceSetSharePlayFramerateHost", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSharePlayFramerateHost(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSharePlayFramerateHost");

    [SysAbiExport(Nid = "BhRxR+R0NFA", ExportName = "sceUserServiceSetSharePlayResolutionHost", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSharePlayResolutionHost(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSharePlayResolutionHost");

    [SysAbiExport(Nid = "EYvRF1VUpUU", ExportName = "sceUserServiceSetShareStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetShareStatus(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetShareStatus");

    [SysAbiExport(Nid = "II+V6wXKS-E", ExportName = "sceUserServiceSetShareStatus2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetShareStatus2(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetShareStatus2");

    [SysAbiExport(Nid = "5jL7UM+AdbQ", ExportName = "sceUserServiceSetSystemLoggerHashedAccountId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSystemLoggerHashedAccountId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSystemLoggerHashedAccountId");

    [SysAbiExport(Nid = "tNZY3tIIo0M", ExportName = "sceUserServiceSetSystemLoggerHashedAccountIdClockType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSystemLoggerHashedAccountIdClockType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSystemLoggerHashedAccountIdClockType");

    [SysAbiExport(Nid = "U07X36vgbA0", ExportName = "sceUserServiceSetSystemLoggerHashedAccountIdParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSystemLoggerHashedAccountIdParam(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSystemLoggerHashedAccountIdParam");

    [SysAbiExport(Nid = "qSgs-wwrlLU", ExportName = "sceUserServiceSetSystemLoggerHashedAccountIdTtl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetSystemLoggerHashedAccountIdTtl(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetSystemLoggerHashedAccountIdTtl");

    [SysAbiExport(Nid = "b6+TytWccPE", ExportName = "sceUserServiceSetTeamShowAboutTeam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetTeamShowAboutTeam(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetTeamShowAboutTeam");

    [SysAbiExport(Nid = "JZ5NzN-TGIQ", ExportName = "sceUserServiceSetThemeBgImageDimmer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeBgImageDimmer(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeBgImageDimmer");

    [SysAbiExport(Nid = "N4qrFLcXLpY", ExportName = "sceUserServiceSetThemeBgImageWaveColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeBgImageWaveColor(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeBgImageWaveColor");

    [SysAbiExport(Nid = "a41mGTpWvY4", ExportName = "sceUserServiceSetThemeBgImageZoom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeBgImageZoom(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeBgImageZoom");

    [SysAbiExport(Nid = "ALyjUuyowuI", ExportName = "sceUserServiceSetThemeEntitlementId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeEntitlementId(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeEntitlementId");

    [SysAbiExport(Nid = "jhy6fa5a4k4", ExportName = "sceUserServiceSetThemeHomeShareOwner", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeHomeShareOwner(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeHomeShareOwner");

    [SysAbiExport(Nid = "HkuBuYhYaPg", ExportName = "sceUserServiceSetThemeTextShadow", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeTextShadow(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeTextShadow");

    [SysAbiExport(Nid = "PKHZK960qZE", ExportName = "sceUserServiceSetThemeWaveColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetThemeWaveColor(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetThemeWaveColor");

    [SysAbiExport(Nid = "f7VSHQHB6Ys", ExportName = "sceUserServiceSetTopMenuLimitItem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetTopMenuLimitItem(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetTopMenuLimitItem");

    [SysAbiExport(Nid = "Tib8zgDd+V0", ExportName = "sceUserServiceSetTopMenuNotificationFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetTopMenuNotificationFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetTopMenuNotificationFlag");

    [SysAbiExport(Nid = "8Q71i3u9lN0", ExportName = "sceUserServiceSetTopMenuTutorialFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetTopMenuTutorialFlag(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetTopMenuTutorialFlag");

    [SysAbiExport(Nid = "ZfUouUx2h8w", ExportName = "sceUserServiceSetTraditionalChineseInputType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetTraditionalChineseInputType(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetTraditionalChineseInputType");

    [SysAbiExport(Nid = "IcM2f5EoRRA", ExportName = "sceUserServiceSetUserGroupIndex", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetUserGroupIndex(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetUserGroupIndex");

    [SysAbiExport(Nid = "QfYasZZPvoQ", ExportName = "sceUserServiceSetUserGroupName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetUserGroupName(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetUserGroupName");

    [SysAbiExport(Nid = "Jqu2XFr5UvA", ExportName = "sceUserServiceSetUserName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetUserName(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetUserName");

    [SysAbiExport(Nid = "cBgv9pnmunI", ExportName = "sceUserServiceSetUserStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetUserStatus(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetUserStatus");

    [SysAbiExport(Nid = "CokWh8qGANk", ExportName = "sceUserServiceSetVibrationEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVibrationEnabled(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVibrationEnabled");

    [SysAbiExport(Nid = "z1Uh28yzDzI", ExportName = "sceUserServiceSetVoiceRecognitionLastUsedOsk", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVoiceRecognitionLastUsedOsk(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVoiceRecognitionLastUsedOsk");

    [SysAbiExport(Nid = "1JNYgwRcANI", ExportName = "sceUserServiceSetVoiceRecognitionTutorialState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVoiceRecognitionTutorialState(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVoiceRecognitionTutorialState");

    [SysAbiExport(Nid = "4nEjiZH1LKM", ExportName = "sceUserServiceSetVolumeForController", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVolumeForController(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVolumeForController");

    [SysAbiExport(Nid = "bkQ7aNx62Qg", ExportName = "sceUserServiceSetVolumeForGenericUSB", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVolumeForGenericUSB(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVolumeForGenericUSB");

    [SysAbiExport(Nid = "7EnjUtnAN+o", ExportName = "sceUserServiceSetVolumeForMorpheusSidetone", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVolumeForMorpheusSidetone(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVolumeForMorpheusSidetone");

    [SysAbiExport(Nid = "WQ-l-i2gJko", ExportName = "sceUserServiceSetVolumeForSidetone", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceSetVolumeForSidetone(CpuContext ctx) => SetStoredSetting(ctx, "sceUserServiceSetVolumeForSidetone");

    [SysAbiExport(Nid = "spW--yoLQ9o", ExportName = "sceUserServiceUnregisterEventCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUserService")]
    public static int UserServiceUnregisterEventCallback(CpuContext ctx) => ReturnOk(ctx);

    private static int InitializeSecondGeneration(CpuContext ctx)
    {
        _settings.Clear();
        Interlocked.Exchange(ref _foregroundUserId, PrimaryUserId);
        Interlocked.Exchange(ref _loginEventDelivered, 0);
        return ctx.SetReturn(0);
    }

    private static int ReturnOk(CpuContext ctx) => ctx.SetReturn(0);

    private static int ReturnUserPredicate(CpuContext ctx, int value)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(userId == 0 || userId == PrimaryUserId ? value : 0);
    }

    private static int WritePrimaryUser(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(outputAddress, Volatile.Read(ref _foregroundUserId))
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int SetForegroundUser(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        Interlocked.Exchange(ref _foregroundUserId, userId);
        return ctx.SetReturn(0);
    }

    private static int WriteRegisteredUsers(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> users = stackalloc byte[sizeof(int) * 16];
        for (var index = 0; index < 16; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                users[(index * sizeof(int))..],
                index == 0 ? PrimaryUserId : InvalidUserId);
        }

        return ctx.Memory.TryWrite(outputAddress, users)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteStoredSetting(CpuContext ctx, string settingName, int defaultValue)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var value = _settings.GetOrAdd(NormalizeSettingName(settingName), defaultValue);
        return ctx.TryWriteInt32(outputAddress, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int SetStoredSetting(CpuContext ctx, string settingName)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        _settings[NormalizeSettingName(settingName)] = unchecked((int)ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(0);
    }

    private static string NormalizeSettingName(string settingName)
    {
        const string getPrefix = "sceUserServiceGet";
        const string setPrefix = "sceUserServiceSet";
        if (settingName.StartsWith(getPrefix, StringComparison.Ordinal))
        {
            return settingName[getPrefix.Length..];
        }

        return settingName.StartsWith(setPrefix, StringComparison.Ordinal)
            ? settingName[setPrefix.Length..]
            : settingName;
    }

    private static int WriteUserText(CpuContext ctx, string value)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        var capacity = ctx[CpuRegister.Rdx];
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        if (capacity != 0 && capacity < (ulong)bytes.Length)
        {
            return ctx.SetReturn(OrbisUserServiceErrorBufferTooShort);
        }

        return ctx.Memory.TryWrite(outputAddress, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteDateOfBirth(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> date = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(date[0..], 1990);
        BinaryPrimitives.WriteInt32LittleEndian(date[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(date[8..], 1);
        return ctx.Memory.TryWrite(outputAddress, date)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // Per-user setting getters: (userId, int* outValue). The setting is off /
    // default (0) for the primary user; unknown users are rejected the same
    // way the other per-user getters are.
    private static int WriteAccessibilitySetting(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(valueAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static void TraceUserService(string message)
    {
        if (_traceUserService)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] user_service.{message}");
        }
    }
}
