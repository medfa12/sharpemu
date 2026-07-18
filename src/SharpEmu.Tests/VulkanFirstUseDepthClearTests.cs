// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// First-use depth initialization must be compare-neutral for the draw's
/// ZFUNC: reverse-Z Greater/GreaterOrEqual tests start at the 0.0 far plane
/// (a hardcoded 1.0 fill silently rejects every fragment of the frame's
/// first depth-tested draw), Less/LessOrEqual start at 1.0, and compares
/// with no neutral value fall back to the guest DB_DEPTH_CLEAR value.
/// </summary>
public sealed class VulkanFirstUseDepthClearTests
{
    [Theory]
    [InlineData(1u)] // Less
    [InlineData(3u)] // LessOrEqual
    public void LessStyleCompareInitializesToOne(uint compareOp)
    {
        Assert.Equal(
            1.0f,
            VulkanVideoPresenter.SelectFirstUseDepthClearValue(compareOp, 0.25f));
    }

    [Theory]
    [InlineData(4u)] // Greater
    [InlineData(6u)] // GreaterOrEqual
    public void ReverseZCompareInitializesToZero(uint compareOp)
    {
        Assert.Equal(
            0.0f,
            VulkanVideoPresenter.SelectFirstUseDepthClearValue(compareOp, 0.25f));
    }

    [Theory]
    [InlineData(0u)] // Never
    [InlineData(2u)] // Equal
    [InlineData(5u)] // NotEqual
    [InlineData(7u)] // Always
    public void NonOrderingCompareUsesGuestClearDepth(uint compareOp)
    {
        Assert.Equal(
            0.25f,
            VulkanVideoPresenter.SelectFirstUseDepthClearValue(compareOp, 0.25f));
    }
}
