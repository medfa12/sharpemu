// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Irregular MRT layouts (mismatched attachment extents, duplicate-address
/// slots) must execute instead of being skipped: mismatched extents render at
/// the shared minimum extent, and a duplicate slot's writes are redirected to
/// a cached scratch surface so the first slot at the address keeps the
/// authoritative content. Astro Bot's title scene submits such a draw every
/// frame.
/// </summary>
public sealed class VulkanMrtIrregularLayoutTests
{
    private static VulkanGuestRenderTarget Target(
        ulong address,
        uint width,
        uint height) => new(address, width, height, Format: 10, NumberType: 0);

    [Fact]
    public void RenderExtentIsSharedMinimumAcrossTargets()
    {
        var extent = VulkanVideoPresenter.ComputeMrtRenderExtent(
        [
            Target(0x1000, 1920, 1080),
            Target(0x2000, 960, 540),
            Target(0x3000, 1920, 2160),
        ]);

        Assert.Equal((960u, 540u), extent);
    }

    [Fact]
    public void MatchingExtentsAreUnchanged()
    {
        var extent = VulkanVideoPresenter.ComputeMrtRenderExtent(
        [
            Target(0x1000, 1920, 1080),
            Target(0x2000, 1920, 1080),
        ]);

        Assert.Equal((1920u, 1080u), extent);
    }

    [Fact]
    public void DistinctAddressesReportNoAliasedSlots()
    {
        Assert.Null(VulkanVideoPresenter.FindAliasedColorSlots(
        [
            Target(0x1000, 1920, 1080),
            Target(0x2000, 1920, 1080),
            Target(0x3000, 1920, 1080),
        ]));
    }

    [Fact]
    public void DuplicateAddressFlagsLaterSlotOnly()
    {
        var aliased = VulkanVideoPresenter.FindAliasedColorSlots(
        [
            Target(0x1000, 1920, 1080),
            Target(0x2000, 1920, 1080),
            Target(0x1000, 1920, 1080),
        ]);

        Assert.NotNull(aliased);
        Assert.False(aliased[0]);
        Assert.False(aliased[1]);
        Assert.True(aliased[2]);
    }

    [Fact]
    public void EveryRepeatOfAnAddressAfterTheFirstIsFlagged()
    {
        var aliased = VulkanVideoPresenter.FindAliasedColorSlots(
        [
            Target(0x1000, 1920, 1080),
            Target(0x1000, 1920, 1080),
            Target(0x1000, 1920, 1080),
        ]);

        Assert.NotNull(aliased);
        Assert.False(aliased[0]);
        Assert.True(aliased[1]);
        Assert.True(aliased[2]);
    }

    [Fact]
    public void ScratchAddressesAreDistinctPerSlotAndExtent()
    {
        var bySlot0 = VulkanVideoPresenter.AliasedSlotScratchAddress(0, 1920, 1080);
        var bySlot1 = VulkanVideoPresenter.AliasedSlotScratchAddress(1, 1920, 1080);
        var bySmallerExtent = VulkanVideoPresenter.AliasedSlotScratchAddress(1, 960, 540);

        Assert.NotEqual(bySlot0, bySlot1);
        Assert.NotEqual(bySlot1, bySmallerExtent);
    }

    [Fact]
    public void ScratchAddressesSitAboveCanonicalGuestAddresses()
    {
        // Guest user VAs stay within the canonical 48-bit range; the scratch
        // keyspace must never collide with a real render target address.
        var scratch = VulkanVideoPresenter.AliasedSlotScratchAddress(7, 16384, 16384);
        Assert.True(scratch >= 0xFFFF_0000_0000_0000UL);
    }
}
