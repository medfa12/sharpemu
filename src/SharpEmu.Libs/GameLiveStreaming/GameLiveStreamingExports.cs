// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.GameLiveStreaming;

public static class GameLiveStreamingExports
{
    private const int StatusSize = 72;
    private static int _initialized;
    private static ulong _callback;
    private static ulong _callbackArgument;

    private static int Success(CpuContext ctx) => ctx.SetReturn(0);
    private static int ClearOptional(CpuContext ctx, ulong address, int size)
    {
        if (address == 0) return Success(ctx);
        var bytes = new byte[size];
        return ctx.Memory.TryWrite(address, bytes)
            ? Success(ctx)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "NqkTzemliC0", ExportName = "sceGameLiveStreamingApplySocialFeedbackMessageFilter", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ApplySocialFeedbackMessageFilter(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "PC4jq87+YQI", ExportName = "sceGameLiveStreamingCheckCallback", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int CheckCallback(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "FcHBfHjFXkA", ExportName = "sceGameLiveStreamingClearPresetSocialFeedbackCommands", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ClearPresetSocialFeedbackCommands(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "lZ2Sd0uEvpo", ExportName = "sceGameLiveStreamingClearSocialFeedbackMessages", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ClearSocialFeedbackMessages(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "6c2zGtThFww", ExportName = "sceGameLiveStreamingClearSpoilerTag", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ClearSpoilerTag(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "dWM80AX39o4", ExportName = "sceGameLiveStreamingEnableLiveStreaming", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int EnableLiveStreaming(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "wBOQWjbWMfU", ExportName = "sceGameLiveStreamingEnableSocialFeedback", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int EnableSocialFeedback(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "aRSQNqbats4", ExportName = "sceGameLiveStreamingGetCurrentBroadcastScreenLayout", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetCurrentBroadcastScreenLayout(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 ? Success(ctx) : ClearOptional(ctx, ctx[CpuRegister.Rdi], sizeof(uint));
    [SysAbiExport(Nid = "CoPMx369EqM", ExportName = "sceGameLiveStreamingGetCurrentStatus", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetCurrentStatus(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], StatusSize);
    [SysAbiExport(Nid = "lK8dLBNp9OE", ExportName = "sceGameLiveStreamingGetCurrentStatus2", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetCurrentStatus2(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], StatusSize);
    [SysAbiExport(Nid = "OIIm19xu+NM", ExportName = "sceGameLiveStreamingGetProgramInfo", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetProgramInfo(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "PMx7N4WqNdo", ExportName = "sceGameLiveStreamingGetSocialFeedbackMessages", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetSocialFeedbackMessages(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "yeQKjHETi40", ExportName = "sceGameLiveStreamingGetSocialFeedbackMessagesCount", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GetSocialFeedbackMessagesCount(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 ? Success(ctx) : ClearOptional(ctx, ctx[CpuRegister.Rdi], sizeof(uint));
    [SysAbiExport(Nid = "kvYEw2lBndk", ExportName = "sceGameLiveStreamingInitialize", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int Initialize(CpuContext ctx) { Volatile.Write(ref _initialized, 1); return Success(ctx); }
    [SysAbiExport(Nid = "ysWfX5PPbfc", ExportName = "sceGameLiveStreamingLaunchLiveViewer", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int LaunchLiveViewer(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "cvRCb7DTAig", ExportName = "sceGameLiveStreamingLaunchLiveViewerA", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int LaunchLiveViewerA(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "K0QxEbD7q+c", ExportName = "sceGameLiveStreamingPermitLiveStreaming", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int PermitLiveStreaming(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "-EHnU68gExU", ExportName = "sceGameLiveStreamingPermitServerSideRecording", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int PermitServerSideRecording(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "hggKhPySVgI", ExportName = "sceGameLiveStreamingPostSocialMessage", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int PostSocialMessage(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "nFP8qT9YXbo", ExportName = "sceGameLiveStreamingRegisterCallback", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int RegisterCallback(CpuContext ctx) { _callback = ctx[CpuRegister.Rdi]; _callbackArgument = ctx[CpuRegister.Rsi]; return Success(ctx); }
    [SysAbiExport(Nid = "b5RaMD2J0So", ExportName = "sceGameLiveStreamingScreenCloseSeparateMode", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenCloseSeparateMode(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "hBdd8n6kuvE", ExportName = "sceGameLiveStreamingScreenConfigureSeparateMode", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenConfigureSeparateMode(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "uhCmn81s-mU", ExportName = "sceGameLiveStreamingScreenInitialize", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenInitialize(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "fo5B8RUaBxQ", ExportName = "sceGameLiveStreamingScreenInitializeSeparateModeParameter", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenInitializeSeparateModeParameter(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "iorzW0pKOiA", ExportName = "sceGameLiveStreamingScreenOpenSeparateMode", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenOpenSeparateMode(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "gDSvt78H3Oo", ExportName = "sceGameLiveStreamingScreenSetMode", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenSetMode(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "HE93dr-5rx4", ExportName = "sceGameLiveStreamingScreenTerminate", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int ScreenTerminate(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "3PSiwAzFISE", ExportName = "sceGameLiveStreamingSetCameraFrameSetting", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetCameraFrameSetting(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "TwuUzTKKeek", ExportName = "sceGameLiveStreamingSetDefaultServiceProviderPermission", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetDefaultServiceProviderPermission(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Gw6S4oqlY7E", ExportName = "sceGameLiveStreamingSetGuardAreas", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetGuardAreas(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "QmQYwQ7OTJI", ExportName = "sceGameLiveStreamingSetInvitationSessionId", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetInvitationSessionId(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Sb5bAXyUt5c", ExportName = "sceGameLiveStreamingSetLinkCommentPreset", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetLinkCommentPreset(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "q-kxuaF7URU", ExportName = "sceGameLiveStreamingSetMaxBitrate", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetMaxBitrate(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "hUY-mSOyGL0", ExportName = "sceGameLiveStreamingSetMetadata", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetMetadata(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ycodiP2I0xo", ExportName = "sceGameLiveStreamingSetPresetSocialFeedbackCommands", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetPresetSocialFeedbackCommands(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "x6deXUpQbBo", ExportName = "sceGameLiveStreamingSetPresetSocialFeedbackCommandsDescription", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetPresetSocialFeedbackCommandsDescription(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "mCoz3k3zPmA", ExportName = "sceGameLiveStreamingSetServiceProviderPermission", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetServiceProviderPermission(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "ZuX+zzz2DkA", ExportName = "sceGameLiveStreamingSetSpoilerTag", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetSpoilerTag(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "MLvYI86FFAo", ExportName = "sceGameLiveStreamingSetStandbyScreenResource", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int SetStandbyScreenResource(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "y0KkAydy9xE", ExportName = "sceGameLiveStreamingStartGenerateStandbyScreenResource", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int StartGenerateStandbyScreenResource(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "Y1WxX7dPMCw", ExportName = "sceGameLiveStreamingStartSocialFeedbackMessageFiltering", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int StartSocialFeedbackMessageFiltering(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "D7dg5QJ4FlE", ExportName = "sceGameLiveStreamingStopGenerateStandbyScreenResource", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int StopGenerateStandbyScreenResource(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "bYuGUBuIsaY", ExportName = "sceGameLiveStreamingStopSocialFeedbackMessageFiltering", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int StopSocialFeedbackMessageFiltering(CpuContext ctx) => Success(ctx);
    [SysAbiExport(Nid = "9yK6Fk8mKOQ", ExportName = "sceGameLiveStreamingTerminate", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int Terminate(CpuContext ctx) { Volatile.Write(ref _initialized, 0); _callback = 0; _callbackArgument = 0; return Success(ctx); }
    [SysAbiExport(Nid = "5XHaH3kL+bA", ExportName = "sceGameLiveStreamingUnregisterCallback", Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int UnregisterCallback(CpuContext ctx) { _callback = 0; _callbackArgument = 0; return Success(ctx); }
}
