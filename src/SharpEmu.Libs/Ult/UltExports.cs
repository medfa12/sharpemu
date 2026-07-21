// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Ult;

public static class UltExports
{
    internal const int ErrorNull = unchecked((int)0x80810001);
    internal const int ErrorAlignment = unchecked((int)0x80810002);
    internal const int ErrorRange = unchecked((int)0x80810003);
    internal const int ErrorInvalid = unchecked((int)0x80810004);
    internal const int ErrorPermission = unchecked((int)0x80810005);
    internal const int ErrorState = unchecked((int)0x80810006);
    internal const int ErrorBusy = unchecked((int)0x80810007);
    internal const int ErrorAgain = unchecked((int)0x80810008);

    private const int RuntimeSize = 4096;
    private const int WaitingPoolSize = 256;
    private const int QueueDataPoolSize = 512;
    private const int QueueSize = 512;
    private const int PrimitiveSize = 256;
    private const int RuntimeOptParamSize = 128;
    private const int PrimitiveOptParamSize = 16;

    private sealed class RuntimeState
    {
        public required uint MaxUlthreads { get; init; }
        public required uint WorkerThreads { get; init; }
        public required ulong WorkArea { get; init; }
    }

    private sealed class WaitingPoolState
    {
        public object Gate { get; } = new();
        public required uint NumThreads { get; init; }
        public required uint NumSyncObjects { get; init; }
        public required ulong WorkArea { get; init; }
        public uint UsedSyncObjects { get; set; }
        public uint References { get; set; }
    }

    private sealed class QueueDataPoolState
    {
        public object Gate { get; } = new();
        public required uint NumData { get; init; }
        public required ulong DataSize { get; init; }
        public required uint NumQueueObjects { get; init; }
        public required ulong WaitingPool { get; init; }
        public required ulong WorkArea { get; init; }
        public uint ActiveQueues { get; set; }
    }

    private sealed class QueueState
    {
        public object Gate { get; } = new();
        public Queue<byte[]> Items { get; } = new();
        public required ulong DataSize { get; init; }
        public required uint Capacity { get; init; }
        public required ulong WaitingPool { get; init; }
        public required ulong DataPool { get; init; }
        public int PushWaiters { get; set; }
        public int PopWaiters { get; set; }
        public bool Alive { get; set; } = true;
    }

    private sealed class MutexState
    {
        public object Gate { get; } = new();
        public required ulong WaitingPool { get; init; }
        public uint Attribute { get; init; }
        public int OwnerThreadId { get; set; }
        public int Recursion { get; set; }
        public int Waiters { get; set; }
        public int DependentConditions { get; set; }
        public bool Alive { get; set; } = true;
    }

    private sealed class ConditionState
    {
        public object Gate { get; } = new();
        public required ulong Mutex { get; init; }
        public required ulong WaitingPool { get; init; }
        public ulong SignalEpoch { get; set; }
        public int Waiters { get; set; }
        public bool Alive { get; set; } = true;
    }

    private sealed class SemaphoreState
    {
        public object Gate { get; } = new();
        public required ulong WaitingPool { get; init; }
        public int Resources { get; set; }
        public int Waiters { get; set; }
        public bool Alive { get; set; } = true;
    }

    private sealed class ReaderWriterLockState
    {
        public object Gate { get; } = new();
        public Dictionary<int, int> ReaderDepths { get; } = new();
        public required ulong WaitingPool { get; init; }
        public int Readers { get; set; }
        public int WriterThreadId { get; set; }
        public int WriterRecursion { get; set; }
        public int WaitingReaders { get; set; }
        public int WaitingWriters { get; set; }
        public bool Alive { get; set; } = true;
    }

    private static readonly ConcurrentDictionary<ulong, RuntimeState> Runtimes = new();
    private static readonly ConcurrentDictionary<ulong, WaitingPoolState> WaitingPools = new();
    private static readonly ConcurrentDictionary<ulong, QueueDataPoolState> QueueDataPools = new();
    private static readonly ConcurrentDictionary<ulong, QueueState> Queues = new();
    private static readonly ConcurrentDictionary<ulong, MutexState> Mutexes = new();
    private static readonly ConcurrentDictionary<ulong, ConditionState> Conditions = new();
    private static readonly ConcurrentDictionary<ulong, SemaphoreState> Semaphores = new();
    private static readonly ConcurrentDictionary<ulong, ReaderWriterLockState> ReaderWriterLocks = new();

