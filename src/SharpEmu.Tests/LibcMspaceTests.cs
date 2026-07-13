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
}
