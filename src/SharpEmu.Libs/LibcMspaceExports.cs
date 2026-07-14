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
        Trace($"malloc mspace=0x{mspace:X} size=0x{size:X} -> 0x{address:X} known={_arenas.ContainsKey(mspace)}");
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
}
