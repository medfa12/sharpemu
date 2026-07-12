// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class TrackedCpuMemoryTests
{
    private sealed class ScriptedMemory(bool readResult, bool writeResult) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => readResult;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => writeResult;
    }

    private sealed class AllocatingMemory : ICpuMemory, IGuestMemoryAllocator
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => true;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0xABCD_0000;
            return true;
        }
    }

    [Fact]
    public void SuccessfulAccess_LeavesNoFailure()
    {
        var memory = new TrackedCpuMemory(new ScriptedMemory(readResult: true, writeResult: true));

        Assert.True(memory.TryRead(0x1000, new byte[4]));
        Assert.True(memory.TryWrite(0x1000, new byte[4]));
        Assert.Null(memory.LastFailure);
    }

    [Fact]
    public void FailedRead_RecordsAddressLengthAndDirection()
    {
        var memory = new TrackedCpuMemory(new ScriptedMemory(readResult: false, writeResult: true));

        Assert.False(memory.TryRead(0xDEAD_0000, new byte[8]));

        Assert.True(memory.LastFailure.HasValue);
        var failure = memory.LastFailure.Value;
        Assert.Equal(0xDEAD_0000UL, failure.Address);
        Assert.Equal(8, failure.Size);
        Assert.False(failure.IsWrite);
    }

    [Fact]
    public void FailedWrite_RecordsWriteDirection()
    {
        var memory = new TrackedCpuMemory(new ScriptedMemory(readResult: true, writeResult: false));

        Assert.False(memory.TryWrite(0xBEEF_0000, new byte[2]));

        Assert.True(memory.LastFailure.HasValue);
        var failure = memory.LastFailure.Value;
        Assert.True(failure.IsWrite);
        Assert.Equal(2, failure.Size);
    }

    [Fact]
    public void TryAllocateGuestMemory_DelegatesWhenInnerSupportsIt()
    {
        var memory = new TrackedCpuMemory(new AllocatingMemory());

        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x10, out var address));
        Assert.Equal(0xABCD_0000UL, address);
    }

    [Fact]
    public void TryAllocateGuestMemory_FailsWhenInnerCannotAllocate()
    {
        var memory = new TrackedCpuMemory(new ScriptedMemory(readResult: true, writeResult: true));

        Assert.False(memory.TryAllocateGuestMemory(0x1000, 0x10, out var address));
        Assert.Equal(0UL, address);
    }
}
