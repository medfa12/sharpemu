// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceLibcMspace* semantics over a game-donated arena: create/destroy, aligned
/// bump allocation within the arena bounds, calloc zeroing, realloc copying,
/// usable-size lookup, and stats reporting.
/// </summary>
public sealed class LibcMspaceTests : IDisposable
{
    private const ulong ArenaBase = 0x10_0000;
    private const ulong ArenaCapacity = 0x1_0000;
    private const ulong StatsAddress = 0x4000;

    public LibcMspaceTests()
    {
        LibcMspaceExports.ResetForTests();
    }

    public void Dispose()
    {
        LibcMspaceExports.ResetForTests();
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        memory.TryWrite(ArenaBase, new byte[ArenaCapacity]);
        memory.TryWrite(StatsAddress, new byte[0x28]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static ulong CreateArena(CpuContext ctx, ulong capacity = ArenaCapacity)
    {
        ctx[CpuRegister.Rdi] = 0; // name (optional)
        ctx[CpuRegister.Rsi] = ArenaBase;
        ctx[CpuRegister.Rdx] = capacity;
        ctx[CpuRegister.Rcx] = 0;
        LibcMspaceExports.LibcMspaceCreate(ctx);
        return ctx[CpuRegister.Rax];
    }

    private static ulong Malloc(CpuContext ctx, ulong mspace, ulong size)
    {
        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = size;
        LibcMspaceExports.LibcMspaceMalloc(ctx);
        return ctx[CpuRegister.Rax];
    }

    [Fact]
    public void Create_ReturnsArenaBaseAsHandle()
    {
        var ctx = NewContext(out _);
        Assert.Equal(ArenaBase, CreateArena(ctx));
    }

    [Fact]
    public void Create_NullBaseOrZeroCapacity_ReturnsNull()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = ArenaCapacity;
        LibcMspaceExports.LibcMspaceCreate(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rsi] = ArenaBase;
        ctx[CpuRegister.Rdx] = 0;
        LibcMspaceExports.LibcMspaceCreate(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Malloc_ReturnsAlignedPointersInsideArena()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);

        var first = Malloc(ctx, mspace, 24);
        var second = Malloc(ctx, mspace, 8);

        Assert.NotEqual(0UL, first);
        Assert.NotEqual(0UL, second);
        Assert.Equal(0UL, first % 16);
        Assert.Equal(0UL, second % 16);
        Assert.True(second > first);
        Assert.True(second + 8 <= ArenaBase + ArenaCapacity);
    }

    [Fact]
    public void Malloc_UnknownMspace_ReturnsNull()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);

