// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Counting-condvar semantics of <c>PthreadCondWaitCore</c>/<c>PthreadCondSignalCore</c>,
/// driven through the real guest exports (scePthreadCondWait/Signal/Broadcast) on
/// live host threads -- the same seam production uses. Waiters park in place on the
/// condvar's SyncRoot (host Monitor.Wait), so a plain test thread can register and
/// wake without the guest scheduler.
///
/// Focus: the signal-epoch guard added in the lost-wakeup fix. A counting condvar
/// alone (SignalsPending) lets a thread that signals and immediately re-enters
/// cond_wait steal the SignalsPending token that PulseAll already delivered to an
/// older, still-waking waiter; both then park forever. The fix snapshots the
/// SignalEpoch at registration and only consumes a token when SignalEpoch advanced
/// past that snapshot, binding each wake to a waiter that was present at signal time.
///
/// The condvar/mutex state objects and their SyncRoot/SignalEpoch/SignalsPending/
/// Waiters fields are private nested types, so the state machine is inspected and
/// pre-seeded via reflection. Every consume decision under test runs the real core.
/// </summary>
public sealed class KernelPthreadCondvarTests
{
    private static readonly Type ExportsType = typeof(KernelPthreadCompatExports);

    private static readonly Type CondStateType =
        ExportsType.GetNestedType("PthreadCondState", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PthreadCondState not found");

    private static readonly Type MutexStateType =
        ExportsType.GetNestedType("PthreadMutexState", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PthreadMutexState not found");

    private static readonly IDictionary CondStates =
        (IDictionary)(ExportsType.GetField("_condStates", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("_condStates not found")).GetValue(null)!;

    private static readonly IDictionary MutexStates =
        (IDictionary)(ExportsType.GetField("_mutexStates", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("_mutexStates not found")).GetValue(null)!;

    private static readonly PropertyInfo WaitersProp = CondStateType.GetProperty("Waiters")!;
    private static readonly PropertyInfo EpochProp = CondStateType.GetProperty("SignalEpoch")!;
    private static readonly PropertyInfo PendingProp = CondStateType.GetProperty("SignalsPending")!;
    private static readonly PropertyInfo SyncRootProp = CondStateType.GetProperty("SyncRoot")!;

    private const int Ok = (int)OrbisGen2Result.ORBIS_GEN2_OK;

    // Bounded so a lost-wakeup regression trips fast instead of hanging the suite.
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(2);

    // Distinct address blocks per test so the static state dictionaries never bleed
    // across cases (cond word, then one mutex word per participating waiter thread).
    private static ulong _nextBlock = 0x8000_0000;

    private static (ulong Cond, ulong[] Mutexes) AllocAddresses(int waiterCount)
    {
        var baseAddr = Interlocked.Add(ref _nextBlock, 0x1_0000);
        var mutexes = new ulong[waiterCount];
        for (var i = 0; i < waiterCount; i++)
        {
            mutexes[i] = baseAddr + 0x100 + ((ulong)i * 0x100);
        }

        return (baseAddr, mutexes);
    }

    // ---- reflection helpers over the private condvar/mutex state machine ----

    private static object CreateCond(ulong address)
    {
        var state = Activator.CreateInstance(CondStateType, nonPublic: true)!;
        CondStates[address] = state;
        return state;
    }

    private static void CreateMutex(ulong address)
    {
        // Fresh, unowned mutex: cond_wait adopts it, unlocks, re-locks on return.
        MutexStates[address] = Activator.CreateInstance(MutexStateType, nonPublic: true)!;
    }

    private static int Waiters(object cond) => (int)WaitersProp.GetValue(cond)!;

    private static ulong Epoch(object cond) => (ulong)EpochProp.GetValue(cond)!;

    private static int Pending(object cond) => (int)PendingProp.GetValue(cond)!;

    private static void SetEpoch(object cond, ulong value) => EpochProp.SetValue(cond, value);

    private static void SetPending(object cond, int value) => PendingProp.SetValue(cond, value);

    private static object SyncRoot(object cond) => SyncRootProp.GetValue(cond)!;

    // ---- real guest-export invocations ----

    private static int CondWait(ulong cond, ulong mutex)
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = cond;
        ctx[CpuRegister.Rsi] = mutex;
        return KernelPthreadCompatExports.PosixPthreadCondWait(ctx);
    }

    private static int CondSignal(ulong cond)
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = cond;
        return KernelPthreadCompatExports.PosixPthreadCondSignal(ctx);
    }

    private static int CondBroadcast(ulong cond)
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = cond;
        return KernelPthreadCompatExports.PosixPthreadCondBroadcast(ctx);
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            Thread.Sleep(1);
        }
    }

