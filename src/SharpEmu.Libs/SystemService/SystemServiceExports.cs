// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;

namespace SharpEmu.Libs.SystemService;

public static class SystemServiceExports
{
    private const int OrbisSystemServiceErrorParameter = unchecked((int)0x80A10003);
    private const int OrbisSystemServiceErrorNoEvent = unchecked((int)0x80A10004);
    private const int SystemServiceStatusSize = 0x0C;
    private const int DisplaySafeAreaInfoSize = sizeof(float) + 128;
    private const int HdrToneMapLuminanceSize = 3 * sizeof(float);
    private const int DaemonEventSize = sizeof(int) + 8192;
    private const int TitleWorkaroundInfoSize = 16;
    private static readonly object _stateGate = new();
    private static bool _musicPlayerDisabled;
    private static bool _applicationSuspended;
    private static bool _personalEyeDistanceSettingEnabled = true;
    private static bool _suspendConfirmationEnabled = true;
    private static int _gpuLoadEmulationMode;
    private static int _renderingMode;

    internal static bool MusicPlayerDisabled
    {
        get
        {
            lock (_stateGate)
            {
                return _musicPlayerDisabled;
            }
        }
    }

    internal static bool PersonalEyeDistanceSettingEnabled
    {
        get
        {
            lock (_stateGate)
            {
                return _personalEyeDistanceSettingEnabled;
            }
        }
    }

    internal static bool SuspendConfirmationEnabled
    {
        get
        {
            lock (_stateGate)
            {
                return _suspendConfirmationEnabled;
            }
        }
    }

