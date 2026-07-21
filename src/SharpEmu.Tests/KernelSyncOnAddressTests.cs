// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

public sealed class KernelSyncOnAddressTests
{
    private const ulong Address = 0x9000;
    private static readonly TimeSpan InterleaveTimeout = TimeSpan.FromSeconds(2);

    private static CpuContext CreateContext(ICpuMemory memory)
        => new(memory, Generation.Gen5);

    private static int Wait(CpuContext ctx, ulong expectedValue, ulong size, ulong timeoutMicroseconds)
    {
        ctx[CpuRegister.Rdi] = Address;
        ctx[CpuRegister.Rsi] = expectedValue;
        ctx[CpuRegister.Rdx] = size;
        ctx[CpuRegister.Rcx] = timeoutMicroseconds;
        return KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx);
    }

    private static int Wake(CpuContext ctx, ulong count)
    {
        ctx[CpuRegister.Rdi] = Address;
        ctx[CpuRegister.Rsi] = count;
        return KernelSyncOnAddressCompatExports.SyncOnAddressWake(ctx);
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Wait_ValueMismatch_ReturnsImmediately()
    {
        KernelSyncOnAddressCompatExports.ResetForTests();
        var memory = new SparseGuestMemory();
        var ctx = CreateContext(memory);
        Assert.True(ctx.TryWriteUInt32(Address, 7));

        var stopwatch = Stopwatch.StartNew();
        var result = Wait(ctx, 8, sizeof(uint), 0);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, KernelSyncOnAddressCompatExports.GetWaiterCountForTests(Address));
    }

    [Fact]
    public void Wake_ReleasesOnlyRequestedWaiters()
    {
        KernelSyncOnAddressCompatExports.ResetForTests();
        var memory = new SparseGuestMemory();
        var setupContext = CreateContext(memory);
        Assert.True(setupContext.TryWriteUInt32(Address, 42));

        var results = new[] { int.MinValue, int.MinValue };
        var completedCount = 0;
        var waiters = new Thread[2];
        for (var i = 0; i < waiters.Length; i++)
        {
            var index = i;
            waiters[i] = new Thread(() =>
            {
                results[index] = Wait(CreateContext(memory), 42, sizeof(uint), 5_000_000);
                Interlocked.Increment(ref completedCount);
            })
            {
                IsBackground = true,
                Name = $"sync-on-address-waiter-{i}",
            };
            waiters[i].Start();
        }

        WaitUntil(
            () => KernelSyncOnAddressCompatExports.GetWaiterCountForTests(Address) == 2,
            InterleaveTimeout,
            "address waiter registrations");

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wake(CreateContext(memory), 1));
        WaitUntil(() => Volatile.Read(ref completedCount) == 1, InterleaveTimeout, "first waiter wake");
        Assert.Equal(1, KernelSyncOnAddressCompatExports.GetWaiterCountForTests(Address));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wake(CreateContext(memory), 1));
        WaitUntil(() => Volatile.Read(ref completedCount) == 2, InterleaveTimeout, "second waiter wake");
        foreach (var waiter in waiters)
        {
            Assert.True(waiter.Join(InterleaveTimeout));
        }

        Assert.All(results, result => Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result));
        Assert.Equal(0, KernelSyncOnAddressCompatExports.GetWaiterCountForTests(Address));
    }

    [Fact]
    public void Wait_TimeoutExpires()
    {
        KernelSyncOnAddressCompatExports.ResetForTests();
        var memory = new SparseGuestMemory();
        var ctx = CreateContext(memory);
        Assert.True(ctx.TryWriteUInt64(Address, 0x1234));

        var stopwatch = Stopwatch.StartNew();
        var result = Wait(ctx, 0x1234, sizeof(ulong), 30_000);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(15));
        Assert.True(stopwatch.Elapsed < InterleaveTimeout);
        Assert.Equal(0, KernelSyncOnAddressCompatExports.GetWaiterCountForTests(Address));
    }
}
