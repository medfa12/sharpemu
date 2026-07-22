// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpSignalingExports
{
    private const int ErrorNotInitialized = unchecked((int)0x80552701);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80552702);
    private const int ErrorContextNotFound = unchecked((int)0x80552705);
    private const int ErrorRequestNotFound = unchecked((int)0x80552707);
    private const int ErrorConnectionNotFound = unchecked((int)0x8055270E);
    private const int ErrorResultNotFound = unchecked((int)0x80552713);
    private const int ErrorInvalidArgument = unchecked((int)0x80552715);
    private const int DefaultMemorySize = 0x20000;
    private static readonly object StateGate = new();
    private static readonly Dictionary<int, ContextState> Contexts = [];
    private static readonly Dictionary<int, ConnectionState> Connections = [];
    private static readonly Dictionary<int, int> PeerNetInfoRequests = [];
    private static int _nextContextId = 1;
    private static int _nextConnectionId = 1;
    private static int _nextRequestId = 1;
    private static uint _peakConnectionCount;
    private static long _memorySize = DefaultMemorySize;
    private static bool _initialized;

    private sealed class ContextState
    {
        public required bool AccountVariant { get; init; }
        public int OptionValue { get; set; } = 1;
    }

    private sealed class ConnectionState
    {
        public required int ContextId { get; init; }
        public bool Active { get; set; } = true;
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            _initialized = false;
            _memorySize = DefaultMemorySize;
            _nextContextId = 1;
            _nextConnectionId = 1;
            _nextRequestId = 1;
            _peakConnectionCount = 0;
            Contexts.Clear();
            Connections.Clear();
            PeerNetInfoRequests.Clear();
        }
    }

    [SysAbiExport(Nid = "3KOuC4RmZZU", ExportName = "sceNpSignalingInitialize", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingInitialize(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (_initialized)
            {
                return ctx.SetReturn(ErrorAlreadyInitialized);
            }

            _memorySize = unchecked((long)ctx[CpuRegister.Rdi]);
            if (_memorySize == 0)
            {
                _memorySize = DefaultMemorySize;
            }

            _initialized = true;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "NPhw0UXaNrk", ExportName = "sceNpSignalingTerminate", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingTerminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            ResetStateLocked();
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "5yYjEdd4t8Y", ExportName = "sceNpSignalingCreateContext", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingCreateContext(CpuContext ctx) => CreateContext(ctx, accountVariant: false);

    [SysAbiExport(Nid = "dDLNFdY8dws", ExportName = "sceNpSignalingCreateContextA", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingCreateContextA(CpuContext ctx) => CreateContext(ctx, accountVariant: true);

    [SysAbiExport(Nid = "hx+LIg-1koI", ExportName = "sceNpSignalingDeleteContext", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingDeleteContext(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            if (!Contexts.Remove(contextId))
            {
                return ctx.SetReturn(ErrorContextNotFound);
            }

            foreach (var connectionId in Connections.Where(pair => pair.Value.ContextId == contextId).Select(pair => pair.Key).ToArray())
            {
                Connections.Remove(connectionId);
            }

            foreach (var requestId in PeerNetInfoRequests.Where(pair => pair.Value == contextId).Select(pair => pair.Key).ToArray())
            {
                PeerNetInfoRequests.Remove(requestId);
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "0UvTFeomAUM", ExportName = "sceNpSignalingActivateConnection", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingActivateConnection(CpuContext ctx) => ActivateConnection(ctx, accountVariant: false);

    [SysAbiExport(Nid = "ZPLavCKqAB0", ExportName = "sceNpSignalingActivateConnectionA", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingActivateConnectionA(CpuContext ctx) => ActivateConnection(ctx, accountVariant: true);

    [SysAbiExport(Nid = "6UEembipgrM", ExportName = "sceNpSignalingDeactivateConnection", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingDeactivateConnection(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateConnectionLocked(unchecked((int)ctx[CpuRegister.Rdi]), unchecked((int)ctx[CpuRegister.Rsi]), out var connection);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            connection!.Active = false;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "b4qaXPzMJxo", ExportName = "sceNpSignalingTerminateConnection", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingTerminateConnection(CpuContext ctx)
    {
        lock (StateGate)
        {
            var connectionId = unchecked((int)ctx[CpuRegister.Rsi]);
            var validation = ValidateConnectionLocked(unchecked((int)ctx[CpuRegister.Rdi]), connectionId, out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            Connections.Remove(connectionId);
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "bD-JizUb3JM", ExportName = "sceNpSignalingGetConnectionStatus", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdx];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            if (!Contexts.ContainsKey(contextId))
            {
                return ctx.SetReturn(ErrorContextNotFound);
            }

            var connectionId = unchecked((int)ctx[CpuRegister.Rsi]);
            var status = Connections.TryGetValue(connectionId, out var connection) && connection.ContextId == contextId && connection.Active ? 2u : 0u;
            if (!ctx.TryWriteUInt32(statusAddress, status) ||
                (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rcx], 0)) ||
                (ctx[CpuRegister.R8] != 0 && !ctx.TryWriteUInt16(ctx[CpuRegister.R8], 0)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "AN3h0EBSX7A", ExportName = "sceNpSignalingGetConnectionInfo", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionInfo(CpuContext ctx) => GetConnectionInfo(ctx, accountVariant: false);

    [SysAbiExport(Nid = "rcylknsUDwg", ExportName = "sceNpSignalingGetConnectionInfoA", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionInfoA(CpuContext ctx) => GetConnectionInfo(ctx, accountVariant: true);

    [SysAbiExport(Nid = "GQ0hqmzj0F4", ExportName = "sceNpSignalingGetConnectionFromNpId", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionFromNpId(CpuContext ctx) => GetConnectionForContext(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "CkPxQjSm018", ExportName = "sceNpSignalingGetConnectionFromPeerAddress", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionFromPeerAddress(CpuContext ctx) => GetConnectionForContext(ctx, ctx[CpuRegister.Rcx], 1);

    [SysAbiExport(Nid = "B7cT9aVby7A", ExportName = "sceNpSignalingGetConnectionFromPeerAddressA", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionFromPeerAddressA(CpuContext ctx) => GetConnectionForContext(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rsi]);

    [SysAbiExport(Nid = "IHRDvZodPYY", ExportName = "sceNpSignalingSetContextOption", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingSetContextOption(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((int)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var optionId = unchecked((int)ctx[CpuRegister.Rsi]);
            var optionValue = unchecked((int)ctx[CpuRegister.Rdx]);
            if (optionId != 1 || (optionValue != 0 && optionValue != 1))
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            state!.OptionValue = optionValue;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "npU5V56id34", ExportName = "sceNpSignalingGetContextOption", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetContextOption(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdx];
        if (outAddress == 0 || unchecked((int)ctx[CpuRegister.Rsi]) != 1)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((int)ctx[CpuRegister.Rdi]), out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            return ctx.TryWriteInt32(outAddress, state!.OptionValue, checkNil: true)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "U8AQMlOFBc8", ExportName = "sceNpSignalingGetLocalNetInfo", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetLocalNetInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            var validation = ValidateContextLocked(unchecked((int)ctx[CpuRegister.Rdi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            return WriteNetInfo(ctx, infoAddress);
        }
    }

    [SysAbiExport(Nid = "zFgFHId7vAE", ExportName = "sceNpSignalingGetPeerNetInfo", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetPeerNetInfo(CpuContext ctx) => CreatePeerNetInfoRequest(ctx, accountVariant: false);

    [SysAbiExport(Nid = "Shr7bZq8QHY", ExportName = "sceNpSignalingGetPeerNetInfoA", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetPeerNetInfoA(CpuContext ctx) => CreatePeerNetInfoRequest(ctx, accountVariant: true);

    [SysAbiExport(Nid = "X1G4kkN2R-8", ExportName = "sceNpSignalingCancelPeerNetInfo", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingCancelPeerNetInfo(CpuContext ctx)
    {
        lock (StateGate)
        {
            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            var validation = ValidateContextLocked(contextId, out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var requestId = unchecked((int)ctx[CpuRegister.Rsi]);
            return ctx.SetReturn(PeerNetInfoRequests.Remove(requestId, out var owner) && owner == contextId ? 0 : ErrorRequestNotFound);
        }
    }

    [SysAbiExport(Nid = "2HajCEGgG4s", ExportName = "sceNpSignalingGetPeerNetInfoResult", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetPeerNetInfoResult(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            var validation = ValidateContextLocked(contextId, out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var requestId = unchecked((int)ctx[CpuRegister.Rsi]);
            if (!PeerNetInfoRequests.Remove(requestId, out var owner) || owner != contextId)
            {
                return ctx.SetReturn(ErrorResultNotFound);
            }

            return WriteNetInfo(ctx, infoAddress);
        }
    }

    [SysAbiExport(Nid = "tOpqyDyMje4", ExportName = "sceNpSignalingGetMemoryInfo", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetMemoryInfo(CpuContext ctx)
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
            BinaryPrimitives.WriteUInt64LittleEndian(info[0x10..], unchecked((ulong)_memorySize));
            return ctx.Memory.TryWrite(infoAddress, info)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(Nid = "C6ZNCDTj00Y", ExportName = "sceNpSignalingGetConnectionStatistics", LibraryName = "libSceNpSignaling", Target = Generation.Gen4 | Generation.Gen5)]
    public static int sceNpSignalingGetConnectionStatistics(CpuContext ctx)
    {
        var statsAddress = ctx[CpuRegister.Rdi];
        if (statsAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var active = (uint)Connections.Count(pair => pair.Value.Active);
            Span<byte> stats = stackalloc byte[0x10];
            stats.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(stats, _peakConnectionCount);
            BinaryPrimitives.WriteUInt32LittleEndian(stats[0x04..], active);
            BinaryPrimitives.WriteUInt32LittleEndian(stats[0x0C..], active);
            return ctx.Memory.TryWrite(statsAddress, stats)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int CreateContext(CpuContext ctx, bool accountVariant)
    {
        var owner = ctx[CpuRegister.Rdi];
        var outAddress = ctx[CpuRegister.Rcx];
        if ((!accountVariant && owner == 0) || (accountVariant && unchecked((int)owner) == -1) || outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            var contextId = _nextContextId++;
            Contexts[contextId] = new ContextState { AccountVariant = accountVariant };
            if (!ctx.TryWriteInt32(outAddress, contextId, checkNil: true))
            {
                Contexts.Remove(contextId);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    private static int ActivateConnection(CpuContext ctx, bool accountVariant)
    {
        var peerAddress = ctx[CpuRegister.Rsi];
        var outAddress = ctx[CpuRegister.Rdx];
        if (peerAddress == 0 || outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            var validation = ValidateContextLocked(contextId, out var context);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (context!.AccountVariant != accountVariant)
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            var connectionId = _nextConnectionId++;
            Connections[connectionId] = new ConnectionState { ContextId = contextId };
            _peakConnectionCount = Math.Max(_peakConnectionCount, (uint)Connections.Count);
            if (!ctx.TryWriteInt32(outAddress, connectionId, checkNil: true))
            {
                Connections.Remove(connectionId);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    private static int GetConnectionInfo(CpuContext ctx, bool accountVariant)
    {
        var outAddress = ctx[CpuRegister.Rcx];
        if (outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateConnectionLocked(unchecked((int)ctx[CpuRegister.Rdi]), unchecked((int)ctx[CpuRegister.Rsi]), out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var infoCode = unchecked((int)ctx[CpuRegister.Rdx]);
            var size = infoCode switch
            {
                1 or 2 or 6 => 4,
                3 => 36,
                4 or 5 => 8,
                7 when accountVariant => 16,
                _ => 0,
            };
            if (size == 0 || (accountVariant && infoCode == 3) || (!accountVariant && infoCode == 7))
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            return WriteZero(ctx, outAddress, size);
        }
    }

    private static int GetConnectionForContext(CpuContext ctx, ulong outAddress, ulong requiredInput)
    {
        if (outAddress == 0 || requiredInput == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            var validation = ValidateContextLocked(contextId, out _);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var connection = Connections.FirstOrDefault(pair => pair.Value.ContextId == contextId && pair.Value.Active);
            if (connection.Value is null)
            {
                return ctx.SetReturn(ErrorConnectionNotFound);
            }

            return ctx.TryWriteInt32(outAddress, connection.Key, checkNil: true)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int CreatePeerNetInfoRequest(CpuContext ctx, bool accountVariant)
    {
        var peerAddress = ctx[CpuRegister.Rsi];
        var outAddress = ctx[CpuRegister.Rdx];
        if (peerAddress == 0 || outAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
            var validation = ValidateContextLocked(contextId, out var context);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (context!.AccountVariant != accountVariant)
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }

            var requestId = _nextRequestId++;
            PeerNetInfoRequests[requestId] = contextId;
            if (!ctx.TryWriteUInt32(outAddress, unchecked((uint)requestId)))
            {
                PeerNetInfoRequests.Remove(requestId);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    private static int WriteNetInfo(CpuContext ctx, ulong infoAddress)
    {
        if (infoAddress == 0 || !ctx.TryReadUInt64(infoAddress, out var size) || size != 0x18)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        Span<byte> info = stackalloc byte[0x18];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, 0x18);
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteZero(CpuContext ctx, ulong address, int size)
    {
        Span<byte> bytes = size <= 64 ? stackalloc byte[size] : new byte[size];
        bytes.Clear();
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ValidateContextLocked(int contextId, out ContextState? state)
    {
        state = null;
        if (!_initialized)
        {
            return ErrorNotInitialized;
        }

        return Contexts.TryGetValue(contextId, out state) ? 0 : ErrorContextNotFound;
    }

    private static int ValidateConnectionLocked(int contextId, int connectionId, out ConnectionState? connection)
    {
        connection = null;
        var validation = ValidateContextLocked(contextId, out _);
        if (validation != 0)
        {
            return validation;
        }

        return Connections.TryGetValue(connectionId, out connection) && connection.ContextId == contextId
            ? 0
            : ErrorConnectionNotFound;
    }

    private static void ResetStateLocked()
    {
        _initialized = false;
        Contexts.Clear();
        Connections.Clear();
        PeerNetInfoRequests.Clear();
    }
}
