// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs;

/// <summary>
/// HLE for the sceLibcMspace* family: game-created heaps carved out of memory
/// the title donates at create time. Allocation is a first-fit-free bump
/// allocator over the donated arena — free is a no-op, so a long-lived mspace
/// only ever grows — which is enough for titles that create dedicated
/// subsystem heaps during boot and mostly allocate into them.
///
/// The mspace handle handed back to the guest is the arena base address, as
/// with the real allocator (which keeps its bookkeeping header there).
///
/// Each allocation is preceded by a 16-byte dlmalloc-style chunk header
/// (prev-foot at ptr-0x10, chunk size with in-use bits at ptr-0x08): titles
/// read the size word behind returned pointers for their own heap statistics,
/// so it must be mapped and hold a plausible masked size. Free-list
/// bookkeeping stays host-side; calloc zeroes explicitly.
/// </summary>
public static class LibcMspaceExports
{
    private const ulong DefaultAlignment = 16;
    private const ulong ChunkHeaderSize = 16;
    private const ulong ChunkInUseBits = 0x3;
    private const string LibraryName = "libSceLibcInternal";

    private sealed class MspaceArena
    {
        public MspaceArena(ulong baseAddress, ulong capacity)
        {
            BaseAddress = baseAddress;
            Capacity = capacity;
            Bump = baseAddress;
        }

        public ulong BaseAddress { get; }
        public ulong Capacity { get; }
        public ulong Bump { get; set; }
        public ulong PeakUsed { get; set; }
        public Dictionary<ulong, ulong> AllocationSizes { get; } = new();

        // Freed chunks as [start, length) spans covering header + payload,
        // reusable by later allocations. No coalescing: spans are recycled
        // whole, with any surplus split back onto the list.
        public List<(ulong Start, ulong Length)> FreeSpans { get; } = new();
        public object Gate { get; } = new();
    }

    private static readonly ConcurrentDictionary<ulong, MspaceArena> _arenas = new();

    // The first handful of mspace calls are logged unconditionally: they land
    // during heap bring-up, where a wrong pointer shows up much later as an
    // access violation inside the title's own allocator wrapper.
    private static int _traceBudget = 16;

    // Stats calls are rare and often feed title-side heap sizing decisions, so
    // they get their own budget instead of competing with per-malloc tracing.
    private static int _statsTraceBudget = 64;

