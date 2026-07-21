// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpSessionSignalingExports
{
    private const int NpSessionSignalingErrorInvalidArgument = unchecked((int)0x80553303);
    private const int NpSessionSignalingErrorNotInitialized = unchecked((int)0x80553301);
    private const int NpSessionSignalingErrorContextNotFound = unchecked((int)0x80553308);
    private const int NpSessionSignalingErrorGroupNotFound = unchecked((int)0x8055330A);
    private const int NpSessionSignalingErrorConnectionNotFound = unchecked((int)0x8055330C);
    private static readonly object StateGate = new();
    private static readonly HashSet<int> Contexts = [];
    private static int _nextContext = 1;
    private static int _nextRequest = 1;
    private static bool _initialized;

    [SysAbiExport(
        Nid = "ysmw6J-P8Ak",
        ExportName = "sceNpSessionSignalingInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingInitialize(CpuContext ctx)
    {
        lock (StateGate)
        {
            _initialized = true;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "CqJuNXo5yiM",
        ExportName = "sceNpSessionSignalingTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingTerminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            _initialized = false;
            Contexts.Clear();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "GtuZGmN-tKw",
        ExportName = "sceNpSessionSignalingCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingCreateContext(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        if (ctx[CpuRegister.Rdi] == 0 || outAddress == 0)
        {
            return ctx.SetReturn(NpSessionSignalingErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(NpSessionSignalingErrorNotInitialized);
            }

            var id = _nextContext++;
            Contexts.Add(id);
            if (!ctx.TryWriteInt32(outAddress, id, checkNil: true))
            {
                Contexts.Remove(id);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Z9Q9LzQDXf0",
        ExportName = "sceNpSessionSignalingDestroyContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingDestroyContext(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Contexts.Remove(unchecked((int)ctx[CpuRegister.Rdi]))
                ? 0
                : NpSessionSignalingErrorContextNotFound);
        }
    }

    [SysAbiExport(
        Nid = "r8mVMwlafF8",
        ExportName = "sceNpSessionSignalingRequestPrepare",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingRequestPrepare(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Contexts.Contains(contextId))
            {
                return ctx.SetReturn(NpSessionSignalingErrorContextNotFound);
            }

            if (outAddress == 0)
            {
                return ctx.SetReturn(NpSessionSignalingErrorInvalidArgument);
            }

            return ctx.TryWriteInt32(outAddress, _nextRequest++, checkNil: true)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "9r7dM3puxMk",
        ExportName = "sceNpSessionSignalingActivateUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingActivateUser(CpuContext ctx) => ReturnNoGroup(ctx, ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "r4XacqHvkn4",
        ExportName = "sceNpSessionSignalingActivateSession",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingActivateSession(CpuContext ctx) => ReturnNoGroup(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(
        Nid = "cQkBH-pXhF0",
        ExportName = "sceNpSessionSignalingDeactivate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingDeactivate(CpuContext ctx) =>
        ctx.SetReturn(IsKnownContext(unchecked((int)ctx[CpuRegister.Rdi]))
            ? NpSessionSignalingErrorGroupNotFound
            : NpSessionSignalingErrorContextNotFound);

    [SysAbiExport(
        Nid = "yJw2m6UWDYU",
        ExportName = "sceNpSessionSignalingGetConnectionInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetConnectionInfo(CpuContext ctx) => ReturnNoConnection(ctx);

    [SysAbiExport(
        Nid = "n1fn2KFeLDA",
        ExportName = "sceNpSessionSignalingGetConnectionStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetConnectionStatus(CpuContext ctx) => ReturnNoConnection(ctx);

    [SysAbiExport(
        Nid = "dJJ0UPrrsok",
        ExportName = "sceNpSessionSignalingGetConnectionFromPeerAddress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetConnectionFromPeerAddress(CpuContext ctx) =>
        ReturnNoConnectionId(ctx, ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "RcGZnakPiOk",
        ExportName = "sceNpSessionSignalingGetConnectionFromNetAddress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetConnectionFromNetAddress(CpuContext ctx) =>
        ReturnNoConnectionId(ctx, ctx[CpuRegister.Rcx]);

    [SysAbiExport(
        Nid = "OTilStjd9L8",
        ExportName = "sceNpSessionSignalingGetLocalNetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetLocalNetInfo(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(NpSessionSignalingErrorInvalidArgument);
        }

        return ctx.SetReturn(IsKnownContext(unchecked((int)ctx[CpuRegister.Rdi]))
            ? NpSessionSignalingErrorGroupNotFound
            : NpSessionSignalingErrorContextNotFound);
    }

    [SysAbiExport(
        Nid = "+Q++Q49a9z8",
        ExportName = "sceNpSessionSignalingGetConnectionStatistics",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetConnectionStatistics(CpuContext ctx) =>
        ctx.SetReturn(ctx[CpuRegister.Rdi] == 0
            ? NpSessionSignalingErrorInvalidArgument
            : NpSessionSignalingErrorConnectionNotFound);

    [SysAbiExport(
        Nid = "lbXTXRG5nyM",
        ExportName = "sceNpSessionSignalingGetMemoryInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingGetMemoryInfo(CpuContext ctx) =>
        ctx.SetReturn(NpSessionSignalingErrorInvalidArgument);

    private static int ReturnNoGroup(CpuContext ctx, ulong groupIdAddress)
    {
        if (!IsKnownContext(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NpSessionSignalingErrorContextNotFound);
        }

        if (groupIdAddress != 0 && !ctx.TryWriteInt32(groupIdAddress, -1, checkNil: true))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpSessionSignalingErrorGroupNotFound);
    }

    private static int ReturnNoConnection(CpuContext ctx) =>
        ctx.SetReturn(IsKnownContext(unchecked((int)ctx[CpuRegister.Rdi]))
            ? NpSessionSignalingErrorConnectionNotFound
            : NpSessionSignalingErrorContextNotFound);

    private static int ReturnNoConnectionId(CpuContext ctx, ulong connectionIdAddress)
    {
        if (!IsKnownContext(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NpSessionSignalingErrorContextNotFound);
        }

        if (connectionIdAddress == 0)
        {
            return ctx.SetReturn(NpSessionSignalingErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(connectionIdAddress, -1, checkNil: true)
            ? ctx.SetReturn(NpSessionSignalingErrorConnectionNotFound)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsKnownContext(int contextId)
    {
        lock (StateGate)
        {
            return _initialized && Contexts.Contains(contextId);
        }
    }
}
