// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

public sealed class VirtualMemoryTests
{
    private const ulong Base = 0x0000_0004_0000_0000UL;

    [Fact]
    public void Map_ZeroSize_Throws()
    {
        var memory = new VirtualMemory();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => memory.Map(Base, 0, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_FileDataLargerThanMemorySize_Throws()
    {
        var memory = new VirtualMemory();
        var fileData = new byte[0x20];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => memory.Map(Base, 0x10, 0, fileData, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_RegionLargerThanTwoGigabytes_Throws()
    {
        var memory = new VirtualMemory();

        Assert.Throws<NotSupportedException>(
            () => memory.Map(Base, (ulong)int.MaxValue + 1, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_AddressRangeOverflowsAddressSpace_Throws()
    {
        var memory = new VirtualMemory();

        Assert.Throws<OverflowException>(
            () => memory.Map(ulong.MaxValue - 0x10, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Theory]
    [InlineData(0x000UL)] // identical start
    [InlineData(0x080UL)] // starts inside existing region
    public void Map_OverlappingRegion_Throws(ulong offset)
    {
        var memory = new VirtualMemory();
        memory.Map(Base, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.Throws<InvalidOperationException>(
            () => memory.Map(Base + offset, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_RegionContainingExistingRegion_Throws()
    {
        var memory = new VirtualMemory();
        memory.Map(Base + 0x100, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.Throws<InvalidOperationException>(
            () => memory.Map(Base, 0x1000, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_AdjacentRegions_Succeeds()
    {
        var memory = new VirtualMemory();
        memory.Map(Base, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);
        memory.Map(Base + 0x100, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.Equal(2, memory.SnapshotRegions().Count);
    }

    [Fact]
    public void TryRead_UnmappedAddress_ReturnsFalse()
    {
        var memory = new VirtualMemory();

        Assert.False(memory.TryRead(Base, new byte[1]));
    }

    [Fact]
    public void TryRead_ReturnsMappedFileData()
    {
        var memory = new VirtualMemory();
        byte[] fileData = [0x11, 0x22, 0x33, 0x44];
        memory.Map(Base, 0x100, 0, fileData, ProgramHeaderFlags.Read);

        var buffer = new byte[4];
        Assert.True(memory.TryRead(Base, buffer));
        Assert.Equal(fileData, buffer);
    }

    [Fact]
    public void TryRead_BeyondFileData_IsZeroFilled()
    {
        var memory = new VirtualMemory();
        byte[] fileData = [0xFF, 0xFF];
        memory.Map(Base, 0x10, 0, fileData, ProgramHeaderFlags.Read);

        var buffer = new byte[0x10];
        Assert.True(memory.TryRead(Base, buffer));
        Assert.Equal(0xFF, buffer[1]);
        Assert.All(buffer[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TryWrite_ThenRead_RoundTripsAtOffset()
    {
        var memory = new VirtualMemory();
        memory.Map(Base, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.True(memory.TryWrite(Base + 0x40, payload));

        var buffer = new byte[4];
        Assert.True(memory.TryRead(Base + 0x40, buffer));
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public void TryRead_PastEndOfRegion_ReturnsFalse()
    {
        var memory = new VirtualMemory();
        memory.Map(Base, 0x10, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.False(memory.TryRead(Base + 0x08, new byte[0x10]));
    }

    [Fact]
    public void TryRead_AcrossAdjacentRegions_ReturnsFalse()
    {
        // Documents current behavior: regions are independent backing arrays, so a
        // read spanning two contiguously mapped regions fails rather than stitching.
        var memory = new VirtualMemory();
        memory.Map(Base, 0x10, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);
        memory.Map(Base + 0x10, 0x10, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.False(memory.TryRead(Base + 0x08, new byte[0x10]));
    }

    [Fact]
    public void TryWrite_UnmappedAddress_ReturnsFalse()
    {
        var memory = new VirtualMemory();

        Assert.False(memory.TryWrite(Base, new byte[] { 0x01 }));
    }

    [Fact]
    public void SnapshotRegions_ReportsMappingMetadata()
    {
        var memory = new VirtualMemory();
        byte[] fileData = [0x01, 0x02];
        memory.Map(Base, 0x100, 0x2000, fileData, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(Base, region.VirtualAddress);
        Assert.Equal(0x100UL, region.MemorySize);
        Assert.Equal(0x2000UL, region.FileOffset);
        Assert.Equal(2UL, region.FileSize);
        Assert.Equal(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, region.Protection);
    }

    [Fact]
    public void Clear_RemovesAllRegions()
    {
        var memory = new VirtualMemory();
        memory.Map(Base, 0x100, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        memory.Clear();

        Assert.Empty(memory.SnapshotRegions());
        Assert.False(memory.TryRead(Base, new byte[1]));
    }
}
