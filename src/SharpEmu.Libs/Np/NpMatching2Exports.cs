// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpMatching2Exports
{
    private const int ErrorAlreadyInitialized = unchecked((int)0x80550C02);
    private const int ErrorNotInitialized = unchecked((int)0x80550C03);
    private const int ErrorContextAlreadyStarted = unchecked((int)0x80550C07);
    private const int ErrorContextNotStarted = unchecked((int)0x80550C08);
    private const int ErrorInvalidArgument = unchecked((int)0x80550C0A);
    private const int ErrorInvalidContextId = unchecked((int)0x80550C0B);
    private static readonly object StateGate = new();
    private static readonly Dictionary<ushort, ContextState> Contexts = [];
    private static ushort _nextContextId = 1;
    private static uint _nextRequestId = 1;
    private static ulong _poolSize;
    private static ulong _sslPoolSize;
    private static ulong _contextCallback;
    private static ulong _contextCallbackArgument;
    private static bool _initialized;

    private sealed class ContextState
    {
        public bool Started { get; set; }
        public ulong DefaultRequestCallback { get; set; }
        public ulong DefaultRequestCallbackArgument { get; set; }
        public Dictionary<int, (ulong Callback, ulong Argument)> Callbacks { get; } = [];
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            _initialized = false;
            _nextContextId = 1;
            _nextRequestId = 1;
            _poolSize = 0;
            _sslPoolSize = 0;
            _contextCallback = 0;
            _contextCallbackArgument = 0;
            Contexts.Clear();
        }
    }

    [SysAbiExport(Nid = "10t3e5+JPnU", ExportName = "sceNpMatching2Initialize", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2Initialize(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0 || !ctx.TryReadUInt64(paramAddress + 0x20, out var size) || (size != 0x28 && size != 0x30))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (_initialized)
            {
                return ctx.SetReturn(ErrorAlreadyInitialized);
            }

            if (!ctx.TryReadUInt64(paramAddress, out _poolSize))
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            _sslPoolSize = 0;
            if (size == 0x30 && !ctx.TryReadUInt64(paramAddress + 0x28, out _sslPoolSize))
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            _initialized = true;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "Mqp3lJ+sjy4", ExportName = "sceNpMatching2Terminate", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2Terminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _initialized = false;
            Contexts.Clear();
            _contextCallback = 0;
            _contextCallbackArgument = 0;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "YfmpW719rMo", ExportName = "sceNpMatching2CreateContext", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2CreateContext(CpuContext ctx) => CreateContext(ctx, expectedSize: 0x28, requireNpId: true);

    [SysAbiExport(Nid = "ajvzc8e2upo", ExportName = "sceNpMatching2CreateContextA", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2CreateContextA(CpuContext ctx) => CreateContext(ctx, expectedSize: 0x10, requireNpId: false);

    [SysAbiExport(Nid = "Nz-ZE7ur32I", ExportName = "sceNpMatching2DestroyContext", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2DestroyContext(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            return ctx.SetReturn(Contexts.Remove(unchecked((ushort)ctx[CpuRegister.Rdi])) ? 0 : ErrorInvalidContextId);
        }
    }

    [SysAbiExport(Nid = "7vjNQ6Z1op0", ExportName = "sceNpMatching2ContextStart", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2ContextStart(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (state!.Started)
            {
                return ctx.SetReturn(ErrorContextAlreadyStarted);
            }

            state.Started = true;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "-f6M4caNe8k", ExportName = "sceNpMatching2ContextStop", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2ContextStop(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (!state!.Started)
            {
                return ctx.SetReturn(ErrorContextNotStarted);
            }

            state.Started = false;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "pFzhpCMlJXQ", ExportName = "sceNpMatching2AbortContextStart", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2AbortContextStart(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            state!.Started = false;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "fQQfP87I7hs", ExportName = "sceNpMatching2RegisterContextCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterContextCallback(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _contextCallback = ctx[CpuRegister.Rdi];
            _contextCallbackArgument = ctx[CpuRegister.Rsi];
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "4Nj7u5B5yCA", ExportName = "sceNpMatching2RegisterLobbyEventCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterLobbyEventCallback(CpuContext ctx) => RegisterCallback(ctx, 1);

    [SysAbiExport(Nid = "DnPUsBAe8oI", ExportName = "sceNpMatching2RegisterLobbyMessageCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterLobbyMessageCallback(CpuContext ctx) => RegisterCallback(ctx, 2);

    [SysAbiExport(Nid = "p+2EnxmaAMM", ExportName = "sceNpMatching2RegisterRoomEventCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterRoomEventCallback(CpuContext ctx) => RegisterCallback(ctx, 3);

    [SysAbiExport(Nid = "uBESzz4CQws", ExportName = "sceNpMatching2RegisterRoomMessageCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterRoomMessageCallback(CpuContext ctx) => RegisterCallback(ctx, 4);

    [SysAbiExport(Nid = "0UMeWRGnZKA", ExportName = "sceNpMatching2RegisterSignalingCallback", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2RegisterSignalingCallback(CpuContext ctx) => RegisterCallback(ctx, 5);

    [SysAbiExport(Nid = "+8e7wXLmjds", ExportName = "sceNpMatching2SetDefaultRequestOptParam", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetDefaultRequestOptParam(CpuContext ctx)
    {
        var optionAddress = ctx[CpuRegister.Rsi];
        if (optionAddress == 0 || !ctx.TryReadUInt64(optionAddress, out var callback) || !ctx.TryReadUInt64(optionAddress + 8, out var argument))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            state!.DefaultRequestCallback = callback;
            state.DefaultRequestCallbackArgument = argument;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "LhCPctIICxQ", ExportName = "sceNpMatching2GetServerId", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetServerId(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            return ctx.TryWriteUInt16(outAddress, 1)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "gpSAvdheZ0Q", ExportName = "sceNpMatching2GetMemoryInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetMemoryInfo(CpuContext ctx) => WriteMemoryInfo(ctx, _poolSize);

    [SysAbiExport(Nid = "8btynvj0KNA", ExportName = "sceNpMatching2GetSslMemoryInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetSslMemoryInfo(CpuContext ctx) => WriteMemoryInfo(ctx, _sslPoolSize);

    [SysAbiExport(Nid = "KC+GnHzrK2o", ExportName = "sceNpMatching2GetRoomMemberIdListLocal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomMemberIdListLocal(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var listAddress = ctx[CpuRegister.Rcx];
            var capacity = Math.Min(ctx[CpuRegister.R8], 256UL);
            if (capacity == 0)
            {
                return ctx.SetReturn(0);
            }

            if (listAddress == 0)
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            return WriteZeroRaw(ctx, listAddress, checked((int)capacity * sizeof(ushort)))
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "vbtWT3lZBOM", ExportName = "sceNpMatching2GetRoomPasswordLocal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomPasswordLocal(CpuContext ctx) => WriteLocalValue(ctx, ctx[CpuRegister.Rdx], sizeof(ulong));

    [SysAbiExport(Nid = "cgQhq3E0eGo", ExportName = "sceNpMatching2GetSignalingOptParamLocal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetSignalingOptParamLocal(CpuContext ctx) => WriteLocalValue(ctx, ctx[CpuRegister.Rdx], 8);

    [SysAbiExport(Nid = "tHD5FPFXtu4", ExportName = "sceNpMatching2SignalingGetConnectionStatus", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetConnectionStatus(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if ((ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteInt32(ctx[CpuRegister.Rcx], 0)) ||
                (ctx[CpuRegister.R8] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.R8], 0)) ||
                (ctx[CpuRegister.R9] != 0 && !ctx.TryWriteUInt16(ctx[CpuRegister.R9], 0)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "twVupeaYYrk", ExportName = "sceNpMatching2SignalingGetConnectionInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetConnectionInfo(CpuContext ctx) => WriteConnectionInfo(ctx, 0x24);

    [SysAbiExport(Nid = "nNeC3F8-g+4", ExportName = "sceNpMatching2SignalingGetConnectionInfoA", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetConnectionInfoA(CpuContext ctx) => WriteConnectionInfo(ctx, 0x10);

    [SysAbiExport(Nid = "380EWm2DrVg", ExportName = "sceNpMatching2SignalingGetLocalNetInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetLocalNetInfo(CpuContext ctx) => WriteLocalValue(ctx, ctx[CpuRegister.Rsi], 0x18);

    [SysAbiExport(Nid = "CTy4PBhpWDw", ExportName = "sceNpMatching2SignalingGetPeerNetInfoResult", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetPeerNetInfoResult(CpuContext ctx) => WriteLocalValue(ctx, ctx[CpuRegister.Rdx], 0x18);

    [SysAbiExport(Nid = "zCWZmXXN600", ExportName = "sceNpMatching2CreateJoinRoom", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2CreateJoinRoom(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "V6KSpKv9XJE", ExportName = "sceNpMatching2CreateJoinRoomA", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2CreateJoinRoomA(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "wyvlEgZ-55w", ExportName = "sceNpMatching2GetLobbyInfoList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetLobbyInfoList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "1JtbJ0kxm3E", ExportName = "sceNpMatching2GetLobbyMemberDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetLobbyMemberDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "1Z4Xxumgm+Y", ExportName = "sceNpMatching2GetLobbyMemberDataInternalList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetLobbyMemberDataInternalList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "26vWrPAWJfM", ExportName = "sceNpMatching2GetRoomDataExternalList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomDataExternalList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "Jraxifmoet4", ExportName = "sceNpMatching2GetRoomDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "dMQ+xGvTdqM", ExportName = "sceNpMatching2GetRoomMemberDataExternalList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomMemberDataExternalList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "5lhvOqheFBA", ExportName = "sceNpMatching2GetRoomMemberDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetRoomMemberDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "qeF-q5KDtAc", ExportName = "sceNpMatching2GetUserInfoList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetUserInfoList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "GyI2f9yDUXM", ExportName = "sceNpMatching2GetUserInfoListA", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetUserInfoListA(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "rJNPJqDCpiI", ExportName = "sceNpMatching2GetWorldInfoList", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GetWorldInfoList(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "NCP3bLGPt+o", ExportName = "sceNpMatching2GrantRoomOwner", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2GrantRoomOwner(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "n5JmImxTiZU", ExportName = "sceNpMatching2JoinLobby", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2JoinLobby(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "CSIMDsVjs-g", ExportName = "sceNpMatching2JoinRoom", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2JoinRoom(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "gQ6cUriNpgs", ExportName = "sceNpMatching2JoinRoomA", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2JoinRoomA(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "AUVfU6byg3c", ExportName = "sceNpMatching2KickoutRoomMember", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2KickoutRoomMember(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "BBbJ92uUdCg", ExportName = "sceNpMatching2LeaveLobby", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2LeaveLobby(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "BD6kfx442Do", ExportName = "sceNpMatching2LeaveRoom", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2LeaveRoom(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "VqZX7POg2Mk", ExportName = "sceNpMatching2SearchRoom", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SearchRoom(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "K+KtxhPsMZ4", ExportName = "sceNpMatching2SendLobbyChatMessage", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SendLobbyChatMessage(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "opDpl74pi2E", ExportName = "sceNpMatching2SendRoomChatMessage", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SendRoomChatMessage(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "Iw2h0Jrrb5U", ExportName = "sceNpMatching2SendRoomMessage", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SendRoomMessage(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "ir2CzSs9K-g", ExportName = "sceNpMatching2SetLobbyMemberDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetLobbyMemberDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "q7GK98-nYSE", ExportName = "sceNpMatching2SetRoomDataExternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetRoomDataExternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "S9D8JSYIrjE", ExportName = "sceNpMatching2SetRoomDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetRoomDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "HoqTrkS9c5Q", ExportName = "sceNpMatching2SetRoomMemberDataInternal", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetRoomMemberDataInternal(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "ES3UMUWWj9U", ExportName = "sceNpMatching2SetSignalingOptParam", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetSignalingOptParam(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "meEjIdbjAA0", ExportName = "sceNpMatching2SetUserInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SetUserInfo(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "GNSN5849fjU", ExportName = "sceNpMatching2SignalingCancelPeerNetInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingCancelPeerNetInfo(CpuContext ctx) => ReturnForContext(ctx);

    [SysAbiExport(Nid = "8CqniKDzjvg", ExportName = "sceNpMatching2SignalingGetPeerNetInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetPeerNetInfo(CpuContext ctx) => SubmitRequest(ctx);

    [SysAbiExport(Nid = "wUmwXZHaX1w", ExportName = "sceNpMatching2SignalingGetPingInfo", LibraryName = "libSceNpMatching2", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpMatching2SignalingGetPingInfo(CpuContext ctx) => SubmitRequest(ctx);

    private static int CreateContext(CpuContext ctx, ulong expectedSize, bool requireNpId)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var outAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        var sizeOffset = expectedSize == 0x28 ? 0x20UL : 0x08UL;
        if (!ctx.TryReadUInt64(paramAddress + sizeOffset, out var size) || size != expectedSize ||
            (requireNpId && (!ctx.TryReadUInt64(paramAddress, out var npId) || npId == 0)))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var contextId = AllocateContextIdLocked();
            if (contextId == 0)
            {
                return ctx.SetReturn(unchecked((int)0x80550C04));
            }

            Contexts[contextId] = new ContextState();
            if (!ctx.TryWriteUInt16(outAddress, contextId))
            {
                Contexts.Remove(contextId);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    private static ushort AllocateContextIdLocked()
    {
        for (var attempt = 0; attempt < ushort.MaxValue - 1; attempt++)
        {
            var id = _nextContextId++;
            if (id != 0 && !Contexts.ContainsKey(id))
            {
                return id;
            }
        }

        return 0;
    }

    private static int RegisterCallback(CpuContext ctx, int kind)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            state!.Callbacks[kind] = (ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
            return ctx.SetReturn(0);
        }
    }

    private static int SubmitRequest(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rsi];
        var requestIdAddress = ctx[CpuRegister.Rcx];
        if (requestAddress == 0 || requestIdAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var requestId = _nextRequestId++;
            if (requestId == 0)
            {
                requestId = _nextRequestId++;
            }

            return ctx.TryWriteUInt32(requestIdAddress, requestId)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int ReturnForContext(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _));
        }
    }

    private static int WriteMemoryInfo(CpuContext ctx, ulong maximumSize)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            Span<byte> info = stackalloc byte[0x18];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info[0x10..], maximumSize);
            return ctx.Memory.TryWrite(infoAddress, info)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int WriteLocalValue(CpuContext ctx, ulong outAddress, int size)
    {
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            return WriteZeroRaw(ctx, outAddress, size)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int WriteConnectionInfo(CpuContext ctx, int size)
    {
        var outAddress = ctx[CpuRegister.R8];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((ushort)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            return WriteZeroRaw(ctx, outAddress, size)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static bool WriteZeroRaw(CpuContext ctx, ulong address, int size)
    {
        Span<byte> bytes = size <= 64 ? stackalloc byte[size] : new byte[size];
        bytes.Clear();
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int ValidateContextLocked(ushort contextId, out ContextState? state)
    {
        state = null;
        if (!_initialized)
        {
            return ErrorNotInitialized;
        }

        return Contexts.TryGetValue(contextId, out state) ? 0 : ErrorInvalidContextId;
    }
}
