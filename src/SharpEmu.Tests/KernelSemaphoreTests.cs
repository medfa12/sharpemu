// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceKernelCreate/Poll/Signal/Cancel/DeleteSema semantics driven through the
/// guest ABI. sceKernelWaitSema is exercised only on the host-blocking fallback
/// path (a non-guest test thread cannot park in the guest scheduler); its
/// scheduler-parking path is covered by the scheduler tests.
/// </summary>
public sealed class KernelSemaphoreTests
{
    private const ulong OutHandleAddress = 0x1000;
    private const ulong NameAddress = 0x2000;
    private const ulong WaitersAddress = 0x3000;

    private static CpuContext NewContext(string name = "sema")
    {
        var memory = new SparseGuestMemory();
        var bytes = Encoding.UTF8.GetBytes(name);
        memory.TryWrite(NameAddress, bytes);
        memory.TryWrite(NameAddress + (ulong)bytes.Length, new byte[] { 0 });
        memory.WriteUInt64(OutHandleAddress, 0);
        memory.WriteUInt32(WaitersAddress, 0xFFFF_FFFF);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static OrbisGen2Result Create(
        CpuContext ctx,
        int initialCount,
        int maxCount,
        out uint handle,
        uint attr = 0,
        ulong outAddress = OutHandleAddress,
        ulong nameAddress = NameAddress,
        ulong optionAddress = 0)
    {
        ctx[CpuRegister.Rdi] = outAddress;
        ctx[CpuRegister.Rsi] = nameAddress;
        ctx[CpuRegister.Rdx] = attr;
        ctx[CpuRegister.Rcx] = unchecked((ulong)(long)initialCount);
        ctx[CpuRegister.R8] = unchecked((ulong)(long)maxCount);
        ctx[CpuRegister.R9] = optionAddress;

        var result = (OrbisGen2Result)KernelSemaphoreCompatExports.KernelCreateSema(ctx);
        handle = 0;
        if (result == OrbisGen2Result.ORBIS_GEN2_OK)
        {
            Assert.True(ctx.TryReadUInt32(OutHandleAddress, out handle));
        }

        return result;
    }

    private static uint CreateOk(CpuContext ctx, int initialCount, int maxCount)
    {
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Create(ctx, initialCount, maxCount, out var handle));
        Assert.NotEqual(0u, handle);
        return handle;
    }