    private static void Trace(string message)
    {
        if (Interlocked.Decrement(ref _traceBudget) < 0)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][INFO] mspace.{message}");
    }

    private static void TraceStats(string message)
    {
        if (Interlocked.Decrement(ref _statsTraceBudget) < 0)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][INFO] mspace.{message}");
    }

    [SysAbiExport(
        Nid = "-hn1tcVHq5Q",
        ExportName = "sceLibcMspaceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceCreate(CpuContext ctx)
    {
        // (const char* name, void* base, size_t capacity, uint flags) -> mspace
        var baseAddress = ctx[CpuRegister.Rsi];
        var capacity = ctx[CpuRegister.Rdx];
        if (baseAddress == 0 || capacity == 0 || baseAddress > ulong.MaxValue - capacity)
        {
            return ReturnPointer(ctx, 0);
        }

        _arenas[baseAddress] = new MspaceArena(baseAddress, capacity);
        TraceStats($"create base=0x{baseAddress:X} capacity=0x{capacity:X}");
        return ReturnPointer(ctx, baseAddress);
    }

    [SysAbiExport(
        Nid = "W6SiVSiCDtI",
        ExportName = "sceLibcMspaceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceDestroy(CpuContext ctx)
    {
        _arenas.TryRemove(ctx[CpuRegister.Rdi], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "OJjm-QOIHlI",
        ExportName = "sceLibcMspaceMalloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceMalloc(CpuContext ctx)
    {
        var mspace = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        var address = Allocate(ctx, mspace, size, DefaultAlignment);
        // Peek the budget before building the message: this is the hottest
        // import during level loads, and the interpolation plus the ContainsKey
        // probe would otherwise run on every call long after the budget is spent.
        if (Volatile.Read(ref _traceBudget) > 0)
        {
            Trace($"malloc mspace=0x{mspace:X} size=0x{size:X} -> 0x{address:X} known={_arenas.ContainsKey(mspace)}");
        }

        return ReturnPointer(ctx, address);
    }

    [SysAbiExport(
        Nid = "LYo3GhIlB38",
        ExportName = "sceLibcMspaceCalloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceCalloc(CpuContext ctx)
    {
        // (mspace, size_t nelem, size_t elsize)
        var count = ctx[CpuRegister.Rsi];
        var elementSize = ctx[CpuRegister.Rdx];
        if (count != 0 && elementSize > ulong.MaxValue / count)
        {
            return ReturnPointer(ctx, 0);
        }

        var total = count * elementSize;
        var address = Allocate(ctx, ctx[CpuRegister.Rdi], total, DefaultAlignment);
        if (address != 0 && !TryZeroGuestRange(ctx, address, total))
        {
            address = 0;
        }

        return ReturnPointer(ctx, address);
    }

    [SysAbiExport(
        Nid = "gigoVHZvVPE",
        ExportName = "sceLibcMspaceRealloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceRealloc(CpuContext ctx)
    {
        // (mspace, void* ptr, size_t size)
        var mspace = ctx[CpuRegister.Rdi];
        var pointer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        if (pointer == 0)
        {
            return ReturnPointer(ctx, Allocate(ctx, mspace, size, DefaultAlignment));
        }

        if (size == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var oldSize = LookupAllocationSize(pointer);
        var address = Allocate(ctx, mspace, size, DefaultAlignment);
        if (address != 0 && oldSize != 0 && !TryCopyGuestRange(ctx, pointer, address, Math.Min(oldSize, size)))
        {
            address = 0;
        }

        if (address != 0 && _arenas.TryGetValue(mspace, out var arena))
        {
            lock (arena.Gate)
            {
                if (arena.AllocationSizes.Remove(pointer, out var releasedSize))
                {
                    arena.FreeSpans.Add((pointer - ChunkHeaderSize, ChunkHeaderSize + releasedSize));
                }
            }
        }

        return ReturnPointer(ctx, address);
    }

    [SysAbiExport(
        Nid = "iF1iQHzxBJU",
        ExportName = "sceLibcMspaceMemalign",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceMemalign(CpuContext ctx)
    {
        // (mspace, size_t alignment, size_t size)
        var alignment = ctx[CpuRegister.Rsi];
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var address = Allocate(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rdx], Math.Max(alignment, DefaultAlignment));
        return ReturnPointer(ctx, address);
    }

    [SysAbiExport(
        Nid = "Vla-Z+eXlxo",
        ExportName = "sceLibcMspaceFree",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceFree(CpuContext ctx)
    {
        // (mspace, void* ptr)
        var mspace = ctx[CpuRegister.Rdi];
        var pointer = ctx[CpuRegister.Rsi];
        if (pointer != 0 && _arenas.TryGetValue(mspace, out var arena))
        {
            lock (arena.Gate)
            {
                if (arena.AllocationSizes.Remove(pointer, out var size))
                {
                    arena.FreeSpans.Add((pointer - ChunkHeaderSize, ChunkHeaderSize + size));
                }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "fEoW6BJsPt4",
        ExportName = "sceLibcMspaceMallocUsableSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceMallocUsableSize(CpuContext ctx)
    {
        // (void* ptr) -> size_t
        var pointer = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = pointer == 0 ? 0 : LookupAllocationSize(pointer);
        return 0;
    }

    [SysAbiExport(
        Nid = "mfHdJTIvhuo",
        ExportName = "sceLibcMspaceMallocStats",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceMallocStats(CpuContext ctx)
    {
        return WriteMallocStats(ctx);
    }

    [SysAbiExport(
        Nid = "k04jLXu3+Ic",
        ExportName = "sceLibcMspaceMallocStatsFast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = LibraryName)]
    public static int LibcMspaceMallocStatsFast(CpuContext ctx)
    {
        return WriteMallocStats(ctx);
    }

    [SysAbiExport(Nid = "xGT4Mc55ViQ", ExportName = "internal__Fofind", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFofind(CpuContext ctx)
    {
        if (ctx.Memory is not IGuestMemoryAllocator allocator)
        {
            return ReturnPointer(ctx, 0);
        }

        lock (_internalFileGate)
        {
            for (var index = InitialFileIndex; index < MaxFileIndex; index++)
            {
                if (_internalFiles.Values.Any(file => file.Index == (byte)index))
                {
                    continue;
                }

                if (!allocator.TryAllocateGuestMemory(FileObjectSize, 0x10, out var address) ||
                    !TryInitializeFileObject(ctx, address, (byte)index, InitialFileMode, -1))
                {
                    return ReturnPointer(ctx, 0);
                }

                _internalFiles[address] = new InternalFileState((byte)index);
                return ReturnPointer(ctx, address);
            }
        }

        return ReturnPointer(ctx, 0);
    }

    [SysAbiExport(Nid = "jVDuvE3s5Bs", ExportName = "internal__Fofree", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFofree(CpuContext ctx)
    {
        var fileAddress = ctx[CpuRegister.Rdi];
        if (fileAddress != 0 && ctx.TryReadByte(fileAddress + FileIndexOffset, out var index))
        {
            _internalFiles.TryRemove(fileAddress, out _);
            _internalMutexes.TryRemove(fileAddress + FileMutexOffset, out _);
            _ = TryInitializeFileObject(ctx, fileAddress, index, 0, -1);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "sQL8D-jio7U", ExportName = "internal__Fopen", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFopen(CpuContext ctx)
    {
        var fd = OpenFileDescriptor(
            ctx,
            ctx[CpuRegister.Rdi],
            unchecked((ushort)ctx[CpuRegister.Rsi]),
            ctx[CpuRegister.Rdx] != 0);
        return ReturnSignedValue(ctx, fd);
    }

    [SysAbiExport(Nid = "dREVnZkAKRE", ExportName = "internal__Foprep", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFoprep(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var modeAddress = ctx[CpuRegister.Rsi];
        var fileAddress = ctx[CpuRegister.Rdx];
        var suppliedFd = unchecked((int)ctx[CpuRegister.Rcx]);
        var privateMode = unchecked((int)ctx[CpuRegister.R8]) == 0x55;

        if (fileAddress == 0 ||
            !ctx.TryReadNullTerminatedUtf8(modeAddress, 16, out var modeString) ||
            !TryParseFileMode(modeString, out var mode))
        {
            return ReturnPointer(ctx, 0);
        }

        var index = ctx.TryReadByte(fileAddress + FileIndexOffset, out var currentIndex)
            ? currentIndex
            : (byte)0;
        var preservedMode = ctx.TryReadUInt16(fileAddress + FileModeOffset, out var currentMode)
            ? (ushort)(currentMode & InitialFileMode)
            : (ushort)0;
        mode |= preservedMode;

        var fd = pathAddress == 0 && suppliedFd >= 0
            ? suppliedFd
            : OpenFileDescriptor(ctx, pathAddress, mode, privateMode);
        if (fd < 0 || !TryInitializeFileObject(ctx, fileAddress, index, mode, fd))
        {
            _ = TryInitializeFileObject(ctx, fileAddress, index, preservedMode, -1);
            return ReturnPointer(ctx, 0);
        }

        _internalFiles[fileAddress] = new InternalFileState(index);
        InitializeInternalMutex(ctx, fileAddress + FileMutexOffset);
        return ReturnPointer(ctx, fileAddress);
    }

    [SysAbiExport(Nid = "9s3P+LCvWP8", ExportName = "internal__Frprep", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFrprep(CpuContext ctx)
    {
        var fileAddress = ctx[CpuRegister.Rdi];
        if (!TryReadFileCore(ctx, fileAddress, out var mode, out var fd, out var buffer, out var end, out var next, out var readEnd))
        {
            return ReturnSignedValue(ctx, -1);
        }

        if (readEnd > next)
        {
            return ReturnSignedValue(ctx, 1);
        }

        if ((mode & 0x100) != 0)
        {
            return ReturnSignedValue(ctx, 0);
        }

        if ((mode & 0xA001) != 1)
        {
            _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, (ushort)((((mode ^ 0x8000) >> 15) << 14) | mode | 0x200));
            return ReturnSignedValue(ctx, -1);
        }

        if ((mode & 0x800) == 0 && buffer == fileAddress + FileCbufOffset &&
            ctx.Memory is IGuestMemoryAllocator allocator &&
            allocator.TryAllocateGuestMemory(FileBufferSize, 0x10, out var allocatedBuffer))
        {
            buffer = allocatedBuffer;
            end = buffer + FileBufferSize;
            mode |= 0x40;
            _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, mode);
            _ = ctx.TryWriteUInt64(fileAddress + FileBufferOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileBendOffset, end);
            _ = ctx.TryWriteUInt64(fileAddress + FileWrendOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileWwendOffset, buffer);
        }

        ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        ctx[CpuRegister.Rsi] = buffer;
        ctx[CpuRegister.Rdx] = end > buffer ? end - buffer : 0;
        var readResult = Kernel.KernelMemoryCompatExports.KernelReadUnderscore(ctx);
        if (readResult != 0)
        {
            _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, (ushort)(mode | 0x4200));
            return ReturnSignedValue(ctx, -1);
        }

        var bytesRead = ctx[CpuRegister.Rax];
        _ = ctx.TryWriteUInt64(fileAddress + FileNextOffset, buffer);
        _ = ctx.TryWriteUInt64(fileAddress + FileRendOffset, buffer + bytesRead);
        _ = ctx.TryWriteUInt64(fileAddress + FileWendOffset, buffer);
        if (bytesRead != 0)
        {
            _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, (ushort)(mode | 0x5000));
            return ReturnSignedValue(ctx, 1);
        }

        _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, (ushort)((mode & 0xAEFF) | 0x4100));
        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "A+Y3xfrWLLo", ExportName = "internal__Fspos", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalFspos(CpuContext ctx)
    {
        var fileAddress = ctx[CpuRegister.Rdi];
        var filePositionAddress = ctx[CpuRegister.Rsi];
        var offset = unchecked((long)ctx[CpuRegister.Rdx]);
        var whence = unchecked((int)ctx[CpuRegister.Rcx]);
        if (whence is < 0 or > 2 ||
            !ctx.TryReadUInt16(fileAddress + FileModeOffset, out var mode) ||
            (mode & 3) == 0 ||
            !ctx.TryReadInt32(fileAddress + FileHandleOffset, out var fd))
        {
            return ReturnSignedValue(ctx, -1);
        }

        if (filePositionAddress != 0)
        {
            if (!ctx.TryReadUInt64(filePositionAddress, out var baseOffset))
            {
                return ReturnSignedValue(ctx, -1);
            }

            offset = unchecked(offset + (long)baseOffset);
        }

        ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        ctx[CpuRegister.Rsi] = unchecked((ulong)offset);
        ctx[CpuRegister.Rdx] = unchecked((ulong)whence);
        var seekResult = Kernel.KernelMemoryCompatExports.KernelLseek(ctx);
        if (seekResult != 0)
        {
            return ReturnSignedValue(ctx, -1);
        }

        if (ctx.TryReadUInt64(fileAddress + FileBufferOffset, out var buffer))
        {
            _ = ctx.TryWriteUInt64(fileAddress + FileNextOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileRendOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileWrendOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileWendOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileWwendOffset, buffer);
            _ = ctx.TryWriteUInt64(fileAddress + FileRbackOffset, fileAddress + FileCbufOffset);
            _ = ctx.TryWriteUInt64(fileAddress + FileWrbackOffset, fileAddress + FileUnknown1Offset);
            _ = ctx.TryWriteUInt64(fileAddress + FileRsaveOffset, 0);
        }

        if (filePositionAddress != 0)
        {
            Span<byte> state = stackalloc byte[16];
            if (!ctx.Memory.TryRead(filePositionAddress + 8, state) ||
                !ctx.Memory.TryWrite(fileAddress + FileWstateOffset, state))
            {
                return ReturnSignedValue(ctx, -1);
            }
        }

        _ = ctx.TryWriteUInt16(fileAddress + FileModeOffset, (ushort)(mode & 0xCEFF));
        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "Ss3108pBuZY", ExportName = "internal__Nnl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalNnl(CpuContext ctx)
    {
        var first = ctx[CpuRegister.Rsi];
        var second = ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rax] = first < second ? second - first : 0;
        return 0;
    }

    [SysAbiExport(Nid = "vZkmJmvqueY", ExportName = "internal__Lockfilelock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalLockfilelock(CpuContext ctx)
    {
        var fileAddress = ctx[CpuRegister.Rdi];
        if (fileAddress != 0 && ctx.TryReadUInt64(fileAddress + FileMutexOffset, out var marker) && marker != 0)
        {
            ctx[CpuRegister.Rdi] = fileAddress + FileMutexOffset;
            _ = InternalMtxlock(ctx);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "0x7rx8TKy2Y", ExportName = "internal__Unlockfilelock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalUnlockfilelock(CpuContext ctx)
    {
        var fileAddress = ctx[CpuRegister.Rdi];
        if (fileAddress != 0 && ctx.TryReadUInt64(fileAddress + FileMutexOffset, out var marker) && marker != 0)
        {
            ctx[CpuRegister.Rdi] = fileAddress + FileMutexOffset;
            _ = InternalMtxunlock(ctx);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "z7STeF6abuU", ExportName = "internal__Mtxinit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalMtxinit(CpuContext ctx)
    {
        return ReturnSignedValue(ctx, InitializeInternalMutex(ctx, ctx[CpuRegister.Rdi]) ? 0 : 1);
    }

    [SysAbiExport(Nid = "pE4Ot3CffW0", ExportName = "internal__Mtxlock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalMtxlock(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!_internalMutexes.TryGetValue(address, out var state) ||
            !Monitor.TryEnter(state.Gate, TimeSpan.FromSeconds(1)))
        {
            return ReturnSignedValue(ctx, 1);
        }

        state.OwnerThreadId = Environment.CurrentManagedThreadId;
        state.Recursion++;
        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "cMwgSSmpE5o", ExportName = "internal__Mtxunlock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalMtxunlock(CpuContext ctx)
    {
        if (!_internalMutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state) ||
            state.OwnerThreadId != Environment.CurrentManagedThreadId ||
            state.Recursion == 0)
        {
            return ReturnSignedValue(ctx, 1);
        }

        state.Recursion--;
        if (state.Recursion == 0)
        {
            state.OwnerThreadId = 0;
        }

        Monitor.Exit(state.Gate);
        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "LaPaA6mYA38", ExportName = "internal__Mtxdst", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalMtxdst(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (!_internalMutexes.TryGetValue(address, out var state) || state.Recursion != 0)
        {
            return ReturnSignedValue(ctx, 1);
        }

        _internalMutexes.TryRemove(address, out _);
        _ = ctx.TryWriteUInt64(address, 0);
        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "JBcgYuW8lPU", ExportName = "internal_acos", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAcos(CpuContext ctx) => ReturnDouble(ctx, Math.Acos(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "QI-x0SL8jhw", ExportName = "internal_acosf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAcosf(CpuContext ctx) => ReturnFloat(ctx, MathF.Acos(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "7Ly52zaL44Q", ExportName = "internal_asin", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAsin(CpuContext ctx) => ReturnDouble(ctx, Math.Asin(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "GZWjF-YIFFk", ExportName = "internal_asinf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAsinf(CpuContext ctx) => ReturnFloat(ctx, MathF.Asin(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "OXmauLdQ8kY", ExportName = "internal_atan", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAtan(CpuContext ctx) => ReturnDouble(ctx, Math.Atan(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "HUbZmOnT-Dg", ExportName = "internal_atan2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAtan2(CpuContext ctx) => ReturnDouble(ctx, Math.Atan2(ReadDouble(ctx, 0), ReadDouble(ctx, 1)));

    [SysAbiExport(Nid = "EH-x713A99c", ExportName = "internal_atan2f", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAtan2f(CpuContext ctx) => ReturnFloat(ctx, MathF.Atan2(ReadFloat(ctx, 0), ReadFloat(ctx, 1)));

    [SysAbiExport(Nid = "weDug8QD-lE", ExportName = "internal_atanf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalAtanf(CpuContext ctx) => ReturnFloat(ctx, MathF.Atan(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "2WE3BTYVwKM", ExportName = "internal_cos", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalCos(CpuContext ctx) => ReturnDouble(ctx, Math.Cos(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "-P6FNMzk2Kc", ExportName = "internal_cosf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalCosf(CpuContext ctx) => ReturnFloat(ctx, MathF.Cos(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "NVadfnzQhHQ", ExportName = "internal_exp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalExp(CpuContext ctx) => ReturnDouble(ctx, Math.Exp(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "dnaeGXbjP6E", ExportName = "internal_exp2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalExp2(CpuContext ctx) => ReturnDouble(ctx, Math.Pow(2.0, ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "wuAQt-j+p4o", ExportName = "internal_exp2f", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalExp2f(CpuContext ctx) => ReturnFloat(ctx, MathF.Pow(2.0f, ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "8zsu04XNsZ4", ExportName = "internal_expf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalExpf(CpuContext ctx) => ReturnFloat(ctx, MathF.Exp(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "rtV7-jWC6Yg", ExportName = "internal_log", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalLog(CpuContext ctx) => ReturnDouble(ctx, Math.Log(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "WuMbPBKN1TU", ExportName = "internal_log10", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalLog10(CpuContext ctx) => ReturnDouble(ctx, Math.Log10(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "lhpd6Wk6ccs", ExportName = "internal_log10f", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalLog10f(CpuContext ctx) => ReturnFloat(ctx, MathF.Log10(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "RQXLbdT2lc4", ExportName = "internal_logf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalLogf(CpuContext ctx) => ReturnFloat(ctx, MathF.Log(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "9LCjpWyQ5Zc", ExportName = "internal_pow", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalPow(CpuContext ctx) => ReturnDouble(ctx, Math.Pow(ReadDouble(ctx, 0), ReadDouble(ctx, 1)));

    [SysAbiExport(Nid = "1D0H2KNjshE", ExportName = "internal_powf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalPowf(CpuContext ctx) => ReturnFloat(ctx, MathF.Pow(ReadFloat(ctx, 0), ReadFloat(ctx, 1)));

    [SysAbiExport(Nid = "H8ya2H00jbI", ExportName = "internal_sin", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalSin(CpuContext ctx) => ReturnDouble(ctx, Math.Sin(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "jMB7EFyu30Y", ExportName = "internal_sincos", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalSincos(CpuContext ctx)
    {
        var (sin, cos) = Math.SinCos(ReadDouble(ctx, 0));
        _ = ctx.TryWriteUInt64(ctx[CpuRegister.Rdi], unchecked((ulong)BitConverter.DoubleToInt64Bits(sin)));
        _ = ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], unchecked((ulong)BitConverter.DoubleToInt64Bits(cos)));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "pztV4AF18iI", ExportName = "internal_sincosf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalSincosf(CpuContext ctx)
    {
        var (sin, cos) = MathF.SinCos(ReadFloat(ctx, 0));
        _ = ctx.TryWriteUInt32(ctx[CpuRegister.Rdi], BitConverter.SingleToUInt32Bits(sin));
        _ = ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], BitConverter.SingleToUInt32Bits(cos));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Q4rRL34CEeE", ExportName = "internal_sinf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalSinf(CpuContext ctx) => ReturnFloat(ctx, MathF.Sin(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "T7uyNqP7vQA", ExportName = "internal_tan", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalTan(CpuContext ctx) => ReturnDouble(ctx, Math.Tan(ReadDouble(ctx, 0)));

    [SysAbiExport(Nid = "ZE6RNL+eLbk", ExportName = "internal_tanf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalTanf(CpuContext ctx) => ReturnFloat(ctx, MathF.Tan(ReadFloat(ctx, 0)));

    [SysAbiExport(Nid = "NFLs+dRJGNg", ExportName = "internal_memcpy_s", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalMemcpyS(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var destinationSize = ctx[CpuRegister.Rsi];
        var source = ctx[CpuRegister.Rdx];
        var count = ctx[CpuRegister.Rcx];
        if (count == 0)
        {
            return ReturnSignedValue(ctx, 0);
        }

        if (destination == 0 || source == 0 || destinationSize > SecureRsizeMax)
        {
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        if (count > destinationSize)
        {
            _ = TryZeroGuestRange(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureRangeError);
        }

        if (RangesOverlap(destination, count, source, count))
        {
            _ = TryZeroGuestRange(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        if (!TryCopyGuestRange(ctx, source, destination, count))
        {
            _ = TryZeroGuestRange(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        return ReturnSignedValue(ctx, 0);
    }

    [SysAbiExport(Nid = "K+gcnFFJKVc", ExportName = "internal_strcat_s", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalStrcatS(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var destinationSize = ctx[CpuRegister.Rsi];
        var source = ctx[CpuRegister.Rdx];
        if (!TryReadCStringBytes(ctx, destination, destinationSize, out var destinationBytes) ||
            !TryReadCStringBytes(ctx, source, SecureStringLimit, out var sourceBytes))
        {
            ClearStringDestination(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        var required = (ulong)destinationBytes.Length + (ulong)sourceBytes.Length + 1;
        if (required > destinationSize)
        {
            ClearStringDestination(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureRangeError);
        }

        return ctx.Memory.TryWrite(destination + (ulong)destinationBytes.Length, sourceBytes.Append((byte)0).ToArray())
            ? ReturnSignedValue(ctx, 0)
            : ReturnSignedValue(ctx, SecureInvalidArgument);
    }

    [SysAbiExport(Nid = "5Xa2ACNECdo", ExportName = "internal_strcpy_s", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalStrcpyS(CpuContext ctx)
    {
        return CopySecureString(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], SecureStringLimit + 1);
    }

    [SysAbiExport(Nid = "YNzNkJzYqEg", ExportName = "internal_strncpy_s", Target = Generation.Gen4 | Generation.Gen5, LibraryName = LibraryName)]
    public static int InternalStrncpyS(CpuContext ctx)
    {
        var count = ctx[CpuRegister.Rcx];
        return CopySecureString(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], count);
    }

    // SceLibcMallocManagedSize: u16 size, u16 version, u32 reserved, then
    // max/current system size and max/current in-use size as 64-bit values.
    private static int WriteMallocStats(CpuContext ctx)
    {
        var mspace = ctx[CpuRegister.Rdi];
        var statsAddress = ctx[CpuRegister.Rsi];
        if (statsAddress == 0 || !_arenas.TryGetValue(mspace, out var arena))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong used;
        ulong peak;
        lock (arena.Gate)
        {
            used = arena.Bump - arena.BaseAddress;
            peak = arena.PeakUsed;
        }

        var ok = ctx.TryWriteUInt64(statsAddress + 0x08, arena.Capacity) &&
                 ctx.TryWriteUInt64(statsAddress + 0x10, arena.Capacity) &&
                 ctx.TryWriteUInt64(statsAddress + 0x18, peak) &&
                 ctx.TryWriteUInt64(statsAddress + 0x20, used);
        TraceStats($"stats mspace=0x{mspace:X} capacity=0x{arena.Capacity:X} peak=0x{peak:X} used=0x{used:X}");
        return ok
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static ulong Allocate(CpuContext ctx, ulong mspace, ulong size, ulong alignment)
    {
        if (!_arenas.TryGetValue(mspace, out var arena))
        {
            return 0;
        }

        // malloc(0) yields a valid minimum chunk; titles assert on a null
        // return even for zero-byte requests.
        if (size == 0)
        {
            size = 1;
        }

        lock (arena.Gate)
        {
            var address = TakeFromFreeSpans(arena, size, alignment);
            if (address == 0)
            {
                address = AlignUp(arena.Bump + ChunkHeaderSize, alignment);
                if (address < arena.Bump ||
                    address - arena.BaseAddress > arena.Capacity ||
                    size > arena.Capacity - (address - arena.BaseAddress))
                {
                    return 0;
                }

                arena.Bump = address + size;
            }

            var chunkSize = AlignUp(size + ChunkHeaderSize, DefaultAlignment);
            if (!ctx.TryWriteUInt64(address - ChunkHeaderSize, 0) ||
                !ctx.TryWriteUInt64(address - (ChunkHeaderSize / 2), chunkSize | ChunkInUseBits))
            {
                return 0;
            }

            arena.PeakUsed = Math.Max(arena.PeakUsed, arena.Bump - arena.BaseAddress);
            arena.AllocationSizes[address] = size;
            return address;
        }
    }

    // Best-fit over freed chunks; any surplus beyond the reused portion goes
    // back on the list so a large freed block can serve many small requests.
    private static ulong TakeFromFreeSpans(MspaceArena arena, ulong size, ulong alignment)
    {
        var bestIndex = -1;
        var bestWaste = ulong.MaxValue;
        ulong bestAddress = 0;
        for (var i = 0; i < arena.FreeSpans.Count; i++)
        {
            var (start, length) = arena.FreeSpans[i];
            var address = AlignUp(start + ChunkHeaderSize, alignment);
            var needed = address - start + size;
            if (address < start || needed > length)
            {
                continue;
            }

            var waste = length - needed;
            if (waste < bestWaste)
            {
                bestWaste = waste;
                bestIndex = i;
                bestAddress = address;
            }

            if (waste == 0)
            {
                break;
            }
        }

        if (bestIndex < 0)
        {
            return 0;
        }

        var span = arena.FreeSpans[bestIndex];
        arena.FreeSpans.RemoveAt(bestIndex);
        var used = bestAddress - span.Start + size;
        var surplus = span.Length - used;
        if (surplus >= ChunkHeaderSize * 2)
        {
            arena.FreeSpans.Add((span.Start + used, surplus));
        }

        return bestAddress;
    }

    private static ulong LookupAllocationSize(ulong pointer)
    {
        foreach (var arena in _arenas.Values)
        {
            lock (arena.Gate)
            {
                if (arena.AllocationSizes.TryGetValue(pointer, out var size))
                {
                    return size;
                }
            }
        }

        return 0;
    }

    private static bool TryZeroGuestRange(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[256];
        for (ulong offset = 0; offset < length;)
        {
            var chunk = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunk]))
            {
                return false;
            }

            offset += (ulong)chunk;
        }

        return true;
    }

    private static bool TryCopyGuestRange(CpuContext ctx, ulong source, ulong destination, ulong length)
    {
        Span<byte> buffer = stackalloc byte[256];
        for (ulong offset = 0; offset < length;)
        {
            var chunk = (int)Math.Min((ulong)buffer.Length, length - offset);
            if (!ctx.Memory.TryRead(source + offset, buffer[..chunk]) ||
                !ctx.Memory.TryWrite(destination + offset, buffer[..chunk]))
            {
                return false;
            }

            offset += (ulong)chunk;
        }

        return true;
    }

    private const int InitialFileIndex = 5;
    private const int MaxFileIndex = 0x100;
    private const ushort InitialFileMode = 0x80;
    private const ulong FileObjectSize = 456;
    private const ulong FileBufferSize = 0x10000;
    private const ulong FileModeOffset = 0;
    private const ulong FileIndexOffset = 2;
    private const ulong FileHandleOffset = 4;
    private const ulong FileBufferOffset = 8;
    private const ulong FileBendOffset = 16;
    private const ulong FileNextOffset = 24;
    private const ulong FileRendOffset = 32;
    private const ulong FileWendOffset = 40;
    private const ulong FileRbackOffset = 48;
    private const ulong FileWrbackOffset = 56;
    private const ulong FileUnknown1Offset = 68;
    private const ulong FileRsaveOffset = 72;
    private const ulong FileWrendOffset = 80;
    private const ulong FileWwendOffset = 88;
    private const ulong FileWstateOffset = 96;
    private const ulong FileCbufOffset = 126;
    private const ulong FileMutexOffset = 128;
    private const ulong SecureRsizeMax = 0x7FFF_FFFF;
    private const ulong SecureStringLimit = 1024 * 1024;
    private const int SecureInvalidArgument = 22;
    private const int SecureRangeError = 34;

    private static readonly object _internalFileGate = new();
    private static readonly ConcurrentDictionary<ulong, InternalFileState> _internalFiles = new();
    private static readonly ConcurrentDictionary<ulong, InternalMutexState> _internalMutexes = new();

    private sealed class InternalFileState(byte index)
    {
        public byte Index { get; } = index;
    }

    private sealed class InternalMutexState
    {
        public object Gate { get; } = new();
        public int OwnerThreadId { get; set; }
        public int Recursion { get; set; }
    }

    private static bool TryInitializeFileObject(CpuContext ctx, ulong address, byte index, ushort mode, int fd)
    {
        Span<byte> zeroes = stackalloc byte[(int)FileObjectSize];
        zeroes.Clear();
        var cbuf = address + FileCbufOffset;
        return ctx.Memory.TryWrite(address, zeroes) &&
               ctx.TryWriteUInt16(address + FileModeOffset, mode) &&
               TryWriteInternalByte(ctx, address + FileIndexOffset, index) &&
               ctx.TryWriteInt32(address + FileHandleOffset, fd) &&
               ctx.TryWriteUInt64(address + FileBufferOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileBendOffset, cbuf + 1) &&
               ctx.TryWriteUInt64(address + FileNextOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileRendOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileWendOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileRbackOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileWrbackOffset, address + FileUnknown1Offset) &&
               ctx.TryWriteUInt64(address + FileWrendOffset, cbuf) &&
               ctx.TryWriteUInt64(address + FileWwendOffset, cbuf);
    }

    private static bool TryReadFileCore(
        CpuContext ctx,
        ulong address,
        out ushort mode,
        out int fd,
        out ulong buffer,
        out ulong end,
        out ulong next,
        out ulong readEnd)
    {
        mode = 0;
        fd = -1;
        buffer = 0;
        end = 0;
        next = 0;
        readEnd = 0;
        return address != 0 &&
               ctx.TryReadUInt16(address + FileModeOffset, out mode) &&
               ctx.TryReadInt32(address + FileHandleOffset, out fd) &&
               ctx.TryReadUInt64(address + FileBufferOffset, out buffer) &&
               ctx.TryReadUInt64(address + FileBendOffset, out end) &&
               ctx.TryReadUInt64(address + FileNextOffset, out next) &&
               ctx.TryReadUInt64(address + FileRendOffset, out readEnd);
    }

    private static bool TryParseFileMode(string modeString, out ushort mode)
    {
        mode = modeString.Length > 0
            ? modeString[0] switch
            {
                'r' => (ushort)1,
                'w' => (ushort)0x1A,
                'a' => (ushort)0x16,
                _ => (ushort)0,
            }
            : (ushort)0;
        if (mode == 0)
        {
            return false;
        }

        foreach (var character in modeString.AsSpan(1))
        {
            switch (character)
            {
                case '+':
                    mode |= 3;
                    break;
                case 'b':
                    break;
                case 'x':
                    mode |= 0x40;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static int OpenFileDescriptor(CpuContext ctx, ulong pathAddress, ushort mode, bool privateMode)
    {
        if (pathAddress == 0)
        {
            return -1;
        }

        var largeMode = (uint)mode;
        var createFlag = (int)((largeMode << 5) & 0x200);
        var exclusiveFlag = (int)((largeMode << 5) & 0x800);
        var miscellaneousFlags = (int)((largeMode & 8) * 0x80 + (largeMode & 4) * 2);
        var accessFlag = Math.Max((int)(largeMode & 3) - 1, 0);
        ctx[CpuRegister.Rdi] = pathAddress;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(createFlag | exclusiveFlag | miscellaneousFlags | accessFlag));
        ctx[CpuRegister.Rdx] = privateMode ? 0x180UL : 0x1B6UL;
        return Kernel.KernelMemoryCompatExports.KernelOpenUnderscore(ctx) == 0
            ? unchecked((int)ctx[CpuRegister.Rax])
            : -1;
    }

    private static bool InitializeInternalMutex(CpuContext ctx, ulong address)
    {
        if (address == 0 || !ctx.TryWriteUInt64(address, address))
        {
            return false;
        }

        _internalMutexes[address] = new InternalMutexState();
        return true;
    }

    private static double ReadDouble(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var bits, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    private static float ReadFloat(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var bits, out _);
        return BitConverter.UInt32BitsToSingle(unchecked((uint)bits));
    }

    private static int ReturnDouble(CpuContext ctx, double value)
    {
        ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), 0);
        return 0;
    }

    private static int ReturnFloat(CpuContext ctx, float value)
    {
        ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(value), 0);
        return 0;
    }

    private static int ReturnSignedValue(CpuContext ctx, int value)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)value);
        return 0;
    }

    private static bool RangesOverlap(ulong first, ulong firstLength, ulong second, ulong secondLength)
    {
        if (first == second)
        {
            return false;
        }

        var firstEnd = firstLength > ulong.MaxValue - first ? ulong.MaxValue : first + firstLength;
        var secondEnd = secondLength > ulong.MaxValue - second ? ulong.MaxValue : second + secondLength;
        return first < secondEnd && second < firstEnd;
    }

    private static bool TryReadCStringBytes(CpuContext ctx, ulong address, ulong capacity, out byte[] bytes)
    {
        bytes = [];
        if (address == 0 || capacity == 0 || capacity > SecureStringLimit)
        {
            return false;
        }

        var result = new List<byte>();
        for (ulong offset = 0; offset < capacity; offset++)
        {
            if (!ctx.TryReadByte(address + offset, out var value))
            {
                return false;
            }

            if (value == 0)
            {
                bytes = result.ToArray();
                return true;
            }

            result.Add(value);
        }

        return false;
    }

    private static int CopySecureString(CpuContext ctx, ulong destination, ulong destinationSize, ulong source, ulong count)
    {
        if (destination == 0 || source == 0 || destinationSize == 0 || destinationSize > SecureRsizeMax)
        {
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        var readLimit = Math.Min(count, SecureStringLimit);
        var sourceBytes = new List<byte>();
        var terminated = false;
        for (ulong offset = 0; offset < readLimit; offset++)
        {
            if (!ctx.TryReadByte(source + offset, out var value))
            {
                ClearStringDestination(ctx, destination, destinationSize);
                return ReturnSignedValue(ctx, SecureInvalidArgument);
            }

            if (value == 0)
            {
                terminated = true;
                break;
            }

            sourceBytes.Add(value);
        }

        if (!terminated && count > SecureStringLimit)
        {
            ClearStringDestination(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureRangeError);
        }

        if ((ulong)sourceBytes.Count + 1 > destinationSize)
        {
            ClearStringDestination(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureRangeError);
        }

        sourceBytes.Add(0);
        if (!ctx.Memory.TryWrite(destination, sourceBytes.ToArray()))
        {
            ClearStringDestination(ctx, destination, destinationSize);
            return ReturnSignedValue(ctx, SecureInvalidArgument);
        }

        return ReturnSignedValue(ctx, 0);
    }

    private static void ClearStringDestination(CpuContext ctx, ulong destination, ulong destinationSize)
    {
        if (destination != 0 && destinationSize != 0)
        {
            _ = TryWriteInternalByte(ctx, destination, 0);
        }
    }

    private static bool TryWriteInternalByte(CpuContext ctx, ulong address, byte value)
    {
        Span<byte> data = stackalloc byte[1];
        data[0] = value;
        return ctx.Memory.TryWrite(address, data);
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static int ReturnPointer(CpuContext ctx, ulong address)
    {
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    internal static void ResetForTests()
    {
        _arenas.Clear();
    }

    internal static void ResetInternalForTests()
    {
        _internalFiles.Clear();
        _internalMutexes.Clear();
    }
}
