// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpManagerExports
{
    private const int NpTitleIdSize = 16;
    private const int NpTitleSecretSize = 128;
    private const uint NpReachabilityStateUnavailable = 0;
    private const ulong OfflineAccountId = 1;

    // OrbisNpState (shadPS4 src/core/libraries/np/np_manager.h:22): Unknown=0, SignedOut=1, SignedIn=2.
    private const uint NpStateSignedOut = 1;
    private const uint NpStateSignedIn = 2;

    // OrbisNpReachabilityState (shadPS4 src/core/libraries/np/np_manager.h:28):
    // Unavailable=0, Available=1, Reachable=2. Reachable is the terminal "PSN is
    // reachable" state; titles that gate on full online readiness poll for it, so
    // it cannot leave the state machine "less online" the way Available might.
    private const uint NpReachabilityStateReachable = 2;

    // Local user id handed out by sceUserServiceGetInitialUser (UserServiceExports.PrimaryUserId).
    private const int PrimaryUserId = 1000;

    // SHARPEMU_NP_FAKE_SIGNED_IN=1: present a coherent SIGNED_IN PSN/NP state so
    // titles whose boot gamemode gates on online-init (Astro Bot) can advance to
    // the main menu. It flips sceNpGetState to SIGNED_IN, sceNpGetNpReachabilityState
    // to Reachable, and - crucially - actually delivers the registered state
    // callback with SIGNED_IN instead of silently dropping it. Off by default:
    // every path below stays byte-identical to the offline behavior when unset.
    // Signal 4 (NpWebApi2 NOT_SIGNED_IN) keeps its own SHARPEMU_NP_FAKE_USERCTX flag.
    private static readonly bool _fakeSignedIn =
        Environment.GetEnvironmentVariable("SHARPEMU_NP_FAKE_SIGNED_IN") == "1";

    private enum NpStateCallbackKind
    {
        // SceNpStateCallback: (userId, state, SceNpId* npId, void* userdata).
        WithNpId,

        // SceNpStateCallbackA / ...ForToolkit: (userId, state, void* userdata).
        Simple,
    }

    private readonly record struct RegisteredStateCallback(ulong Address, ulong UserData, NpStateCallbackKind Kind);

    private static readonly object _stateCallbacksLock = new();
    private static readonly List<RegisteredStateCallback> _stateCallbacks = new();

    [SysAbiExport(
        Nid = "3Zl8BePTh9Y",
        ExportName = "sceNpCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "S7QTn72PrDw",
        ExportName = "sceNpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpDeleteRequest(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JELHf4xPufo",
        ExportName = "sceNpCheckCallbackForLib",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallbackForLib(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline profile: the online id payload is left untouched and the call
    // reports success, matching the other offline NpManager stubs here.
    [SysAbiExport(
        Nid = "XDncXQIJUSk",
        ExportName = "sceNpGetOnlineId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetOnlineId(CpuContext ctx)
    {
        // Gen5 ABI: user ID, then output structure.
        return WriteOfflineOnlineId(ctx, ctx[CpuRegister.Rsi]);
    }

    // sceNpGetAccountIdA(userId, SceNpAccountId*): the out parameter is a plain 64-bit account
    // id, not an OnlineId structure. A stable local-only id keeps titles that use it as a profile
    // key consistent with the signed-in state reported by sceNpGetState.
    [SysAbiExport(
        Nid = "rbknaUjpqWo",
        ExportName = "sceNpGetAccountIdA",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountIdA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var accountIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || accountIdAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(accountIdAddress, OfflineAccountId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // sceNpGetAccountCountryA(userId, SceNpCountryCode*): a two-letter ISO code plus terminator.
    [SysAbiExport(
        Nid = "JT+t00a3TxA",
        ExportName = "sceNpGetAccountCountryA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountryA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var countryAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || countryAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> country = stackalloc byte[4];
        "US"u8.CopyTo(country);
        return ctx.Memory.TryWrite(countryAddress, country)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // Offline profile: PSN is unreachable, and the out parameter must say so. Leaving the state
    // untouched lets titles read stale stack data and keep polling for connectivity.
    [SysAbiExport(
        Nid = "e-ZuhGEoeC4",
        ExportName = "sceNpGetNpReachabilityState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetNpReachabilityState(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var reachability = _fakeSignedIn ? NpReachabilityStateReachable : NpReachabilityStateUnavailable;
        TraceNp($"get_reachability_state user={userId} value={reachability}");
        return ctx.TryWriteUInt32(stateAddress, reachability)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "VfRSmPmj8Q8",
        ExportName = "sceNpRegisterStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallback(CpuContext ctx)
    {
        return RegisterStateCallback(ctx, NpStateCallbackKind.WithNpId);
    }

    [SysAbiExport(
        Nid = "qQJfO8HAiaY",
        ExportName = "sceNpRegisterStateCallbackA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallbackA(CpuContext ctx)
    {
        return RegisterStateCallback(ctx, NpStateCallbackKind.Simple);
    }

    [SysAbiExport(
        Nid = "0c7HbXRKUt4",
        ExportName = "sceNpRegisterStateCallbackForToolkit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManagerForToolkit")]
    public static int NpRegisterStateCallbackForToolkit(CpuContext ctx)
    {
        return RegisterStateCallback(ctx, NpStateCallbackKind.Simple);
    }

    // sceNpRegisterStateCallback*(callback, userdata): rdi is the guest callback
    // entry point, rsi the userdata. The offline default dropped both and returned
    // success, so the title's NP state machine never received a state event and
    // spun forever at online-init. Under _fakeSignedIn we record the registration
    // and immediately deliver a SIGNED_IN event, mirroring how the real NpManager
    // dispatches the current state to a freshly registered callback.
    private static int RegisterStateCallback(CpuContext ctx, NpStateCallbackKind kind)
    {
        var callbackAddress = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];

        if (_fakeSignedIn && callbackAddress != 0)
        {
            lock (_stateCallbacksLock)
            {
                _stateCallbacks.Add(new RegisteredStateCallback(callbackAddress, userData, kind));
            }

            FireStateCallback(ctx, callbackAddress, userData, kind);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Delivers a SIGNED_IN state event to a guest-registered callback via the same
    // scheduler path the AvPlayer texture allocator (AvPlayerExports.cs) and
    // pthread_once (KernelPthreadCompatExports.cs) use to re-enter guest code from
    // an HLE export thread: GuestThreadExecution.Scheduler.TryCallGuestFunction. It
    // runs on the guest thread that made the registration call, so re-entering guest
    // code here is exactly as safe as those precedents.
    //
    // SceNpStateCallback          : (userId, state, SceNpId* npId, void* userdata)
    // SceNpStateCallbackA         : (userId, state, void* userdata)
    // SceNpStateCallbackForToolkit: (userId, state, void* userdata)
    // The scheduler forwards at most three integer arguments (arg0/arg1/arg2 ->
    // rdi/rsi/rdx; rcx is forced to 0), so the npId variant is fired with a null
    // npId and cannot carry userdata; every variant still receives state=SIGNED_IN,
    // which is the signal the online-init gate polls for.
    private static void FireStateCallback(
        CpuContext ctx,
        ulong callbackAddress,
        ulong userData,
        NpStateCallbackKind kind)
    {
        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null)
        {
            return;
        }

        var userdataArg = kind == NpStateCallbackKind.Simple ? userData : 0;
        if (scheduler.TryCallGuestFunction(
                ctx,
                callbackAddress,
                (ulong)PrimaryUserId,
                NpStateSignedIn,
                userdataArg,
                0,
                0,
                "np_state_callback",
                out _,
                out var error))
        {
            TraceNp($"state_callback_fired cb=0x{callbackAddress:X16} state={NpStateSignedIn} kind={kind}");
        }
        else
        {
            TraceNp($"state_callback_fire_failed cb=0x{callbackAddress:X16}: {error}");
        }
    }

    [SysAbiExport(
        Nid = "eQH7nWPcAgc",
        ExportName = "sceNpGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _fakeSignedIn ? NpStateSignedIn : NpStateSignedOut;
        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, state);
        TraceNp($"get_state value={state}");
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ec63y59l9tw",
        ExportName = "sceNpSetNpTitleId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetNpTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        var titleSecretAddress = ctx[CpuRegister.Rsi];
        if (titleIdAddress == 0 || titleSecretAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> titleId = stackalloc byte[NpTitleIdSize];
        Span<byte> titleSecret = stackalloc byte[NpTitleSecretSize];
        if (!ctx.Memory.TryRead(titleIdAddress, titleId) ||
            !ctx.Memory.TryRead(titleSecretAddress, titleSecret))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"set_np_title_id title='{ReadTitleId(titleId)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static string ReadTitleId(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < 12 && length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int WriteOfflineOnlineId(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceNpOnlineId is a 16-byte handle plus four trailing bytes.
        Span<byte> onlineId = stackalloc byte[20];
        "Player"u8.CopyTo(onlineId);
        return ctx.Memory.TryWrite(address, onlineId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
