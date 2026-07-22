// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    private const int PrimaryUserId = 1000;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;
    private static readonly long InputSampleIntervalTicks = Math.Max(1, Stopwatch.Frequency / 1000);

    [ThreadStatic]
    private static long _lastInputSampleTicks;

    [ThreadStatic]
    private static PadState _cachedInputState;

    private static bool _initialized;
    private static int _controlsAnnouncementLogged;

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx) => PadOpenCore(ctx, extended: false);

    [SysAbiExport(
        Nid = "WFIiSfXGUq8",
        ExportName = "scePadOpenExt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpenExt(CpuContext ctx) => PadOpenCore(ctx, extended: true);

    // scePadOpen rejects a non-null 4th arg and non-standard ports; scePadOpenExt accepts a
    // ScePadOpenExtParam* plus ports 1/2 (racing titles retry scePadOpenExt(type=2) forever if rejected).
    private static int PadOpenCore(CpuContext ctx, bool extended)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var parameterAddress = ctx[CpuRegister.Rcx];
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        var typeAccepted = extended ? type is 0 or 1 or 2 : type == StandardPortType;
        if (userId != PrimaryUserId || !typeAccepted || index != 0 || (!extended && parameterAddress != 0))
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        if (Interlocked.Exchange(ref _controlsAnnouncementLogged, 1) == 0)
        {
            Console.Error.WriteLine(DualSenseReader.TryGetState(out _)
                ? "[LOADER][INFO] Controls: DualSense connected (keyboard fallback also active)."
                : XInputReader.TryGetState(out _)
                    ? "[LOADER][INFO] Controls: Xbox controller connected (keyboard fallback also active)."
                    : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
        }

        return ctx.SetReturn(PrimaryPadHandle);
    }

    [SysAbiExport(
        Nid = "6ncge5+l5Qs",
        ExportName = "scePadClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "vDLMoJLde8I",
        ExportName = "scePadSetTiltCorrectionState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTiltCorrectionState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "gjP9-KQzoUk",
        ExportName = "scePadGetControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[ControllerInformationSize];
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;
        information[0x0C] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "hGbf2QTBmqc",
        ExportName = "scePadGetExtControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetExtControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Base ScePadControllerInformation + device-class/connection fields: report a connected
        // DualSense so the guest's open -> get-ext-info -> close probe loop resolves.
        Span<byte> information = stackalloc byte[0x40];
        information.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;   // connected count
        information[0x0C] = 1;   // connected
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);
        information[0x1C] = 0;   // deviceClass: 0 = standard controller / DualSense
        information[0x1D] = 1;   // connected (ext)
        information[0x1E] = 0;   // connectionType: local

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "q1cHNfGycLI",
        ExportName = "scePadRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(1)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "kazv1NzSB8c", ExportName = "scePadConnectPort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadConnectPort(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "AcslpN1jHR8", ExportName = "scePadDeviceClassGetExtendedInformation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDeviceClassGetExtendedInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[20];
        information.Clear();
        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "IHPqcbc0zCA", ExportName = "scePadDeviceClassParseData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDeviceClassParseData(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var classDataAddress = ctx[CpuRegister.Rdx];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || classDataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> classData = stackalloc byte[24];
        classData.Clear();
        return ctx.Memory.TryWrite(classDataAddress, classData)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "d7bXuEBycDI", ExportName = "scePadDeviceOpen", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDeviceOpen(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "0aziJjRZxqQ", ExportName = "scePadDisableVibration", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDisableVibration(CpuContext ctx)
    {
        DualSenseReader.SetRumble(0, 0);
        XInputReader.SetRumble(0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "pnZXireDoeI", ExportName = "scePadDisconnectDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDisconnectDevice(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "9ez71nWSvD0", ExportName = "scePadDisconnectPort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadDisconnectPort(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "77ooWxGOIVs", ExportName = "scePadEnableAutoDetect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadEnableAutoDetect(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "+cE4Jx431wc", ExportName = "scePadEnableExtensionPort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadEnableExtensionPort(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "E1KEw5XMGQQ", ExportName = "scePadEnableSpecificDeviceClass", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadEnableSpecificDeviceClass(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "DD-KiRLBqkQ", ExportName = "scePadEnableUsbConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadEnableUsbConnection(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "Q66U8FdrMaw", ExportName = "scePadGetBluetoothAddress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetBluetoothAddress(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "qtasqbvwgV4", ExportName = "scePadGetCapability", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetCapability(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "Uq6LgTJEmQs", ExportName = "scePadGetDataInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetDataInternal(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "hDgisSGkOgw", ExportName = "scePadGetDeviceId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetDeviceId(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "4rS5zG7RFaM", ExportName = "scePadGetDeviceInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetDeviceInfo(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "1DmZjZAuzEM", ExportName = "scePadGetExtensionUnitInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetExtensionUnitInfo(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "PZSoY8j0Pko", ExportName = "scePadGetFeatureReport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetFeatureReport(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "u1GRHp+oWoY", ExportName = "scePadGetHandle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetHandle(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        return userId == PrimaryUserId && type == StandardPortType && index == 0
            ? ctx.SetReturn(PrimaryPadHandle)
            : ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
    }

    [SysAbiExport(Nid = "kiA9bZhbnAg", ExportName = "scePadGetIdleCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetIdleCount(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "1Odcw19nADw", ExportName = "scePadGetInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetInfo(CpuContext ctx) => WritePadInfo(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "4x5Im8pr0-4", ExportName = "scePadGetInfoByPortType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetInfoByPortType(CpuContext ctx)
    {
        var portType = unchecked((int)ctx[CpuRegister.Rdi]);
        return portType == StandardPortType
            ? WritePadInfo(ctx, ctx[CpuRegister.Rsi])
            : ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
    }

    [SysAbiExport(Nid = "vegw8qax5MI", ExportName = "scePadGetLicenseControllerInformation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetLicenseControllerInformation(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "WPIB7zBWxVE", ExportName = "scePadGetMotionSensorPosition", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetMotionSensorPosition(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "k4+nDV9vbT0", ExportName = "scePadGetMotionTimerUnit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetMotionTimerUnit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "do-JDWX+zRs", ExportName = "scePadGetSphereRadius", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetSphereRadius(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "QuOaoOcSOw0", ExportName = "scePadGetVersionInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadGetVersionInfo(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "bi0WNvZ1nug", ExportName = "scePadIsBlasterConnected", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsBlasterConnected(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "mEC+xJKyIjQ", ExportName = "scePadIsDS4Connected", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsDs4Connected(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "d2Qk-i8wGak", ExportName = "scePadIsLightBarBaseBrightnessControllable", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsLightBarBaseBrightnessControllable(CpuContext ctx) => ctx.SetReturn(1);

    [SysAbiExport(Nid = "4y9RNPSBsqg", ExportName = "scePadIsMoveConnected", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsMoveConnected(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "9e56uLgk5y0", ExportName = "scePadIsMoveReproductionModel", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsMoveReproductionModel(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "pFTi-yOrVeQ", ExportName = "scePadIsValidHandle", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadIsValidHandle(CpuContext ctx) =>
        ctx.SetReturn(unchecked((int)ctx[CpuRegister.Rdi]) == PrimaryPadHandle ? 1 : 0);

    [SysAbiExport(Nid = "CfwUlQtCFi4", ExportName = "scePadMbusInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadMbusInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "s7CvzS+9ZIs", ExportName = "scePadMbusTerm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadMbusTerm(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "71E9e6n+2R8", ExportName = "scePadOpenExt2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadOpenExt2(CpuContext ctx) => PadOpenCore(ctx, extended: true);

    [SysAbiExport(Nid = "DrUu8cPrje8", ExportName = "scePadOutputReport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadOutputReport(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "fm1r2vv5+OU", ExportName = "scePadReadBlasterForTracker", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadReadBlasterForTracker(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "QjwkT2Ycmew", ExportName = "scePadReadExt", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadReadExt(CpuContext ctx) => ReadNeutralPadData(ctx, multiple: true);

    [SysAbiExport(Nid = "2NhkFTRnXHk", ExportName = "scePadReadForTracker", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadReadForTracker(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "3u4M8ck9vJM", ExportName = "scePadReadHistory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadReadHistory(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "5Wf4q349s+Q", ExportName = "scePadReadStateExt", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadReadStateExt(CpuContext ctx) => ReadNeutralPadData(ctx, multiple: false);

    [SysAbiExport(Nid = "+4c9xRLmiXQ", ExportName = "scePadResetLightBarAll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadResetLightBarAll(CpuContext ctx)
    {
        DualSenseReader.ResetLightbar();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "+Yp6+orqf1M", ExportName = "scePadResetLightBarAllByPortType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadResetLightBarAllByPortType(CpuContext ctx)
    {
        DualSenseReader.ResetLightbar();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "rIZnR6eSpvk", ExportName = "scePadResetOrientation", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadResetOrientation(CpuContext ctx) => ReturnForPrimaryHandle(ctx);

    [SysAbiExport(Nid = "jbAqAvLEP4A", ExportName = "scePadResetOrientationForTracker", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadResetOrientationForTracker(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "KLmYx9ij2h0", ExportName = "scePadSetAngularVelocityBiasCorrectionState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetAngularVelocityBiasCorrectionState(CpuContext ctx) => ReturnForPrimaryHandle(ctx);

    [SysAbiExport(Nid = "r44mAxdSG+U", ExportName = "scePadSetAngularVelocityDeadbandState", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetAngularVelocityDeadbandState(CpuContext ctx) => ReturnForPrimaryHandle(ctx);

    [SysAbiExport(Nid = "ew647HuKi2Y", ExportName = "scePadSetAutoPowerOffCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetAutoPowerOffCount(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "MbTt1EHYCTg", ExportName = "scePadSetButtonRemappingInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetButtonRemappingInfo(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "MLA06oNfF+4", ExportName = "scePadSetConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetConnection(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "bsbHFI0bl5s", ExportName = "scePadSetExtensionReport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetExtensionReport(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "xqgVCEflEDY", ExportName = "scePadSetFeatureReport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetFeatureReport(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "lrjFx4xWnY8", ExportName = "scePadSetForceIntercepted", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetForceIntercepted(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "dhQXEvmrVNQ", ExportName = "scePadSetLightBarBaseBrightness", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetLightBarBaseBrightness(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "etaQhgPHDRY", ExportName = "scePadSetLightBarBlinking", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetLightBarBlinking(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "iHuOWdvQVpg", ExportName = "scePadSetLightBarForTracker", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetLightBarForTracker(CpuContext ctx) => PadSetLightBar(ctx);

    [SysAbiExport(Nid = "o-6Y99a8dKU", ExportName = "scePadSetLoginUserNumber", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetLoginUserNumber(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "flYYxek1wy8", ExportName = "scePadSetProcessFocus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetProcessFocus(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "DmBx8K+jDWw", ExportName = "scePadSetProcessPrivilege", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetProcessPrivilege(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "FbxEpTRDou8", ExportName = "scePadSetProcessPrivilegeOfButtonRemapping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetProcessPrivilegeOfButtonRemapping(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "yah8Bk4TcYY", ExportName = "scePadSetShareButtonMaskForRemotePlay", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetShareButtonMaskForRemotePlay(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "z+GEemoTxOo", ExportName = "scePadSetUserColor", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetUserColor(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "8BOObG94-tc", ExportName = "scePadSetVibrationForce", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetVibrationForce(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "--jrY4SHfm8", ExportName = "scePadSetVrTrackingMode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSetVrTrackingMode(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "zFJ35q3RVnY", ExportName = "scePadShareOutputData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadShareOutputData(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "80XdmVYsNPA", ExportName = "scePadStartRecording", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadStartRecording(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "gAHvg6JPIic", ExportName = "scePadStopRecording", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadStopRecording(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "Oi7FzRWFr0Y", ExportName = "scePadSwitchConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadSwitchConnection(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "0MB5x-ieRGI", ExportName = "scePadVertualDeviceAddDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVertualDeviceAddDevice(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "N7tpsjWQ87s", ExportName = "scePadVirtualDeviceAddDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVirtualDeviceAddDevice(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "PFec14-UhEQ", ExportName = "scePadVirtualDeviceDeleteDevice", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVirtualDeviceDeleteDevice(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "pjPCronWdxI", ExportName = "scePadVirtualDeviceDisableButtonRemapping", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVirtualDeviceDisableButtonRemapping(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "LKXfw7VJYqg", ExportName = "scePadVirtualDeviceGetRemoteSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVirtualDeviceGetRemoteSetting(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "IWOyO5jKuZg", ExportName = "scePadVirtualDeviceInsertData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePad")]
    public static int PadVirtualDeviceInsertData(CpuContext ctx) => ctx.SetReturn(0);

    private static int ReturnForPrimaryHandle(CpuContext ctx) =>
        unchecked((int)ctx[CpuRegister.Rdi]) == PrimaryPadHandle
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);

    private static int WritePadInfo(CpuContext ctx, ulong informationAddress)
    {
        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[0x98];
        information.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x08..], PrimaryPadHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x0C..], 0x00000101);
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x18..], 0x00FF0000);
        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ReadNeutralPadData(CpuContext ctx, bool multiple)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = multiple ? unchecked((int)ctx[CpuRegister.Rdx]) : 1;
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        data[0x04] = 128;
        data[0x05] = 128;
        data[0x06] = 128;
        data[0x07] = 128;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        data[0x68] = 1;
        return ctx.Memory.TryWrite(dataAddress, data)
            ? ctx.SetReturn(multiple ? 1 : 0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
    Nid = "W2G-yoyMF5U",
    ExportName = "scePadSetVibrationMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libScePad")]
    public static int PadSetVibrationMode(CpuContext ctx)
    {
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "2JgFB2n9oUM",
        ExportName = "scePadSetTriggerEffect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTriggerEffect(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> parameter = stackalloc byte[120];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var triggerMask = parameter[0];
        XInputReader.SetTriggerRumble(
            (triggerMask & 0x01) != 0 ? DecodeTriggerVibration(parameter[8..64]) : null,
            (triggerMask & 0x02) != 0 ? DecodeTriggerVibration(parameter[64..120]) : null);
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "znaWI0gpuo8",
        ExportName = "scePadGetTriggerEffectState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetTriggerEffectState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Both adaptive triggers report the neutral "no effect active" state;
        // trigger effects are decoded straight to rumble in PadSetTriggerEffect
        // and never latch a state machine here.
        Span<byte> state = stackalloc byte[8];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static byte DecodeTriggerVibration(ReadOnlySpan<byte> command)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(command);
        var amplitude = mode switch
        {
            3 when command[10] != 0 => command[9],
            6 when command[8] != 0 => command[9..19].ToArray().Max(),
            _ => (byte)0,
        };
        return (byte)(Math.Min(amplitude, (byte)8) * 255 / 8);
    }

    [SysAbiExport(
        Nid = "yFVnOdGxvZY",
        ExportName = "scePadSetVibration",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetVibration(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadVibrationParam: { uint8_t largeMotor; uint8_t smallMotor; }
        Span<byte> parameter = stackalloc byte[2];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        DualSenseReader.SetRumble(parameter[0], parameter[1]);
        XInputReader.SetRumble(parameter[0], parameter[1]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RR4novUEENY",
        ExportName = "scePadSetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadColor: { uint8_t r; uint8_t g; uint8_t b; uint8_t reserved; }
        Span<byte> color = stackalloc byte[4];
        if (!ctx.Memory.TryRead(parameterAddress, color))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        DualSenseReader.SetLightbar(color[0], color[1], color[2]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "DscD1i9HX1w",
        ExportName = "scePadResetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadResetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        DualSenseReader.ResetLightbar();
        return ctx.SetReturn(0);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var input = ReadHostInputState();
        var buttons = input.Buttons;
        var leftX = input.LeftX;
        var leftY = input.LeftY;
        var rightX = input.RightX;
        var rightY = input.RightY;
        var l2 = input.L2;
        var r2 = input.R2;

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = l2;
        data[0x09] = r2;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        var timestampTicks = Stopwatch.GetTimestamp();
        var timestampMicroseconds =
            ((ulong)(timestampTicks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(timestampTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);
        data[0x68] = 1;

        return ctx.Memory.TryWrite(dataAddress, data);
    }

    private static PadState ReadHostInputState()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastInputSampleTicks != 0 && now - _lastInputSampleTicks < InputSampleIntervalTicks)
        {
            return _cachedInputState;
        }

        var acceptsKeyboardInput = IsEmulatorWindowFocused();
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons() : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x41), IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x57), IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x4A), IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x49), IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && IsKeyDown(0x46) ? (byte)255 : (byte)0;

        if (DualSenseReader.TryGetState(out var pad))
        {
            buttons |= pad.Buttons;
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.L2);
            r2 = Math.Max(r2, pad.R2);
        }

        if (XInputReader.TryGetState(out var xpad))
        {
            buttons |= xpad.Buttons;
            leftX = MergeAxis(xpad.LeftX, leftX);
            leftY = MergeAxis(xpad.LeftY, leftY);
            rightX = MergeAxis(xpad.RightX, rightX);
            rightY = MergeAxis(xpad.RightY, rightY);
            l2 = Math.Max(l2, xpad.L2);
            r2 = Math.Max(r2, xpad.R2);
        }

        _cachedInputState = new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2);
        _lastInputSampleTicks = now;
        return _cachedInputState;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsKeyDown(int vk) =>
        (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsEmulatorWindowFocused()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static uint ReadKeyboardButtons()
    {
        uint buttons = 0;
        // D-pad
        if (IsKeyDown(0x25)) buttons |= 0x0080; // Left
        if (IsKeyDown(0x27)) buttons |= 0x0020; // Right
        if (IsKeyDown(0x26)) buttons |= 0x0010; // Up
        if (IsKeyDown(0x28)) buttons |= 0x0040; // Down
        // Face buttons
        if (IsKeyDown(0x5A) || IsKeyDown(0x0D)) buttons |= 0x4000; // Z / Enter = Cross
        if (IsKeyDown(0x58) || IsKeyDown(0x1B)) buttons |= 0x2000; // X / Escape = Circle
        if (IsKeyDown(0x43)) buttons |= 0x8000; // C = Square
        if (IsKeyDown(0x56)) buttons |= 0x1000; // V = Triangle
        // Shoulder buttons
        if (IsKeyDown(0x51)) buttons |= 0x0400; // Q = L1
        if (IsKeyDown(0x45)) buttons |= 0x0800; // E = R1
        if (IsKeyDown(0x52)) buttons |= 0x0100; // R = L2 (digital)
        if (IsKeyDown(0x46)) buttons |= 0x0200; // F = R2 (digital)
        // Options (Start)
        if (IsKeyDown(0x09) || IsKeyDown(0x08)) buttons |= 0x0008; // Tab / Backspace = Options
        return buttons;
    }

    private static byte ReadAnalogStick(bool negative, bool positive)
    {
        if (negative && !positive) return 0;
        if (positive && !negative) return 255;
        return 128;
    }

    private static byte MergeAxis(byte controller, byte keyboard)
    {
        const int Deadzone = 10;
        return Math.Abs(controller - 128) > Deadzone ? controller : keyboard;
    }
}