    private static void WaitUntilBlocked(Thread thread, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for thread to block: {what}");
            }

            Thread.Sleep(1);
        }
    }

    private sealed class Waiter
    {
        public Thread Thread = null!;
        public int Result = int.MinValue;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private static Waiter StartWaiter(ulong cond, ulong mutex, string name)
    {
        var waiter = new Waiter();
        waiter.Thread = new Thread(() =>
        {
            waiter.Result = CondWait(cond, mutex);
            waiter.Done.Set();
        })
        {
            IsBackground = true,
            Name = name,
        };
        waiter.Thread.Start();
        return waiter;
    }

    /// <summary>
    /// The steal interleaving: waiter W1 registers at epoch E; a signal advances
    /// the epoch (E+1) and leaves one pending token; then a *newcomer* W2 registers
    /// at epoch E+1 on the same condvar. The token belongs to W1 (present at signal
    /// time). W2 must not consume it.
    ///
    /// The pending token is planted directly on the state (epoch++, pending=1)
    /// WITHOUT pulsing, so W1 stays in its timed wait slice and does not contend for
    /// SyncRoot. W2 is then released into an uncontested critical section, guaranteeing
    /// it evaluates the consume predicate before W1 re-checks -- exactly the ordering
    /// that steals the token when the epoch guard is absent. This mutates only the two
    /// fields the real signal core would set for a single waiter; the consume decision
    /// under test is 100% the production wait loop.
    ///
    /// Revert the guard (predicate "SignalsPending == 0", consume "if SignalsPending > 0")
    /// and W2 consumes the token synchronously on entry (pending -> 0); W1 then re-checks,
    /// finds nothing pending, and parks forever -> the w1.Join below times out.
    /// </summary>
    [Fact]
    public void ReenteringWaiterDoesNotStealAnOlderWaitersWake()
    {
        // Repeat: the designed ordering is the common case, but looping removes any
        // chance a regression slips through on a single unlucky schedule.
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var (condAddr, mutexes) = AllocAddresses(2);
            var cond = CreateCond(condAddr);
            CreateMutex(mutexes[0]);
            CreateMutex(mutexes[1]);

            // W1 registers at epoch 0 and parks.
            var w1 = StartWaiter(condAddr, mutexes[0], $"W1-{iteration}");
            WaitUntil(() => Waiters(cond) == 1, RegisterTimeout, "W1 to register");
            Assert.Equal(0UL, Epoch(cond));

            var syncRoot = SyncRoot(cond);
            Waiter w2;

            Monitor.Enter(syncRoot);
            try
            {
                // Model a signal delivered to W1 (the sole waiter present): epoch
                // advances past W1's snapshot and one token becomes pending. No pulse,
                // so W1 keeps sleeping its wait slice and will not race W2 for the lock.
                SetEpoch(cond, Epoch(cond) + 1);
                SetPending(cond, 1);

                // W2 -- the re-entering signaler -- registers at the new epoch. It
                // blocks entering the critical section we hold.
                w2 = StartWaiter(condAddr, mutexes[1], $"W2-{iteration}");
                WaitUntilBlocked(w2.Thread, RegisterTimeout, "W2 to reach SyncRoot");
            }
            finally
            {
                // W2 now acquires uncontested and runs its predicate before W1 wakes.
                Monitor.Exit(syncRoot);
            }

            // W1 owns the token: it must wake and return OK. With the guard reverted
            // W2 would have consumed it, leaving W1 parked forever (this Join times out).
            Assert.True(
                w1.Thread.Join(WakeTimeout),
                "W1 deadlocked: its wake token was stolen (signal-epoch guard missing)");
            Assert.Equal(Ok, w1.Result);

            // W2 arrived after the signal: it must NOT have consumed W1's token.
            Assert.False(w2.Done.IsSet, "W2 stole the wake token meant for W1");

            // A genuine second signal (epoch advances past W2's snapshot) releases W2
            // cleanly -- no residual deadlock.
            Assert.Equal(Ok, CondSignal(condAddr));
            Assert.True(w2.Thread.Join(WakeTimeout), "W2 deadlocked after its own signal");
            Assert.Equal(Ok, w2.Result);
            Assert.Equal(0, Pending(cond));
        }
    }

    [Fact]
    public void WaiterThenSignalWakesTheWaiter()
    {
        var (condAddr, mutexes) = AllocAddresses(1);
        var cond = CreateCond(condAddr);
        CreateMutex(mutexes[0]);

        var w = StartWaiter(condAddr, mutexes[0], "waiter");
        WaitUntil(() => Waiters(cond) == 1, RegisterTimeout, "waiter to register");
        Assert.False(w.Done.IsSet);

        Assert.Equal(Ok, CondSignal(condAddr));
        Assert.True(w.Thread.Join(WakeTimeout), "waiter never woke after signal");
        Assert.Equal(Ok, w.Result);
        Assert.Equal(0, Pending(cond));
    }

    [Fact]
    public void SignalBeforeAnyWaiterIsANoOpAndLeavesNoPhantomWake()
    {
        var (condAddr, mutexes) = AllocAddresses(1);
        var cond = CreateCond(condAddr);
        CreateMutex(mutexes[0]);

        // POSIX: signaling an empty condvar is a no-op. The epoch still advances, but
        // no token may accumulate (else a later waiter would observe a phantom wake).
        Assert.Equal(Ok, CondSignal(condAddr));
        Assert.Equal(1UL, Epoch(cond));
        Assert.Equal(0, Pending(cond));

        // A waiter arriving afterwards must block: the earlier signal is gone.
        var w = StartWaiter(condAddr, mutexes[0], "late-waiter");
        WaitUntil(() => Waiters(cond) == 1, RegisterTimeout, "late waiter to register");
        Assert.False(
            w.Done.Wait(TimeSpan.FromMilliseconds(300)),
            "waiter woke on a phantom token from a pre-arrival signal");

        // A fresh signal wakes it.
        Assert.Equal(Ok, CondSignal(condAddr));
        Assert.True(w.Thread.Join(WakeTimeout), "waiter never woke after the real signal");
        Assert.Equal(Ok, w.Result);
    }

    [Fact]
    public void BroadcastWakesEveryWaiter()
    {
        const int count = 4;
        var (condAddr, mutexes) = AllocAddresses(count);
        var cond = CreateCond(condAddr);
        for (var i = 0; i < count; i++)
        {
            CreateMutex(mutexes[i]);
        }

        var waiters = new Waiter[count];
        for (var i = 0; i < count; i++)
        {
            waiters[i] = StartWaiter(condAddr, mutexes[i], $"bcast-{i}");
        }

        WaitUntil(() => Waiters(cond) == count, RegisterTimeout, "all waiters to register");

        Assert.Equal(Ok, CondBroadcast(condAddr));

        foreach (var w in waiters)
        {
            Assert.True(w.Thread.Join(WakeTimeout), "a broadcast waiter never woke");
            Assert.Equal(Ok, w.Result);
        }

        Assert.Equal(0, Waiters(cond));
        Assert.Equal(0, Pending(cond));
    }
}
