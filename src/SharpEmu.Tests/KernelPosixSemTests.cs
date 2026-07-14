// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// POSIX sem_init/wait/trywait/post/getvalue/destroy driven through the guest
/// ABI. A non-guest test thread cannot park in the guest scheduler, so sem_wait
/// here exercises the host-blocking fallback; the scheduler-parking path is
/// covered by the scheduler tests.
/// </summary>
public sealed class KernelPosixSemTests
{
    private const ulong SemAddress = 0x4000;
    private const ulong OutValueAddress = 0x5000;

    private static CpuContext NewContext()
    {
        KernelPosixSemExports.ResetForTests();
        return new CpuContext(new SparseGuestMemory(), Generation.Gen5);
    }

    private static int Init(CpuContext ctx, ulong sem, uint value)
    {
        ctx[CpuRegister.Rdi] = sem;
        ctx[CpuRegister.Rdx] = value;
        return KernelPosixSemExports.SemInit(ctx);
    }

    private static int Wait(CpuContext ctx, ulong sem)
    {
        ctx[CpuRegister.Rdi] = sem;
        return KernelPosixSemExports.SemWait(ctx);
    }

    private static int Trywait(CpuContext ctx, ulong sem)
    {
        ctx[CpuRegister.Rdi] = sem;
        return KernelPosixSemExports.SemTrywait(ctx);
    }

    private static int Post(CpuContext ctx, ulong sem)
    {
        ctx[CpuRegister.Rdi] = sem;
        return KernelPosixSemExports.SemPost(ctx);
    }

    private static int GetValue(CpuContext ctx, ulong sem, ulong outAddress)
    {
        ctx[CpuRegister.Rdi] = sem;
        ctx[CpuRegister.Rsi] = outAddress;
        return KernelPosixSemExports.SemGetvalue(ctx);
    }

    [Fact]
    public void Trywait_DrainsCountThenFails()
    {
        var ctx = NewContext();
        Assert.Equal(0, Init(ctx, SemAddress, 2));

        Assert.Equal(0, Trywait(ctx, SemAddress));
        Assert.Equal(0, Trywait(ctx, SemAddress));
        Assert.Equal(-1, Trywait(ctx, SemAddress)); // exhausted -> EAGAIN
    }

    [Fact]
    public void WaitConsumesAvailableCountWithoutBlocking()
    {
        var ctx = NewContext();
        Assert.Equal(0, Init(ctx, SemAddress, 1));

        Assert.Equal(0, Wait(ctx, SemAddress)); // count 1 -> returns immediately
        Assert.Equal(-1, Trywait(ctx, SemAddress)); // now drained
    }

    [Fact]
    public void GetValueReportsCurrentCount()
    {
        var ctx = NewContext();
        Assert.Equal(0, Init(ctx, SemAddress, 3));

        Assert.Equal(0, GetValue(ctx, SemAddress, OutValueAddress));
        Assert.True(ctx.TryReadInt32(OutValueAddress, out var value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void PostThenWaitPairsUp()
    {
        var ctx = NewContext();
        Assert.Equal(0, Init(ctx, SemAddress, 0));

        Assert.Equal(-1, Trywait(ctx, SemAddress)); // empty
        Assert.Equal(0, Post(ctx, SemAddress));
        Assert.Equal(0, Wait(ctx, SemAddress)); // consumes the post, no block
    }

    [Fact]
    public async Task Wait_HostThread_BlocksUntilPosted()
    {
        var ctx = NewContext();
        Assert.Equal(0, Init(ctx, SemAddress, 0));

        var waitResult = -1;
        var waitStarted = new ManualResetEventSlim(false);
        var waiter = Task.Run(() =>
        {
            var waitCtx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
            waitStarted.Set();
            waitResult = Wait(waitCtx, SemAddress);
        });

        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(2)));
        // A count-0 wait must actually block, not spin-return.
        var early = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Assert.NotSame(waiter, early);

        Assert.Equal(0, Post(ctx, SemAddress));
        var woke = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(waiter, woke);
        await waiter;
        Assert.Equal(0, waitResult);
    }
}
