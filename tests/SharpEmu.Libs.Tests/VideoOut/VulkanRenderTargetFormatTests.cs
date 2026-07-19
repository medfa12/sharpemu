// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetFormatTests
{
    // CB_COLOR_INFO.COMP_SWAP is decoded independently of FORMAT. For the
    // COLOR_2_10_10_10 target the STD ordering exposes the low 10-bit
    // component as R (A2B10G10R10 on Vulkan); ALT reverses R and B. Ignoring
    // COMP_SWAP here left the intro compositing with dark/incorrect colors.
    [Theory]
    [InlineData(0u, Format.A2B10G10R10UnormPack32)]
    [InlineData(1u, Format.A2R10G10B10UnormPack32)]
    public void Color2101010HonorsComponentSwap(uint componentSwap, Format expected)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out var result));
        Assert.Equal(expected, result.Format);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Color2101010RejectsUnsupportedComponentSwap(uint componentSwap)
    {
        Assert.False(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out _));
    }
}
