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

    private const int NpRequestIdOffset = 0x20000000;
    private const int NpRequestLimit = 32;
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);
    private const int NpErrorSignedOut = unchecked((int)0x80550006);
    private const int NpErrorCallbackAlreadyRegistered = unchecked((int)0x80550008);
    private const int NpErrorCallbackNotRegistered = unchecked((int)0x80550009);
    private const int NpErrorInvalidSize = unchecked((int)0x80550011);
    private const int NpErrorAborted = unchecked((int)0x80550012);
    private const int NpErrorRequestMax = unchecked((int)0x80550013);
    private const int NpErrorRequestNotFound = unchecked((int)0x80550014);
    private const int NpErrorInvalidId = unchecked((int)0x80550015);
    private const int NpErrorCallbackMax = unchecked((int)0x8055001D);

    private static int _nextNpRequestIndex;
    private static int _nextPresenceCallback;
    private static readonly Dictionary<int, NpRequestState> _npRequests = new();
    private static readonly Dictionary<int, (ulong Callback, ulong UserData)> _presenceCallbacks = new();
    private static readonly object _extendedStateLock = new();
    private static ulong _legacyPresenceCallback;
    private static ulong _reachabilityCallback;
    private static ulong _plusCallback;

    private enum NpRequestStatus
    {
        Ready,
        Aborted,
        Complete
    }

    private sealed class NpRequestState(bool asynchronous)
    {
        public bool Asynchronous { get; } = asynchronous;
        public NpRequestStatus Status { get; set; } = NpRequestStatus.Ready;
        public int Result { get; set; }
    }

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

    [SysAbiExport(Nid = "GpLQDNKICac", ExportName = "sceNpCreateRequest", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCreateRequest(CpuContext ctx) => CreateNpRequest(ctx, asynchronous: false);

    [SysAbiExport(Nid = "eiqMCt9UshI", ExportName = "sceNpCreateAsyncRequest", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCreateAsyncRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return size == 24 ? CreateNpRequest(ctx, asynchronous: true) : ctx.SetReturn(NpErrorInvalidSize);
    }

    [SysAbiExport(Nid = "OzKvTvg3ZYU", ExportName = "sceNpAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpAbortRequest(CpuContext ctx)
    {
        lock (_extendedStateLock)
        {
            if (!_npRequests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(NpErrorRequestNotFound);
            }
            if (request.Status != NpRequestStatus.Complete)
            {
                request.Status = NpRequestStatus.Aborted;
                request.Result = NpErrorAborted;
            }
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "uqcPJLWL08M", ExportName = "sceNpPollAsync", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpPollAsync(CpuContext ctx) => GetNpAsyncResult(ctx);
    [SysAbiExport(Nid = "jyi5p9XWUSs", ExportName = "sceNpWaitAsync", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpWaitAsync(CpuContext ctx) => GetNpAsyncResult(ctx);

    [SysAbiExport(Nid = "2rsFmlGWleQ", ExportName = "sceNpCheckNpAvailability", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCheckNpAvailability(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        return CompleteNpOfflineRequest(ctx);
    }
    [SysAbiExport(Nid = "8Z2Jc5GvGDI", ExportName = "sceNpCheckNpAvailabilityA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCheckNpAvailabilityA(CpuContext ctx) => unchecked((int)ctx[CpuRegister.Rsi]) == -1
        ? ctx.SetReturn(NpErrorInvalidArgument)
        : CompleteNpOfflineRequest(ctx);
    [SysAbiExport(Nid = "KfGZg2y73oM", ExportName = "sceNpCheckNpReachability", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCheckNpReachability(CpuContext ctx) => unchecked((int)ctx[CpuRegister.Rsi]) == -1
        ? ctx.SetReturn(NpErrorInvalidArgument)
        : CompleteNpOfflineRequest(ctx);

    [SysAbiExport(Nid = "r6MyYJkryz8", ExportName = "sceNpCheckPlus", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpCheckPlus(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];
        if (parameterAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (size != 56)
        {
            return ctx.SetReturn(NpErrorInvalidSize);
        }
        Span<byte> result = stackalloc byte[33];
        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return CompleteNpOfflineRequest(ctx);
    }

    [SysAbiExport(Nid = "Ghz9iWDUtC4", ExportName = "sceNpGetAccountCountry", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountry(CpuContext ctx) => WriteCountry(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);
    [SysAbiExport(Nid = "8VBTeRf1ZwI", ExportName = "sceNpGetAccountDateOfBirth", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountDateOfBirth(CpuContext ctx) => WriteDateOfBirth(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);
    [SysAbiExport(Nid = "q3M7XzBKC3s", ExportName = "sceNpGetAccountDateOfBirthA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountDateOfBirthA(CpuContext ctx) => WriteDateOfBirth(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdi]) != -1);
    [SysAbiExport(Nid = "a8R9-75u4iM", ExportName = "sceNpGetAccountId", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountId(CpuContext ctx) => WriteAccountId(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);

    [SysAbiExport(Nid = "KZ1Mj9yEGYc", ExportName = "sceNpGetAccountLanguage", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountLanguage(CpuContext ctx) => WriteLanguageAndComplete(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rsi] != 0);
    [SysAbiExport(Nid = "TPMbgIxvog0", ExportName = "sceNpGetAccountLanguageA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetAccountLanguageA(CpuContext ctx) => WriteLanguageAndComplete(ctx, ctx[CpuRegister.Rdx], unchecked((int)ctx[CpuRegister.Rsi]) != -1);

    [SysAbiExport(Nid = "IPb1hd1wAGc", ExportName = "sceNpGetGamePresenceStatus", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetGamePresenceStatus(CpuContext ctx) => WritePresenceStatus(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);
    [SysAbiExport(Nid = "oPO9U42YpgI", ExportName = "sceNpGetGamePresenceStatusA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetGamePresenceStatusA(CpuContext ctx) => WritePresenceStatus(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdi]) != -1);

    [SysAbiExport(Nid = "p-o74CnoNzY", ExportName = "sceNpGetNpId", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetNpId(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var npIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || npIdAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> npId = stackalloc byte[36];
        "Player"u8.CopyTo(npId);
        npId[28] = 1;
        return ctx.Memory.TryWrite(npIdAddress, npId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "ilwLM4zOmu4", ExportName = "sceNpGetParentalControlInfo", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetParentalControlInfo(CpuContext ctx) => WriteParentalInfoAndComplete(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], ctx[CpuRegister.Rsi] != 0);
    [SysAbiExport(Nid = "m9L3O6yst-U", ExportName = "sceNpGetParentalControlInfoA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetParentalControlInfoA(CpuContext ctx) => WriteParentalInfoAndComplete(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], unchecked((int)ctx[CpuRegister.Rsi]) != -1);

    [SysAbiExport(Nid = "VgYczPGB5ss", ExportName = "sceNpGetUserIdByAccountId", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetUserIdByAccountId(CpuContext ctx) => WriteUserId(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);
    [SysAbiExport(Nid = "F6E4ycq9Dbg", ExportName = "sceNpGetUserIdByOnlineId", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpGetUserIdByOnlineId(CpuContext ctx) => WriteUserId(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdi] != 0);

    [SysAbiExport(Nid = "Oad3rvY-NJQ", ExportName = "sceNpHasSignedUp", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpHasSignedUp(CpuContext ctx) => WriteBoolean(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdi]) != -1, value: true);
    [SysAbiExport(Nid = "Ybu6AxV6S0o", ExportName = "sceNpIsPlusMember", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpIsPlusMember(CpuContext ctx) => WriteBoolean(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdi]) != -1, value: false);

    [SysAbiExport(Nid = "A2CQ3kgSopQ", ExportName = "sceNpSetContentRestriction", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpSetContentRestriction(CpuContext ctx)
    {
        var restrictionAddress = ctx[CpuRegister.Rdi];
        if (restrictionAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        if (!ctx.TryReadUInt64(restrictionAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(size == 24 ? 0 : NpErrorInvalidSize);
    }

    [SysAbiExport(Nid = "KO+11cgC7N0", ExportName = "sceNpSetGamePresenceOnline", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpSetGamePresenceOnline(CpuContext ctx) => ValidateNpRequest(ctx, ctx[CpuRegister.Rsi] != 0);
    [SysAbiExport(Nid = "C0gNCiRIi4U", ExportName = "sceNpSetGamePresenceOnlineA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpSetGamePresenceOnlineA(CpuContext ctx) => ValidateNpRequest(ctx, unchecked((int)ctx[CpuRegister.Rsi]) != -1);

    [SysAbiExport(Nid = "-QglDeRr8D8", ExportName = "sceNpSetTimeout", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpSetTimeout(CpuContext ctx) => ValidateNpRequest(ctx, unchecked((int)ctx[CpuRegister.Rdi]) > 0);

    [SysAbiExport(Nid = "uFJpaKNBAj4", ExportName = "sceNpRegisterGamePresenceCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpRegisterGamePresenceCallback(CpuContext ctx)
    {
        lock (_extendedStateLock)
        {
            _legacyPresenceCallback = ctx[CpuRegister.Rdi];
        }
        return ctx.SetReturn(0);
    }
    [SysAbiExport(Nid = "KswxLxk4c1Y", ExportName = "sceNpRegisterGamePresenceCallbackA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpRegisterGamePresenceCallbackA(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        lock (_extendedStateLock)
        {
            if (_presenceCallbacks.Count >= 8)
            {
                return ctx.SetReturn(NpErrorCallbackMax);
            }
            var callbackId = ++_nextPresenceCallback;
            _presenceCallbacks[callbackId] = (ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
            return ctx.SetReturn(callbackId);
        }
    }
    [SysAbiExport(Nid = "aJZyCcHxzu4", ExportName = "sceNpUnregisterGamePresenceCallbackA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpUnregisterGamePresenceCallbackA(CpuContext ctx)
    {
        lock (_extendedStateLock)
        {
            return ctx.SetReturn(_presenceCallbacks.Remove(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : NpErrorCallbackNotRegistered);
        }
    }

    [SysAbiExport(Nid = "hw5KNqAAels", ExportName = "sceNpRegisterNpReachabilityStateCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpRegisterNpReachabilityStateCallback(CpuContext ctx) => RegisterSingletonCallback(ctx, ref _reachabilityCallback);
    [SysAbiExport(Nid = "cRILAEvn+9M", ExportName = "sceNpUnregisterNpReachabilityStateCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpUnregisterNpReachabilityStateCallback(CpuContext ctx) => UnregisterSingletonCallback(ctx, ref _reachabilityCallback);
    [SysAbiExport(Nid = "GImICnh+boA", ExportName = "sceNpRegisterPlusEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpRegisterPlusEventCallback(CpuContext ctx) => RegisterSingletonCallback(ctx, ref _plusCallback);
    [SysAbiExport(Nid = "xViqJdDgKl0", ExportName = "sceNpUnregisterPlusEventCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpUnregisterPlusEventCallback(CpuContext ctx) => UnregisterSingletonCallback(ctx, ref _plusCallback);

    [SysAbiExport(Nid = "mjjTXh+NHWY", ExportName = "sceNpUnregisterStateCallback", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpUnregisterStateCallback(CpuContext ctx)
    {
        lock (_stateCallbacksLock)
        {
            var index = _stateCallbacks.FindIndex(callback => callback.Kind == NpStateCallbackKind.WithNpId);
            if (index < 0)
            {
                return ctx.SetReturn(_fakeSignedIn ? NpErrorCallbackNotRegistered : 0);
            }
            _stateCallbacks.RemoveAt(index);
            return ctx.SetReturn(0);
        }
    }
    [SysAbiExport(Nid = "M3wFXbYQtAA", ExportName = "sceNpUnregisterStateCallbackA", Target = Generation.Gen5, LibraryName = "libSceNpManager")]
    public static int NpUnregisterStateCallbackA(CpuContext ctx)
    {
        lock (_stateCallbacksLock)
        {
            var index = _stateCallbacks.FindIndex(callback => callback.Kind == NpStateCallbackKind.Simple);
            if (index >= 0)
            {
                _stateCallbacks.RemoveAt(index);
            }
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "YIvqqvJyjEc", ExportName = "sceNpUnregisterStateCallbackForToolkit", Target = Generation.Gen5, LibraryName = "libSceNpManagerForToolkit")]
    public static int NpUnregisterStateCallbackForToolkit(CpuContext ctx)
    {
        lock (_stateCallbacksLock)
        {
            var index = _stateCallbacks.FindIndex(callback => callback.Kind == NpStateCallbackKind.Simple);
            if (index < 0)
            {
                return ctx.SetReturn(_fakeSignedIn ? NpErrorCallbackNotRegistered : 0);
            }

            _stateCallbacks.RemoveAt(index);
            return ctx.SetReturn(0);
        }
    }

    internal static void ResetExtendedStateForTests()
    {
        lock (_extendedStateLock)
        {
            _npRequests.Clear();
            _presenceCallbacks.Clear();
            _nextNpRequestIndex = 0;
            _nextPresenceCallback = 0;
            _legacyPresenceCallback = 0;
            _reachabilityCallback = 0;
            _plusCallback = 0;
        }
    }

    private static int CreateNpRequest(CpuContext ctx, bool asynchronous)
    {
        lock (_extendedStateLock)
        {
            if (_npRequests.Count >= NpRequestLimit)
            {
                return ctx.SetReturn(NpErrorRequestMax);
            }
            var requestId = NpRequestIdOffset + ++_nextNpRequestIndex;
            _npRequests[requestId] = new NpRequestState(asynchronous);
            return ctx.SetReturn(requestId);
        }
    }

    private static int CompleteNpOfflineRequest(CpuContext ctx)
    {
        lock (_extendedStateLock)
        {
            if (!_npRequests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(NpErrorRequestNotFound);
            }
            if (request.Status == NpRequestStatus.Aborted)
            {
                request.Result = NpErrorAborted;
                return ctx.SetReturn(NpErrorAborted);
            }
            if (request.Status == NpRequestStatus.Complete)
            {
                request.Result = NpErrorInvalidArgument;
                return ctx.SetReturn(NpErrorInvalidArgument);
            }
            request.Status = NpRequestStatus.Complete;
            request.Result = NpErrorSignedOut;
            return ctx.SetReturn(request.Asynchronous ? 0 : NpErrorSignedOut);
        }
    }

    private static int GetNpAsyncResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        lock (_extendedStateLock)
        {
            if (!_npRequests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
            {
                return ctx.SetReturn(NpErrorRequestNotFound);
            }
            if (!request.Asynchronous || request.Status == NpRequestStatus.Ready)
            {
                return ctx.SetReturn(NpErrorInvalidId);
            }
            return ctx.TryWriteInt32(resultAddress, request.Result)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    private static int ValidateNpRequest(CpuContext ctx, bool validArguments)
    {
        if (!validArguments)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        lock (_extendedStateLock)
        {
            return ctx.SetReturn(_npRequests.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : NpErrorRequestNotFound);
        }
    }

    private static int WriteCountry(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> country = stackalloc byte[4];
        "US"u8.CopyTo(country);
        return ctx.Memory.TryWrite(address, country)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteDateOfBirth(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> date = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(date, 2000);
        date[2] = 1;
        date[3] = 1;
        return ctx.Memory.TryWrite(address, date)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteAccountId(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        return ctx.TryWriteUInt64(address, OfflineAccountId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteLanguageAndComplete(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> language = stackalloc byte[16];
        "en-US"u8.CopyTo(language);
        if (!ctx.Memory.TryWrite(address, language))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return CompleteNpOfflineRequest(ctx);
    }

    private static int WritePresenceStatus(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        return ctx.TryWriteInt32(address, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteParentalInfoAndComplete(CpuContext ctx, ulong ageAddress, ulong infoAddress, bool validIdentity)
    {
        if (!validIdentity || ageAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> age = stackalloc byte[1];
        age[0] = 21;
        Span<byte> info = stackalloc byte[3];
        if (!ctx.Memory.TryWrite(ageAddress, age) || !ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return CompleteNpOfflineRequest(ctx);
    }

    private static int WriteUserId(CpuContext ctx, ulong address, bool validIdentity)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        return ctx.TryWriteInt32(address, PrimaryUserId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteBoolean(CpuContext ctx, ulong address, bool validIdentity, bool value)
    {
        if (!validIdentity || address == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        Span<byte> result = stackalloc byte[1];
        result[0] = value ? (byte)1 : (byte)0;
        return ctx.Memory.TryWrite(address, result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int RegisterSingletonCallback(CpuContext ctx, ref ulong callbackSlot)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(NpErrorInvalidArgument);
        }
        lock (_extendedStateLock)
        {
            if (callbackSlot != 0)
            {
                return ctx.SetReturn(NpErrorCallbackAlreadyRegistered);
            }
            callbackSlot = ctx[CpuRegister.Rdi];
            return ctx.SetReturn(0);
        }
    }

    private static int UnregisterSingletonCallback(CpuContext ctx, ref ulong callbackSlot)
    {
        lock (_extendedStateLock)
        {
            if (callbackSlot == 0)
            {
                return ctx.SetReturn(NpErrorCallbackNotRegistered);
            }
            callbackSlot = 0;
            return ctx.SetReturn(0);
        }
    }
}
