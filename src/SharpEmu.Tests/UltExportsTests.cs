// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ult;
using Xunit;

namespace SharpEmu.Tests;

public sealed class UltExportsTests : IDisposable
{
    private const ulong Runtime = 0x1000;
    private const ulong WaitingPool = 0x3000;
    private const ulong QueueDataPool = 0x5000;
    private const ulong Queue = 0x7000;
    private const ulong Mutex = 0x9000;
    private const ulong Condition = 0xB000;
    private const ulong Semaphore = 0xD000;
    private const ulong ReaderWriterLock = 0xF000;

    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public UltExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        UltExports.UltFinalize(_ctx);
    }

    public void Dispose()
    {
        UltExports.UltFinalize(_ctx);
    }

    private static int Result(CpuContext ctx) => unchecked((int)ctx[CpuRegister.Rax]);

    private void CreateWaitingPool(uint syncObjects = 8)
    {
        _ctx[CpuRegister.Rdi] = WaitingPool;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 4;
        _ctx[CpuRegister.Rcx] = syncObjects;
        _ctx[CpuRegister.R8] = 0x20_000;
        Assert.Equal(0, UltExports.UltWaitingQueueResourcePoolCreate(_ctx));
    }

    private void CreateMutex()
    {
        _ctx[CpuRegister.Rdi] = Mutex;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = WaitingPool;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, UltExports.UltMutexCreate(_ctx));
    }

    [Fact]
    public void WorkAreaSizes_MatchReferenceFormulas()
    {
        _ctx[CpuRegister.Rdi] = 10;
        _ctx[CpuRegister.Rsi] = 3;
        UltExports.UltUlthreadRuntimeGetWorkAreaSize(_ctx);
        Assert.Equal(10UL * 256 + 3UL * 16 * 1024, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = 4;
        _ctx[CpuRegister.Rsi] = 7;
        UltExports.UltWaitingQueueResourcePoolGetWorkAreaSize(_ctx);
        Assert.Equal(11UL * 256, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = 5;
        _ctx[CpuRegister.Rsi] = 13;
        _ctx[CpuRegister.Rdx] = 2;
        UltExports.UltQueueDataResourcePoolGetWorkAreaSize(_ctx);
        Assert.Equal(5UL * 16 + 2UL * 512, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void OptParamInitializers_ClearTheirDocumentedStorageOnly()
    {
        const ulong primitiveParam = 0x11_000;
        _memory.TryWrite(primitiveParam, Enumerable.Repeat((byte)0xA5, 24).ToArray());
        _ctx[CpuRegister.Rdi] = primitiveParam;

        Assert.Equal(0, UltExports.UltMutexOptParamInitialize(_ctx));
        var primitiveBytes = new byte[24];
        Assert.True(_memory.TryRead(primitiveParam, primitiveBytes));
        Assert.All(primitiveBytes[..16], value => Assert.Equal(0, value));
        Assert.All(primitiveBytes[16..], value => Assert.Equal(0xA5, value));

        const ulong runtimeParam = 0x12_000;
        _memory.TryWrite(runtimeParam, Enumerable.Repeat((byte)0x5A, 136).ToArray());
        _ctx[CpuRegister.Rdi] = runtimeParam;

        Assert.Equal(0, UltExports.UltUlthreadRuntimeOptParamInitialize(_ctx));
        var runtimeBytes = new byte[136];
        Assert.True(_memory.TryRead(runtimeParam, runtimeBytes));
        Assert.All(runtimeBytes[..128], value => Assert.Equal(0, value));
        Assert.All(runtimeBytes[128..], value => Assert.Equal(0x5A, value));
    }

    [Fact]
    public void Runtime_CreateRejectsDuplicateAndDestroyInvalidatesObject()
    {
        _ctx[CpuRegister.Rdi] = Runtime;
        _ctx[CpuRegister.Rdx] = 16;
        _ctx[CpuRegister.Rcx] = 3;
        _ctx[CpuRegister.R8] = 0x20_000;

        Assert.Equal(0, UltExports.UltUlthreadRuntimeCreate(_ctx));
        Assert.Equal(UltExports.ErrorState, UltExports.UltUlthreadRuntimeCreate(_ctx));
        Assert.Equal(0, UltExports.UltUlthreadRuntimeDestroy(_ctx));
        Assert.Equal(UltExports.ErrorState, UltExports.UltUlthreadRuntimeDestroy(_ctx));
    }

    [Fact]
    public void Queue_PreservesFifoDataAndReportsCapacity()
    {
        CreateWaitingPool();

        _ctx[CpuRegister.Rdi] = QueueDataPool;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = 4;
        _ctx[CpuRegister.R8] = 1;
        _ctx[CpuRegister.R9] = WaitingPool;
        Assert.Equal(0, UltExports.UltQueueDataResourcePoolCreate(_ctx));
        _ctx[CpuRegister.Rdi] = WaitingPool;
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltWaitingQueueResourcePoolDestroy(_ctx));

        _ctx[CpuRegister.Rdi] = Queue;
        _ctx[CpuRegister.Rdx] = 4;
        _ctx[CpuRegister.Rcx] = WaitingPool;
        _ctx[CpuRegister.R8] = QueueDataPool;
        Assert.Equal(0, UltExports.UltQueueCreate(_ctx));

        _memory.WriteUInt32(0x13_000, 0x1122_3344);
        _memory.WriteUInt32(0x13_100, 0x5566_7788);
        _ctx[CpuRegister.Rdi] = Queue;
        _ctx[CpuRegister.Rsi] = 0x13_000;
        Assert.Equal(0, UltExports.UltQueueTryPush(_ctx));
        _ctx[CpuRegister.Rsi] = 0x13_100;
        Assert.Equal(0, UltExports.UltQueueTryPush(_ctx));
        Assert.Equal(UltExports.ErrorAgain, UltExports.UltQueueTryPush(_ctx));

        _ctx[CpuRegister.Rsi] = 0x13_200;
        Assert.Equal(0, UltExports.UltQueueTryPop(_ctx));
        Assert.True(_ctx.TryReadUInt32(0x13_200, out var first));
        Assert.Equal(0x1122_3344u, first);
        Assert.Equal(0, UltExports.UltQueueTryPop(_ctx));
        Assert.Equal(UltExports.ErrorAgain, UltExports.UltQueueTryPop(_ctx));

        _ctx[CpuRegister.Rdi] = QueueDataPool;
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltQueueDataResourcePoolDestroy(_ctx));
        _ctx[CpuRegister.Rdi] = Queue;
        Assert.Equal(0, UltExports.UltQueueDestroy(_ctx));
        _ctx[CpuRegister.Rdi] = QueueDataPool;
        Assert.Equal(0, UltExports.UltQueueDataResourcePoolDestroy(_ctx));
    }

    [Fact]
    public void Mutex_IsRecursiveAndPoolRemainsBusyUntilDestroy()
    {
        CreateWaitingPool();
        CreateMutex();
        _ctx[CpuRegister.Rdi] = Mutex;

        Assert.Equal(0, UltExports.UltMutexLock(_ctx));
        Assert.Equal(0, UltExports.UltMutexTryLock(_ctx));
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltMutexDestroy(_ctx));
        Assert.Equal(0, UltExports.UltMutexUnlock(_ctx));
        Assert.Equal(0, UltExports.UltMutexUnlock(_ctx));
        Assert.Equal(0, UltExports.UltMutexDestroy(_ctx));

        _ctx[CpuRegister.Rdi] = WaitingPool;
        Assert.Equal(0, UltExports.UltWaitingQueueResourcePoolDestroy(_ctx));
    }

    [Fact]
    public void Condition_DependencyPreventsMutexDestroy()
    {
        CreateWaitingPool();
        CreateMutex();

        _ctx[CpuRegister.Rdi] = Condition;
        _ctx[CpuRegister.Rdx] = Mutex;
        Assert.Equal(0, UltExports.UltConditionVariableCreate(_ctx));

        _ctx[CpuRegister.Rdi] = Mutex;
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltMutexDestroy(_ctx));
        _ctx[CpuRegister.Rdi] = Condition;
        Assert.Equal(UltExports.ErrorPermission, UltExports.UltConditionVariableWait(_ctx));
        Assert.Equal(0, UltExports.UltConditionVariableSignal(_ctx));
        Assert.Equal(0, UltExports.UltConditionVariableDestroy(_ctx));
        _ctx[CpuRegister.Rdi] = Mutex;
        Assert.Equal(0, UltExports.UltMutexDestroy(_ctx));
    }

    [Fact]
    public async Task Condition_WaitReleasesMutexAndSignalWakesWaiter()
    {
        CreateWaitingPool();
        CreateMutex();
        _ctx[CpuRegister.Rdi] = Condition;
        _ctx[CpuRegister.Rdx] = Mutex;
        Assert.Equal(0, UltExports.UltConditionVariableCreate(_ctx));

        using var locked = new ManualResetEventSlim();
        var waiter = Task.Run(() =>
        {
            var waitContext = new CpuContext(_memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = Mutex;
            if (UltExports.UltMutexLock(waitContext) != 0)
            {
                return UltExports.ErrorState;
            }

            locked.Set();
            waitContext[CpuRegister.Rdi] = Condition;
            var result = UltExports.UltConditionVariableWait(waitContext);
            waitContext[CpuRegister.Rdi] = Mutex;
            return result != 0 ? result : UltExports.UltMutexUnlock(waitContext);
        });

        Assert.True(locked.Wait(TimeSpan.FromSeconds(2)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!waiter.IsCompleted && DateTime.UtcNow < deadline)
        {
            _ctx[CpuRegister.Rdi] = Condition;
            Assert.Equal(0, UltExports.UltConditionVariableSignal(_ctx));
            await Task.Delay(1);
        }

        Assert.Equal(0, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Semaphore_TryAcquireAndReleaseTrackResources()
    {
        CreateWaitingPool();
        _ctx[CpuRegister.Rdi] = Semaphore;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = WaitingPool;
        _ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, UltExports.UltSemaphoreCreate(_ctx));

        _ctx[CpuRegister.Rdi] = Semaphore;
        _ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(0, UltExports.UltSemaphoreTryAcquire(_ctx));
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(UltExports.ErrorAgain, UltExports.UltSemaphoreTryAcquire(_ctx));
        Assert.Equal(0, UltExports.UltSemaphoreRelease(_ctx));
        Assert.Equal(0, UltExports.UltSemaphoreTryAcquire(_ctx));
        Assert.Equal(0, UltExports.UltSemaphoreDestroy(_ctx));
    }

    [Fact]
    public async Task Semaphore_AcquireBlocksUntilRelease()
    {
        CreateWaitingPool();
        _ctx[CpuRegister.Rdi] = Semaphore;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = WaitingPool;
        Assert.Equal(0, UltExports.UltSemaphoreCreate(_ctx));

        using var started = new ManualResetEventSlim();
        var waiter = Task.Run(() =>
        {
            var waitContext = new CpuContext(_memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = Semaphore;
            waitContext[CpuRegister.Rsi] = 1;
            started.Set();
            return UltExports.UltSemaphoreAcquire(waitContext);
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(waiter.IsCompleted);
        _ctx[CpuRegister.Rdi] = Semaphore;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, UltExports.UltSemaphoreRelease(_ctx));
        Assert.Equal(0, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ReaderWriterLock_ExcludesWriteWhileReadHeld()
    {
        CreateWaitingPool();
        _ctx[CpuRegister.Rdi] = ReaderWriterLock;
        _ctx[CpuRegister.Rdx] = WaitingPool;
        Assert.Equal(0, UltExports.UltReaderWriterLockCreate(_ctx));

        _ctx[CpuRegister.Rdi] = ReaderWriterLock;
        Assert.Equal(0, UltExports.UltReaderWriterLockLockRead(_ctx));
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltReaderWriterLockTryLockWrite(_ctx));
        Assert.Equal(UltExports.ErrorBusy, UltExports.UltReaderWriterLockDestroy(_ctx));
        Assert.Equal(0, UltExports.UltReaderWriterLockUnlockRead(_ctx));
        Assert.Equal(0, UltExports.UltReaderWriterLockTryLockWrite(_ctx));
        Assert.Equal(0, UltExports.UltReaderWriterLockUnlockWrite(_ctx));
        Assert.Equal(0, UltExports.UltReaderWriterLockDestroy(_ctx));
    }

    [Fact]
    public void NullAndMisalignedObjectsUseUltErrorDomain()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(UltExports.ErrorNull, UltExports.UltMutexCreate(_ctx));
        Assert.Equal(UltExports.ErrorNull, Result(_ctx));

        _ctx[CpuRegister.Rdi] = 0x1003;
        Assert.Equal(UltExports.ErrorAlignment, UltExports.UltSemaphoreCreate(_ctx));
        Assert.Equal(UltExports.ErrorAlignment, Result(_ctx));
    }
}
