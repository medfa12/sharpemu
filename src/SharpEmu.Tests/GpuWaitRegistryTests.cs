// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// SHARPEMU_GPU_WAIT_MODE=force writes a satisfying value into a watched
/// WAIT_REG_MEM label instead of suspending the DCB. The computed value must
/// pass the same masked comparison the wait registry uses to resume waiters,
/// and must not disturb label bits outside the mask.
/// </summary>
public sealed class GpuWaitRegistryTests
{
    private static GpuWaitRegistry.WaitingDcb Waiter(
        uint compareFunction,
        ulong reference,
        ulong mask) => new()
    {
        ReferenceValue = reference,
        Mask = mask,
        CompareFunction = compareFunction,
    };

    [Theory]
    [InlineData(1u, 0x10ul, 0xFFul, 0x50ul)]         // < : ref-1
    [InlineData(2u, 0x10ul, 0xFFul, 0x50ul)]         // <= : ref
    [InlineData(3u, 0x10ul, 0xFFul, 0x50ul)]         // == : ref
    [InlineData(4u, 0x10ul, 0xFFul, 0x10ul)]         // != : ~ref
    [InlineData(5u, 0x10ul, 0xFFul, 0x05ul)]         // >= : ref
    [InlineData(6u, 0x10ul, 0xFFul, 0x05ul)]         // > : ref+1
    [InlineData(3u, 0xDEAD_BEEFul, 0xFFFF_FFFFul, 0ul)]
    [InlineData(5u, 0x1_0000_0000ul, ulong.MaxValue, 0ul)] // 64-bit label
    public void ForceSatisfyValuePassesCompare(
        uint compareFunction,
        ulong reference,
        ulong mask,
        ulong currentValue)
    {
        var waiter = Waiter(compareFunction, reference, mask);
        Assert.False(GpuWaitRegistry.Compare(waiter, currentValue));

        Assert.True(GpuWaitRegistry.TryGetForceSatisfyValue(
            waiter, currentValue, out var satisfied));
        Assert.True(GpuWaitRegistry.Compare(waiter, satisfied));
    }

    [Fact]
    public void ForceSatisfyPreservesBitsOutsideMask()
    {
        var waiter = Waiter(compareFunction: 3, reference: 0x42, mask: 0xFF);
        var currentValue = 0xAABB_CCDD_0000_0011ul;

        Assert.True(GpuWaitRegistry.TryGetForceSatisfyValue(
            waiter, currentValue, out var satisfied));

        Assert.Equal(0xAABB_CCDD_0000_0042ul, satisfied);
        Assert.True(GpuWaitRegistry.Compare(waiter, satisfied));
    }

    [Theory]
    [InlineData(1u, 0ul, 0xFFul)]           // < 0 has no satisfying value
    [InlineData(6u, 0xFFul, 0xFFul)]        // > mask has no satisfying value
    [InlineData(3u, 0x100ul, 0xFFul)]       // == reference outside the mask
    [InlineData(5u, 0x100ul, 0xFFul)]       // >= reference outside the mask
    [InlineData(3u, 0x42ul, 0ul)]           // zero mask: writes cannot help
    public void ForceSatisfyRejectsImpossibleComparisons(
        uint compareFunction,
        ulong reference,
        ulong mask)
    {
        var waiter = Waiter(compareFunction, reference, mask);

        Assert.False(GpuWaitRegistry.TryGetForceSatisfyValue(
            waiter, currentValue: 0, out _));
    }

    [Fact]
    public void ForceSatisfy32BitLabelFitsInDword()
    {
        // 32-bit waits carry 32-bit masks and references; the satisfying value
        // must survive a 32-bit store and re-read (zero extension).
        var waiter = Waiter(compareFunction: 5, reference: 0xFFFF_FFFF, mask: 0xFFFF_FFFF);

        Assert.True(GpuWaitRegistry.TryGetForceSatisfyValue(
            waiter, currentValue: 0, out var satisfied));

        Assert.Equal(satisfied, (uint)satisfied);
        Assert.True(GpuWaitRegistry.Compare(waiter, (uint)satisfied));
    }
}
