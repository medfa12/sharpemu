// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

// FakeCpuMemory plus the IGuestAddressSpace surface the HLE scratch allocator
// resolves through ctx.Memory. Allocation requests are satisfied from a bump
// pointer inside the backed region (the requested address is ignored), so
// exports that hand out guest scratch objects can run without a live guest.
internal sealed class FakeGuestMemory : ICpuMemory, IGuestAddressSpace
{
    private readonly FakeCpuMemory _inner;
    private readonly ulong _limit;
    private ulong _next;

    public FakeGuestMemory(ulong baseAddress, int size, ulong allocationBase)
    {
        _inner = new FakeCpuMemory(baseAddress, size);
        _limit = baseAddress + (ulong)size;
        _next = allocationBase;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
        _inner.TryRead(virtualAddress, destination);

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
        _inner.TryWrite(virtualAddress, source);

    public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) =>
        TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var address) ? address : 0;

    public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
    {
        actualAddress = 0;
        if (size == 0)
        {
            return false;
        }

        var step = Math.Max(alignment, 1UL);
        var aligned = (_next + step - 1) / step * step;
        if (aligned < _next || aligned + size > _limit)
        {
            return false;
        }

        actualAddress = aligned;
        _next = aligned + size;
        return true;
    }

    public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
        TryAllocateAtOrAbove(0, size, executable: false, alignment, out address);
}
