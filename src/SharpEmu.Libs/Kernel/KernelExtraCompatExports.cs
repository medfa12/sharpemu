// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelExtraCompatExports
{
    private const int PosixEinval = 22;
    private const int AioCompleted = 3;
    private const int AioAborted = 4;
    private const int EventSize = 32;
    private const int AioRequestSize = 40;
    private const ulong SyntheticSemaphoreBase = 0x00006008_0000_0000UL;

    private static readonly ConcurrentDictionary<int, byte> _kqueues = new();
    private static readonly ConcurrentDictionary<int, int> _aioStates = new();
    private static readonly ConcurrentDictionary<ulong, MutexExtensionState> _mutexExtensions = new();
    private static readonly ConcurrentDictionary<ulong, AttributeExtensionState> _attributeExtensions = new();
    private static readonly ConcurrentDictionary<ulong, CondAttributeState> _condAttributes = new();
    private static readonly ConcurrentDictionary<ulong, RwlockAttributeState> _rwlockAttributes = new();
    private static readonly ConcurrentDictionary<ulong, ulong> _signalHandlers = new();
    private static readonly ConcurrentDictionary<ulong, byte> _onceControls = new();
    private static int _nextKqueue = 0x4000;
    private static int _nextAioId = 1;
    private static long _nextSemaphoreId;

    private sealed class MutexExtensionState
    {
        public int SpinLoops;
        public int YieldLoops;
        public int Kind;
        public int PriorityCeiling;
        public int Protocol = 0;
        public int ProcessShared;
        public int Type = 0;
    }

    private sealed class AttributeExtensionState
    {
        public int InheritSched = 4;
        public int Policy = 1;
        public int Scope;
        public int CreateSuspended;
    }

    private sealed class CondAttributeState
    {
        public int ClockId;
        public int ProcessShared;
    }

    private sealed class RwlockAttributeState
    {
        public int ProcessShared;
        public int Type;
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    private static int Invalid(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

    private static int Fault(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

    private static int PosixFailure(CpuContext ctx, int error)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, error);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    private static int WriteInt32(CpuContext ctx, ulong address, int value)
    {
        if (address == 0)
        {
            return Invalid(ctx);
        }

        return ctx.TryWriteInt32(address, value) ? Ok(ctx) : Fault(ctx);
    }

    private static int WriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        if (address == 0)
        {
            return Invalid(ctx);
        }

        return ctx.TryWriteUInt64(address, value) ? Ok(ctx) : Fault(ctx);
    }

    private static bool TryClear(CpuContext ctx, ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return address != 0 || size == 0;
        }

        var remaining = size;
        Span<byte> zero = stackalloc byte[256];
        zero.Clear();
        while (remaining != 0)
        {
            var count = (int)Math.Min((ulong)zero.Length, remaining);
            if (!ctx.Memory.TryWrite(address, zero[..count]))
            {
                return false;
            }

            address += (ulong)count;
            remaining -= (ulong)count;
        }

        return true;
    }

    private static int CallWithRsi(CpuContext ctx, ulong rsi, Func<CpuContext, int> handler)
    {
        var saved = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rsi] = rsi;
        try
        {
            return handler(ctx);
        }
        finally
        {
            ctx[CpuRegister.Rsi] = saved;
        }
    }

    [SysAbiExport(Nid = "pG70GT5yRo4", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries01(CpuContext ctx) => KernelSocketCompatExports.Socket(ctx);
    [SysAbiExport(Nid = "6O8EwYOgH9Y", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries02(CpuContext ctx) => SocketGetsockopt(ctx);
    [SysAbiExport(Nid = "fFxGkxF2bVo", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries03(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "pxnCmagrtao", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries04(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "3e+4Iv7IJ8U", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries05(CpuContext ctx) => SocketAccept(ctx);
    [SysAbiExport(Nid = "TUuiYS2kE8s", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries06(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "MZb0GKT3mo8", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries07(CpuContext ctx) => SocketPair(ctx);
    [SysAbiExport(Nid = "K1S8oc61xiM", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries08(CpuContext ctx) { var value = unchecked((uint)ctx[CpuRegister.Rdi]); ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value); return 0; }
    [SysAbiExport(Nid = "fZOeZIOEmLw", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries09(CpuContext ctx) => KernelMemoryCompatExports.KernelWrite(ctx);
    [SysAbiExport(Nid = "oBr313PppNE", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries10(CpuContext ctx) => KernelMemoryCompatExports.KernelWrite(ctx);
    [SysAbiExport(Nid = "Ez8xjo9UF4E", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries11(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "lUk6wrGXyMw", ExportName = "Libraries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Libraries12(CpuContext ctx) => SocketRecvfrom(ctx);

    [SysAbiExport(Nid = "hI7oVeOluPM", ExportName = "Libraries", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixLibraries01(CpuContext ctx) => SocketRecvmsg(ctx);
    [SysAbiExport(Nid = "TXFFFiNldU8", ExportName = "Libraries", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixLibraries02(CpuContext ctx) => SocketGetpeername(ctx);
    [SysAbiExport(Nid = "5jRCs2axtr4", ExportName = "Libraries", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixLibraries03(CpuContext ctx) => SocketInetNtop(ctx);
    [SysAbiExport(Nid = "aNeavPDNKzA", ExportName = "Libraries", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixLibraries04(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }

    [SysAbiExport(Nid = "qH1gXoq71RY", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis01(CpuContext ctx) => KernelPthreadCompatExports.PosixPthreadMutexInit(ctx);
    [SysAbiExport(Nid = "W6OrTBO95UY", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis02(CpuContext ctx) => PosixMutexIsowned(ctx);
    [SysAbiExport(Nid = "IafI2PxcPnQ", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis03(CpuContext ctx) => KernelPthreadCompatExports.PosixPthreadMutexTrylock(ctx);
    [SysAbiExport(Nid = "pOmNmyRKlIE", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis04(CpuContext ctx) => PosixMutexGetspinloops(ctx);
    [SysAbiExport(Nid = "AWS3NyViL9o", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis05(CpuContext ctx) => PosixMutexGetyieldloops(ctx);
    [SysAbiExport(Nid = "42YkUouoMI0", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis06(CpuContext ctx) => PosixMutexSetspinloops(ctx);
    [SysAbiExport(Nid = "bP+cqFmBW+A", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis07(CpuContext ctx) => PosixMutexSetyieldloops(ctx);
    [SysAbiExport(Nid = "n2MMpvU8igI", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis08(CpuContext ctx) => KernelPthreadCompatExports.PosixPthreadMutexattrInit(ctx);
    [SysAbiExport(Nid = "rH2mWEndluc", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis09(CpuContext ctx) => PosixMutexattrGetkind(ctx);
    [SysAbiExport(Nid = "SgjMpyH9Z9I", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis10(CpuContext ctx) => PosixMutexattrGetprioceiling(ctx);
    [SysAbiExport(Nid = "GoTmFeui+hQ", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis11(CpuContext ctx) => PosixMutexattrGetprotocol(ctx);
    [SysAbiExport(Nid = "losEubHc64c", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis12(CpuContext ctx) => PosixMutexattrGetpshared(ctx);
    [SysAbiExport(Nid = "gquEhBrS2iw", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis13(CpuContext ctx) => PosixMutexattrGettype(ctx);
    [SysAbiExport(Nid = "UWZbVSFze24", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis14(CpuContext ctx) => PosixMutexattrSetkind(ctx);
    [SysAbiExport(Nid = "532IaQguwMg", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis15(CpuContext ctx) => PosixMutexattrSetprioceiling(ctx);
    [SysAbiExport(Nid = "mxKx9bxXF2I", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis16(CpuContext ctx) => PosixMutexattrSetpshared(ctx);
    [SysAbiExport(Nid = "LcOZBHGqbFk", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis17(CpuContext ctx) => PosixRwlockAttrGetpshared(ctx);
    [SysAbiExport(Nid = "Kyls1ChFyrc", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis18(CpuContext ctx) => PosixRwlockAttrGettype(ctx);
    [SysAbiExport(Nid = "-ZvQH18j10c", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis19(CpuContext ctx) => PosixRwlockAttrSetpshared(ctx);
    [SysAbiExport(Nid = "h-OifiouBd8", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis20(CpuContext ctx) => PosixRwlockAttrSettype(ctx);
    [SysAbiExport(Nid = "iPtZRWICjrM", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis21(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "adh--6nIqTk", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis22(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "XD3mDeybCnk", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis23(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "bIHoZCTomsI", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis24(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "lpMP8HhkBbg", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis25(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], 4);
    [SysAbiExport(Nid = "NMyIQ9WgWbU", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis26(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], 1);
    [SysAbiExport(Nid = "+7B2AEKKns8", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis27(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], 0);
    [SysAbiExport(Nid = "GZSR0Ooae9Q", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis28(CpuContext ctx) => PosixAttrSetcreatesuspend(ctx);
    [SysAbiExport(Nid = "YdZfEZfRnPk", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis29(CpuContext ctx) => PosixAttrSetscope(ctx);
    [SysAbiExport(Nid = "F+yfmduIBB8", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis30(CpuContext ctx) => PosixAttrSetstackaddr(ctx);
    [SysAbiExport(Nid = "6xMew9+rZwI", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis31(CpuContext ctx) => PosixCondattrSetpshared(ctx);
    [SysAbiExport(Nid = "c-bxj027czs", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis32(CpuContext ctx) => PosixCondattrSetclock(ctx);
    [SysAbiExport(Nid = "Dn-DRWi9t54", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis33(CpuContext ctx) => PosixCondattrGetpshared(ctx);
    [SysAbiExport(Nid = "6qM3kO5S3Oo", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis34(CpuContext ctx) => PosixCondattrGetclock(ctx);
    [SysAbiExport(Nid = "o69RpYO-Mu0", ExportName = "ORBIS", LibraryName = "libkernel", Target = Generation.Gen5)] public static int Orbis35(CpuContext ctx) => KernelPthreadCompatExports.PthreadCondSignal(ctx);

    [SysAbiExport(Nid = "1xvtUVx1-Sg", ExportName = "__pthread_cleanup_push_imp", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadCleanupPushImp(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "7NwggrWJ5cA", ExportName = "__sys_regmgr_call", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int SysRegmgrCall(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdx];
        var outSize = ctx[CpuRegister.Rcx];
        return outAddress == 0 || outSize == 0 || TryClear(ctx, outAddress, Math.Min(outSize, 0x1000UL)) ? Ok(ctx) : Fault(ctx);
    }

    [SysAbiExport(Nid = "igMefp4SAv0", ExportName = "get_authinfo", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int GetAuthInfo(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 || TryClear(ctx, ctx[CpuRegister.Rdi], 0x88) ? Ok(ctx) : Fault(ctx);

    [SysAbiExport(Nid = "iKJMWrAumPE", ExportName = "getargc", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int GetArgc(CpuContext ctx) { ctx[CpuRegister.Rax] = 1; return 0; }

    [SysAbiExport(Nid = "FJmglmTMdr4", ExportName = "getargv", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int GetArgv(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }

    [SysAbiExport(Nid = "sfKygSjIbI8", ExportName = "getdirentries", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Getdirentries(CpuContext ctx) => KernelMemoryCompatExports.KernelGetdirentries(ctx);

    [SysAbiExport(Nid = "PfccT7qURYE", ExportName = "kernel_ioctl", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelIoctl01(CpuContext ctx) => KernelIoctlCore(ctx);
    [SysAbiExport(Nid = "wW+k21cmbwQ", ExportName = "kernel_ioctl", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelIoctl02(CpuContext ctx) => KernelIoctlCore(ctx);

    private static int KernelIoctlCore(CpuContext ctx)
    {
        var argument = ctx[CpuRegister.Rdx];
        if (argument != 0 && !TryClear(ctx, argument, 16))
        {
            return Fault(ctx);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "smIj7eqzZE8", ExportName = "posix_clock_getres", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixClockGetres(CpuContext ctx) => ClockGetresCore(ctx, posix: true);

    [SysAbiExport(Nid = "wRYVA5Zolso", ExportName = "sceKernelClockGetres", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelClockGetres(CpuContext ctx) => ClockGetresCore(ctx, posix: false);

    private static int ClockGetresCore(CpuContext ctx, bool posix)
    {
        var address = ctx[CpuRegister.Rsi];
        if (address == 0)
        {
            return posix ? PosixFailure(ctx, PosixEinval) : Invalid(ctx);
        }

        Span<byte> timespec = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(timespec, 0);
        BinaryPrimitives.WriteInt64LittleEndian(timespec[8..], 100);
        return ctx.Memory.TryWrite(address, timespec) ? Ok(ctx) : (posix ? PosixFailure(ctx, 14) : Fault(ctx));
    }

    [SysAbiExport(Nid = "NhpspxdjEKU", ExportName = "posix_nanosleep", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixNanosleep(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0 || !ctx.TryReadUInt64(requestAddress, out var seconds) || !ctx.TryReadUInt64(requestAddress + 8, out var nanoseconds) || unchecked((long)seconds) < 0 || nanoseconds >= 1_000_000_000UL)
        {
            return PosixFailure(ctx, PosixEinval);
        }
        var requestedTicks = seconds > 0 ? TimeSpan.TicksPerSecond : (long)Math.Min(nanoseconds / 100, (ulong)TimeSpan.TicksPerSecond);
        if (requestedTicks > 0) Thread.Sleep(TimeSpan.FromTicks(Math.Min(requestedTicks, TimeSpan.TicksPerMillisecond * 10)));
        if (ctx[CpuRegister.Rsi] != 0 && !TryClear(ctx, ctx[CpuRegister.Rsi], 16)) return PosixFailure(ctx, 14);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "0wu33hunNdE", ExportName = "posix_sleep", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSleep(CpuContext ctx)
    {
        var seconds = ctx[CpuRegister.Rdi];
        if (seconds != 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(seconds, 1UL)));
        }
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "d7nUj1LOdDU", ExportName = "posix_clock_settime", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixClockSettime(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "VdXIDAbJ3tQ", ExportName = "posix_settimeofday", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixSettimeofday(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ChCOChPU-YM", ExportName = "sceKernelSettimeofday", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelSettimeofday(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "-ZR+hG7aDHw", ExportName = "sceKernelSleep", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelSleep(CpuContext ctx) => PosixSleep(ctx);

    [SysAbiExport(Nid = "k+AXqu2-eBc", ExportName = "posix_getpagesize", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixGetpagesize(CpuContext ctx) { ctx[CpuRegister.Rax] = 0x4000; return 0; }
    [SysAbiExport(Nid = "kg4x8Prhfxw", ExportName = "posix_getuid", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixGetuid(CpuContext ctx) { ctx[CpuRegister.Rax] = 1; return 0; }
    [SysAbiExport(Nid = "mkawd0NA9ts", ExportName = "posix_sysconf", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSysconf(CpuContext ctx) { ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi] switch { 8 => 8UL, 47 => 0x4000UL, _ => 1UL }; return 0; }

    [SysAbiExport(Nid = "BPE9s9vQQXo", ExportName = "posix_mmap", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixMmap(CpuContext ctx)
    {
        var length = ctx[CpuRegister.Rsi];
        if (length == 0 || !KernelMemoryCompatExports.TryAllocateHleData(ctx, length, 0x4000, out var address))
        {
            return PosixFailure(ctx, length == 0 ? PosixEinval : 12);
        }
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    [SysAbiExport(Nid = "PGhQHd-dzv8", ExportName = "sceKernelMmap", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelMmap(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsp] == 0 || !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 8, out var stackResultAddress) ? 0 : stackResultAddress;
        var length = ctx[CpuRegister.Rsi];
        if (resultAddress == 0 || length == 0)
        {
            return Invalid(ctx);
        }
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, length, 0x4000, out var mappedAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }
        return ctx.TryWriteUInt64(resultAddress, mappedAddress) ? Ok(ctx) : Fault(ctx);
    }

    [SysAbiExport(Nid = "YQOfxL4QfeU", ExportName = "posix_mprotect", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixMprotect(CpuContext ctx) => KernelMemoryCompatExports.KernelMprotect(ctx);
    [SysAbiExport(Nid = "tZY4+SZNFhA", ExportName = "posix_msync", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixMsync(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "UqDGjXA5yUM", ExportName = "posix_munmap", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixMunmap(CpuContext ctx) => KernelMemoryCompatExports.KernelMunmap(ctx);
    [SysAbiExport(Nid = "3k6kx-zOOSQ", ExportName = "sceKernelMlock", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelMlock(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 && ctx[CpuRegister.Rsi] != 0 ? Invalid(ctx) : Ok(ctx);

    [SysAbiExport(Nid = "kc+LEEIYakc", ExportName = "sceKernelMapNamedSystemFlexibleMemory", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelMapNamedSystemFlexibleMemory(CpuContext ctx) => KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(ctx);

    [SysAbiExport(Nid = "BC+OG5m9+bw", ExportName = "sceKernelGetDirectMemoryType", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetDirectMemoryType(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
        {
            return Invalid(ctx);
        }
        if (!ctx.TryWriteInt32(ctx[CpuRegister.Rsi], 0) || !ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0) || !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], 0))
        {
            return Fault(ctx);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "yDBwVAolDgg", ExportName = "sceKernelIsStack", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelIsStack(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], 0)) return Fault(ctx);
        if (ctx[CpuRegister.Rdx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0)) return Fault(ctx);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "L0v2Go5jOuM", ExportName = "sceKernelGetPrtAperture", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetPrtAperture(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], 0)) return Fault(ctx);
        if (ctx[CpuRegister.Rdx] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0)) return Fault(ctx);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "qCSfqDILlns", ExportName = "sceKernelMemoryPoolExpand", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int MemoryPoolExpand(CpuContext ctx) => WriteUInt64(ctx, ctx[CpuRegister.R8], ctx[CpuRegister.Rdi]);
    [SysAbiExport(Nid = "pU-QydtGcGY", ExportName = "sceKernelMemoryPoolReserve", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int MemoryPoolReserve(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.R8];
        if (outAddress == 0 || ctx[CpuRegister.Rsi] == 0) return Invalid(ctx);
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx] == 0 ? 0x200000UL : ctx[CpuRegister.Rdx], out var address))
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        return ctx.TryWriteUInt64(outAddress, address) ? Ok(ctx) : Fault(ctx);
    }
    [SysAbiExport(Nid = "Vzl66WmfLvk", ExportName = "sceKernelMemoryPoolCommit", LibraryName = "libkernel", Target = Generation.Gen5)] public static int MemoryPoolCommit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "LXo1tpFqJGs", ExportName = "sceKernelMemoryPoolDecommit", LibraryName = "libkernel", Target = Generation.Gen5)] public static int MemoryPoolDecommit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "YN878uKRBbE", ExportName = "sceKernelMemoryPoolBatch", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int MemoryPoolBatch(CpuContext ctx) => ctx[CpuRegister.Rdx] == 0 || ctx.TryWriteInt32(ctx[CpuRegister.Rdx], unchecked((int)ctx[CpuRegister.Rsi])) ? Ok(ctx) : Fault(ctx);
    [SysAbiExport(Nid = "bvD+95Q6asU", ExportName = "sceKernelMemoryPoolGetBlockStats", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int MemoryPoolGetBlockStats(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 && ctx[CpuRegister.Rsi] != 0 ? Invalid(ctx) : (TryClear(ctx, ctx[CpuRegister.Rdi], Math.Min(ctx[CpuRegister.Rsi], 0x80UL)) ? Ok(ctx) : Fault(ctx));

    [SysAbiExport(Nid = "YeU23Szo3BM", ExportName = "sceKernelGetAllowedSdkVersionOnSystem", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetAllowedSdkVersionOnSystem(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 ? Invalid(ctx) : (ctx.TryWriteUInt32(ctx[CpuRegister.Rdi], 0x08000000) ? Ok(ctx) : Fault(ctx));

    [SysAbiExport(Nid = "Mv1zUObHvXI", ExportName = "sceKernelGetSystemSwVersion", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetSystemSwVersion(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return Ok(ctx);
        Span<byte> version = stackalloc byte[32];
        version.Clear();
        "8.000.000"u8.CopyTo(version);
        BinaryPrimitives.WriteUInt32LittleEndian(version[28..], 0x08000000);
        return ctx.Memory.TryWrite(address, version) ? Ok(ctx) : Fault(ctx);
    }

    [SysAbiExport(Nid = "G-MYv5erXaU", ExportName = "sceKernelGetAppInfo", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetAppInfo(CpuContext ctx) => ctx[CpuRegister.Rsi] == 0 ? Invalid(ctx) : (TryClear(ctx, ctx[CpuRegister.Rsi], 0x100) ? Ok(ctx) : Fault(ctx));

    [SysAbiExport(Nid = "VOx8NGmHXTs", ExportName = "sceKernelGetCpumode", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetCpumode(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "g0VTBxfJyu0", ExportName = "sceKernelGetCurrentCpu", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetCurrentCpu(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "0vTn5IDMU9A", ExportName = "sceKernelGetMainSocId", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetMainSocId(CpuContext ctx) { ctx[CpuRegister.Rax] = 0x900001; return 0; }
    [SysAbiExport(Nid = "+g+UP8Pyfmo", ExportName = "sceKernelGetProcessType", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetProcessType(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "rNRtm1uioyY", ExportName = "sceKernelHasNeoMode", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelHasNeoMode(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "xeu-pV8wkKs", ExportName = "sceKernelIsInSandbox", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelIsInSandbox(CpuContext ctx) { ctx[CpuRegister.Rax] = 1; return 0; }

    [SysAbiExport(Nid = "JGfTMBOdUJo", ExportName = "sceKernelGetFsSandboxRandomWord", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGetFsSandboxRandomWord(CpuContext ctx)
    {
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, 4, 4, out var address) || !ctx.Memory.TryWrite(address, "sys\0"u8))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    [SysAbiExport(Nid = "1yca4VvfcNA", ExportName = "sceKernelTitleWorkaroundIsEnabled", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelTitleWorkaroundIsEnabled(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rdx], 0);

    [SysAbiExport(Nid = "kOcnerypnQA", ExportName = "sceKernelGettimezone", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelGettimezone(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return Invalid(ctx);
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now);
        if (!ctx.TryWriteInt32(address, unchecked((int)-offset.TotalMinutes)) || !ctx.TryWriteInt32(address + 4, 0)) return Fault(ctx);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "9JYNqN6jAKI", ExportName = "sceKernelDebugOutText", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelDebugOutText(CpuContext ctx)
    {
        var textAddress = ctx[CpuRegister.Rsi];
        if (textAddress != 0 && ctx.TryReadNullTerminatedUtf8(textAddress, 4096, out var message))
        {
            Console.Error.Write(message);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "D4yla3vx4tY", ExportName = "sceKernelError", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelError(CpuContext ctx)
    {
        var error = unchecked((int)ctx[CpuRegister.Rdi]);
        var result = error == 0 ? 0 : unchecked((int)0x80020000) + error;
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    [SysAbiExport(Nid = "LwG8g3niqwA", ExportName = "sceKernelDlsym", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelDlsym(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdx];
        if (outAddress == 0) return Invalid(ctx);
        if (!ctx.TryWriteUInt64(outAddress, 0)) return Fault(ctx);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
    }

    [SysAbiExport(Nid = "VW3TVZiM4-E", ExportName = "sceKernelFtruncate", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelFtruncate(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ih4CD9-gghM", ExportName = "posix_ftruncate", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixFtruncate(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "2G6i6hMIUUY", ExportName = "posix_getdents", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixGetdents(CpuContext ctx) => KernelMemoryCompatExports.KernelGetdents(ctx);
    [SysAbiExport(Nid = "juWbTNM+8hw", ExportName = "posix_fsync", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixFsync(CpuContext ctx) => KernelMemoryCompatExports.KernelFsync(ctx);
    [SysAbiExport(Nid = "JGMio+21L4c", ExportName = "posix_mkdir", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixMkdir(CpuContext ctx) => KernelMemoryCompatExports.KernelMkdir(ctx);
    [SysAbiExport(Nid = "c7ZnT7V1B98", ExportName = "posix_rmdir", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixRmdir(CpuContext ctx) => KernelMemoryCompatExports.KernelRmdir(ctx);
    [SysAbiExport(Nid = "VAzswvTOCzI", ExportName = "posix_unlink", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixUnlink(CpuContext ctx) => KernelMemoryCompatExports.KernelUnlink(ctx);
    [SysAbiExport(Nid = "52NcYU9+lEo", ExportName = "sceKernelRename", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelRename(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "NN01qLRhiqU", ExportName = "posix_rename", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixRename(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "uWyW3v98sU4", ExportName = "sceKernelCheckReachability", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelCheckReachability(CpuContext ctx) => ctx[CpuRegister.Rdi] == 0 ? Invalid(ctx) : Ok(ctx);

    [SysAbiExport(Nid = "ezv-RSBNKqI", ExportName = "posix_pread", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPread(CpuContext ctx) => KernelMemoryCompatExports.KernelPread(ctx);
    [SysAbiExport(Nid = "yTj62I7kw4s", ExportName = "sceKernelPreadv", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelPreadv(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Pread);
    [SysAbiExport(Nid = "C2kJ-byS5rM", ExportName = "posix_pwrite", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPwrite(CpuContext ctx) => PwriteCore(ctx);
    [SysAbiExport(Nid = "nKWi-N2HBV4", ExportName = "sceKernelPwrite", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelPwrite(CpuContext ctx) => PwriteCore(ctx);
    [SysAbiExport(Nid = "FCcmRZhWtOk", ExportName = "posix_pwritev", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPwritev(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Pwrite);
    [SysAbiExport(Nid = "mBd4AfLP+u8", ExportName = "sceKernelPwritev", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelPwritev(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Pwrite);
    [SysAbiExport(Nid = "+WRlkKjZvag", ExportName = "readv", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Readv(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Read);
    [SysAbiExport(Nid = "R74tt43xP6k", ExportName = "sceKernelAddHRTimerEvent", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelAddHRTimerEvent(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "kAt6VDbHmro", ExportName = "sceKernelWritev", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelWritev(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Write);
    [SysAbiExport(Nid = "YSHRBRLn2pI", ExportName = "writev", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int Writev(CpuContext ctx) => VectorIo(ctx, VectorIoKind.Write);

    private enum VectorIoKind { Read, Pread, Write, Pwrite }

    private static int VectorIo(CpuContext ctx, VectorIoKind kind)
    {
        var fd = ctx[CpuRegister.Rdi];
        var iovAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (count < 0 || count > 1024 || (count != 0 && iovAddress == 0)) return Invalid(ctx);
        long total = 0;
        for (var index = 0; index < count; index++)
        {
            var entry = iovAddress + (ulong)(index * 16);
            if (!ctx.TryReadUInt64(entry, out var buffer) || !ctx.TryReadUInt64(entry + 8, out var length)) return Fault(ctx);
            var savedRsi = ctx[CpuRegister.Rsi];
            var savedRdx = ctx[CpuRegister.Rdx];
            var savedRcx = ctx[CpuRegister.Rcx];
            ctx[CpuRegister.Rdi] = fd;
            ctx[CpuRegister.Rsi] = buffer;
            ctx[CpuRegister.Rdx] = length;
            ctx[CpuRegister.Rcx] = unchecked((ulong)(offset + total));
            var result = kind switch
            {
                VectorIoKind.Read => KernelMemoryCompatExports.KernelRead(ctx),
                VectorIoKind.Pread => KernelMemoryCompatExports.KernelPread(ctx),
                VectorIoKind.Write => KernelMemoryCompatExports.KernelWrite(ctx),
                _ => PwriteCore(ctx),
            };
            ctx[CpuRegister.Rsi] = savedRsi;
            ctx[CpuRegister.Rdx] = savedRdx;
            ctx[CpuRegister.Rcx] = savedRcx;
            if (result != 0) return result;
            total += unchecked((long)ctx[CpuRegister.Rax]);
            if (ctx[CpuRegister.Rax] < length) break;
        }
        ctx[CpuRegister.Rax] = unchecked((ulong)total);
        return 0;
    }

    private static int PwriteCore(CpuContext ctx)
    {
        var fd = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var length = ctx[CpuRegister.Rdx];
        var offset = ctx[CpuRegister.Rcx];
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 1;
        var seekResult = KernelMemoryCompatExports.KernelLseek(ctx);
        if (seekResult != 0) return seekResult;
        var original = ctx[CpuRegister.Rax];
        ctx[CpuRegister.Rdi] = fd;
        ctx[CpuRegister.Rsi] = offset;
        ctx[CpuRegister.Rdx] = 0;
        seekResult = KernelMemoryCompatExports.KernelLseek(ctx);
        if (seekResult != 0) return seekResult;
        ctx[CpuRegister.Rdi] = fd;
        ctx[CpuRegister.Rsi] = buffer;
        ctx[CpuRegister.Rdx] = length;
        var writeResult = KernelMemoryCompatExports.KernelWrite(ctx);
        var written = ctx[CpuRegister.Rax];
        ctx[CpuRegister.Rdi] = fd;
        ctx[CpuRegister.Rsi] = original;
        ctx[CpuRegister.Rdx] = 0;
        _ = KernelMemoryCompatExports.KernelLseek(ctx);
        ctx[CpuRegister.Rax] = written;
        return writeResult;
    }

    [SysAbiExport(Nid = "iWsFlYMf3Kw", ExportName = "posix_pthread_cleanup_pop", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPthreadCleanupPop(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "RVxb0Ssa5t0", ExportName = "posix_pthread_cleanup_pop", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPthreadCleanupPop2(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "4ZeZWcMsAV0", ExportName = "posix_pthread_cleanup_push", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPthreadCleanupPush(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "Jb2uGFMr688", ExportName = "posix_pthread_getaffinity_np", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPthreadGetaffinity(CpuContext ctx)
    {
        var outMask = ctx[CpuRegister.Rdx];
        if (ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] < 8 || outMask == 0) return Invalid(ctx);
        return CallWithRsi(ctx, outMask, KernelPthreadExtendedCompatExports.PthreadGetaffinity);
    }

    [SysAbiExport(Nid = "5KWrg7-ZqvE", ExportName = "posix_pthread_setaffinity_np", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPthreadSetaffinity(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] < 8 || ctx[CpuRegister.Rdx] == 0 || !ctx.TryReadUInt64(ctx[CpuRegister.Rdx], out var mask)) return Invalid(ctx);
        return CallWithRsi(ctx, mask, KernelPthreadExtendedCompatExports.PthreadSetaffinity);
    }

    [SysAbiExport(Nid = "oxMp8uPqa+U", ExportName = "posix_pthread_set_name_np", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPthreadSetName(CpuContext ctx) => KernelPthreadCompatExports.PthreadRename(ctx);
    [SysAbiExport(Nid = "yH-uQW3LbX0", ExportName = "posix_pthread_kill", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixPthreadKill(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "lb8lnYo-o7k", ExportName = "posix_pthread_rwlock_timedrdlock", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockTimedRead(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "9zklzAl9CGM", ExportName = "posix_pthread_rwlock_timedwrlock", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockTimedWrite(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "SFxTMOfuCkE", ExportName = "posix_pthread_rwlock_tryrdlock", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockTryRead(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "XhWHn6P5R7U", ExportName = "posix_pthread_rwlock_trywrlock", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockTryWrite(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "xFebsA4YsFI", ExportName = "posix_pthread_rwlockattr_init", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrInit(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return Invalid(ctx);
        _rwlockAttributes[address] = new RwlockAttributeState();
        return KernelPthreadExtendedCompatExports.PthreadRwlockattrInit(ctx);
    }
    [SysAbiExport(Nid = "qsdmgXjqSgk", ExportName = "posix_pthread_rwlockattr_destroy", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrDestroy(CpuContext ctx)
    {
        _rwlockAttributes.TryRemove(ctx[CpuRegister.Rdi], out _);
        return KernelPthreadExtendedCompatExports.PthreadRwlockattrDestroy(ctx);
    }
    [SysAbiExport(Nid = "VqEMuCv-qHY", ExportName = "posix_pthread_rwlockattr_getpshared", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrGetpshared(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _rwlockAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared);
    [SysAbiExport(Nid = "l+bG5fsYkhg", ExportName = "posix_pthread_rwlockattr_gettype_np", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrGettype(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _rwlockAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Type);
    [SysAbiExport(Nid = "OuKg+kRDD7U", ExportName = "posix_pthread_rwlockattr_setpshared", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrSetpshared(CpuContext ctx) { _rwlockAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "8NuOHiTr1Vw", ExportName = "posix_pthread_rwlockattr_settype_np", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixRwlockAttrSettype(CpuContext ctx) { _rwlockAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Type = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }

    [SysAbiExport(Nid = "KiJEPEWRyUY", ExportName = "posix_sigaction", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSigaction(CpuContext ctx)
    {
        var signal = ctx[CpuRegister.Rdi];
        var action = ctx[CpuRegister.Rsi];
        var oldAction = ctx[CpuRegister.Rdx];
        _signalHandlers.TryGetValue(signal, out var oldHandler);
        if (oldAction != 0)
        {
            Span<byte> old = stackalloc byte[32];
            old.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(old, oldHandler);
            if (!ctx.Memory.TryWrite(oldAction, old)) return PosixFailure(ctx, 14);
        }
        if (action != 0)
        {
            if (!ctx.TryReadUInt64(action, out var handler)) return PosixFailure(ctx, 14);
            _signalHandlers[signal] = handler;
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "JUimFtKe0Kc", ExportName = "posix_sigaddset", LibraryName = "libkernel", Target = Generation.Gen5)] public static int PosixSigaddset(CpuContext ctx) => SignalSetBit(ctx, true);
    [SysAbiExport(Nid = "Nd-u09VFSCA", ExportName = "posix_sigdelset", LibraryName = "libkernel", Target = Generation.Gen5)] public static int PosixSigdelset(CpuContext ctx) => SignalSetBit(ctx, false);
    [SysAbiExport(Nid = "+F7C-hdk7+E", ExportName = "posix_sigemptyset", LibraryName = "libkernel", Target = Generation.Gen5)] public static int PosixSigemptyset(CpuContext ctx) => ctx[CpuRegister.Rdi] != 0 && TryClear(ctx, ctx[CpuRegister.Rdi], 16) ? Ok(ctx) : PosixFailure(ctx, PosixEinval);
    [SysAbiExport(Nid = "VkTAsrZDcJ0", ExportName = "posix_sigfillset", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSigfillset(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0) return PosixFailure(ctx, PosixEinval);
        Span<byte> bits = stackalloc byte[16]; bits.Fill(0xFF);
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], bits) ? Ok(ctx) : PosixFailure(ctx, 14);
    }
    [SysAbiExport(Nid = "JnNl8Xr-z4Y", ExportName = "posix_sigismember", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSigismember(CpuContext ctx)
    {
        var signal = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdi] == 0 || signal < 1 || signal > 128) return PosixFailure(ctx, PosixEinval);
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rdi] + (ulong)((signal - 1) / 64 * 8), out var bits)) return PosixFailure(ctx, 14);
        ctx[CpuRegister.Rax] = (bits & (1UL << ((signal - 1) & 63))) != 0 ? 1UL : 0UL;
        return 0;
    }
    [SysAbiExport(Nid = "sHziAegVp74", ExportName = "posix_sigalstack", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSigalstack(CpuContext ctx) => ctx[CpuRegister.Rsi] == 0 || TryClear(ctx, ctx[CpuRegister.Rsi], 24) ? Ok(ctx) : PosixFailure(ctx, 14);
    [SysAbiExport(Nid = "aPcyptbOiZs", ExportName = "posix_sigprocmask", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSigprocmask(CpuContext ctx) => ctx[CpuRegister.Rdx] == 0 || TryClear(ctx, ctx[CpuRegister.Rdx], 16) ? Ok(ctx) : PosixFailure(ctx, 14);
    [SysAbiExport(Nid = "VADc3MNQ3cM", ExportName = "posix_signal", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PosixSignal(CpuContext ctx)
    {
        var signal = ctx[CpuRegister.Rdi];
        _signalHandlers.TryGetValue(signal, out var old);
        _signalHandlers[signal] = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = old;
        return 0;
    }

    private static int SignalSetBit(CpuContext ctx, bool set)
    {
        var signal = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdi] == 0 || signal < 1 || signal > 128) return PosixFailure(ctx, PosixEinval);
        var address = ctx[CpuRegister.Rdi] + (ulong)((signal - 1) / 64 * 8);
        if (!ctx.TryReadUInt64(address, out var bits)) bits = 0;
        var mask = 1UL << ((signal - 1) & 63);
        bits = set ? bits | mask : bits & ~mask;
        return ctx.TryWriteUInt64(address, bits) ? Ok(ctx) : PosixFailure(ctx, 14);
    }

    [SysAbiExport(Nid = "GEnUkDZoUwY", ExportName = "scePthreadSemInit", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemInit(CpuContext ctx)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        if (semAddress == 0 || ctx[CpuRegister.Rsi] != 0) return Invalid(ctx);
        var handle = SyntheticSemaphoreBase + unchecked((ulong)Interlocked.Increment(ref _nextSemaphoreId));
        if (!ctx.TryWriteUInt64(semAddress, handle)) return Fault(ctx);
        var savedRdx = ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rdx] = savedRdx;
        return KernelPosixSemExports.SemInit(ctx);
    }
    [SysAbiExport(Nid = "Vwc+L05e6oE", ExportName = "scePthreadSemDestroy", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemDestroy(CpuContext ctx) { var result = KernelPosixSemExports.SemDestroy(ctx); _ = ctx.TryWriteUInt64(ctx[CpuRegister.Rdi], 0); return result; }
    [SysAbiExport(Nid = "DjpBvGlaWbQ", ExportName = "scePthreadSemGetvalue", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemGetvalue(CpuContext ctx) => KernelPosixSemExports.SemGetvalue(ctx);
    [SysAbiExport(Nid = "aishVAiFaYM", ExportName = "scePthreadSemPost", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemPost(CpuContext ctx) => KernelPosixSemExports.SemPost(ctx);
    [SysAbiExport(Nid = "H2a+IN9TP0E", ExportName = "scePthreadSemTrywait", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemTrywait(CpuContext ctx) => KernelPosixSemExports.SemTrywait(ctx);
    [SysAbiExport(Nid = "fjN6NQHhK8k", ExportName = "scePthreadSemTimedwait", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemTimedwait(CpuContext ctx) => KernelPosixSemExports.SemTrywait(ctx);
    [SysAbiExport(Nid = "C36iRE0F5sE", ExportName = "scePthreadSemWait", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int PthreadSemWait(CpuContext ctx) => KernelPosixSemExports.SemTrywait(ctx);

    [SysAbiExport(Nid = "Ucsu-OK+els", ExportName = "posix_pthread_attr_get_np", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrGetNp(CpuContext ctx)
    {
        var outAttributeAddress = ctx[CpuRegister.Rsi];
        if (ctx[CpuRegister.Rdi] == 0 || outAttributeAddress == 0) return Invalid(ctx);
        var savedRdi = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rdi] = outAttributeAddress;
        try { return KernelPthreadExtendedCompatExports.PthreadAttrInit(ctx); }
        finally { ctx[CpuRegister.Rdi] = savedRdi; }
    }
    [SysAbiExport(Nid = "-wzZ7dvA7UU", ExportName = "posix_pthread_attr_getaffinity_np", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrGetaffinity(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] < 8 || ctx[CpuRegister.Rdx] == 0) return Invalid(ctx);
        return CallWithRsi(ctx, ctx[CpuRegister.Rdx], KernelPthreadExtendedCompatExports.PthreadAttrGetaffinity);
    }
    [SysAbiExport(Nid = "VUT1ZSrHT0I", ExportName = "posix_pthread_attr_getdetachstate", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetdetachstate(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetdetachstate(ctx);
    [SysAbiExport(Nid = "JNkVVsVDmOk", ExportName = "posix_pthread_attr_getguardsize", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetguardsize(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetguardsize(ctx);
    [SysAbiExport(Nid = "oLjPqUKhzes", ExportName = "posix_pthread_attr_getinheritsched", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetinheritsched(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).InheritSched);
    [SysAbiExport(Nid = "qlk9pSLsUmM", ExportName = "posix_pthread_attr_getschedparam", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetschedparam(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetschedparam(ctx);
    [SysAbiExport(Nid = "RtLRV-pBTTY", ExportName = "posix_pthread_attr_getschedpolicy", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetschedpolicy(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Policy);
    [SysAbiExport(Nid = "e2G+cdEkOmU", ExportName = "posix_pthread_attr_getscope", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetscope(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Scope);
    [SysAbiExport(Nid = "vQm4fDEsWi8", ExportName = "posix_pthread_attr_getstack", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetstack(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetstack(ctx);
    [SysAbiExport(Nid = "DxmIMUQ-wXY", ExportName = "posix_pthread_attr_getstackaddr", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetstackaddr(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetstackaddr(ctx);
    [SysAbiExport(Nid = "0qOtCR-ZHck", ExportName = "posix_pthread_attr_getstacksize", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrGetstacksize(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrGetstacksize(ctx);

    [SysAbiExport(Nid = "o8pd4juNbgc", ExportName = "posix_pthread_attr_setaffinity_np", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetaffinity(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] < 8 || ctx[CpuRegister.Rdx] == 0 || !ctx.TryReadUInt64(ctx[CpuRegister.Rdx], out var mask)) return Invalid(ctx);
        return CallWithRsi(ctx, mask, KernelPthreadExtendedCompatExports.PthreadAttrSetaffinity);
    }
    [SysAbiExport(Nid = "Q2y5IqSDZGs", ExportName = "posix_pthread_attr_setcreatesuspend_np", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetcreatesuspend(CpuContext ctx) { _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).CreateSuspended = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "E+tyo3lp5Lw", ExportName = "posix_pthread_attr_setdetachstate", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrSetdetachstate(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrSetdetachstate(ctx);
    [SysAbiExport(Nid = "JKyG3SWyA10", ExportName = "posix_pthread_attr_setguardsize", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrSetguardsize(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrSetguardsize(ctx);
    [SysAbiExport(Nid = "7ZlAakEf0Qg", ExportName = "posix_pthread_attr_setinheritsched", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetinheritsched(CpuContext ctx) { _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).InheritSched = unchecked((int)ctx[CpuRegister.Rsi]); return KernelPthreadExtendedCompatExports.PthreadAttrSetinheritsched(ctx); }
    [SysAbiExport(Nid = "euKRgm0Vn2M", ExportName = "posix_pthread_attr_setschedparam", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrSetschedparam(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrSetschedparam(ctx);
    [SysAbiExport(Nid = "JarMIy8kKEY", ExportName = "posix_pthread_attr_setschedpolicy", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetschedpolicy(CpuContext ctx) { _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Policy = unchecked((int)ctx[CpuRegister.Rsi]); return KernelPthreadExtendedCompatExports.PthreadAttrSetschedpolicy(ctx); }
    [SysAbiExport(Nid = "xesmlSI-KCI", ExportName = "posix_pthread_attr_setscope", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetscope(CpuContext ctx) { _attributeExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Scope = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "-SrbXpGR1f0", ExportName = "posix_pthread_attr_setstack", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixAttrSetstack(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadAttrSetstack(ctx);
    [SysAbiExport(Nid = "suCrEbr0xIQ", ExportName = "posix_pthread_attr_setstackaddr", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixAttrSetstackaddr(CpuContext ctx)
    {
        var savedRdx = ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rdx] = 0x100000;
        try { return KernelPthreadExtendedCompatExports.PthreadAttrSetstack(ctx); }
        finally { ctx[CpuRegister.Rdx] = savedRdx; }
    }

    [SysAbiExport(Nid = "dJcuQVn6-Iw", ExportName = "posix_pthread_condattr_destroy", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixCondattrDestroy(CpuContext ctx) { _condAttributes.TryRemove(ctx[CpuRegister.Rdi], out _); return KernelPthreadCompatExports.PthreadCondattrDestroy(ctx); }
    [SysAbiExport(Nid = "cTDYxTUNPhM", ExportName = "posix_pthread_condattr_getclock", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixCondattrGetclock(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _condAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ClockId);
    [SysAbiExport(Nid = "h0qUqSuOmC8", ExportName = "posix_pthread_condattr_getpshared", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixCondattrGetpshared(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _condAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared);
    [SysAbiExport(Nid = "mKoTx03HRWA", ExportName = "posix_pthread_condattr_init", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixCondattrInit(CpuContext ctx) { _condAttributes[ctx[CpuRegister.Rdi]] = new(); return KernelPthreadCompatExports.PthreadCondattrInit(ctx); }
    [SysAbiExport(Nid = "EjllaAqAPZo", ExportName = "posix_pthread_condattr_setclock", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixCondattrSetclock(CpuContext ctx) { _condAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ClockId = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "3BpP850hBT4", ExportName = "posix_pthread_condattr_setpshared", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixCondattrSetpshared(CpuContext ctx) { _condAttributes.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "K953PF5u6Pc", ExportName = "posix_pthread_cond_reltimedwait_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixCondReltimedwait(CpuContext ctx) => ctx.SetReturn(60);
    [SysAbiExport(Nid = "CI6Qy73ae10", ExportName = "posix_pthread_cond_signalto_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixCondSignalto(CpuContext ctx) => KernelPthreadCompatExports.PthreadCondSignal(ctx);

    [SysAbiExport(Nid = "FIs3-UQT9sg", ExportName = "posix_pthread_getschedparam", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixPthreadGetschedparam(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadGetschedparam(ctx);
    [SysAbiExport(Nid = "9vyP6Z7bqzc", ExportName = "posix_pthread_rename_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixPthreadRename(CpuContext ctx) => KernelPthreadCompatExports.PthreadRename(ctx);
    [SysAbiExport(Nid = "lZzFeSxPl08", ExportName = "posix_pthread_setcancelstate", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPthreadSetcancelstate(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteInt32(ctx[CpuRegister.Rsi], 0)) return Fault(ctx);
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "a2P9wYGeZvc", ExportName = "posix_pthread_setprio", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixPthreadSetprio(CpuContext ctx) => KernelPthreadExtendedCompatExports.PthreadSetprio(ctx);
    [SysAbiExport(Nid = "Z4QosVuAsA0", ExportName = "posix_pthread_once", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixPthreadOnce(CpuContext ctx)
    {
        var control = ctx[CpuRegister.Rdi];
        if (control == 0 || ctx[CpuRegister.Rsi] == 0) return Invalid(ctx);
        if (_onceControls.TryAdd(control, 1)) _ = ctx.TryWriteInt32(control, 2);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "x4vQj3JKKmc", ExportName = "posix_pthread_mutex_getspinloops_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexGetspinloops(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).SpinLoops);
    [SysAbiExport(Nid = "OxEIUqkByy4", ExportName = "posix_pthread_mutex_getyieldloops_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexGetyieldloops(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).YieldLoops);
    [SysAbiExport(Nid = "gKqzW-zWhvY", ExportName = "posix_pthread_mutex_isowned_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexIsowned(CpuContext ctx) { ctx[CpuRegister.Rax] = 0; return 0; }
    [SysAbiExport(Nid = "5-ncLMtL5+g", ExportName = "posix_pthread_mutex_setspinloops_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexSetspinloops(CpuContext ctx) { _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).SpinLoops = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "frFuGprJmPc", ExportName = "posix_pthread_mutex_setyieldloops_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexSetyieldloops(CpuContext ctx) { _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).YieldLoops = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "Io9+nTKXZtA", ExportName = "posix_pthread_mutex_timedlock", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexTimedlock(CpuContext ctx) => KernelPthreadCompatExports.PosixPthreadMutexTrylock(ctx);

    [SysAbiExport(Nid = "U6SNV+RnyLQ", ExportName = "posix_pthread_mutexattr_getkind_np", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrGetkind(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Kind);
    [SysAbiExport(Nid = "+m8+quqOwhM", ExportName = "posix_pthread_mutexattr_getprioceiling", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrGetprioceiling(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).PriorityCeiling);
    [SysAbiExport(Nid = "yDaWxUE50s0", ExportName = "posix_pthread_mutexattr_getprotocol", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrGetprotocol(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Protocol);
    [SysAbiExport(Nid = "PmL-TwKUzXI", ExportName = "posix_pthread_mutexattr_getpshared", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrGetpshared(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared);
    [SysAbiExport(Nid = "GZFlI7RhuQo", ExportName = "posix_pthread_mutexattr_gettype", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrGettype(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).Type);
    [SysAbiExport(Nid = "J9rlRuQ8H5s", ExportName = "posix_pthread_mutexattr_setkind_np", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixMutexattrSetkind(CpuContext ctx)
    {
        var state = _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new());
        state.Kind = unchecked((int)ctx[CpuRegister.Rsi]);
        state.Type = state.Kind;
        return KernelPthreadCompatExports.PosixPthreadMutexattrSettype(ctx);
    }
    [SysAbiExport(Nid = "ZLvf6lVAc4M", ExportName = "posix_pthread_mutexattr_setprioceiling", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrSetprioceiling(CpuContext ctx) { _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).PriorityCeiling = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }
    [SysAbiExport(Nid = "EXv3ztGqtDM", ExportName = "posix_pthread_mutexattr_setpshared", LibraryName = "libScePosix", Target = Generation.Gen5)] public static int PosixMutexattrSetpshared(CpuContext ctx) { _mutexExtensions.GetOrAdd(ctx[CpuRegister.Rdi], _ => new()).ProcessShared = unchecked((int)ctx[CpuRegister.Rsi]); return Ok(ctx); }

    [SysAbiExport(Nid = "CBNtXOoef-E", ExportName = "posix_sched_get_priority_max", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixSchedPriorityMax(CpuContext ctx) { var policy = unchecked((int)ctx[CpuRegister.Rdi]); ctx[CpuRegister.Rax] = policy is 1 or 2 ? 256UL : unchecked((ulong)PosixEinval); return 0; }
    [SysAbiExport(Nid = "m0iS6jNsXds", ExportName = "posix_sched_get_priority_min", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixSchedPriorityMin(CpuContext ctx) { var policy = unchecked((int)ctx[CpuRegister.Rdi]); ctx[CpuRegister.Rax] = policy is 1 or 2 ? 767UL : unchecked((ulong)PosixEinval); return 0; }
    [SysAbiExport(Nid = "6XG4B33N09g", ExportName = "sched_yield", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int SchedYield(CpuContext ctx) { Thread.Yield(); return Ok(ctx); }

    [SysAbiExport(Nid = "nh2IFMgKTv8", ExportName = "posix_kqueue", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixKqueue(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextKqueue);
        _kqueues[handle] = 0;
        ctx[CpuRegister.Rax] = unchecked((uint)handle);
        return 0;
    }

    [SysAbiExport(Nid = "RW-GEfpnsqg", ExportName = "posix_kevent", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixKevent(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var eventList = ctx[CpuRegister.Rcx];
        var eventCount = ctx[CpuRegister.R8];
        if (!_kqueues.ContainsKey(handle)) return PosixFailure(ctx, 9);
        if (eventList != 0 && eventCount != 0 && !TryClear(ctx, eventList, Math.Min(eventCount, 1024UL) * EventSize)) return PosixFailure(ctx, 14);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(Nid = "57ZK+ODEXWY", ExportName = "sceKernelAddTimerEvent", LibraryName = "libkernel", Target = Generation.Gen5)] public static int KernelAddTimerEvent(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "J+LF6LwObXU", ExportName = "sceKernelDeleteHRTimerEvent", LibraryName = "libkernel", Target = Generation.Gen5)] public static int KernelDeleteHRTimerEvent(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "YWQFUyXIVdU", ExportName = "sceKernelDeleteTimerEvent", LibraryName = "libkernel", Target = Generation.Gen5)] public static int KernelDeleteTimerEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "1vDaenmJtyA", ExportName = "sceKernelOpenEventFlag", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelOpenEventFlag(CpuContext ctx)
    {
        var savedRdx = ctx[CpuRegister.Rdx];
        var savedRcx = ctx[CpuRegister.Rcx];
        var savedR8 = ctx[CpuRegister.R8];
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        try { return KernelEventFlagCompatExports.KernelCreateEventFlag(ctx); }
        finally { ctx[CpuRegister.Rdx] = savedRdx; ctx[CpuRegister.Rcx] = savedRcx; ctx[CpuRegister.R8] = savedR8; }
    }
    [SysAbiExport(Nid = "s9-RaxukuzQ", ExportName = "sceKernelCloseEventFlag", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int KernelCloseEventFlag(CpuContext ctx) => KernelEventFlagCompatExports.KernelDeleteEventFlag(ctx);

    [SysAbiExport(Nid = "9WK-vhNXimw", ExportName = "sceKernelAioSetParam", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioSetParam(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "HgX7+AORI58", ExportName = "sceKernelAioSubmitReadCommands", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioSubmitRead(CpuContext ctx) => AioSubmit(ctx, multiple: false);
    [SysAbiExport(Nid = "lXT0m3P-vs4", ExportName = "sceKernelAioSubmitReadCommandsMultiple", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioSubmitReadMultiple(CpuContext ctx) => AioSubmit(ctx, multiple: true);
    [SysAbiExport(Nid = "XQ8C8y+de+E", ExportName = "sceKernelAioSubmitWriteCommands", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioSubmitWrite(CpuContext ctx) => AioSubmit(ctx, multiple: false);
    [SysAbiExport(Nid = "xT3Cpz0yh6Y", ExportName = "sceKernelAioSubmitWriteCommandsMultiple", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioSubmitWriteMultiple(CpuContext ctx) => AioSubmit(ctx, multiple: true);

    private static int AioSubmit(CpuContext ctx, bool multiple)
    {
        var requests = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var idsAddress = ctx[CpuRegister.Rcx];
        if (requests == 0 || idsAddress == 0 || count < 0 || count > 512) return Fault(ctx);
        var firstId = 0;
        for (var index = 0; index < count; index++)
        {
            var id = Interlocked.Increment(ref _nextAioId);
            if (firstId == 0) firstId = id;
            _aioStates[id] = AioCompleted;
            var requestAddress = requests + (ulong)(index * AioRequestSize);
            if (ctx.TryReadUInt64(requestAddress + 24, out var resultAddress) && resultAddress != 0)
            {
                if (!ctx.TryWriteUInt64(resultAddress, 0) || !ctx.TryWriteUInt32(resultAddress + 8, AioCompleted)) return Fault(ctx);
            }
            if (multiple && !ctx.TryWriteInt32(idsAddress + (ulong)(index * sizeof(int)), id)) return Fault(ctx);
        }
        if (!multiple && !ctx.TryWriteInt32(idsAddress, firstId)) return Fault(ctx);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "2pOuoWoCxdk", ExportName = "sceKernelAioPollRequest", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int AioPollRequest(CpuContext ctx) => WriteInt32(ctx, ctx[CpuRegister.Rsi], _aioStates.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var state) ? state : AioAborted);
    [SysAbiExport(Nid = "o7O4z3jwKzo", ExportName = "sceKernelAioPollRequests", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int AioPollRequests(CpuContext ctx) => AioArrayOperation(ctx, abort: false, remove: false);
    [SysAbiExport(Nid = "fR521KIGgb8", ExportName = "sceKernelAioCancelRequest", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int AioCancelRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (id != 0) _aioStates[id] = AioAborted;
        return WriteInt32(ctx, ctx[CpuRegister.Rsi], id == 0 ? 2 : AioAborted);
    }
    [SysAbiExport(Nid = "3Lca1XBrQdY", ExportName = "sceKernelAioCancelRequests", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioCancelRequests(CpuContext ctx) => AioArrayOperation(ctx, abort: true, remove: false);
    [SysAbiExport(Nid = "5TgME6AYty4", ExportName = "sceKernelAioDeleteRequest", LibraryName = "libkernel", Target = Generation.Gen5)]
    public static int AioDeleteRequest(CpuContext ctx)
    {
        _aioStates.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _);
        return WriteInt32(ctx, ctx[CpuRegister.Rsi], 0);
    }
    [SysAbiExport(Nid = "Ft3EtsZzAoY", ExportName = "sceKernelAioDeleteRequests", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioDeleteRequests(CpuContext ctx) => AioArrayOperation(ctx, abort: true, remove: true);
    [SysAbiExport(Nid = "KOF-oJbQVvc", ExportName = "sceKernelAioWaitRequest", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioWaitRequest(CpuContext ctx) => AioPollRequest(ctx);
    [SysAbiExport(Nid = "lgK+oIWkJyA", ExportName = "sceKernelAioWaitRequests", LibraryName = "libkernel", Target = Generation.Gen5)] public static int AioWaitRequests(CpuContext ctx) => AioArrayOperation(ctx, abort: false, remove: false);

    private static int AioArrayOperation(CpuContext ctx, bool abort, bool remove)
    {
        var idsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var statesAddress = ctx[CpuRegister.Rdx];
        if (idsAddress == 0 || statesAddress == 0 || count < 0 || count > 512) return Fault(ctx);
        for (var index = 0; index < count; index++)
        {
            if (!ctx.TryReadInt32(idsAddress + (ulong)(index * sizeof(int)), out var id)) return Fault(ctx);
            var state = _aioStates.TryGetValue(id, out var knownState) ? knownState : AioAborted;
            if (remove)
            {
                state = 0;
                _aioStates.TryRemove(id, out _);
            }
            else if (abort)
            {
                state = AioAborted;
                _aioStates[id] = state;
            }
            if (!ctx.TryWriteInt32(statesAddress + (ulong)(index * sizeof(int)), state)) return Fault(ctx);
        }
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "T8fER+tIGgk", ExportName = "posix_select", LibraryName = "libScePosix", Target = Generation.Gen5)]
    public static int PosixSelect(CpuContext ctx)
    {
        var nfds = unchecked((int)ctx[CpuRegister.Rdi]);
        if (nfds < 0) return PosixFailure(ctx, PosixEinval);
        var bytes = (ulong)Math.Max(0, (nfds + 63) / 64 * 8);
        foreach (var address in new[] { ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx] })
        {
            if (address != 0 && !TryClear(ctx, address, bytes)) return PosixFailure(ctx, 14);
        }
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SocketGetsockopt(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (valueAddress == 0 || lengthAddress == 0 || !ctx.TryReadUInt32(lengthAddress, out var length)) return PosixFailure(ctx, 14);
        if (!TryClear(ctx, valueAddress, Math.Min(length, 256u))) return PosixFailure(ctx, 14);
        return Ok(ctx);
    }

    private static int SocketAccept(CpuContext ctx)
    {
        var sockaddrAddress = ctx[CpuRegister.Rsi];
        var lengthAddress = ctx[CpuRegister.Rdx];
        if (sockaddrAddress != 0 && !TryClear(ctx, sockaddrAddress, 16)) return PosixFailure(ctx, 14);
        if (lengthAddress != 0 && !ctx.TryWriteUInt32(lengthAddress, 16)) return PosixFailure(ctx, 14);
        return KernelSocketCompatExports.Socket(ctx);
    }

    private static int SocketPair(CpuContext ctx)
    {
        var pairAddress = ctx[CpuRegister.Rcx];
        if (pairAddress == 0) return PosixFailure(ctx, 14);
        _ = KernelSocketCompatExports.Socket(ctx);
        var first = unchecked((int)ctx[CpuRegister.Rax]);
        _ = KernelSocketCompatExports.Socket(ctx);
        var second = unchecked((int)ctx[CpuRegister.Rax]);
        if (!ctx.TryWriteInt32(pairAddress, first) || !ctx.TryWriteInt32(pairAddress + 4, second)) return PosixFailure(ctx, 14);
        return Ok(ctx);
    }

    private static int SocketRecvfrom(CpuContext ctx)
    {
        var address = ctx[CpuRegister.R8];
        var lengthAddress = ctx[CpuRegister.R9];
        if (address != 0 && !TryClear(ctx, address, 16)) return PosixFailure(ctx, 14);
        if (lengthAddress != 0 && !ctx.TryWriteUInt32(lengthAddress, 16)) return PosixFailure(ctx, 14);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SocketGetpeername(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rsi];
        var lengthAddress = ctx[CpuRegister.Rdx];
        if (address == 0 || lengthAddress == 0) return PosixFailure(ctx, 14);
        if (!ctx.TryReadUInt32(lengthAddress, out var length)) return PosixFailure(ctx, 14);
        if (!TryClear(ctx, address, Math.Min(length, 16u)) || !ctx.TryWriteUInt32(lengthAddress, Math.Min(length, 16u))) return PosixFailure(ctx, 14);
        return Ok(ctx);
    }

    private static int SocketRecvmsg(CpuContext ctx)
    {
        var messageAddress = ctx[CpuRegister.Rsi];
        if (messageAddress == 0) return PosixFailure(ctx, 14);
        if (!ctx.TryReadUInt64(messageAddress, out var nameAddress) ||
            !ctx.TryReadUInt32(messageAddress + 8, out var nameLength) ||
            !ctx.TryReadUInt64(messageAddress + 32, out var controlAddress) ||
            !ctx.TryReadUInt32(messageAddress + 40, out var controlLength))
        {
            return PosixFailure(ctx, 14);
        }
        if (nameAddress != 0 && nameLength != 0 && !TryClear(ctx, nameAddress, Math.Min(nameLength, 256u))) return PosixFailure(ctx, 14);
        if (controlAddress != 0 && controlLength != 0 && !TryClear(ctx, controlAddress, Math.Min(controlLength, 4096u))) return PosixFailure(ctx, 14);
        if (!ctx.TryWriteUInt32(messageAddress + 8, 0) || !ctx.TryWriteUInt32(messageAddress + 40, 0) || !ctx.TryWriteInt32(messageAddress + 44, 0)) return PosixFailure(ctx, 14);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SocketInetNtop(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = ctx[CpuRegister.Rcx];
        if (unchecked((int)ctx[CpuRegister.Rdi]) != 2 || sourceAddress == 0 || destinationAddress == 0) return PosixFailure(ctx, PosixEinval);
        Span<byte> source = stackalloc byte[4];
        if (!ctx.Memory.TryRead(sourceAddress, source)) return PosixFailure(ctx, 14);
        var text = $"{source[0]}.{source[1]}.{source[2]}.{source[3]}";
        var utf8 = Encoding.UTF8.GetBytes(text + "\0");
        if ((ulong)utf8.Length > destinationSize || !ctx.Memory.TryWrite(destinationAddress, utf8)) return PosixFailure(ctx, 28);
        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }
}
