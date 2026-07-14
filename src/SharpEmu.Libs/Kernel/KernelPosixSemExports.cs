// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// POSIX unnamed semaphores (sem_init/sem_wait/sem_post/sem_destroy and the
/// try/timed/getvalue variants). The guest passes the address of its own sem_t
/// storage; we key a host <see cref="SemaphoreSlim"/> off that address rather
/// than interpreting the opaque sem_t layout. sem_wait blocks the calling
/// host thread (guest threads run 1:1 on host threads), so a producer thread's
/// sem_post wakes it -- the same synchronous host-blocking model the pthread
/// mutex path uses.
/// </summary>
public static class KernelPosixSemExports
{
    private const int SemValueMax = int.MaxValue;

    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _sems = new();

    private static SemaphoreSlim Resolve(ulong sem, int initialIfMissing)
        => _sems.GetOrAdd(sem, _ => new SemaphoreSlim(Math.Clamp(initialIfMissing, 0, SemValueMax), SemValueMax));

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

        var fresh = new SemaphoreSlim(Math.Clamp(value, 0, SemValueMax), SemValueMax);
        if (_sems.TryRemove(sem, out var old))
        {
            old.Dispose();
        }

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

        Resolve(sem, 0).Wait();
        return ctx.SetReturn(0);
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

        // Not available -> -1 (guest maps to EAGAIN); the sem stays untouched.
        return ctx.SetReturn(Resolve(sem, 0).Wait(0) ? 0 : -1);
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

        Resolve(sem, 0).Wait();
        return ctx.SetReturn(0);
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

        try
        {
            Resolve(sem, 0).Release();
        }
        catch (SemaphoreFullException)
        {
            // Already at SEM_VALUE_MAX; a real sem would return EOVERFLOW, but
            // dropping the extra post keeps the count sane without erroring.
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

        var count = _sems.TryGetValue(sem, out var s) ? s.CurrentCount : 0;
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
        if (sem != 0 && _sems.TryRemove(sem, out var s))
        {
            s.Dispose();
        }

        Trace($"destroy sem=0x{sem:X}");
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        foreach (var s in _sems.Values)
        {
            s.Dispose();
        }

        _sems.Clear();
    }
}
