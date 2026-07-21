// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

public sealed class DrawObjectPoolMathTests
{
    [Theory]
    [InlineData(4096UL, 4096UL)]
    [InlineData(4097UL, 8192UL)]
    [InlineData(0UL, 4UL)]
    [InlineData(1UL, 4UL)]
    [InlineData(3UL, 4UL)]
    [InlineData(1UL << 40, 1UL << 40)]
    public void SizeClass_RoundsUpToPowerOfTwoWithFloor(ulong input, ulong expected)
    {
        Assert.Equal(expected, DrawObjectPoolMath.SizeClass(input));
    }

    [Fact]
    public void SelectLruEvictions_ZeroRequest_ReturnsEmpty()
    {
        var entries = new (long LastUse, ulong Bytes)[] { (1, 100) };

        Assert.Empty(DrawObjectPoolMath.SelectLruEvictions(entries, 0));
    }

    [Fact]
    public void SelectLruEvictions_SingleOldestEntryCoversRequest()
    {
        var entries = new (long LastUse, ulong Bytes)[]
        {
            (5, 200),
            (1, 500),
            (3, 300),
        };

        var result = DrawObjectPoolMath.SelectLruEvictions(entries, 400);

        Assert.Equal([1], result);
    }

    [Fact]
    public void SelectLruEvictions_PicksOldestFirstCumulatively()
    {
        var entries = new (long LastUse, ulong Bytes)[]
        {
            (5, 100), // index 0
            (1, 50),  // index 1 (oldest)
            (3, 50),  // index 2
        };

        var result = DrawObjectPoolMath.SelectLruEvictions(entries, 90);

        Assert.Equal([1, 2], result);
    }

    [Fact]
    public void SelectLruEvictions_TiesBreakByAscendingIndex()
    {
        var entries = new (long LastUse, ulong Bytes)[]
        {
            (1, 10),
            (1, 10),
            (1, 10),
        };

        var result = DrawObjectPoolMath.SelectLruEvictions(entries, 15);

        Assert.Equal([0, 1], result);
    }

    [Fact]
    public void SelectLruEvictions_RequestExceedsTotal_ReturnsEverything()
    {
        var entries = new (long LastUse, ulong Bytes)[]
        {
            (2, 10),
            (1, 10),
        };

        var result = DrawObjectPoolMath.SelectLruEvictions(entries, 1000);

        Assert.Equal(2, result.Length);
        Assert.Contains(0, result);
        Assert.Contains(1, result);
    }

    [Fact]
    public void PooledTextureImageKey_SameFieldsAreEqual()
    {
        var a = new PooledTextureImageKey(1, 64, 128, true);
        var b = new PooledTextureImageKey(1, 64, 128, true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(2, 64, 128, true)]
    [InlineData(1, 65, 128, true)]
    [InlineData(1, 64, 129, true)]
    [InlineData(1, 64, 128, false)]
    public void PooledTextureImageKey_EachFieldBreaksEquality(
        int format, uint width, uint height, bool attachmentUsage)
    {
        var baseline = new PooledTextureImageKey(1, 64, 128, true);
        var other = new PooledTextureImageKey(format, width, height, attachmentUsage);

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void DescriptorPoolBucketKey_SameFieldsAreEqual()
    {
        var a = new DescriptorPoolBucketKey(1, 2, 3);
        var b = new DescriptorPoolBucketKey(1, 2, 3);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DescriptorPoolBucketKey_IsPositionSensitive()
    {
        var a = new DescriptorPoolBucketKey(1, 0, 2);
        var b = new DescriptorPoolBucketKey(1, 2, 0);

        Assert.NotEqual(a, b);
    }
}
