// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the device-memory ledger that keeps the presenter's live
/// device-local byte count and budget decisions honest. The Vulkan calls it
/// feeds are exercised on real hardware; the arithmetic is exercised here.
/// </summary>
public sealed class VulkanDeviceMemoryLedgerTests
{
    private const ulong Mega = 1024 * 1024;

    [Fact]
    public void TrackAndUntrack_KeepNetLiveDeviceLocalBytes()
    {
        var ledger = new VulkanDeviceMemoryLedger();

        ledger.Track(1, 50 * Mega, deviceLocal: true);
        ledger.Track(2, 8 * Mega, deviceLocal: true);
        Assert.Equal(58 * Mega, ledger.LiveDeviceLocalBytes);

        ledger.Untrack(1);
        Assert.Equal(8 * Mega, ledger.LiveDeviceLocalBytes);
        Assert.Equal(1, ledger.AllocationCount);
    }

    [Fact]
    public void HostVisibleAllocations_DoNotCountAgainstDeviceLocalBudget()
    {
        var ledger = new VulkanDeviceMemoryLedger();

        ledger.Track(1, 512 * Mega, deviceLocal: false);

        Assert.Equal(0UL, ledger.LiveDeviceLocalBytes);
        Assert.Equal(512 * Mega, ledger.LiveHostVisibleBytes);
    }

    [Fact]
    public void TrackAndUntrack_KeepNetLiveHostVisibleBytes()
    {
        var ledger = new VulkanDeviceMemoryLedger();

        ledger.Track(1, 32 * Mega, deviceLocal: false);
        ledger.Track(2, 64 * Mega, deviceLocal: false);
        ledger.Track(3, 100 * Mega, deviceLocal: true);
        Assert.Equal(96 * Mega, ledger.LiveHostVisibleBytes);

        ledger.Untrack(2);
        Assert.Equal(32 * Mega, ledger.LiveHostVisibleBytes);
        Assert.Equal(100 * Mega, ledger.LiveDeviceLocalBytes);
    }

    [Fact]
    public void ReusedHandle_MovesBytesBetweenHeapCounters()
    {
        var ledger = new VulkanDeviceMemoryLedger();

        // A handle reused with a different classification must not leave
        // stale bytes behind in the other counter.
        ledger.Track(5, 40 * Mega, deviceLocal: true);
        ledger.Track(5, 40 * Mega, deviceLocal: false);

        Assert.Equal(0UL, ledger.LiveDeviceLocalBytes);
        Assert.Equal(40 * Mega, ledger.LiveHostVisibleBytes);
        Assert.Equal(1, ledger.AllocationCount);
    }

    [Fact]
    public void WouldExceedHostVisibleBudget_DisabledWhenBudgetUnknown()
    {
        var ledger = new VulkanDeviceMemoryLedger();
        ledger.Track(1, 500 * Mega, deviceLocal: false);

        Assert.False(ledger.WouldExceedHostVisibleBudget(ulong.MaxValue / 2));
    }

    [Fact]
    public void WouldExceedHostVisibleBudget_TriggersOnlyPastTheCap()
    {
        var ledger = new VulkanDeviceMemoryLedger { HostVisibleBudgetBytes = 100 * Mega };
        ledger.Track(1, 60 * Mega, deviceLocal: false);

        Assert.False(ledger.WouldExceedHostVisibleBudget(40 * Mega));
        Assert.True(ledger.WouldExceedHostVisibleBudget(41 * Mega));
    }

    [Fact]
    public void HostVisibleBudget_IgnoresDeviceLocalBytes()
    {
        var ledger = new VulkanDeviceMemoryLedger { HostVisibleBudgetBytes = 100 * Mega };
        ledger.Track(1, 90 * Mega, deviceLocal: true);

        Assert.False(ledger.WouldExceedHostVisibleBudget(50 * Mega));
    }

    [Fact]
    public void UntrackingUnknownOrZeroHandle_IsHarmless()
    {
        var ledger = new VulkanDeviceMemoryLedger();
        ledger.Track(1, 4 * Mega, deviceLocal: true);

        ledger.Untrack(0);
        ledger.Untrack(99);

        Assert.Equal(4 * Mega, ledger.LiveDeviceLocalBytes);
    }

    [Fact]
    public void ReusedHandle_ReplacesPreviousAllocation()
    {
        var ledger = new VulkanDeviceMemoryLedger();

        // The driver may hand back a previously-freed handle value; a missed
        // Untrack must not double-count it forever.
        ledger.Track(7, 10 * Mega, deviceLocal: true);
        ledger.Track(7, 2 * Mega, deviceLocal: true);

        Assert.Equal(2 * Mega, ledger.LiveDeviceLocalBytes);
        Assert.Equal(1, ledger.AllocationCount);
    }

    [Fact]
    public void WouldExceedBudget_DisabledWhenBudgetUnknown()
    {
        var ledger = new VulkanDeviceMemoryLedger();
        ledger.Track(1, 500 * Mega, deviceLocal: true);

        Assert.False(ledger.WouldExceedBudget(ulong.MaxValue / 2));
        Assert.Equal(0UL, ledger.BytesOverBudget(ulong.MaxValue / 2));
    }

    [Fact]
    public void WouldExceedBudget_TriggersOnlyPastTheCap()
    {
        var ledger = new VulkanDeviceMemoryLedger { BudgetBytes = 100 * Mega };
        ledger.Track(1, 60 * Mega, deviceLocal: true);

        Assert.False(ledger.WouldExceedBudget(40 * Mega));
        Assert.True(ledger.WouldExceedBudget(41 * Mega));
        Assert.Equal(1 * Mega, ledger.BytesOverBudget(41 * Mega));
    }

    [Fact]
    public void BytesOverBudget_ShrinksAsAllocationsAreFreed()
    {
        var ledger = new VulkanDeviceMemoryLedger { BudgetBytes = 100 * Mega };
        ledger.Track(1, 90 * Mega, deviceLocal: true);
        ledger.Track(2, 30 * Mega, deviceLocal: true);

        Assert.Equal(70 * Mega, ledger.BytesOverBudget(50 * Mega));

        ledger.Untrack(1);
        Assert.Equal(0UL, ledger.BytesOverBudget(50 * Mega));
    }
}