        Assert.Equal(0UL, Malloc(ctx, mspace + 0x40, 8));
    }

    [Fact]
    public void Malloc_ZeroSize_ReturnsValidMinimumChunk()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);

        var first = Malloc(ctx, mspace, 0);
        var second = Malloc(ctx, mspace, 0);
        Assert.NotEqual(0UL, first);
        Assert.NotEqual(0UL, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Malloc_ExhaustsArena_ReturnsNull()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx, capacity: 64);

        Assert.NotEqual(0UL, Malloc(ctx, mspace, 48));
        Assert.Equal(0UL, Malloc(ctx, mspace, 48));
    }

    [Fact]
    public void Calloc_ZeroesTheAllocation()
    {
        var ctx = NewContext(out var memory);
        var mspace = CreateArena(ctx);

        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = 4;
        ctx[CpuRegister.Rdx] = 1;
        LibcMspaceExports.LibcMspaceCalloc(ctx);
        var address = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);

        memory.TryWrite(address, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 4;
        LibcMspaceExports.LibcMspaceCalloc(ctx);
        var second = ctx[CpuRegister.Rax];

        Assert.NotEqual(0UL, second);
        Assert.True(ctx.TryReadUInt32(second, out var contents));
        Assert.Equal(0U, contents);
    }

    [Fact]
    public void Malloc_WritesReadableChunkHeaderBehindPointer()
    {
        // Titles read the dlmalloc size word at [ptr-8] for heap statistics;
        // it must be mapped, in-use flagged, and at least the payload size.
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);
        var address = Malloc(ctx, mspace, 40);

        Assert.True(address - 16 >= ArenaBase);
        Assert.True(ctx.TryReadUInt64(address - 8, out var head));
        Assert.Equal(0x3UL, head & 0xF);
        Assert.True((head & ~0xFUL) >= 40);
        Assert.True(ctx.TryReadUInt64(address - 16, out _));
    }

    [Fact]
    public void Realloc_CopiesOldContents()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);
        var original = Malloc(ctx, mspace, 8);
        Assert.True(ctx.TryWriteUInt64(original, 0xDEADBEEF12345678UL));

        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = original;
        ctx[CpuRegister.Rdx] = 32;
        LibcMspaceExports.LibcMspaceRealloc(ctx);
        var grown = ctx[CpuRegister.Rax];

        Assert.NotEqual(0UL, grown);
        Assert.NotEqual(original, grown);
        Assert.True(ctx.TryReadUInt64(grown, out var copied));
        Assert.Equal(0xDEADBEEF12345678UL, copied);
    }

    [Fact]
    public void Memalign_HonorsAlignment()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);
        Malloc(ctx, mspace, 8);

        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = 256;
        ctx[CpuRegister.Rdx] = 32;
        LibcMspaceExports.LibcMspaceMemalign(ctx);
        var aligned = ctx[CpuRegister.Rax];

        Assert.NotEqual(0UL, aligned);
        Assert.Equal(0UL, aligned % 256);
    }

    [Fact]
    public void Memalign_NonPowerOfTwo_ReturnsNull()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);

        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = 24;
        ctx[CpuRegister.Rdx] = 32;
        LibcMspaceExports.LibcMspaceMemalign(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void MallocUsableSize_ReportsAllocationSize()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);
        var address = Malloc(ctx, mspace, 40);

        ctx[CpuRegister.Rdi] = address;
        LibcMspaceExports.LibcMspaceMallocUsableSize(ctx);
        Assert.Equal(40UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = address + 8;
        LibcMspaceExports.LibcMspaceMallocUsableSize(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void MallocStats_ReportsCapacityAndUsage()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);
        Malloc(ctx, mspace, 0x100);

        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = StatsAddress;
        LibcMspaceExports.LibcMspaceMallocStats(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(ctx.TryReadUInt64(StatsAddress + 0x08, out var maxSystem));
        Assert.True(ctx.TryReadUInt64(StatsAddress + 0x20, out var currentInuse));
        Assert.Equal(ArenaCapacity, maxSystem);
        Assert.Equal(0x110UL, currentInuse); // payload + 16-byte chunk header
    }

    [Fact]
    public void Free_MakesSpaceReusable()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx, capacity: 0xA0);

        // Fill the arena (two 0x40 chunks + headers), free everything, and
        // allocate again: without reuse the second round would exhaust it.
        var first = Malloc(ctx, mspace, 0x40);
        var second = Malloc(ctx, mspace, 0x40);
        Assert.NotEqual(0UL, first);
        Assert.NotEqual(0UL, second);
        Assert.Equal(0UL, Malloc(ctx, mspace, 0x40));

        foreach (var pointer in new[] { first, second })
        {
            ctx[CpuRegister.Rdi] = mspace;
            ctx[CpuRegister.Rsi] = pointer;
            LibcMspaceExports.LibcMspaceFree(ctx);
        }

        Assert.NotEqual(0UL, Malloc(ctx, mspace, 0x40));
        Assert.NotEqual(0UL, Malloc(ctx, mspace, 0x40));
    }

    [Fact]
    public void Destroy_ForgetsTheArena()
    {
        var ctx = NewContext(out _);
        var mspace = CreateArena(ctx);

        ctx[CpuRegister.Rdi] = mspace;
        LibcMspaceExports.LibcMspaceDestroy(ctx);

        Assert.Equal(0UL, Malloc(ctx, mspace, 8));
    }

    private static ulong Free(CpuContext ctx, ulong mspace, ulong pointer)
    {
        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = pointer;
        LibcMspaceExports.LibcMspaceFree(ctx);
        return ctx[CpuRegister.Rax];
    }

    private static ulong Realloc(CpuContext ctx, ulong mspace, ulong pointer, ulong size)
    {
        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = pointer;
        ctx[CpuRegister.Rdx] = size;
        LibcMspaceExports.LibcMspaceRealloc(ctx);
        return ctx[CpuRegister.Rax];
    }

    private static ulong Memalign(CpuContext ctx, ulong mspace, ulong alignment, ulong size)
    {
        ctx[CpuRegister.Rdi] = mspace;
        ctx[CpuRegister.Rsi] = alignment;
        ctx[CpuRegister.Rdx] = size;
        LibcMspaceExports.LibcMspaceMemalign(ctx);
        return ctx[CpuRegister.Rax];
    }

    // Regression guard for the "random data-structure corruption during heavy
    // allocation" failure class: hammer the arena with a mixed malloc / free /
    // realloc / memalign workload and assert every live chunk (its 16-byte
    // header plus payload) stays disjoint from every other live chunk. An
    // allocator that ever hands back a region overlapping a still-live one
    // silently corrupts whatever the guest built there -- exactly the shape of
    // a std::map node whose child pointer gets clobbered mid-boot.
    [Fact]
    public void MixedWorkload_NeverHandsOutOverlappingChunks()
    {
        var ctx = NewContext(out _);
        // Large arena so pressure comes from the free-list reuse paths, not
        // capacity exhaustion.
        var mspace = CreateArena(ctx, capacity: 0x0100_0000);
        const ulong headerSize = 16;

        // (payloadStart, chunkStart, chunkEnd) for each live allocation.
        var live = new List<(ulong Payload, ulong Start, ulong End)>();

        void AssertDisjoint(ulong payload, ulong size, string op)
        {
            Assert.NotEqual(0UL, payload);
            var start = payload - headerSize;
            var end = payload + size;
            foreach (var (_, otherStart, otherEnd) in live)
            {
                var overlaps = start < otherEnd && otherStart < end;
                Assert.False(
                    overlaps,
                    $"{op} returned chunk [0x{start:X},0x{end:X}) overlapping live [0x{otherStart:X},0x{otherEnd:X})");
            }

            live.Add((payload, start, end));
        }

        // Deterministic LCG so a failure reproduces exactly.
        ulong rng = 0x9E3779B97F4A7C15UL;
        uint Next()
        {
            rng = rng * 6364136223846793005UL + 1442695040888963407UL;
            return (uint)(rng >> 33);
        }

        var sizes = new ulong[] { 8, 16, 24, 32, 0x18, 0x3B8, 0x40, 0x80, 0x100, 1, 0x200 };

        for (var i = 0; i < 4000; i++)
        {
            var pick = Next() % 10;
            if (pick < 5 || live.Count == 0)
            {
                var size = sizes[Next() % (uint)sizes.Length];
                var p = Malloc(ctx, mspace, size);
                if (p != 0)
                {
                    AssertDisjoint(p, size, "malloc");
                }
            }
            else if (pick < 7)
            {
                var idx = (int)(Next() % (uint)live.Count);
                var victim = live[idx];
                live.RemoveAt(idx);
                Free(ctx, mspace, victim.Payload);
            }
            else if (pick < 9)
            {
                var idx = (int)(Next() % (uint)live.Count);
                var victim = live[idx];
                live.RemoveAt(idx);
                var size = sizes[Next() % (uint)sizes.Length];
                var p = Realloc(ctx, mspace, victim.Payload, size);
                if (p != 0)
                {
                    AssertDisjoint(p, size, "realloc");
                }
            }
            else
            {
                var alignment = 1UL << (int)(3 + Next() % 4); // 8,16,32,64
                var size = sizes[Next() % (uint)sizes.Length];
                var p = Memalign(ctx, mspace, alignment, size);
                if (p != 0)
                {
                    Assert.True(p % alignment == 0, $"memalign 0x{p:X} not {alignment}-aligned");
                    AssertDisjoint(p, size, "memalign");
                }
            }
        }
    }
}
