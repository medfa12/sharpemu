// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the presenter self-test frame generator. The generator feeds
/// VulkanVideoPresenter.Submit, which requires exactly width*height*4 BGRA
/// bytes, so size and animation properties are what keep the self-test honest.
/// </summary>
public sealed class VulkanPresenterSelfTestTests
{
    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(16u, 9u)]
    [InlineData(1280u, 720u)]
    public void GenerateTestFrame_ProducesExactBgraSize(uint width, uint height)
    {
        var frame = VulkanPresenterSelfTest.GenerateTestFrame(width, height, 0);

        Assert.Equal((int)(width * height * 4), frame.Length);
    }

    [Fact]
    public void GenerateTestFrame_IsFullyOpaque()
    {
        var frame = VulkanPresenterSelfTest.GenerateTestFrame(8, 8, 3);

        for (var offset = 3; offset < frame.Length; offset += 4)
        {
            Assert.Equal(255, frame[offset]);
        }
    }

    [Fact]
    public void GenerateTestFrame_PlacesScanlineByFrameIndex()
    {
        const uint width = 4;
        const uint height = 4;
        var frame = VulkanPresenterSelfTest.GenerateTestFrame(width, height, 2);

        var rowOffset = (int)(2 * width * 4);
        for (var x = 0; x < width; x++)
        {
            Assert.Equal(255, frame[rowOffset + x * 4]);
            Assert.Equal(255, frame[rowOffset + x * 4 + 1]);
            Assert.Equal(255, frame[rowOffset + x * 4 + 2]);
        }
    }

    [Fact]
    public void GenerateTestFrame_ScanlineWrapsAroundHeight()
    {
        const uint width = 4;
        const uint height = 4;
        var wrapped = VulkanPresenterSelfTest.GenerateTestFrame(width, height, 5);

        var rowOffset = (int)(1 * width * 4);
        Assert.Equal(255, wrapped[rowOffset]);
        Assert.Equal(255, wrapped[rowOffset + 1]);
        Assert.Equal(255, wrapped[rowOffset + 2]);
    }

    [Fact]
    public void GenerateTestFrame_AnimatesBetweenFrames()
    {
        var first = VulkanPresenterSelfTest.GenerateTestFrame(8, 8, 0);
        var second = VulkanPresenterSelfTest.GenerateTestFrame(8, 8, 1);

        Assert.NotEqual(first, second);
    }
}
