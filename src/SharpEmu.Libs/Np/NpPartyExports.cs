// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpPartyExports
{
    private const int ErrorAlreadyInitialized = unchecked((int)0x80552502);
    private const int ErrorNotInitialized = unchecked((int)0x80552503);
    private const int ErrorInvalidArgument = unchecked((int)0x80552504);
    private const int ErrorNotInParty = unchecked((int)0x80552506);
    private const ushort StateNotInParty = 2;
    private static readonly object StateGate = new();
    private static bool _initialized;
    private static int _voiceChatPriority;
    private static ulong _handler;
    private static ulong _handlerArgument;
    private static ulong _privateHandler;
    private static ulong _privateHandlerArgument;

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            _initialized = false;
            _voiceChatPriority = 0;
            _handler = 0;
            _handlerArgument = 0;
            _privateHandler = 0;
            _privateHandlerArgument = 0;
        }
    }

    [SysAbiExport(Nid = "lhYCTQmBkds", ExportName = "sceNpPartyInitialize", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyInitialize(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (_initialized)
            {
                return ctx.SetReturn(ErrorAlreadyInitialized);
            }

            _initialized = true;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "oLYkibiHqRA", ExportName = "sceNpPartyTerminate", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyTerminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _initialized = false;
            _handler = 0;
            _handlerArgument = 0;
            _privateHandler = 0;
            _privateHandlerArgument = 0;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "3e4k2mzLkmc", ExportName = "sceNpPartyCheckCallback", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyCheckCallback(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "nOZRy-slBoA", ExportName = "sceNpPartyCreate", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyCreate(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "XQSUbbnpPBA", ExportName = "sceNpPartyCreateA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyCreateA(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "DRA3ay-1DFQ", ExportName = "sceNpPartyGetId", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetId(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            return ctx.TryWriteUInt64(outAddress, 0)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "F1P+-wpxQow", ExportName = "sceNpPartyGetMemberInfo", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMemberInfo(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "v2RYVGrJDkM", ExportName = "sceNpPartyGetMemberInfoA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMemberInfoA(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "4gOMfNYzllw", ExportName = "sceNpPartyGetMemberSessionInfo", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMemberSessionInfo(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "EKi1jx59SP4", ExportName = "sceNpPartyGetMemberVoiceInfo", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMemberVoiceInfo(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "T2UOKf00ZN0", ExportName = "sceNpPartyGetMembers", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMembers(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "TaNw7W25QJw", ExportName = "sceNpPartyGetMembersA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetMembersA(CpuContext ctx) => ReturnNotInParty(ctx);

    [SysAbiExport(Nid = "aEzKdJzATZ0", ExportName = "sceNpPartyGetState", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetState(CpuContext ctx) => WriteState(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "o7grRhiGHYI", ExportName = "sceNpPartyGetStateAsUser", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetStateAsUser(CpuContext ctx) => WriteState(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "EjyAI+QNgFw", ExportName = "sceNpPartyGetStateAsUserA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetStateAsUserA(CpuContext ctx) => WriteState(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "-lc6XZnQXvM", ExportName = "sceNpPartyGetVoiceChatPriority", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyGetVoiceChatPriority(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            return ctx.TryWriteInt32(outAddress, _voiceChatPriority, checkNil: true)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "nazKyHygHhY", ExportName = "sceNpPartySetVoiceChatPriority", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartySetVoiceChatPriority(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _voiceChatPriority = unchecked((int)ctx[CpuRegister.Rdi]);
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "RXNCDw2GDEg", ExportName = "sceNpPartyJoin", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyJoin(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "J8jAi-tfJHc", ExportName = "sceNpPartyLeave", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyLeave(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "kA88gbv71ao", ExportName = "sceNpPartyRegisterHandler", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyRegisterHandler(CpuContext ctx) => RegisterHandler(ctx);

    [SysAbiExport(Nid = "+v4fVHMwFWc", ExportName = "sceNpPartyRegisterHandlerA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyRegisterHandlerA(CpuContext ctx) => RegisterHandler(ctx);

    [SysAbiExport(Nid = "zo4G5WWYpKg", ExportName = "sceNpPartyRegisterPrivateHandler", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyRegisterPrivateHandler(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _privateHandler = ctx[CpuRegister.Rdi];
            _privateHandlerArgument = ctx[CpuRegister.Rsi];
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "zQ7gIvt11Pc", ExportName = "sceNpPartyUnregisterPrivateHandler", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyUnregisterPrivateHandler(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _privateHandler = 0;
            _privateHandlerArgument = 0;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "U6VdUe-PNAY", ExportName = "sceNpPartySendBinaryMessage", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartySendBinaryMessage(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "-MFiL7hEnPE", ExportName = "sceNpPartyShowInvitationList", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyShowInvitationList(CpuContext ctx) => ReturnIfInitialized(ctx);

    [SysAbiExport(Nid = "yARHEYLajs0", ExportName = "sceNpPartyShowInvitationListA", LibraryName = "libSceNpParty", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpPartyShowInvitationListA(CpuContext ctx) => ReturnIfInitialized(ctx);

    private static int RegisterHandler(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _handler = ctx[CpuRegister.Rdi];
            _handlerArgument = ctx[CpuRegister.Rsi];
            return ctx.SetReturn(0);
        }
    }

    private static int WriteState(CpuContext ctx, ulong stateAddress)
    {
        if (stateAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            return ctx.TryWriteUInt16(stateAddress, StateNotInParty)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int ReturnIfInitialized(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(_initialized ? 0 : ErrorNotInitialized);
        }
    }

    private static int ReturnNotInParty(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(_initialized ? ErrorNotInParty : ErrorNotInitialized);
        }
    }
}
