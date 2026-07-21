// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelSyncOnAddressCompatExports
{
    private sealed class AddressWaitState
    {
        public object Gate { get; } = new();
        public LinkedList<AddressWaiter> Waiters { get; } = new();
    }

    private sealed class AddressWaiter
    {
        public bool Released { get; set; }
        public LinkedListNode<AddressWaiter>? Node { get; set; }
    }

    private static readonly ConcurrentDictionary<ulong, AddressWaitState> _addressStates = new();

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var expectedValue = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var timeoutMicroseconds = ctx[CpuRegister.Rcx];

        if (address == 0 || !IsSupportedSize(size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _addressStates.GetOrAdd(address, static _ => new AddressWaitState());
        lock (state.Gate)
        {
            if (!TryReadValue(ctx, address, size, out var currentValue))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (currentValue != MaskExpectedValue(expectedValue, size))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
            }

            if (timeoutMicroseconds == 0)
            {
                return WaitWithoutTimeout(ctx, state);
            }

            return WaitWithTimeout(ctx, state, timeoutMicroseconds);
        }
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var requestedCount = ctx[CpuRegister.Rsi];
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (requestedCount == 0 || !_addressStates.TryGetValue(address, out var state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        lock (state.Gate)
        {
            var remaining = requestedCount;
            while (remaining != 0 && state.Waiters.First is { } node)
            {
                state.Waiters.RemoveFirst();
                node.Value.Node = null;
                node.Value.Released = true;
                remaining--;
            }

            Monitor.PulseAll(state.Gate);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int WaitWithoutTimeout(CpuContext ctx, AddressWaitState state)
    {
        var waiter = EnqueueWaiter(state);
        try
        {
            while (!waiter.Released && !GuestThreadBlocking.ShutdownRequested)
            {
                _ = Monitor.Wait(state.Gate, GuestThreadBlocking.WaitSliceMilliseconds);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
        finally
        {
            RemoveWaiter(state, waiter);
        }
    }

    private static int WaitWithTimeout(CpuContext ctx, AddressWaitState state, ulong timeoutMicroseconds)
    {
        var timeout = TimeSpan.FromTicks((long)Math.Min(timeoutMicroseconds, (ulong)(TimeSpan.MaxValue.Ticks / 10)) * 10L);
        var startedAt = Stopwatch.GetTimestamp();
        var waiter = EnqueueWaiter(state);
        try
        {
            while (!waiter.Released && !GuestThreadBlocking.ShutdownRequested)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                var waitMilliseconds = (int)Math.Min(
                    Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)),
                    GuestThreadBlocking.WaitSliceMilliseconds);
                _ = Monitor.Wait(state.Gate, waitMilliseconds);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
        finally
        {
            RemoveWaiter(state, waiter);
        }
    }

    private static AddressWaiter EnqueueWaiter(AddressWaitState state)
    {
        var waiter = new AddressWaiter();
        waiter.Node = state.Waiters.AddLast(waiter);
        return waiter;
    }

    private static void RemoveWaiter(AddressWaitState state, AddressWaiter waiter)
    {
        if (waiter.Node is { } node)
        {
            state.Waiters.Remove(node);
            waiter.Node = null;
        }
    }

    private static bool IsSupportedSize(ulong size) => size is 1 or 2 or 4 or 8;

    private static ulong MaskExpectedValue(ulong value, ulong size) => size switch
    {
        1 => value & byte.MaxValue,
        2 => value & ushort.MaxValue,
        4 => value & uint.MaxValue,
        _ => value,
    };

    private static bool TryReadValue(CpuContext ctx, ulong address, ulong size, out ulong value)
    {
        switch (size)
        {
            case 1:
                if (ctx.TryReadByte(address, out var byteValue))
                {
                    value = byteValue;
                    return true;
                }
                break;
            case 2:
                if (ctx.TryReadUInt16(address, out var uint16Value))
                {
                    value = uint16Value;
                    return true;
                }
                break;
            case 4:
                if (ctx.TryReadUInt32(address, out var uint32Value))
                {
                    value = uint32Value;
                    return true;
                }
                break;
            case 8:
                return ctx.TryReadUInt64(address, out value);
        }

        value = 0;
        return false;
    }

    internal static int GetWaiterCountForTests(ulong address)
    {
        if (!_addressStates.TryGetValue(address, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.Waiters.Count;
        }
    }

    internal static void ResetForTests()
    {
        _addressStates.Clear();
    }
}
