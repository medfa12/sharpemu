// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Tracks live Vulkan device-memory allocations by handle so the presenter
/// knows how much device-local memory is actually in use (the driver only
/// reports gross allocation traffic) and when a new allocation would push
/// usage past the budgeted fraction of the device-local heap.
/// </summary>
internal sealed class VulkanDeviceMemoryLedger
{
    private readonly Dictionary<ulong, (ulong Size, bool DeviceLocal)> _allocations = new();
    private long _liveDeviceLocalBytes;

    /// <summary>
    /// Maximum device-local bytes the presenter should keep live. Zero means
    /// the budget is unknown and budget checks are disabled.
    /// </summary>
    public ulong BudgetBytes { get; set; }

    public ulong LiveDeviceLocalBytes => (ulong)Math.Max(_liveDeviceLocalBytes, 0);

    public int AllocationCount => _allocations.Count;

    public void Track(ulong handle, ulong size, bool deviceLocal)
    {
        if (_allocations.Remove(handle, out var replaced) && replaced.DeviceLocal)
        {
            _liveDeviceLocalBytes -= (long)replaced.Size;
        }

        _allocations[handle] = (size, deviceLocal);
        if (deviceLocal)
        {
            _liveDeviceLocalBytes += (long)size;
        }
    }

    public void Untrack(ulong handle)
    {
        if (_allocations.Remove(handle, out var entry) && entry.DeviceLocal)
        {
            _liveDeviceLocalBytes -= (long)entry.Size;
        }
    }

    public bool WouldExceedBudget(ulong size) =>
        BudgetBytes != 0 && LiveDeviceLocalBytes + size > BudgetBytes;

    public ulong BytesOverBudget(ulong size) =>
        WouldExceedBudget(size) ? LiveDeviceLocalBytes + size - BudgetBytes : 0;
}
