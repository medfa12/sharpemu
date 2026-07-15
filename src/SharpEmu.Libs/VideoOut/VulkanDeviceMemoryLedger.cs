// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Tracks live Vulkan device-memory allocations by handle so the presenter
/// knows how much memory is actually in use (the driver only reports gross
/// allocation traffic) and when a new allocation would push usage past the
/// budgeted fraction of a heap. Allocations are partitioned by the heap they
/// consume: device-local (VRAM) or host-visible (system RAM), each with its
/// own budget.
/// </summary>
internal sealed class VulkanDeviceMemoryLedger
{
    private readonly Dictionary<ulong, (ulong Size, bool DeviceLocal)> _allocations = new();
    private long _liveDeviceLocalBytes;
    private long _liveHostVisibleBytes;

    /// <summary>
    /// Maximum device-local bytes the presenter should keep live. Zero means
    /// the budget is unknown and budget checks are disabled.
    /// </summary>
    public ulong BudgetBytes { get; set; }

    /// <summary>
    /// Maximum host-visible bytes the presenter should keep live. Zero means
    /// the budget is unknown (or the device is UMA, where every heap is
    /// device-local) and budget checks are disabled.
    /// </summary>
    public ulong HostVisibleBudgetBytes { get; set; }

    public ulong LiveDeviceLocalBytes => (ulong)Math.Max(_liveDeviceLocalBytes, 0);

    public ulong LiveHostVisibleBytes => (ulong)Math.Max(_liveHostVisibleBytes, 0);

    public int AllocationCount => _allocations.Count;

    public void Track(ulong handle, ulong size, bool deviceLocal)
    {
        if (_allocations.Remove(handle, out var replaced))
        {
            Apply(replaced.Size, replaced.DeviceLocal, -1);
        }

        _allocations[handle] = (size, deviceLocal);
        Apply(size, deviceLocal, 1);
    }

    public void Untrack(ulong handle)
    {
        if (_allocations.Remove(handle, out var entry))
        {
            Apply(entry.Size, entry.DeviceLocal, -1);
        }
    }

    private void Apply(ulong size, bool deviceLocal, int sign)
    {
        if (deviceLocal)
        {
            _liveDeviceLocalBytes += sign * (long)size;
        }
        else
        {
            _liveHostVisibleBytes += sign * (long)size;
        }
    }

    public bool WouldExceedBudget(ulong size) =>
        BudgetBytes != 0 && LiveDeviceLocalBytes + size > BudgetBytes;

    public ulong BytesOverBudget(ulong size) =>
        WouldExceedBudget(size) ? LiveDeviceLocalBytes + size - BudgetBytes : 0;

    public bool WouldExceedHostVisibleBudget(ulong size) =>
        HostVisibleBudgetBytes != 0 && LiveHostVisibleBytes + size > HostVisibleBudgetBytes;
}
