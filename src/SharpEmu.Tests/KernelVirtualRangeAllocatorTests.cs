// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the reflection-based allocation dispatch in
/// <see cref="KernelVirtualRangeAllocator"/>. Each scenario uses a distinct
/// fake memory type because discovered accessors are cached per Type.
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

    private sealed class MemoryWithThreeArgAllocate : FakeMemoryBase
    {
        public (ulong Address, ulong Size, bool Executable)? LastCall;

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable)
        {
            LastCall = (desiredAddress, size, executable);
            return desiredAddress;
        }
    }

    [Fact]
    public void TryReserve_UsesThreeArgAllocateAt()
    {
        var memory = new MemoryWithThreeArgAllocate();

        Assert.True(Reserve(memory, out var mapped, length: 0x2000));
        Assert.Equal(Desired, mapped);
        Assert.Equal((Desired, 0x2000UL, false), memory.LastCall);
    }

    private sealed class MemoryWithFourArgAllocate : FakeMemoryBase
    {
        public bool? ObservedAllowAlternative;

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable, bool allowAlternative)
        {
            ObservedAllowAlternative = allowAlternative;
            return desiredAddress;
        }
    }

    [Fact]
    public void TryReserve_PassesAllowAlternativeToFourArgOverload()
    {
        var memory = new MemoryWithFourArgAllocate();

        Assert.True(Reserve(memory, out _, allowAlternative: true));
        Assert.True(memory.ObservedAllowAlternative);
    }

    private sealed class MemoryWithSearch : FakeMemoryBase
    {
        public int SearchCalls;
        public int DirectCalls;

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong address)
        {
            SearchCalls++;
            address = desiredAddress + 0x1000;
            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable)
        {
            DirectCalls++;
            return desiredAddress;
        }
    }

    [Fact]
    public void TryReserve_PrefersSearchWhenAllowed()
    {
        var memory = new MemoryWithSearch();

        Assert.True(Reserve(memory, out var mapped, allowSearch: true));
        Assert.Equal(Desired + 0x1000, mapped);
        Assert.Equal(1, memory.SearchCalls);
        Assert.Equal(0, memory.DirectCalls);
    }

    private sealed class MemoryWithSearchNotAllowed : FakeMemoryBase
    {
        public int SearchCalls;

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong address)
        {
            SearchCalls++;
            address = desiredAddress;
            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable) => desiredAddress;
    }

    [Fact]
    public void TryReserve_SkipsSearchWhenNotAllowed()
    {
        var memory = new MemoryWithSearchNotAllowed();

        Assert.True(Reserve(memory, out var mapped, allowSearch: false));
        Assert.Equal(Desired, mapped);
        Assert.Equal(0, memory.SearchCalls);
    }

    private sealed class MemoryWithFailingSearch : FakeMemoryBase
    {
        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable) => desiredAddress;
    }

    [Fact]
    public void TryReserve_FallsBackToAllocateAtWhenSearchFails()
    {
        var memory = new MemoryWithFailingSearch();

        Assert.True(Reserve(memory, out var mapped, allowSearch: true));
        Assert.Equal(Desired, mapped);
    }

    private sealed class InnerMemoryTarget
    {
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable) => desiredAddress;
    }

    private sealed class MemoryWithInner : FakeMemoryBase
    {
        public InnerMemoryTarget Inner { get; } = new();
    }

    [Fact]
    public void TryReserve_TraversesInnerPropertyChain()
    {
        var memory = new MemoryWithInner();

        Assert.True(Reserve(memory, out var mapped));
        Assert.Equal(Desired, mapped);
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

    private sealed class MemoryReturningZero : FakeMemoryBase
    {
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable) => 0;
    }

    [Fact]
    public void TryReserve_ZeroAllocation_ReturnsFalse()
    {
        Assert.False(Reserve(new MemoryReturningZero(), out _));
    }

    private sealed class MemoryThatThrows : FakeMemoryBase
    {
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable)
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
        Assert.False(Reserve(new MemoryWithThreeArgAllocate(), out _, length: 0));
    }
}