    [SysAbiExport(
        Nid = "fZo48un7LK4",
        ExportName = "sceSystemServiceParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetInt(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        var value = parameterId switch
        {
            1 or 2 or 3 or 1000 => 1,
            4 => 180,
            _ => 0,
        };

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        return ctx.Memory.TryWrite(valueAddress, valueBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rPo6tV8D9bM",
        ExportName = "sceSystemServiceGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> status = stackalloc byte[SystemServiceStatusSize];
        status.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(status, 0);
        status[0x06] = 1;

        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1n37q1Bvc5Y",
        ExportName = "sceSystemServiceGetDisplaySafeAreaInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetDisplaySafeAreaInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> info = stackalloc byte[DisplaySafeAreaInfoSize];
        info.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(info, 1.0f);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Vo5V8KAwCmk",
        ExportName = "sceSystemServiceHideSplashScreen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceHideSplashScreen(CpuContext ctx)
    {
        VulkanVideoPresenter.HideSplashScreen();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "656LMQSrg6U",
        ExportName = "sceSystemServiceReceiveEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReceiveEvent(CpuContext ctx)
    {
        // No system events (resume, entitlement updates, url open, ...) are
        // ever queued; games poll this each frame and treat NO_EVENT as idle.
        var eventAddress = ctx[CpuRegister.Rdi];
        return eventAddress == 0
            ? ctx.SetReturn(OrbisSystemServiceErrorParameter)
            : ctx.SetReturn(OrbisSystemServiceErrorNoEvent);
    }

    [SysAbiExport(
        Nid = "3RQ5aQfnstU",
        ExportName = "sceSystemServiceGetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetNoticeScreenSkipFlag(CpuContext ctx)
    {
        // Skip flag off: the title shows its own startup notices as usual.
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        return ctx.TryWriteInt32(flagAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "mPpPxv5CZt4",
        ExportName = "sceSystemServiceGetHdrToneMapLuminance",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetHdrToneMapLuminance(CpuContext ctx)
    {
        // Out-parameter is three floats {max, maxFrameAverage, min} in nits;
        // report bright-HDR-display defaults so titles pick a sane tone map.
        var luminanceAddress = ctx[CpuRegister.Rdi];
        if (luminanceAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> luminance = stackalloc byte[HdrToneMapLuminanceSize];
        BinaryPrimitives.WriteSingleLittleEndian(luminance, 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[sizeof(float)..], 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[(sizeof(float) * 2)..], 0.01f);
        return ctx.Memory.TryWrite(luminanceAddress, luminance)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3s8cHiCBKBE",
        ExportName = "sceSystemServiceReportAbnormalTermination",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReportAbnormalTermination(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "0z7srulNt7U", ExportName = "sceSystemServiceAcquireFb0", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceAcquireFb0(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "0cl8SuwosPQ", ExportName = "sceSystemServiceAddLocalProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceAddLocalProcess(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "cltshBrDLC0", ExportName = "sceSystemServiceAddLocalProcessForPsmKit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceAddLocalProcessForPsmKit(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "FI+VqGdttvI", ExportName = "sceSystemServiceChangeAcpClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeAcpClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "ec72vt3WEQo", ExportName = "sceSystemServiceChangeCpuClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeCpuClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "Z5RgV4Chwxg", ExportName = "sceSystemServiceChangeGpuClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeGpuClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "LFo00RWzqRU", ExportName = "sceSystemServiceChangeMemoryClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeMemoryClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "MyBXslDE+2o", ExportName = "sceSystemServiceChangeMemoryClockToBaseMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeMemoryClockToBaseMode(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "qv+X8gozqF4", ExportName = "sceSystemServiceChangeMemoryClockToDefault", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeMemoryClockToDefault(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "fOsE5pTieqY", ExportName = "sceSystemServiceChangeMemoryClockToMultiMediaMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeMemoryClockToMultiMediaMode(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "5MLppFJZyX4", ExportName = "sceSystemServiceChangeNumberOfGpuCu", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeNumberOfGpuCu(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "lgTlIAEJ33M", ExportName = "sceSystemServiceChangeSamuClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeSamuClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "BQUi7AW+2tQ", ExportName = "sceSystemServiceChangeUvdClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeUvdClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "fzguXBQzNvI", ExportName = "sceSystemServiceChangeVceClock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceChangeVceClock(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "x1UB9bwDSOw", ExportName = "sceSystemServiceDisableMusicPlayer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableMusicPlayer(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _musicPlayerDisabled = true;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "Mr1IgQaRff0", ExportName = "sceSystemServiceDisablePersonalEyeToEyeDistanceSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceDisablePersonalEyeToEyeDistanceSetting(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _personalEyeDistanceSettingEnabled = false;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "PQ+SjXAg3EM", ExportName = "sceSystemServiceDisableSuspendConfirmationDialog", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableSuspendConfirmationDialog(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _suspendConfirmationEnabled = false;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "O3irWUQ2s-g", ExportName = "sceSystemServiceEnablePersonalEyeToEyeDistanceSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceEnablePersonalEyeToEyeDistanceSetting(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _personalEyeDistanceSettingEnabled = true;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "Rn32O5PDlmo", ExportName = "sceSystemServiceEnableSuspendConfirmationDialog", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceEnableSuspendConfirmationDialog(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _suspendConfirmationEnabled = true;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "xjE7xLfrLUk", ExportName = "sceSystemServiceGetAppFocusedAppStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppFocusedAppStatus(CpuContext ctx) => ctx.SetReturn(1);

    [SysAbiExport(Nid = "f4oDTxAJCHE", ExportName = "sceSystemServiceGetAppIdOfBigApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppIdOfBigApp(CpuContext ctx) => ctx.SetReturn(1);

    [SysAbiExport(Nid = "BBSmGrxok5o", ExportName = "sceSystemServiceGetAppIdOfMiniApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppIdOfMiniApp(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "t5ShV0jWEFE", ExportName = "sceSystemServiceGetAppStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppStatus(CpuContext ctx)
    {
        lock (_stateGate)
        {
            return ctx.SetReturn(_applicationSuspended ? 1 : 0);
        }
    }

    [SysAbiExport(Nid = "YLbhAXS20C0", ExportName = "sceSystemServiceGetAppType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppType(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "JFg3az5ITN4", ExportName = "sceSystemServiceGetEventForDaemon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetEventForDaemon(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> eventData = stackalloc byte[DaemonEventSize];
        eventData.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(eventData, -1);
        if (!ctx.Memory.TryWrite(eventAddress, eventData))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisSystemServiceErrorNoEvent);
    }

    [SysAbiExport(Nid = "4imyVMxX5-8", ExportName = "sceSystemServiceGetGpuLoadEmulationMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetGpuLoadEmulationMode(CpuContext ctx)
    {
        lock (_stateGate)
        {
            return ctx.SetReturn(_gpuLoadEmulationMode);
        }
    }

    [SysAbiExport(Nid = "ZNIuJjqdtgI", ExportName = "sceSystemServiceGetLocalProcessStatusList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetLocalProcessStatusList(CpuContext ctx)
    {
        var countAddress = ctx[CpuRegister.Rdx];
        if (countAddress != 0 && !ctx.TryWriteUInt32(countAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "gbUBqHCEgAI", ExportName = "sceSystemServiceGetPSButtonEvent", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetPSButtonEvent(CpuContext ctx) => WriteInt32Output(ctx, ctx[CpuRegister.Rdi], 0);

    [SysAbiExport(Nid = "UMIlrOlGNQU", ExportName = "sceSystemServiceGetParentSocket", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetParentSocket(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "4ZYuSI8i2aM", ExportName = "sceSystemServiceGetParentSocketForPsmKit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetParentSocketForPsmKit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "jA629PcMCKU", ExportName = "sceSystemServiceGetRenderingMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetRenderingMode(CpuContext ctx)
    {
        lock (_stateGate)
        {
            return ctx.SetReturn(_renderingMode);
        }
    }

    [SysAbiExport(Nid = "VrvpoJEoSSU", ExportName = "sceSystemServiceGetTitleWorkaroundInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetTitleWorkaroundInfo(CpuContext ctx) => WriteZeroOutput(ctx, ctx[CpuRegister.Rdi], TitleWorkaroundInfoSize);

    [SysAbiExport(Nid = "s4OcLqLsKn0", ExportName = "sceSystemServiceGetVersionNumberOfCameraCalibrationData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceGetVersionNumberOfCameraCalibrationData(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "d-15YTCUMVU", ExportName = "sceSystemServiceIsAppSuspended", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceIsAppSuspended(CpuContext ctx)
    {
        lock (_stateGate)
        {
            return ctx.SetReturn(_applicationSuspended ? 1 : 0);
        }
    }

    [SysAbiExport(Nid = "SYqaqLuQU6w", ExportName = "sceSystemServiceIsBgmPlaying", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceIsBgmPlaying(CpuContext ctx)
        => ctx.SetReturn(0);

    [SysAbiExport(Nid = "O4x1B7aXRYE", ExportName = "sceSystemServiceIsEyeToEyeDistanceAdjusted", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceIsEyeToEyeDistanceAdjusted(CpuContext ctx)
        => ctx.SetReturn(0);

    [SysAbiExport(Nid = "bMDbofWFNfQ", ExportName = "sceSystemServiceIsScreenSaverOn", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceIsScreenSaverOn(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "KQFyDkgAjVs", ExportName = "sceSystemServiceIsShellUiFgAndGameBgCpuMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceIsShellUiFgAndGameBgCpuMode(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "N4RkyJh7FtA", ExportName = "sceSystemServiceKillApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceKillApp(CpuContext ctx) => SetApplicationSuspended(ctx, true);

    [SysAbiExport(Nid = "6jpZY0WUwLM", ExportName = "sceSystemServiceKillLocalProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceKillLocalProcess(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "7cTc7seJLfQ", ExportName = "sceSystemServiceKillLocalProcessForPsmKit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceKillLocalProcessForPsmKit(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "l4FB3wNa-Ac", ExportName = "sceSystemServiceLaunchApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchApp(CpuContext ctx) => SetApplicationSuspended(ctx, false);

    [SysAbiExport(Nid = "wX9wVFaegaM", ExportName = "sceSystemServiceLaunchEventDetails", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchEventDetails(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "G5AwzWnHxks", ExportName = "sceSystemServiceLaunchTournamentList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchTournamentList(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "wIc92b0x6hk", ExportName = "sceSystemServiceLaunchTournamentsTeamProfile", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchTournamentsTeamProfile(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "-+3hY+y8bNo", ExportName = "sceSystemServiceLaunchWebBrowser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchWebBrowser(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "JoBqSQt1yyA", ExportName = "sceSystemServiceLoadExec", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceLoadExec(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "9ScDVErRRgw", ExportName = "sceSystemServiceNavigateToAnotherApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceNavigateToAnotherApp(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "e4E3MIEAS2A", ExportName = "sceSystemServiceNavigateToGoBack", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceNavigateToGoBack(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "ZeubLhPDitw", ExportName = "sceSystemServiceNavigateToGoBackWithValue", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceNavigateToGoBackWithValue(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "x2-o9eBw3ZU", ExportName = "sceSystemServiceNavigateToGoHome", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceNavigateToGoHome(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "SsC-m-S9JTA", ExportName = "sceSystemServiceParamGetString", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetString(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        if (bufferAddress == 0 || bufferSize == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        ReadOnlySpan<byte> value = parameterId == 6 ? "SharpEmu\0"u8 : "\0"u8;
        if (bufferSize < (ulong)value.Length)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        return ctx.Memory.TryWrite(bufferAddress, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "XbbJC3E+L5M", ExportName = "sceSystemServicePowerTick", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServicePowerTick(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "2xenlv7M-UU", ExportName = "sceSystemServiceRaiseExceptionLocalProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceRaiseExceptionLocalProcess(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "9kPCz7Or+1Y", ExportName = "sceSystemServiceReenableMusicPlayer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceReenableMusicPlayer(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _musicPlayerDisabled = false;
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "Pi3K47Xw0ss", ExportName = "sceSystemServiceRegisterDaemon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceRegisterDaemon(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "Oms065qIClY", ExportName = "sceSystemServiceReleaseFb0", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceReleaseFb0(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "3ZFpzcRqYsk", ExportName = "sceSystemServiceRequestCameraCalibration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceRequestCameraCalibration(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "P71fvnHyFTQ", ExportName = "sceSystemServiceRequestToChangeRenderingMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceRequestToChangeRenderingMode(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _renderingMode = unchecked((int)ctx[CpuRegister.Rdi]);
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "tMuzuZcUIcA", ExportName = "sceSystemServiceResumeLocalProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceResumeLocalProcess(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "DNE77sfNw5Y", ExportName = "sceSystemServiceSetControllerFocusPermission", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSetControllerFocusPermission(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "eLWnPuja+Y8", ExportName = "sceSystemServiceSetGpuLoadEmulationMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSetGpuLoadEmulationMode(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _gpuLoadEmulationMode = unchecked((int)ctx[CpuRegister.Rdi]);
        }

        return SystemServiceOk(ctx);
    }

    [SysAbiExport(Nid = "Xn-eH9-Fu60", ExportName = "sceSystemServiceSetOutOfVrPlayAreaFlag", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSetOutOfVrPlayAreaFlag(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "sgRPNJjrWjg", ExportName = "sceSystemServiceSetOutOfVrPlayZoneWarning", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSetOutOfVrPlayZoneWarning(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "w9wlKcHrmm8", ExportName = "sceSystemServiceShowControllerSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceShowControllerSettings(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "tPfQU2pD4-M", ExportName = "sceSystemServiceShowDisplaySafeAreaSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceShowDisplaySafeAreaSettings(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "f8eZvJ8hV6o", ExportName = "sceSystemServiceShowEyeToEyeDistanceSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceShowEyeToEyeDistanceSetting(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "vY1-RZtvvbk", ExportName = "sceSystemServiceSuspendBackgroundApp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSuspendBackgroundApp(CpuContext ctx) => SetApplicationSuspended(ctx, true);

    [SysAbiExport(Nid = "kTiAx7e2zU4", ExportName = "sceSystemServiceSuspendLocalProcess", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceSuspendLocalProcess(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "zlXqkzPY-ds", ExportName = "sceSystemServiceTickVideoPlayback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceTickVideoPlayback(CpuContext ctx) => SystemServiceOk(ctx);

    [SysAbiExport(Nid = "vOhqz-IMiW4", ExportName = "sceSystemServiceTurnOffScreenSaver", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSystemService")]
    public static int SystemServiceTurnOffScreenSaver(CpuContext ctx) => SystemServiceOk(ctx);

    private static int SystemServiceOk(CpuContext ctx) => ctx.SetReturn(0);

    private static int SetApplicationSuspended(CpuContext ctx, bool suspended)
    {
        lock (_stateGate)
        {
            _applicationSuspended = suspended;
        }

        return SystemServiceOk(ctx);
    }

    private static int WriteInt32Output(CpuContext ctx, ulong outputAddress, int value)
    {
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        return ctx.TryWriteInt32(outputAddress, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteZeroOutput(CpuContext ctx, ulong outputAddress, int size)
    {
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> output = stackalloc byte[size];
        output.Clear();
        return ctx.Memory.TryWrite(outputAddress, output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