    internal static ulong CalculateRuntimeWorkAreaSize(uint maxUlthreads, uint workerThreads)
    {
        return AlignUp(unchecked((ulong)maxUlthreads * 256 + (ulong)workerThreads * 16 * 1024), 8);
    }

    internal static ulong CalculateWaitingPoolWorkAreaSize(uint numThreads, uint numSyncObjects)
    {
        return AlignUp(unchecked((ulong)(numThreads + numSyncObjects) * 256), 8);
    }

    internal static ulong CalculateQueueDataPoolWorkAreaSize(uint numData, ulong dataSize, uint numQueueObjects)
    {
        var dataArea = unchecked((ulong)numData * AlignUp(dataSize, 8));
        var queueArea = unchecked((ulong)numQueueObjects * QueueSize);
        return AlignUp(unchecked(dataArea + queueArea), 8);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        return unchecked((value + alignment - 1) & ~(alignment - 1));
    }

    private static int Return(CpuContext ctx, int result)
    {
        return ctx.SetReturn(result, typeof(long));
    }

    private static int ReturnSize(CpuContext ctx, ulong size)
    {
        ctx[CpuRegister.Rax] = size;
        return 0;
    }

    private static bool IsAligned(ulong address)
    {
        return (address & 7) == 0;
    }

    private static int ValidateObjectAddress(ulong address)
    {
        if (address == 0)
        {
            return ErrorNull;
        }

        return IsAligned(address) ? 0 : ErrorAlignment;
    }

    private static int InitializeOptParam(CpuContext ctx, int size)
    {
        var address = ctx[CpuRegister.Rdi];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        return Return(ctx, ctx.Memory.TryWrite(address, new byte[size]) ? 0 : ErrorInvalid);
    }

    private static bool ClearGuestObject(CpuContext ctx, ulong address, int size)
    {
        return ctx.Memory.TryWrite(address, new byte[size]);
    }

