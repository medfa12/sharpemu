// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

// Holds DCBs whose parsing was suspended on an unsatisfied WAIT_REG_MEM condition.
// AgcExports re-checks every waiter against guest memory on each submit and resumes
// the ones whose condition became true (labels are advanced by ReleaseMem/WriteData/
// DmaData packets or by direct CPU writes).
internal static class GpuWaitRegistry
{
    public struct WaitingDcb
    {
        public ulong CommandBufferAddress;
        public ulong ResumeAddress;
        public uint TotalDwords;
        public uint ResumeOffset;
        public ulong WaitAddress;
        public ulong ReferenceValue;
        public ulong Mask;
        public uint CompareFunction;
        public bool Is64Bit;
        public object? State;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, List<WaitingDcb>> _waiters = new();

    public static void Register(ulong address, WaitingDcb waiter)
    {
        waiter.WaitAddress = address;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                list = new List<WaitingDcb>();
                _waiters.Add(address, list);
            }

            list.Add(waiter);
        }
    }

    // Re-evaluates every registered waiter. readValue receives (address, is64Bit) and
    // returns null when the memory is unreadable; such waiters are kept registered.
    public static List<WaitingDcb>? CollectSatisfied(Func<ulong, bool, ulong?> readValue)
    {
        List<WaitingDcb>? woken = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var value = readValue(address, list[i].Is64Bit);
                    if (value is null || !Compare(list[i], value.Value))
                    {
                        continue;
                    }

                    woken ??= new List<WaitingDcb>();
                    woken.Add(list[i]);
                    list.RemoveAt(i);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied != null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return woken;
    }

    // Computes a label value that satisfies the waiter's comparison: bits outside
    // the mask keep their current value and the masked bits are replaced with a
    // value derived from the reference (ref-1 for '<', ref for '<='/'=='/'>=',
    // ~ref for '!=', ref+1 for '>'). Returns false when no masked value can pass
    // Compare (e.g. "< 0", "> mask", a reference with bits outside the mask, or a
    // zero mask). Only the SHARPEMU_GPU_WAIT_MODE=force legacy path may use this
    // to mutate a watched label.
    public static bool TryGetForceSatisfyValue(
        in WaitingDcb waiter,
        ulong currentValue,
        out ulong satisfied)
    {
        satisfied = 0;
        var mask = waiter.Mask;
        if (mask == 0)
        {
            return false;
        }

        var reference = waiter.ReferenceValue;
        var candidate = waiter.CompareFunction switch
        {
            1 => (reference - 1) & mask,     // <
            2 or 3 or 5 => reference & mask, // <=, ==, >=
            4 => ~reference & mask,          // !=
            6 => (reference + 1) & mask,     // >
            _ => currentValue & mask,        // 0 "always" / 7 reserved already pass
        };

        var value = (currentValue & ~mask) | candidate;
        if (!Compare(waiter, value))
        {
            return false;
        }

        satisfied = value;
        return true;
    }

    public static bool Compare(in WaitingDcb waiter, ulong value)
    {
        var masked = value & waiter.Mask;
        return waiter.CompareFunction switch
        {
            1 => masked < waiter.ReferenceValue,
            2 => masked <= waiter.ReferenceValue,
            3 => masked == waiter.ReferenceValue,
            4 => masked != waiter.ReferenceValue,
            5 => masked >= waiter.ReferenceValue,
            6 => masked > waiter.ReferenceValue,
            // 0 is "always" in the PM4 encoding and 7 is reserved; treating both as
            // satisfied keeps a malformed packet from suspending its DCB forever.
            _ => true,
        };
    }
}
