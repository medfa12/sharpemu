// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// POSIX unnamed semaphores (sem_init/sem_wait/sem_post/sem_destroy and the
/// try/timed/getvalue variants). The guest passes the address of its own sem_t
/// storage; we key state off that address rather than interpreting the opaque
/// sem_t layout.
///
/// A blocking sem_wait parks the calling guest thread through the guest
/// scheduler (the same continuation path sceKernelWaitSema uses) so the thread
/// resumes cleanly through the import trampoline. Only a host-owned thread that
/// cannot park falls back to blocking the host thread directly on the gate --
/// blocking a schedulable guest thread inside the import handler would leave it
/// parked on a foreign stack and corrupt its resume.
/// </summary>
public static class KernelPosixSemExports
{
    private const int SemValueMax = int.MaxValue;

    private sealed class PosixSemaphoreState
    {
        public int Count;
        public int WaitingThreads;
        public object Gate { get; } = new();
    }

    private sealed class PosixSemaphoreWaiter
    {
        // Written and read only under the owning semaphore's Gate.
        public int? Result { get; set; }
    }

    private static readonly ConcurrentDictionary<ulong, PosixSemaphoreState> _sems = new();

    private static PosixSemaphoreState Resolve(ulong sem, int initialIfMissing)
        => _sems.GetOrAdd(sem, _ => new PosixSemaphoreState
        {
            Count = Math.Clamp(initialIfMissing, 0, SemValueMax),
        });

