// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the typed allocation dispatch in
/// <see cref="KernelVirtualRangeAllocator"/>: search-vs-direct ordering, the
/// allowAlternative passthrough, and wrapper unwrapping via
/// <see cref="ICpuMemoryWrapper"/>.
/// </summary>
public sealed class KernelVirtualRangeAllocatorTests
{
    private const ulong Desired = 0x0000_0008_1000_0000UL;

    private static CpuContext Context(ICpuMemory memory) => new(memory, Generation.Gen5);

    private static bool Reserve(
        ICpuMemory memory,
        out ulong mapped,
        ulong length = 0x1000,
        bool allowSearch = false,
        bool allowAlternative = false)
        => KernelVirtualRangeAllocator.TryReserve(
            Context(memory),
            Desired,
            length,
            executable: false,
            alignment: 0x1000,
            allowSearch,
            allowAlternative,
            traceName: "test",
            out mapped);

    private abstract class FakeMemoryBase : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private class FakeAddressSpace : FakeMemoryBase, IGuestAddressSpace
    {
        public (ulong Address, ulong Size, bool Executable, bool AllowAlternative)? LastAllocateAt;
        public int SearchCalls;
        public int DirectCalls;

        public virtual ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
        {
            DirectCalls++;
            LastAllocateAt = (desiredAddress, size, executable, allowAlternative);
            return desiredAddress;
        }

        public virtual bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            SearchCalls++;
            actualAddress = 0;
            return false;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }

        public bool TryFreeGuestMemory(ulong address) => false;
    }

    [Fact]
    public void TryReserve_UsesAllocateAt()
    {
        var memory = new FakeAddressSpace();

        Assert.True(Reserve(memory, out var mapped, length: 0x2000));
        Assert.Equal(Desired, mapped);
        Assert.Equal((Desired, 0x2000UL, false, false), memory.LastAllocateAt);
    }

    [Fact]
    public void TryReserve_PassesAllowAlternativeThrough()
    {
        var memory = new FakeAddressSpace();

        Assert.True(Reserve(memory, out _, allowAlternative: true));
        Assert.NotNull(memory.LastAllocateAt);
        Assert.True(memory.LastAllocateAt!.Value.AllowAlternative);
    }

    private sealed class MemoryWithSucceedingSearch : FakeAddressSpace
    {
        public override bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            SearchCalls++;
            actualAddress = desiredAddress + 0x1000;
            return true;
        }
    }

    [Fact]
    public void TryReserve_PrefersSearchWhenAllowed()
    {
        var memory = new MemoryWithSucceedingSearch();

        Assert.True(Reserve(memory, out var mapped, allowSearch: true));
        Assert.Equal(Desired + 0x1000, mapped);
        Assert.Equal(1, memory.SearchCalls);
        Assert.Equal(0, memory.DirectCalls);
    }

    [Fact]
    public void TryReserve_SkipsSearchWhenNotAllowed()
    {
        var memory = new MemoryWithSucceedingSearch();

        Assert.True(Reserve(memory, out var mapped, allowSearch: false));
        Assert.Equal(Desired, mapped);
        Assert.Equal(0, memory.SearchCalls);
        Assert.Equal(1, memory.DirectCalls);
    }

    [Fact]
    public void TryReserve_FallsBackToAllocateAtWhenSearchFails()
    {
        var memory = new FakeAddressSpace();

        Assert.True(Reserve(memory, out var mapped, allowSearch: true));
        Assert.Equal(Desired, mapped);
        Assert.Equal(1, memory.SearchCalls);
        Assert.Equal(1, memory.DirectCalls);
    }

    private sealed class MemoryWrappingAddressSpace : FakeMemoryBase, ICpuMemoryWrapper
    {
        public FakeAddressSpace InnerAddressSpace { get; } = new();

        public ICpuMemory Inner => InnerAddressSpace;
    }

    [Fact]
    public void TryReserve_UnwrapsCpuMemoryWrapperChain()
    {
        var memory = new MemoryWrappingAddressSpace();

        Assert.True(Reserve(memory, out var mapped));
        Assert.Equal(Desired, mapped);
        Assert.Equal(1, memory.InnerAddressSpace.DirectCalls);
    }

    private sealed class MemoryWithoutAllocation : FakeMemoryBase
    {
    }

    [Fact]
    public void TryReserve_NoAllocatorAvailable_ReturnsFalse()
    {
        Assert.False(Reserve(new MemoryWithoutAllocation(), out var mapped));
        Assert.Equal(0UL, mapped);
    }

    private sealed class MemoryReturningZero : FakeAddressSpace
    {
        public override ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => 0;
    }

    [Fact]
    public void TryReserve_ZeroAllocation_ReturnsFalse()
    {
        Assert.False(Reserve(new MemoryReturningZero(), out _));
    }

    private sealed class MemoryThatThrows : FakeAddressSpace
    {
        public override ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void TryReserve_AllocatorThrows_ReturnsFalse()
    {
        Assert.False(Reserve(new MemoryThatThrows(), out _));
    }

    [Fact]
    public void TryReserve_ZeroLength_ReturnsFalse()
    {
        Assert.False(Reserve(new FakeAddressSpace(), out _, length: 0));
    }
}