    private static OrbisGen2Result Poll(CpuContext ctx, uint handle, int needCount)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)needCount);
        return (OrbisGen2Result)KernelSemaphoreCompatExports.KernelPollSema(ctx);
    }

    private static OrbisGen2Result Wait(CpuContext ctx, uint handle, int needCount, ulong timeoutAddress = 0)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)needCount);
        ctx[CpuRegister.Rdx] = timeoutAddress;
        return (OrbisGen2Result)KernelSemaphoreCompatExports.KernelWaitSema(ctx);
    }

    private static OrbisGen2Result Signal(CpuContext ctx, uint handle, int signalCount)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)signalCount);
        return (OrbisGen2Result)KernelSemaphoreCompatExports.KernelSignalSema(ctx);
    }

    private static OrbisGen2Result Cancel(CpuContext ctx, uint handle, int setCount, ulong waitersAddress = 0)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)setCount);
        ctx[CpuRegister.Rdx] = waitersAddress;
        return (OrbisGen2Result)KernelSemaphoreCompatExports.KernelCancelSema(ctx);
    }

    private static OrbisGen2Result Delete(CpuContext ctx, uint handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        return (OrbisGen2Result)KernelSemaphoreCompatExports.KernelDeleteSema(ctx);
    }

    [Fact]
    public void Poll_DecrementsCountUntilExhausted()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 2, maxCount: 4);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 1));
    }

    [Fact]
    public void Poll_MultiCountAcquireIsAllOrNothing()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 2, maxCount: 4);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 3));
        // The failed multi-acquire must not have consumed anything.
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 2));
    }

    [Fact]
    public void Poll_CountAboveMaxOrBelowOne_IsInvalidArgument()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 1, maxCount: 2);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Poll(ctx, handle, 0));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Poll(ctx, handle, 3)); // > MaxCount
    }

    [Fact]
    public void Signal_IncrementsCount()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 0, maxCount: 3);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Signal(ctx, handle, 2));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 2));
    }

    [Fact]
    public void Signal_BeyondMaxCount_IsRejectedAndCountUnchanged()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 2, maxCount: 3);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Signal(ctx, handle, 2)); // 2+2 > 3
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Signal(ctx, handle, 1));                     // 2+1 == 3
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 3));
    }

    [Fact]
    public void Signal_NonPositiveCount_IsInvalidArgument()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 0, maxCount: 2);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Signal(ctx, handle, 0));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Signal(ctx, handle, -1));
    }

    [Fact]
    public void Cancel_NegativeSetCount_RestoresInitialCount()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 2, maxCount: 4);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 2)); // drain to 0

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Cancel(ctx, handle, -1));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 2)); // back to initial 2
    }

    [Fact]
    public void Cancel_SetsExplicitCountAndReportsWaiters()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 0, maxCount: 4);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Cancel(ctx, handle, 3, WaitersAddress));

        Assert.True(ctx.TryReadUInt32(WaitersAddress, out var waiters));
        Assert.Equal(0u, waiters); // no blocked threads in this test harness
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 3));
    }

    [Fact]
    public void Cancel_SetCountAboveMax_IsInvalidArgument()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 1, maxCount: 2);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Cancel(ctx, handle, 3));
    }

    [Theory]
    [InlineData(-1, 2)]  // negative initial
    [InlineData(3, 2)]   // initial > max
    [InlineData(0, 0)]   // max must be positive
    [InlineData(0, -1)]
    public void Create_InvalidCounts_AreRejected(int initialCount, int maxCount)
    {
        var ctx = NewContext();

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Create(ctx, initialCount, maxCount, out _));
    }

    [Fact]
    public void Create_InvalidAttrNullPointersOrOptions_AreRejected()
    {
        var ctx = NewContext();

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Create(ctx, 0, 1, out _, attr: 3));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Create(ctx, 0, 1, out _, outAddress: 0));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Create(ctx, 0, 1, out _, nameAddress: 0));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Create(ctx, 0, 1, out _, optionAddress: 0xDEAD));
    }

    [Fact]
    public void Delete_RemovesHandle()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 1, maxCount: 1);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Delete(ctx, handle));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Delete(ctx, handle));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Poll(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Signal(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Cancel(ctx, handle, 1));
    }

    [Fact]
    public void Wait_HostThread_ReturnsImmediatelyWhenCountAvailable()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 2, maxCount: 2);

        // Count already satisfies the request, so the wait completes without
        // parking -- it never reaches the host-blocking fallback.
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Wait(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Wait(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 1));
    }

    [Fact]
    public async Task Wait_HostThread_BlocksUntilSignaled()
    {
        var ctx = NewContext();
        var handle = CreateOk(ctx, initialCount: 0, maxCount: 1);

        var waitResult = OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
        var waitStarted = new ManualResetEventSlim(false);
        var waiter = Task.Run(() =>
        {
            var waitCtx = NewContext();
            waitStarted.Set();
            waitResult = Wait(waitCtx, handle, 1);
        });

        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(2)));
        // The whole point of the fix: an infinite wait must actually block a
        // host thread, not return EAGAIN immediately.
        var early = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Assert.NotSame(waiter, early);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Signal(ctx, handle, 1));
        var woke = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(waiter, woke);
        await waiter;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, waitResult);
    }

    [Fact]
    public void Handles_AreUniquePerSemaphore()
    {
        var ctx = NewContext();

        var first = CreateOk(ctx, 0, 1);
        var second = CreateOk(ctx, 0, 1);

        Assert.NotEqual(first, second);
    }
}
