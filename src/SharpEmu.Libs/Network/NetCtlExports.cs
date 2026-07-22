// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Network;

public static class NetCtlExports
{
    private const int MaxCallbacks = 8;
    private const int NatInfoSize = 16;
    private const int NetCtlErrorNoSpace = unchecked((int)0x80412103);
    private const int NetCtlErrorInvalidId = unchecked((int)0x80412105);
    private const int NetCtlErrorInvalidAddress = unchecked((int)0x80412107);
    private const int NetCtlErrorNotAvailable = unchecked((int)0x80412109);
    private const int NetCtlInfoDevice = 1;
    private const int NetCtlInfoEtherAddress = 2;
    private const int NetCtlInfoMtu = 3;
    private const int NetCtlInfoLink = 4;
    private const int NetCtlInfoIpConfig = 11;
    private const int NetCtlInfoDhcpHostname = 12;
    private const int NetCtlInfoPppoeAuthName = 13;
    private const int NetCtlInfoIpAddress = 14;
    private const int NetCtlInfoNetmask = 15;
    private const int NetCtlInfoDefaultRoute = 16;
    private const int NetCtlInfoPrimaryDns = 17;
    private const int NetCtlInfoSecondaryDns = 18;
    private const int NetCtlInfoHttpProxyConfig = 19;
    private const int NetCtlInfoHttpProxyServer = 20;
    private const int NetCtlInfoHttpProxyPort = 21;
    private const int NetCtlDeviceWired = 0;
    private const int NetCtlLinkConnected = 1;
    private const int NetCtlIpConfigStatic = 1;
    private const int NetCtlStateIpObtained = 3;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    [SysAbiExport(
        Nid = "gky0+oaNM4k",
        ExportName = "sceNetCtlInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlInit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JO4yuTuMoKI",
        ExportName = "sceNetCtlGetNatInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetNatInfo(CpuContext ctx)
    {
        var natInfoAddress = ctx[CpuRegister.Rdi];
        if (natInfoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> natInfo = stackalloc byte[NatInfoSize];
        if (!ctx.Memory.TryRead(natInfoAddress, natInfo))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(natInfo[..sizeof(uint)]);
        if (size != NatInfoSize)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, typeof(long));
        }

        BinaryPrimitives.WriteInt32LittleEndian(natInfo[4..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(natInfo[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(natInfo[12..], 0x0200A8C0);
        return ctx.Memory.TryWrite(natInfoAddress, natInfo)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "iQw3iQPhvUQ",
        ExportName = "sceNetCtlCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlCheckCallback(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "uBPlr0lbuiI",
        ExportName = "sceNetCtlGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(stateBytes, NetCtlStateIpObtained);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "UJ+Z7Q+4ck0",
        ExportName = "sceNetCtlRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallback(CpuContext ctx)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var callbackIdAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || callbackIdAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        lock (CallbackGate)
        {
            var callbackId = Array.FindIndex(Callbacks, static callback => callback.Function == 0);
            if (callbackId < 0)
            {
                return ctx.SetReturn(NetCtlErrorNoSpace, typeof(long));
            }

            Span<byte> callbackIdBytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(callbackIdBytes, unchecked((uint)callbackId));
            if (!ctx.Memory.TryWrite(callbackIdAddress, callbackIdBytes))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }

            Callbacks[callbackId] = new CallbackRegistration(function, argument);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK, typeof(long));
    }

    [SysAbiExport(
        Nid = "1NE9OWdBIww",
        ExportName = "sceNetCtlRegisterCallbackV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackV6(CpuContext ctx) => NetCtlRegisterCallback(ctx);

    [SysAbiExport(
        Nid = "obuxdTiwkF8",
        ExportName = "sceNetCtlGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfo(CpuContext ctx)
    {
        var code = unchecked((int)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        return code switch
        {
            NetCtlInfoDevice => WriteUInt32(ctx, infoAddress, NetCtlDeviceWired),
            NetCtlInfoEtherAddress => WriteBytes(ctx, infoAddress, [0x02, 0x53, 0x48, 0x41, 0x52, 0x50]),
            NetCtlInfoMtu => WriteUInt32(ctx, infoAddress, 1500),
            NetCtlInfoLink => WriteUInt32(ctx, infoAddress, NetCtlLinkConnected),
            NetCtlInfoIpConfig => WriteUInt32(ctx, infoAddress, NetCtlIpConfigStatic),
            NetCtlInfoDhcpHostname => WriteAsciiZ(ctx, infoAddress, string.Empty, 256),
            NetCtlInfoPppoeAuthName => WriteAsciiZ(ctx, infoAddress, string.Empty, 128),
            NetCtlInfoIpAddress => WriteAsciiZ(ctx, infoAddress, "192.168.0.2", 16),
            NetCtlInfoNetmask => WriteAsciiZ(ctx, infoAddress, "255.255.255.0", 16),
            NetCtlInfoDefaultRoute => WriteAsciiZ(ctx, infoAddress, "192.168.0.1", 16),
            NetCtlInfoPrimaryDns => WriteAsciiZ(ctx, infoAddress, "1.1.1.1", 16),
            NetCtlInfoSecondaryDns => WriteAsciiZ(ctx, infoAddress, "1.1.1.1", 16),
            NetCtlInfoHttpProxyConfig => WriteUInt32(ctx, infoAddress, 0),
            NetCtlInfoHttpProxyServer => WriteAsciiZ(ctx, infoAddress, string.Empty, 256),
            NetCtlInfoHttpProxyPort => WriteUInt16(ctx, infoAddress, 0),
            _ => ctx.SetReturn(NetCtlErrorNotAvailable, typeof(long)),
        };
    }

    [SysAbiExport(
        Nid = "Rqm2OnZMCz0",
        ExportName = "sceNetCtlUnregisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallback(CpuContext ctx)
    {
        var callbackId = unchecked((int)ctx[CpuRegister.Rdi]);
        if ((uint)callbackId >= MaxCallbacks)
        {
            return ctx.SetReturn(NetCtlErrorInvalidId, typeof(long));
        }

        lock (CallbackGate)
        {
            if (Callbacks[callbackId].Function == 0)
            {
                return ctx.SetReturn(NetCtlErrorInvalidId, typeof(long));
            }

            Callbacks[callbackId] = default;
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "hIUVeUNxAwc",
        ExportName = "sceNetCtlUnregisterCallbackV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallbackV6(CpuContext ctx) => NetCtlUnregisterCallback(ctx);

    [SysAbiExport(
        Nid = "Z4wwCFiBELQ",
        ExportName = "sceNetCtlTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlTerm(CpuContext ctx)
    {
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    private static bool _ipcConnected = true;
    private static bool _bandwidthManagementEnabled;

    [SysAbiExport(Nid = "UF6H6+kjyQs", ExportName = "sceNetCtlCheckCallbackForLibIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlCheckCallbackForLibIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "vv6g8zoanL4", ExportName = "sceNetCtlClearEventForLibIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlClearEventForLibIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "8OJ86vFucfo", ExportName = "sceNetCtlClearEventIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlClearEventIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "HCD46HVTyQg", ExportName = "sceNetCtlConnectConfIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlConnectConfIpcInt(CpuContext ctx) => SetIpcConnection(ctx, true);

    [SysAbiExport(Nid = "ID+Gq3Ddzbg", ExportName = "sceNetCtlConnectIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlConnectIpcInt(CpuContext ctx) => SetIpcConnection(ctx, true);

    [SysAbiExport(Nid = "aPpic8K75YA", ExportName = "sceNetCtlConnectWithRetryIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlConnectWithRetryIpcInt(CpuContext ctx) => SetIpcConnection(ctx, true);

    [SysAbiExport(Nid = "9y4IcsJdTCc", ExportName = "sceNetCtlDisableBandwidthManagementIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlDisableBandwidthManagementIpcInt(CpuContext ctx)
    {
        _bandwidthManagementEnabled = false;
        return NetCtlOk(ctx);
    }

    [SysAbiExport(Nid = "qOefcpoSs0k", ExportName = "sceNetCtlDisconnectIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlDisconnectIpcInt(CpuContext ctx) => SetIpcConnection(ctx, false);

    [SysAbiExport(Nid = "x9bSmRSE+hc", ExportName = "sceNetCtlEnableBandwidthManagementIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlEnableBandwidthManagementIpcInt(CpuContext ctx)
    {
        _bandwidthManagementEnabled = true;
        return NetCtlOk(ctx);
    }

    [SysAbiExport(Nid = "eCUIlA2t5CE", ExportName = "sceNetCtlGetBandwidthInfoIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetBandwidthInfoIpcInt(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "2EfjPXVPk3s", ExportName = "sceNetCtlGetEtherLinkMode", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetEtherLinkMode(CpuContext ctx) => WriteRequiredUInt32(ctx, ctx[CpuRegister.Rdi], 1);

    [SysAbiExport(Nid = "teuK4QnJTGg", ExportName = "sceNetCtlGetIfStat", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetIfStat(CpuContext ctx) => ClearRequired(ctx, ctx[CpuRegister.Rdi], 64);

    [SysAbiExport(Nid = "xstcTqAhTys", ExportName = "sceNetCtlGetInfoIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfoIpcInt(CpuContext ctx) => NetCtlGetInfo(ctx);

    [SysAbiExport(Nid = "arAQRFlwqaA", ExportName = "sceNetCtlGetInfoV6IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfoV6IpcInt(CpuContext ctx) => NetCtlGetInfo(ctx);

    [SysAbiExport(Nid = "x+cnsAxKSHo", ExportName = "sceNetCtlGetNatInfoIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetNatInfoIpcInt(CpuContext ctx) => NetCtlGetNatInfo(ctx);

    [SysAbiExport(Nid = "hhTsdv99azU", ExportName = "sceNetCtlGetNetEvConfigInfoIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetNetEvConfigInfoIpcInt(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "0cBgduPRR+M", ExportName = "sceNetCtlGetResult", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetResult(CpuContext ctx) => WriteRequiredUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "NEtnusbZyAs", ExportName = "sceNetCtlGetResultIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetResultIpcInt(CpuContext ctx) => NetCtlGetResult(ctx);

    [SysAbiExport(Nid = "vdsTa93atXY", ExportName = "sceNetCtlGetResultV6IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetResultV6IpcInt(CpuContext ctx) => NetCtlGetResult(ctx);

    [SysAbiExport(Nid = "wP0Ab2maR1Y", ExportName = "sceNetCtlGetScanInfoBssidForSsidListScanIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetScanInfoBssidForSsidListScanIpcInt(CpuContext ctx) => ClearScanResult(ctx);

    [SysAbiExport(Nid = "Wn-+887Lt2s", ExportName = "sceNetCtlGetScanInfoBssidIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetScanInfoBssidIpcInt(CpuContext ctx) => ClearScanResult(ctx);

    [SysAbiExport(Nid = "FEdkOG1VbQo", ExportName = "sceNetCtlGetScanInfoByBssidIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetScanInfoByBssidIpcInt(CpuContext ctx) => ClearScanResult(ctx);

    [SysAbiExport(Nid = "irV8voIAHDw", ExportName = "sceNetCtlGetScanInfoForSsidListScanIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetScanInfoForSsidListScanIpcInt(CpuContext ctx) => ClearScanResult(ctx);

    [SysAbiExport(Nid = "L97eAHI0xxs", ExportName = "sceNetCtlGetScanInfoForSsidScanIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetScanInfoForSsidScanIpcInt(CpuContext ctx) => ClearScanResult(ctx);

    [SysAbiExport(Nid = "JXlI9EZVjf4", ExportName = "sceNetCtlGetState2IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetState2IpcInt(CpuContext ctx) => WriteIpcState(ctx);

    [SysAbiExport(Nid = "gvnJPMkSoAY", ExportName = "sceNetCtlGetStateIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetStateIpcInt(CpuContext ctx) => WriteIpcState(ctx);

    [SysAbiExport(Nid = "O8Fk4w5MWss", ExportName = "sceNetCtlGetStateV6IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetStateV6IpcInt(CpuContext ctx) => WriteIpcState(ctx);

    [SysAbiExport(Nid = "BXW9b3R1Nw4", ExportName = "sceNetCtlGetWifiType", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlGetWifiType(CpuContext ctx) => WriteRequiredUInt32(ctx, ctx[CpuRegister.Rdi], 0);

    [SysAbiExport(Nid = "YtAnCkTR0K4", ExportName = "sceNetCtlIsBandwidthManagementEnabledIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlIsBandwidthManagementEnabledIpcInt(CpuContext ctx) =>
        WriteRequiredUInt32(ctx, ctx[CpuRegister.Rdi], _bandwidthManagementEnabled ? 1U : 0U);

    [SysAbiExport(Nid = "WRvDk2syatE", ExportName = "sceNetCtlRegisterCallbackForLibIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackForLibIpcInt(CpuContext ctx) => NetCtlRegisterCallback(ctx);

    [SysAbiExport(Nid = "rqkh2kXvLSw", ExportName = "sceNetCtlRegisterCallbackIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackIpcInt(CpuContext ctx) => NetCtlRegisterCallback(ctx);

    [SysAbiExport(Nid = "ipqlpcIqRsQ", ExportName = "sceNetCtlRegisterCallbackV6IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackV6IpcInt(CpuContext ctx) => NetCtlRegisterCallback(ctx);

    [SysAbiExport(Nid = "reIsHryCDx4", ExportName = "sceNetCtlScanIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlScanIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "LJYiiIS4HB0", ExportName = "sceNetCtlSetErrorNotificationEnabledIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlSetErrorNotificationEnabledIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "DjuqqqV08Nk", ExportName = "sceNetCtlSetStunWithPaddingFlagIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlSetStunWithPaddingFlagIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    [SysAbiExport(Nid = "urWaUWkEGZg", ExportName = "sceNetCtlUnregisterCallbackForLibIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallbackForLibIpcInt(CpuContext ctx) => NetCtlUnregisterCallback(ctx);

    [SysAbiExport(Nid = "by9cbB7JGJE", ExportName = "sceNetCtlUnregisterCallbackIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallbackIpcInt(CpuContext ctx) => NetCtlUnregisterCallback(ctx);

    [SysAbiExport(Nid = "Hjxpy28aID8", ExportName = "sceNetCtlUnregisterCallbackV6IpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallbackV6IpcInt(CpuContext ctx) => NetCtlUnregisterCallback(ctx);

    [SysAbiExport(Nid = "1HSvkN9oxO4", ExportName = "sceNetCtlUnsetStunWithPaddingFlagIpcInt", Target = Generation.Gen5, LibraryName = "libSceNetCtl")]
    public static int NetCtlUnsetStunWithPaddingFlagIpcInt(CpuContext ctx) => NetCtlOk(ctx);

    internal static void ResetIpcStateForTests()
    {
        _ipcConnected = true;
        _bandwidthManagementEnabled = false;
        lock (CallbackGate)
        {
            Array.Clear(Callbacks);
        }
    }

    private static int SetIpcConnection(CpuContext ctx, bool connected)
    {
        _ipcConnected = connected;
        return NetCtlOk(ctx);
    }

    private static int WriteIpcState(CpuContext ctx) =>
        WriteRequiredUInt32(ctx, ctx[CpuRegister.Rdi], _ipcConnected ? NetCtlStateIpObtained : 0U);

    private static int ClearScanResult(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdx] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rdx], 0))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ClearOptional(ctx, ctx[CpuRegister.Rsi], 256);
    }

    private static int ClearRequired(CpuContext ctx, ulong address, int byteCount)
    {
        if (address == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        return ClearOptional(ctx, address, byteCount);
    }

    private static int ClearOptional(CpuContext ctx, ulong address, int byteCount)
    {
        if (address == 0)
        {
            return NetCtlOk(ctx);
        }

        return WriteBytes(ctx, address, new byte[byteCount]);
    }

    private static int WriteRequiredUInt32(CpuContext ctx, ulong address, uint value) => address == 0
        ? ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long))
        : WriteUInt32(ctx, address, value);

    private static int NetCtlOk(CpuContext ctx) => ctx.SetReturn(0, typeof(long));

    private static int WriteBytes(CpuContext ctx, ulong address, ReadOnlySpan<byte> bytes) =>
        ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));

    private static int WriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteAsciiZ(CpuContext ctx, ulong address, string value, int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        var copyCount = Math.Min(value.Length, byteCount - 1);
        for (var i = 0; i < copyCount; i++)
        {
            bytes[i] = (byte)value[i];
        }

        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }
}
