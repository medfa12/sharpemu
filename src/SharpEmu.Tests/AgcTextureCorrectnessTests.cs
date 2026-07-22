// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AgcTextureCorrectnessTests
{
    private const ulong TextureAddress = 0x1000;

    [Fact]
    public void TwoDimensionalArrayUploadsEveryDescriptorLayer()
    {
        var memory = new SparseGuestMemory();
        var firstLayer = new byte[256];
        var secondLayer = new byte[256];
        firstLayer[0] = 0x11;
        secondLayer[0] = 0x22;
        Assert.True(memory.TryWrite(TextureAddress, firstLayer));
        Assert.True(memory.TryWrite(TextureAddress + 256, secondLayer));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcExports.TryCreateVulkanGuestDrawTexture(
            context,
            TextureDescriptor(type: 13, lastArray: 1),
            isArrayed: true,
            out var texture));

        Assert.False(texture.IsFallback);
        Assert.True(texture.ArrayedView);
        Assert.Equal(2u, texture.ArrayLayers);
        Assert.Equal(512, texture.RgbaPixels.Length);
        Assert.Equal(0x11, texture.RgbaPixels[0]);
        Assert.Equal(0x22, texture.RgbaPixels[256]);
    }

    [Fact]
    public void ArrayTextureContractUsesArrayViewAndDescriptorLayerCount()
    {
        var texture = new VulkanGuestDrawTexture(
            TextureAddress,
            2,
            1,
            10,
            0,
            new byte[512],
            IsFallback: false,
            IsStorage: false,
            ArrayedView: true,
            ArrayLayers: 2);

        Assert.Equal(2u, VulkanVideoPresenter.GetSampledTextureLayerCount(texture));
        Assert.Equal(
            ImageViewType.Type2DArray,
            VulkanVideoPresenter.GetSampledTextureViewType(texture));
    }

    private static uint[] TextureDescriptor(uint type, uint lastArray)
    {
        const uint width = 2;
        const uint height = 1;
        const uint rgba8Unorm = 56;
        return
        [
            (uint)(TextureAddress >> 8),
            (rgba8Unorm << 20) | ((width - 1) << 30),
            ((width - 1) >> 2) | ((height - 1) << 14),
            (type << 28) | 0xFACu,
            lastArray,
            0,
            0,
            0,
        ];
    }
}
