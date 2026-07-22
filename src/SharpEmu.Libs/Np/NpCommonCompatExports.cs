// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpCommonCompatExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80550003);
    private const int ErrorInvalidPlatformType = unchecked((int)0x80550004);
    private const int ErrorNotMatch = unchecked((int)0x80550609);
    private const int ErrorCalloutNotInitialized = unchecked((int)0x80558001);
    private const int ErrorCalloutAlreadyInitialized = unchecked((int)0x80558002);
    private const int ErrorCalloutDuplicateEntry = unchecked((int)0x80558006);
    private const int ErrorConditionTimedOut = unchecked((int)0x8055800B);
    private const int ErrorMutexBusy = unchecked((int)0x8055800F);
    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();
    private static long _nextThread;
    private static readonly ConcurrentDictionary<ulong, MutexState> Mutexes = new();
    private static readonly ConcurrentDictionary<ulong, byte> Conditions = new();
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, byte>> Callouts = new();
    private static readonly ConcurrentDictionary<ulong, byte> Threads = new();

    private sealed class MutexState(bool recursive)
    {
        public bool Recursive { get; } = recursive;
        public int LockCount { get; set; }
    }

    [SysAbiExport(Nid = "i8UmXTSq7N4", ExportName = "sceNpCmpNpId", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCmpNpId(CpuContext ctx)
    {
        var comparison = CompareNpIds(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out var valid);
        return ctx.SetReturn(!valid ? ErrorInvalidArgument : comparison == 0 ? 0 : ErrorNotMatch);
    }

    [SysAbiExport(Nid = "TcwEFnakiSc", ExportName = "sceNpCmpNpIdInOrder", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCmpNpIdInOrder(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdx];
        var comparison = CompareNpIds(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out var valid);
        if (!valid || outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        return ctx.TryWriteInt32(outputAddress, Math.Sign(comparison))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "dj+O5aD2a0Q", ExportName = "sceNpCmpOnlineId", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCmpOnlineId(CpuContext ctx)
    {
        var comparison = CompareBytes(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], 16, out var valid);
        return ctx.SetReturn(!valid ? ErrorInvalidArgument : comparison == 0 ? 0 : ErrorNotMatch);
    }

    [SysAbiExport(Nid = "hkeX9iuCwlI", ExportName = "sceNpIntIsValidOnlineId", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpIntIsValidOnlineId(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        Span<byte> onlineId = stackalloc byte[20];
        if (address == 0 || !ctx.Memory.TryRead(address, onlineId) || onlineId[16] != 0)
        {
            return ctx.SetReturn(0);
        }
        var length = onlineId[..16].IndexOf((byte)0);
        if (length < 0)
        {
            length = 16;
        }
        if (length is < 3 or > 16 || !IsAsciiAlphaNumeric(onlineId[0]))
        {
            return ctx.SetReturn(0);
        }
        for (var index = 1; index < length; index++)
        {
            var value = onlineId[index];
            if (!IsAsciiAlphaNumeric(value) && value != (byte)'_' && value != (byte)'-')
            {
                return ctx.SetReturn(0);
            }
        }
        return ctx.SetReturn(1);
    }

    [SysAbiExport(Nid = "sXVQUIGmk2U", ExportName = "sceNpGetPlatformType", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpGetPlatformType(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0 || !ctx.TryReadUInt32(address + 24, out var tag))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var result = tag switch
        {
            0 => 0,
            0x00337370 => 1,
            0x32707370 => 2,
            0x00347370 => 3,
            _ => ErrorInvalidPlatformType,
        };
        return ctx.SetReturn(result);
    }

    [SysAbiExport(Nid = "PVVsRmMkO1g", ExportName = "sceNpGetSystemClockUsec", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpGetSystemClockUsec(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var elapsed = Stopwatch.GetTimestamp() - ClockOrigin;
        var microseconds = (elapsed / Stopwatch.Frequency) * 1_000_000L +
                           (elapsed % Stopwatch.Frequency) * 1_000_000L / Stopwatch.Frequency;
        return ctx.TryWriteInt64(outputAddress, microseconds)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "uEwag-0YZPc", ExportName = "sceNpMutexInit", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpMutexInit(CpuContext ctx) => MutexInit(ctx);
    [SysAbiExport(Nid = "lQ11BpMM4LU", ExportName = "sceNpMutexDestroy", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpMutexDestroy(CpuContext ctx) => MutexDestroy(ctx);
    [SysAbiExport(Nid = "r9Bet+s6fKc", ExportName = "sceNpMutexLock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpMutexLock(CpuContext ctx) => MutexLock(ctx);
    [SysAbiExport(Nid = "DuslmoqQ+nk", ExportName = "sceNpMutexTryLock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpMutexTryLock(CpuContext ctx) => MutexLock(ctx);
    [SysAbiExport(Nid = "oZyb9ktuCpA", ExportName = "sceNpMutexUnlock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpMutexUnlock(CpuContext ctx) => MutexUnlock(ctx);

    [SysAbiExport(Nid = "1CiXI-MyEKs", ExportName = "sceNpLwMutexInit", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpLwMutexInit(CpuContext ctx) => MutexInit(ctx);
    [SysAbiExport(Nid = "4zxevggtYrQ", ExportName = "sceNpLwMutexDestroy", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpLwMutexDestroy(CpuContext ctx) => MutexDestroy(ctx);
    [SysAbiExport(Nid = "18j+qk6dRwk", ExportName = "sceNpLwMutexLock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpLwMutexLock(CpuContext ctx) => MutexLock(ctx);
    [SysAbiExport(Nid = "hp0kVgu5Fxw", ExportName = "sceNpLwMutexTryLock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpLwMutexTryLock(CpuContext ctx) => MutexLock(ctx);
    [SysAbiExport(Nid = "CQG2oyx1-nM", ExportName = "sceNpLwMutexUnlock", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpLwMutexUnlock(CpuContext ctx) => MutexUnlock(ctx);

    [SysAbiExport(Nid = "q2tsVO3lM4A", ExportName = "sceNpCondInit", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCondInit(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        Conditions[address] = 0;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "1a+iY5YUJcI", ExportName = "sceNpCondDestroy", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCondDestroy(CpuContext ctx) =>
        ctx.SetReturn(Conditions.TryRemove(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorInvalidArgument);

    [SysAbiExport(Nid = "uMJFOA62mVU", ExportName = "sceNpCondSignal", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCondSignal(CpuContext ctx) =>
        ctx.SetReturn(Conditions.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : ErrorInvalidArgument);

    [SysAbiExport(Nid = "ss2xO9IJxKQ", ExportName = "sceNpCondTimedwait", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCondTimedwait(CpuContext ctx) =>
        ctx.SetReturn(Conditions.ContainsKey(ctx[CpuRegister.Rdi]) && Mutexes.ContainsKey(ctx[CpuRegister.Rsi])
            ? ErrorConditionTimedOut
            : ErrorInvalidArgument);

    [SysAbiExport(Nid = "fhJ5uKzcn0w", ExportName = "sceNpCreateThread", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCreateThread(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        if (threadAddress == 0 || ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var thread = unchecked((ulong)Interlocked.Increment(ref _nextThread));
        Threads[thread] = 0;
        if (!ctx.TryWriteUInt64(threadAddress, thread))
        {
            Threads.TryRemove(thread, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "EjMsfO3GCIA", ExportName = "sceNpJoinThread", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpJoinThread(CpuContext ctx)
    {
        var thread = ctx[CpuRegister.Rdi];
        if (!Threads.TryRemove(thread, out _))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        var resultAddress = ctx[CpuRegister.Rsi];
        return resultAddress == 0 || ctx.TryWriteUInt64(resultAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "9+m5nRdJ-wQ", ExportName = "sceNpCalloutInitCtx", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCalloutInitCtx(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!Callouts.TryAdd(address, new ConcurrentDictionary<ulong, byte>()))
        {
            return ctx.SetReturn(ErrorCalloutAlreadyInitialized);
        }
        Span<byte> state = stackalloc byte[0x28];
        state.Clear();
        state[0x18] = 1;
        if (!ctx.Memory.TryWrite(address, state))
        {
            Callouts.TryRemove(address, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "fClnlkZmA6k", ExportName = "sceNpCalloutStartOnCtx", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCalloutStartOnCtx(CpuContext ctx) => CalloutStart(ctx);
    [SysAbiExport(Nid = "lpr66Gby8dQ", ExportName = "sceNpCalloutStartOnCtx64", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCalloutStartOnCtx64(CpuContext ctx) => CalloutStart(ctx);

    [SysAbiExport(Nid = "in19gH7G040", ExportName = "sceNpCalloutStopOnCtx", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCalloutStopOnCtx(CpuContext ctx)
    {
        if (!Callouts.TryGetValue(ctx[CpuRegister.Rdi], out var entries))
        {
            return ctx.SetReturn(ErrorCalloutNotInitialized);
        }
        var removed = entries.TryRemove(ctx[CpuRegister.Rsi], out _) ? 1U : 0U;
        var removedAddress = ctx[CpuRegister.Rdx];
        return removedAddress == 0 || ctx.TryWriteUInt32(removedAddress, removed)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "AqJ4xkWsV+I", ExportName = "sceNpCalloutTermCtx", Target = Generation.Gen5, LibraryName = "libSceNpCommonCompat")]
    public static int NpCalloutTermCtx(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        Callouts.TryRemove(address, out _);
        Span<byte> state = stackalloc byte[0x28];
        state.Clear();
        return address == 0 || ctx.Memory.TryWrite(address, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int MutexInit(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        Mutexes[address] = new MutexState((ctx[CpuRegister.Rdx] & 1) != 0);
        return ctx.SetReturn(0);
    }

    private static int MutexDestroy(CpuContext ctx) =>
        ctx.SetReturn(Mutexes.TryRemove(ctx[CpuRegister.Rdi], out _) ? 0 : ErrorInvalidArgument);

    private static int MutexLock(CpuContext ctx)
    {
        if (!Mutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        lock (state)
        {
            if (state.LockCount != 0 && !state.Recursive)
            {
                return ctx.SetReturn(ErrorMutexBusy);
            }
            state.LockCount++;
        }
        return ctx.SetReturn(0);
    }

    private static int MutexUnlock(CpuContext ctx)
    {
        if (!Mutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        lock (state)
        {
            if (state.LockCount == 0)
            {
                return ctx.SetReturn(ErrorInvalidArgument);
            }
            state.LockCount--;
        }
        return ctx.SetReturn(0);
    }

    private static int CalloutStart(CpuContext ctx)
    {
        if (!Callouts.TryGetValue(ctx[CpuRegister.Rdi], out var entries))
        {
            return ctx.SetReturn(ErrorCalloutNotInitialized);
        }
        var entryAddress = ctx[CpuRegister.Rsi];
        if (entryAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }
        if (!entries.TryAdd(entryAddress, 0))
        {
            return ctx.SetReturn(ErrorCalloutDuplicateEntry);
        }
        Span<byte> entry = stackalloc byte[0x20];
        entry.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], ctx[CpuRegister.Rcx]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], ctx[CpuRegister.R8]);
        var elapsed = Stopwatch.GetTimestamp() - ClockOrigin;
        var now = (elapsed / Stopwatch.Frequency) * 1_000_000L +
                  (elapsed % Stopwatch.Frequency) * 1_000_000L / Stopwatch.Frequency;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(entry[24..], now + unchecked((long)ctx[CpuRegister.Rdx]));
        return ctx.Memory.TryWrite(entryAddress, entry)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int CompareBytes(CpuContext ctx, ulong firstAddress, ulong secondAddress, int count, out bool valid)
    {
        valid = false;
        if (firstAddress == 0 || secondAddress == 0)
        {
            return 0;
        }
        Span<byte> first = stackalloc byte[count];
        Span<byte> second = stackalloc byte[count];
        if (!ctx.Memory.TryRead(firstAddress, first) || !ctx.Memory.TryRead(secondAddress, second))
        {
            return 0;
        }
        valid = true;
        return first.SequenceCompareTo(second);
    }

    private static int CompareNpIds(CpuContext ctx, ulong firstAddress, ulong secondAddress, out bool valid)
    {
        var comparison = CompareBytes(ctx, firstAddress, secondAddress, 16, out valid);
        if (!valid || comparison != 0)
        {
            return comparison;
        }
        return CompareBytes(ctx, firstAddress + 20, secondAddress + 20, 16, out valid);
    }

    private static bool IsAsciiAlphaNumeric(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _nextThread, 0);
        Mutexes.Clear();
        Conditions.Clear();
        Callouts.Clear();
        Threads.Clear();
    }
}