    private static ulong StackArgument(CpuContext ctx, int index)
    {
        return ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong)), out var value)
            ? value
            : 0;
    }

    private static bool TryReserveSyncObject(ulong poolAddress, out WaitingPoolState? pool)
    {
        pool = null;
        if (poolAddress == 0)
        {
            return true;
        }

        if (!WaitingPools.TryGetValue(poolAddress, out pool))
        {
            return false;
        }

        lock (pool.Gate)
        {
            if (pool.UsedSyncObjects >= pool.NumSyncObjects)
            {
                pool = null;
                return false;
            }

            pool.UsedSyncObjects++;
            return true;
        }
    }

    private static void ReleaseSyncObject(ulong poolAddress)
    {
        if (poolAddress == 0 || !WaitingPools.TryGetValue(poolAddress, out var pool))
        {
            return;
        }

        lock (pool.Gate)
        {
            if (pool.UsedSyncObjects != 0)
            {
                pool.UsedSyncObjects--;
            }
        }
    }

    private static bool TryAddWaitingPoolReference(ulong poolAddress)
    {
        if (poolAddress == 0)
        {
            return true;
        }

        if (!WaitingPools.TryGetValue(poolAddress, out var pool))
        {
            return false;
        }

        lock (pool.Gate)
        {
            pool.References++;
            return true;
        }
    }

    private static void ReleaseWaitingPoolReference(ulong poolAddress)
    {
        if (poolAddress == 0 || !WaitingPools.TryGetValue(poolAddress, out var pool))
        {
            return;
        }

        lock (pool.Gate)
        {
            if (pool.References != 0)
            {
                pool.References--;
            }
        }
    }

    [SysAbiExport(Nid = "hZIg1EWGsHM", ExportName = "sceUltInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltInitialize(CpuContext ctx)
    {
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "d-kSG2fLrvI", ExportName = "sceUltFinalize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltFinalize(CpuContext ctx)
    {
        foreach (var queue in Queues.Values)
        {
            lock (queue.Gate)
            {
                queue.Alive = false;
                Monitor.PulseAll(queue.Gate);
            }
        }

        foreach (var mutex in Mutexes.Values)
        {
            lock (mutex.Gate)
            {
                mutex.Alive = false;
                Monitor.PulseAll(mutex.Gate);
            }
        }

        foreach (var condition in Conditions.Values)
        {
            lock (condition.Gate)
            {
                condition.Alive = false;
                Monitor.PulseAll(condition.Gate);
            }
        }

        foreach (var semaphore in Semaphores.Values)
        {
            lock (semaphore.Gate)
            {
                semaphore.Alive = false;
                Monitor.PulseAll(semaphore.Gate);
            }
        }

        foreach (var rwLock in ReaderWriterLocks.Values)
        {
            lock (rwLock.Gate)
            {
                rwLock.Alive = false;
                Monitor.PulseAll(rwLock.Gate);
            }
        }

        Runtimes.Clear();
        WaitingPools.Clear();
        QueueDataPools.Clear();
        Queues.Clear();
        Mutexes.Clear();
        Conditions.Clear();
        Semaphores.Clear();
        ReaderWriterLocks.Clear();
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "V2u3WLrwh64", ExportName = "sceUltUlthreadRuntimeOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltUlthreadRuntimeOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, RuntimeOptParamSize);
    }

    [SysAbiExport(Nid = "grs2pbc2awM", ExportName = "sceUltUlthreadRuntimeGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltUlthreadRuntimeGetWorkAreaSize(CpuContext ctx)
    {
        return ReturnSize(ctx, CalculateRuntimeWorkAreaSize((uint)ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi]));
    }

    [SysAbiExport(Nid = "jw9FkZBXo-g", ExportName = "sceUltUlthreadRuntimeCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltUlthreadRuntimeCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        var maxUlthreads = (uint)ctx[CpuRegister.Rdx];
        var workerThreads = (uint)ctx[CpuRegister.Rcx];
        if (maxUlthreads == 0 || workerThreads == 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (Runtimes.ContainsKey(address))
        {
            return Return(ctx, ErrorState);
        }

        var state = new RuntimeState
        {
            MaxUlthreads = maxUlthreads,
            WorkerThreads = workerThreads,
            WorkArea = ctx[CpuRegister.R8],
        };
        if (!ClearGuestObject(ctx, address, RuntimeSize))
        {
            return Return(ctx, ErrorInvalid);
        }

        return Return(ctx, Runtimes.TryAdd(address, state) ? 0 : ErrorState);
    }

    [SysAbiExport(Nid = "-gxcs521SvA", ExportName = "sceUltUlthreadRuntimeDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltUlthreadRuntimeDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        return Return(ctx, Runtimes.TryRemove(address, out _) ? 0 : ErrorState);
    }

    [SysAbiExport(Nid = "LuLTRt0rfTw", ExportName = "sceUltWaitingQueueResourcePoolOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltWaitingQueueResourcePoolOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "WIWV1Qd7PFU", ExportName = "sceUltWaitingQueueResourcePoolGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltWaitingQueueResourcePoolGetWorkAreaSize(CpuContext ctx)
    {
        return ReturnSize(ctx, CalculateWaitingPoolWorkAreaSize((uint)ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi]));
    }

    [SysAbiExport(Nid = "YiHujOG9vXY", ExportName = "sceUltWaitingQueueResourcePoolCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltWaitingQueueResourcePoolCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        var numThreads = (uint)ctx[CpuRegister.Rdx];
        var numSyncObjects = (uint)ctx[CpuRegister.Rcx];
        if (numThreads == 0 || numSyncObjects == 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (WaitingPools.ContainsKey(address))
        {
            return Return(ctx, ErrorState);
        }

        var state = new WaitingPoolState
        {
            NumThreads = numThreads,
            NumSyncObjects = numSyncObjects,
            WorkArea = ctx[CpuRegister.R8],
        };
        if (!ClearGuestObject(ctx, address, WaitingPoolSize))
        {
            return Return(ctx, ErrorInvalid);
        }

        return Return(ctx, WaitingPools.TryAdd(address, state) ? 0 : ErrorState);
    }

    [SysAbiExport(Nid = "or55417wcDk", ExportName = "sceUltWaitingQueueResourcePoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltWaitingQueueResourcePoolDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!WaitingPools.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.UsedSyncObjects != 0 || state.References != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            return Return(ctx, WaitingPools.TryRemove(address, out _) ? 0 : ErrorState);
        }
    }

    [SysAbiExport(Nid = "6gYjd50q0CE", ExportName = "sceUltQueueDataResourcePoolOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueDataResourcePoolOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "evj9YPkS8s4", ExportName = "sceUltQueueDataResourcePoolGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueDataResourcePoolGetWorkAreaSize(CpuContext ctx)
    {
        return ReturnSize(ctx, CalculateQueueDataPoolWorkAreaSize(
            (uint)ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], (uint)ctx[CpuRegister.Rdx]));
    }

    [SysAbiExport(Nid = "TFHm6-N6vks", ExportName = "sceUltQueueDataResourcePoolCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueDataResourcePoolCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        var numData = (uint)ctx[CpuRegister.Rdx];
        var dataSize = ctx[CpuRegister.Rcx];
        var numQueueObjects = (uint)ctx[CpuRegister.R8];
        var waitingPool = ctx[CpuRegister.R9];
        if (numData == 0 || dataSize == 0 || dataSize > int.MaxValue || numQueueObjects == 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (QueueDataPools.ContainsKey(address))
        {
            return Return(ctx, ErrorState);
        }

        if (!TryAddWaitingPoolReference(waitingPool))
        {
            return Return(ctx, ErrorInvalid);
        }

        var state = new QueueDataPoolState
        {
            NumData = numData,
            DataSize = dataSize,
            NumQueueObjects = numQueueObjects,
            WaitingPool = waitingPool,
            WorkArea = StackArgument(ctx, 0),
        };
        if (!ClearGuestObject(ctx, address, QueueDataPoolSize))
        {
            ReleaseWaitingPoolReference(waitingPool);
            return Return(ctx, ErrorInvalid);
        }

        if (!QueueDataPools.TryAdd(address, state))
        {
            ReleaseWaitingPoolReference(waitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "dh11uAUWNyM", ExportName = "sceUltQueueDataResourcePoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueDataResourcePoolDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!QueueDataPools.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.ActiveQueues != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            if (!QueueDataPools.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        ReleaseWaitingPoolReference(state.WaitingPool);
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "TkASc9I-xX0", ExportName = "sceUltQueueOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "9Y5keOvb6ok", ExportName = "sceUltQueueCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        var dataSize = ctx[CpuRegister.Rdx];
        var waitingPool = ctx[CpuRegister.Rcx];
        var dataPoolAddress = ctx[CpuRegister.R8];
        if (!QueueDataPools.TryGetValue(dataPoolAddress, out var dataPool))
        {
            return Return(ctx, dataPoolAddress == 0 ? ErrorNull : ErrorInvalid);
        }

        if (dataSize == 0 || dataSize > dataPool.DataSize || dataSize > int.MaxValue)
        {
            return Return(ctx, ErrorRange);
        }

        if (waitingPool != 0 && !WaitingPools.ContainsKey(waitingPool))
        {
            return Return(ctx, ErrorInvalid);
        }

        if (Queues.ContainsKey(address) || !TryReserveSyncObject(waitingPool, out _))
        {
            return Return(ctx, Queues.ContainsKey(address) ? ErrorState : ErrorAgain);
        }

        lock (dataPool.Gate)
        {
            if (dataPool.ActiveQueues >= dataPool.NumQueueObjects)
            {
                ReleaseSyncObject(waitingPool);
                return Return(ctx, ErrorAgain);
            }

            dataPool.ActiveQueues++;
        }

        var state = new QueueState
        {
            DataSize = dataSize,
            Capacity = dataPool.NumData,
            WaitingPool = waitingPool,
            DataPool = dataPoolAddress,
        };
        if (!ClearGuestObject(ctx, address, QueueSize) || !Queues.TryAdd(address, state))
        {
            lock (dataPool.Gate)
            {
                dataPool.ActiveQueues--;
            }

            ReleaseSyncObject(waitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "PP9nZxpSKLY", ExportName = "sceUltQueueDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!Queues.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.PushWaiters != 0 || state.PopWaiters != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            state.Alive = false;
            if (!Queues.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        if (QueueDataPools.TryGetValue(state.DataPool, out var dataPool))
        {
            lock (dataPool.Gate)
            {
                if (dataPool.ActiveQueues != 0)
                {
                    dataPool.ActiveQueues--;
                }
            }
        }

        ReleaseSyncObject(state.WaitingPool);
        return Return(ctx, 0);
    }

    private static int QueuePush(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        if (!Queues.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        if (dataAddress == 0)
        {
            return Return(ctx, ErrorNull);
        }

        var data = new byte[(int)state.DataSize];
        if (!ctx.Memory.TryRead(dataAddress, data))
        {
            return Return(ctx, ErrorInvalid);
        }

        lock (state.Gate)
        {
            while (state.Alive && state.Items.Count >= state.Capacity)
            {
                if (!wait)
                {
                    return Return(ctx, ErrorAgain);
                }

                state.PushWaiters++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.PushWaiters--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            state.Items.Enqueue(data);
            Monitor.PulseAll(state.Gate);
            return Return(ctx, 0);
        }
    }

    private static int QueuePop(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        if (!Queues.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        if (dataAddress == 0)
        {
            return Return(ctx, ErrorNull);
        }

        lock (state.Gate)
        {
            while (state.Alive && state.Items.Count == 0)
            {
                if (!wait)
                {
                    return Return(ctx, ErrorAgain);
                }

                state.PopWaiters++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.PopWaiters--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            var data = state.Items.Peek();
            if (!ctx.Memory.TryWrite(dataAddress, data))
            {
                return Return(ctx, ErrorInvalid);
            }

            state.Items.Dequeue();
            Monitor.PulseAll(state.Gate);
            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "dUwpX3e5NDE", ExportName = "sceUltQueuePush",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueuePush(CpuContext ctx) => QueuePush(ctx, true);

    [SysAbiExport(Nid = "6Mc2Xs7pI1I", ExportName = "sceUltQueueTryPush",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueTryPush(CpuContext ctx) => QueuePush(ctx, false);

    [SysAbiExport(Nid = "RVSq2tsm2yw", ExportName = "sceUltQueuePop",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueuePop(CpuContext ctx) => QueuePop(ctx, true);

    [SysAbiExport(Nid = "uZz3ci7XYqc", ExportName = "sceUltQueueTryPop",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltQueueTryPop(CpuContext ctx) => QueuePop(ctx, false);

    [SysAbiExport(Nid = "1+8t9aHLiz8", ExportName = "sceUltMutexOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "mmt8Sa6tL6c", ExportName = "sceUltMutexCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var waitingPool = ctx[CpuRegister.Rdx];
        var optParam = ctx[CpuRegister.Rcx];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        if (optParam != 0 && !IsAligned(optParam))
        {
            return Return(ctx, ErrorAlignment);
        }

        uint attribute = 0;
        if (optParam != 0 && !ctx.TryReadUInt32(optParam + 8, out attribute))
        {
            return Return(ctx, ErrorInvalid);
        }

        if (Mutexes.ContainsKey(address) || !TryReserveSyncObject(waitingPool, out _))
        {
            return Return(ctx, Mutexes.ContainsKey(address) ? ErrorState : ErrorInvalid);
        }

        var state = new MutexState { WaitingPool = waitingPool, Attribute = attribute };
        if (!ClearGuestObject(ctx, address, PrimitiveSize) || !Mutexes.TryAdd(address, state))
        {
            ReleaseSyncObject(waitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    private static int LockMutex(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!Mutexes.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        var threadId = Environment.CurrentManagedThreadId;
        lock (state.Gate)
        {
            if (state.OwnerThreadId == threadId)
            {
                state.Recursion++;
                return Return(ctx, 0);
            }

            while (state.Alive && state.OwnerThreadId != 0)
            {
                if (!wait)
                {
                    return Return(ctx, ErrorBusy);
                }

                state.Waiters++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.Waiters--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            state.OwnerThreadId = threadId;
            state.Recursion = 1;
            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "8hEGkR1pfr8", ExportName = "sceUltMutexLock",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexLock(CpuContext ctx) => LockMutex(ctx, true);

    [SysAbiExport(Nid = "jOsUG0BJI-Y", ExportName = "sceUltMutexTryLock",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexTryLock(CpuContext ctx) => LockMutex(ctx, false);

    [SysAbiExport(Nid = "h0XebKiMBtk", ExportName = "sceUltMutexUnlock",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexUnlock(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!Mutexes.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        lock (state.Gate)
        {
            if (!state.Alive || state.OwnerThreadId != Environment.CurrentManagedThreadId)
            {
                return Return(ctx, ErrorPermission);
            }

            state.Recursion--;
            if (state.Recursion == 0)
            {
                state.OwnerThreadId = 0;
                Monitor.PulseAll(state.Gate);
            }

            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "jW+HnafeS3Y", ExportName = "sceUltMutexDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltMutexDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!Mutexes.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.OwnerThreadId != 0 || state.Waiters != 0 || state.DependentConditions != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            state.Alive = false;
            if (!Mutexes.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        ReleaseSyncObject(state.WaitingPool);
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "RVmEia0vXMI", ExportName = "sceUltConditionVariableOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "jnKaHGkrxZ4", ExportName = "sceUltConditionVariableCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var mutexAddress = ctx[CpuRegister.Rdx];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        if (!Mutexes.TryGetValue(mutexAddress, out var mutex))
        {
            return Return(ctx, mutexAddress == 0 ? ErrorNull : ErrorInvalid);
        }

        if (Conditions.ContainsKey(address) || !TryReserveSyncObject(mutex.WaitingPool, out _))
        {
            return Return(ctx, Conditions.ContainsKey(address) ? ErrorState : ErrorAgain);
        }

        lock (mutex.Gate)
        {
            if (!mutex.Alive)
            {
                ReleaseSyncObject(mutex.WaitingPool);
                return Return(ctx, ErrorState);
            }

            mutex.DependentConditions++;
        }

        var state = new ConditionState { Mutex = mutexAddress, WaitingPool = mutex.WaitingPool };
        if (!ClearGuestObject(ctx, address, PrimitiveSize) || !Conditions.TryAdd(address, state))
        {
            lock (mutex.Gate)
            {
                mutex.DependentConditions--;
            }

            ReleaseSyncObject(mutex.WaitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "5xGAHCxA8M0", ExportName = "sceUltConditionVariableWait",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!Conditions.TryGetValue(address, out var condition))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        if (!Mutexes.TryGetValue(condition.Mutex, out var mutex))
        {
            return Return(ctx, ErrorState);
        }

        var threadId = Environment.CurrentManagedThreadId;
        int recursion;
        bool alive;
        lock (condition.Gate)
        {
            if (!condition.Alive)
            {
                return Return(ctx, ErrorState);
            }

            lock (mutex.Gate)
            {
                if (!mutex.Alive || mutex.OwnerThreadId != threadId)
                {
                    return Return(ctx, ErrorPermission);
                }

                recursion = mutex.Recursion;
                mutex.OwnerThreadId = 0;
                mutex.Recursion = 0;
                Monitor.PulseAll(mutex.Gate);
            }

            var epoch = condition.SignalEpoch;
            condition.Waiters++;
            try
            {
                while (condition.Alive && epoch == condition.SignalEpoch)
                {
                    Monitor.Wait(condition.Gate);
                }
            }
            finally
            {
                condition.Waiters--;
            }

            alive = condition.Alive;
        }

        lock (mutex.Gate)
        {
            mutex.Waiters++;
            try
            {
                while (mutex.Alive && mutex.OwnerThreadId != 0)
                {
                    Monitor.Wait(mutex.Gate);
                }
            }
            finally
            {
                mutex.Waiters--;
            }

            if (!mutex.Alive)
            {
                return Return(ctx, ErrorState);
            }

            mutex.OwnerThreadId = threadId;
            mutex.Recursion = recursion;
        }

        return Return(ctx, alive ? 0 : ErrorState);
    }

    private static int SignalCondition(CpuContext ctx, bool all)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!Conditions.TryGetValue(address, out var condition))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        lock (condition.Gate)
        {
            if (!condition.Alive)
            {
                return Return(ctx, ErrorState);
            }

            condition.SignalEpoch++;
            if (all)
            {
                Monitor.PulseAll(condition.Gate);
            }
            else
            {
                Monitor.Pulse(condition.Gate);
            }

            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "JTw1cAVkuc0", ExportName = "sceUltConditionVariableSignal",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableSignal(CpuContext ctx) => SignalCondition(ctx, false);

    [SysAbiExport(Nid = "byiceqcMvV0", ExportName = "sceUltConditionVariableSignalAll",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableSignalAll(CpuContext ctx) => SignalCondition(ctx, true);

    [SysAbiExport(Nid = "xrmmI832R4U", ExportName = "sceUltConditionVariableDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltConditionVariableDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!Conditions.TryGetValue(address, out var condition))
        {
            return Return(ctx, ErrorState);
        }

        lock (condition.Gate)
        {
            if (condition.Waiters != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            condition.Alive = false;
            if (!Conditions.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        if (Mutexes.TryGetValue(condition.Mutex, out var mutex))
        {
            lock (mutex.Gate)
            {
                if (mutex.DependentConditions != 0)
                {
                    mutex.DependentConditions--;
                }
            }
        }

        ReleaseSyncObject(condition.WaitingPool);
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "NPRRPNKDBN0", ExportName = "sceUltSemaphoreOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "h5QlIYj+Ro8", ExportName = "sceUltSemaphoreCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var initialResources = unchecked((int)(uint)ctx[CpuRegister.Rdx]);
        var waitingPool = ctx[CpuRegister.Rcx];
        var optParam = ctx[CpuRegister.R8];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        if (optParam != 0 && !IsAligned(optParam))
        {
            return Return(ctx, ErrorAlignment);
        }

        if (initialResources < 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (Semaphores.ContainsKey(address) || !TryReserveSyncObject(waitingPool, out _))
        {
            return Return(ctx, Semaphores.ContainsKey(address) ? ErrorState : ErrorInvalid);
        }

        var state = new SemaphoreState { WaitingPool = waitingPool, Resources = initialResources };
        if (!ClearGuestObject(ctx, address, PrimitiveSize) || !Semaphores.TryAdd(address, state))
        {
            ReleaseSyncObject(waitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    private static int AcquireSemaphore(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        var resources = unchecked((int)(uint)ctx[CpuRegister.Rsi]);
        if (resources <= 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (!Semaphores.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        lock (state.Gate)
        {
            while (state.Alive && state.Resources < resources)
            {
                if (!wait)
                {
                    return Return(ctx, ErrorAgain);
                }

                state.Waiters++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.Waiters--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            state.Resources -= resources;
            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "QAH1ofI97vU", ExportName = "sceUltSemaphoreAcquire",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreAcquire(CpuContext ctx) => AcquireSemaphore(ctx, true);

    [SysAbiExport(Nid = "HA1Ldbi3lPY", ExportName = "sceUltSemaphoreTryAcquire",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreTryAcquire(CpuContext ctx) => AcquireSemaphore(ctx, false);

    [SysAbiExport(Nid = "lbtk5X1mecw", ExportName = "sceUltSemaphoreRelease",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreRelease(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var resources = unchecked((int)(uint)ctx[CpuRegister.Rsi]);
        if (resources <= 0)
        {
            return Return(ctx, ErrorRange);
        }

        if (!Semaphores.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        lock (state.Gate)
        {
            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            if (state.Resources > int.MaxValue - resources)
            {
                return Return(ctx, ErrorRange);
            }

            state.Resources += resources;
            Monitor.PulseAll(state.Gate);
            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "izXyehpoZGo", ExportName = "sceUltSemaphoreDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltSemaphoreDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!Semaphores.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.Waiters != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            state.Alive = false;
            if (!Semaphores.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        ReleaseSyncObject(state.WaitingPool);
        return Return(ctx, 0);
    }

    [SysAbiExport(Nid = "Gw7yn0CEmv8", ExportName = "sceUltReaderWriterLockOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockOptParamInitialize(CpuContext ctx)
    {
        return InitializeOptParam(ctx, PrimitiveOptParamSize);
    }

    [SysAbiExport(Nid = "iIfTXvh1hiM", ExportName = "sceUltReaderWriterLockCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockCreate(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var waitingPool = ctx[CpuRegister.Rdx];
        var validation = ValidateObjectAddress(address);
        if (validation != 0)
        {
            return Return(ctx, validation);
        }

        if (ReaderWriterLocks.ContainsKey(address) || !TryReserveSyncObject(waitingPool, out _))
        {
            return Return(ctx, ReaderWriterLocks.ContainsKey(address) ? ErrorState : ErrorInvalid);
        }

        var state = new ReaderWriterLockState { WaitingPool = waitingPool };
        if (!ClearGuestObject(ctx, address, PrimitiveSize) || !ReaderWriterLocks.TryAdd(address, state))
        {
            ReleaseSyncObject(waitingPool);
            return Return(ctx, ErrorState);
        }

        return Return(ctx, 0);
    }

    private static int LockReaderWriterRead(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!ReaderWriterLocks.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        var threadId = Environment.CurrentManagedThreadId;
        lock (state.Gate)
        {
            while (state.Alive &&
                ((state.WriterThreadId != 0 && state.WriterThreadId != threadId) ||
                 (state.WaitingWriters != 0 && !state.ReaderDepths.ContainsKey(threadId))))
            {
                if (!wait)
                {
                    return Return(ctx, ErrorBusy);
                }

                state.WaitingReaders++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.WaitingReaders--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            state.Readers++;
            state.ReaderDepths.TryGetValue(threadId, out var depth);
            state.ReaderDepths[threadId] = depth + 1;
            return Return(ctx, 0);
        }
    }

    private static int LockReaderWriterWrite(CpuContext ctx, bool wait)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!ReaderWriterLocks.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        var threadId = Environment.CurrentManagedThreadId;
        lock (state.Gate)
        {
            if (state.WriterThreadId == threadId)
            {
                state.WriterRecursion++;
                return Return(ctx, 0);
            }

            if (state.ReaderDepths.ContainsKey(threadId))
            {
                return Return(ctx, ErrorBusy);
            }

            while (state.Alive && (state.WriterThreadId != 0 || state.Readers != 0))
            {
                if (!wait)
                {
                    return Return(ctx, ErrorBusy);
                }

                state.WaitingWriters++;
                try
                {
                    Monitor.Wait(state.Gate);
                }
                finally
                {
                    state.WaitingWriters--;
                }
            }

            if (!state.Alive)
            {
                return Return(ctx, ErrorState);
            }

            state.WriterThreadId = threadId;
            state.WriterRecursion = 1;
            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "Hb9HWFKo9F4", ExportName = "sceUltReaderWriterLockLockRead",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockLockRead(CpuContext ctx) => LockReaderWriterRead(ctx, true);

    [SysAbiExport(Nid = "J7Xs-UluzGk", ExportName = "sceUltReaderWriterLockTryLockRead",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockTryLockRead(CpuContext ctx) => LockReaderWriterRead(ctx, false);

    [SysAbiExport(Nid = "RgKmNey20Ns", ExportName = "sceUltReaderWriterLockLockWrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockLockWrite(CpuContext ctx) => LockReaderWriterWrite(ctx, true);

    [SysAbiExport(Nid = "9Sh0Kk7Xf4w", ExportName = "sceUltReaderWriterLockTryLockWrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockTryLockWrite(CpuContext ctx) => LockReaderWriterWrite(ctx, false);

    [SysAbiExport(Nid = "8Ssk4OU38vw", ExportName = "sceUltReaderWriterLockUnlockRead",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockUnlockRead(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!ReaderWriterLocks.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        var threadId = Environment.CurrentManagedThreadId;
        lock (state.Gate)
        {
            if (!state.ReaderDepths.TryGetValue(threadId, out var depth))
            {
                return Return(ctx, ErrorPermission);
            }

            if (depth == 1)
            {
                state.ReaderDepths.Remove(threadId);
            }
            else
            {
                state.ReaderDepths[threadId] = depth - 1;
            }

            state.Readers--;
            if (state.Readers == 0)
            {
                Monitor.PulseAll(state.Gate);
            }

            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "gcFCn5J5DXY", ExportName = "sceUltReaderWriterLockUnlockWrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockUnlockWrite(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!ReaderWriterLocks.TryGetValue(address, out var state))
        {
            return Return(ctx, address == 0 ? ErrorNull : ErrorState);
        }

        lock (state.Gate)
        {
            if (state.WriterThreadId != Environment.CurrentManagedThreadId)
            {
                return Return(ctx, ErrorPermission);
            }

            state.WriterRecursion--;
            if (state.WriterRecursion == 0)
            {
                state.WriterThreadId = 0;
                Monitor.PulseAll(state.Gate);
            }

            return Return(ctx, 0);
        }
    }

    [SysAbiExport(Nid = "gCA-D2wiiD0", ExportName = "sceUltReaderWriterLockDestroy",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceUlt")]
    public static int UltReaderWriterLockDestroy(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return Return(ctx, ErrorNull);
        }

        if (!ReaderWriterLocks.TryGetValue(address, out var state))
        {
            return Return(ctx, ErrorState);
        }

        lock (state.Gate)
        {
            if (state.Readers != 0 || state.WriterThreadId != 0 ||
                state.WaitingReaders != 0 || state.WaitingWriters != 0)
            {
                return Return(ctx, ErrorBusy);
            }

            state.Alive = false;
            if (!ReaderWriterLocks.TryRemove(address, out _))
            {
                return Return(ctx, ErrorState);
            }
        }

        ReleaseSyncObject(state.WaitingPool);
        return Return(ctx, 0);
    }
}