    private static string GetWakeKey(ulong sem) => $"posix_sem:0x{sem:X}";

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] posix_sem.{message}");
        }
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemInit(CpuContext ctx)
    {
        // (sem_t* sem, int pshared, unsigned value)
        var sem = ctx[CpuRegister.Rdi];
        var value = unchecked((int)(uint)ctx[CpuRegister.Rdx]);
        if (sem == 0)
        {
            return ctx.SetReturn(-1);
        }

        var fresh = new PosixSemaphoreState { Count = Math.Clamp(value, 0, SemValueMax) };
        _sems[sem] = fresh;
        Trace($"init sem=0x{sem:X} value={value}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemWait(CpuContext ctx)
    {
        var sem = ctx[CpuRegister.Rdi];
        if (sem == 0)
        {
            return ctx.SetReturn(-1);
        }

        return Wait(ctx, sem);
    }

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemTrywait(CpuContext ctx)
    {
        var sem = ctx[CpuRegister.Rdi];
        if (sem == 0)
        {
            return ctx.SetReturn(-1);
        }

        var state = Resolve(sem, 0);
        lock (state.Gate)
        {
            if (state.Count >= 1)
            {
                state.Count--;
                return ctx.SetReturn(0);
            }
        }

        // Not available -> -1 (guest maps to EAGAIN); the sem stays untouched.
        return ctx.SetReturn(-1);
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemTimedwait(CpuContext ctx)
    {
        // (sem_t* sem, const timespec* abstime). We don't honor the absolute
        // deadline precisely; block until available, which is safe for callers
        // that use the timeout only as a liveness backstop.
        var sem = ctx[CpuRegister.Rdi];
        if (sem == 0)
        {
            return ctx.SetReturn(-1);
        }

        return Wait(ctx, sem);
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemPost(CpuContext ctx)
    {
        var sem = ctx[CpuRegister.Rdi];
        if (sem == 0)
        {
            return ctx.SetReturn(-1);
        }

        var state = Resolve(sem, 0);
        lock (state.Gate)
        {
            if (state.Count < SemValueMax)
            {
                state.Count++;
            }

            // Wake any host-owned waiter parked on the gate; guest-scheduler
            // waiters are woken separately below. PulseAll under the gate is
            // lock-order safe (it touches no other lock).
            Monitor.PulseAll(state.Gate);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetWakeKey(sem));
        Trace($"post sem=0x{sem:X} count={state.Count}");
        if (GuestSyncTrace.Enabled)
        {
            GuestSyncTrace.Log($"sem.post {KernelPthreadState.CurrentSyncThreadTag()} prim=0x{sem:X} count={state.Count} waiters={state.WaitingThreads} -> ok");
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemGetvalue(CpuContext ctx)
    {
        // (sem_t* sem, int* sval)
        var sem = ctx[CpuRegister.Rdi];
        var outValue = ctx[CpuRegister.Rsi];
        if (sem == 0 || outValue == 0)
        {
            return ctx.SetReturn(-1);
        }

        var count = _sems.TryGetValue(sem, out var state) ? Volatile.Read(ref state.Count) : 0;
        return ctx.TryWriteInt32(outValue, count)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(-1);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SemDestroy(CpuContext ctx)
    {
        var sem = ctx[CpuRegister.Rdi];
        if (sem != 0 && _sems.TryRemove(sem, out var state))
        {
            // Release any host-owned waiter still parked on the gate.
            lock (state.Gate)
            {
                Monitor.PulseAll(state.Gate);
            }

            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetWakeKey(sem));
        }

        Trace($"destroy sem=0x{sem:X}");
        return ctx.SetReturn(0);
    }

    private static int Wait(CpuContext ctx, ulong sem)
    {
        var state = Resolve(sem, 0);
        lock (state.Gate)
        {
            if (state.Count >= 1)
            {
                state.Count--;
                if (GuestSyncTrace.Enabled)
                {
                    GuestSyncTrace.Log($"sem.wait {KernelPthreadState.CurrentSyncThreadTag()} prim=0x{sem:X} count={state.Count} -> ok");
                }
                return ctx.SetReturn(0);
            }

            var waiter = new PosixSemaphoreWaiter();
            if (!GuestThreadExecution.RequestCurrentThreadBlock(
                    ctx,
                    "sem_wait",
                    GetWakeKey(sem),
                    resumeHandler: () => CompleteBlockedWait(state, waiter),
                    wakeHandler: () => TryConsumeBlockedWait(state, waiter)))
            {
                // Host-owned thread: it cannot park in the guest scheduler, so block
                // the host thread on the gate until a sem_post pulses it.
                return HostBlockingWait(ctx, state);
            }

            state.WaitingThreads++;
            Trace($"wait-block sem=0x{sem:X} count={state.Count} waiters={state.WaitingThreads}");
            if (GuestSyncTrace.Enabled)
            {
                GuestSyncTrace.Log($"sem.wait_block {KernelPthreadState.CurrentSyncThreadTag()} prim=0x{sem:X} count={state.Count} waiters={state.WaitingThreads} -> parked");
            }
            return ctx.SetReturn(0);
        }
    }

    // Wake handler: runs under the scheduler's guest-thread gate (lock order:
    // scheduler gate -> semaphore gate). Returns true iff the waiter has a final
    // result and should be re-readied; false leaves it parked.
    private static bool TryConsumeBlockedWait(PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        lock (state.Gate)
        {
            return TryConsumeBlockedWaitLocked(state, waiter);
        }
    }

    private static bool TryConsumeBlockedWaitLocked(PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        if (waiter.Result is not null)
        {
            return true;
        }

        if (state.Count >= 1)
        {
            state.Count--;
            waiter.Result = 0;
            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            if (GuestSyncTrace.Enabled)
            {
                GuestSyncTrace.Log($"sem.wait_wake count={state.Count} waiters={state.WaitingThreads} -> ok");
            }
            return true;
        }

        return false;
    }

    // Resume handler: runs on the woken guest thread outside the scheduler gate;
    // its return value becomes the guest's RAX for the resumed sem_wait.
    private static int CompleteBlockedWait(PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        lock (state.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedWaitLocked(state, waiter))
            {
                // Nothing readies a parked waiter without the wake handler resolving
                // it, so reaching here means the scheduler contract changed. Report
                // success anyway (the destroy path wakes waiters unconditionally).
                waiter.Result = 0;
                state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            }

            return waiter.Result!.Value;
        }
    }

    // Blocks the calling host thread on the gate until the count is available.
    // Must be called while already holding <paramref name="state"/>.Gate:
    // Monitor.Wait atomically releases the gate while parked and reacquires it on
    // wake, so a concurrent sem_post cannot slip a PulseAll between the check and
    // the wait. Used only for host-owned threads that cannot park.
    private static int HostBlockingWait(CpuContext ctx, PosixSemaphoreState state)
    {
        state.WaitingThreads++;
        if (GuestSyncTrace.Enabled)
        {
            GuestSyncTrace.Log($"sem.wait_block {KernelPthreadState.CurrentSyncThreadTag()} count={state.Count} waiters={state.WaitingThreads} host=1 -> parked");
        }
        try
        {
            while (state.Count < 1)
            {
                Monitor.Wait(state.Gate);
            }

            state.Count--;
            if (GuestSyncTrace.Enabled)
            {
                GuestSyncTrace.Log($"sem.wait_wake {KernelPthreadState.CurrentSyncThreadTag()} count={state.Count} host=1 -> ok");
            }
            return ctx.SetReturn(0);
        }
        finally
        {
            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
        }
    }

    internal static void ResetForTests()
    {
        _sems.Clear();
    }
}
