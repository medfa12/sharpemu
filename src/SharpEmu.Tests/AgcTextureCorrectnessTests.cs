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

    [Fact]
    public void MipZeroReadsFromEndOfGfx10MipChain()
    {
        const uint width = 64;
        const uint height = 64;
        const ulong baseMipOffset = 8192;
        var memory = new SparseGuestMemory();
        var mipChain = new byte[24_576];
        Array.Fill(mipChain, (byte)0x11);
        mipChain[(int)baseMipOffset] = 0x22;
        Assert.True(memory.TryWrite(TextureAddress, mipChain));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcExports.TryCreateVulkanGuestDrawTexture(
            context,
            TextureDescriptor(
                type: 9,
                lastArray: 0,
                width,
                height,
                tileMode: 5,
                maxMip: 2),
            isArrayed: false,
            out var texture));

        Assert.False(texture.IsFallback);
        Assert.Equal(0x22, texture.RgbaPixels[0]);
    }

    [Fact]
    public void MipZeroIsCroppedFromGfx10MipTailBlock()
    {
        const uint width = 16;
        const uint height = 16;
        var memory = new SparseGuestMemory();
        var mipTail = new byte[4096];
        mipTail[0] = 0x11;
        var mipZeroOffset = Gfx10Detiler.GetTiledByteOffset(
            tileMode: 5,
            x: 16,
            y: 0,
            pitchElements: 32,
            bytesPerElement: 4);
        mipTail[(int)mipZeroOffset] = 0x33;
        Assert.True(memory.TryWrite(TextureAddress, mipTail));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(AgcExports.TryCreateVulkanGuestDrawTexture(
            context,
            TextureDescriptor(
                type: 9,
                lastArray: 0,
                width,
                height,
                tileMode: 5,
                maxMip: 2),
            isArrayed: false,
            out var texture));

        Assert.False(texture.IsFallback);
        Assert.Equal(0x33, texture.RgbaPixels[0]);
    }

    private static uint[] TextureDescriptor(
        uint type,
        uint lastArray,
        uint width = 2,
        uint height = 1,
        uint tileMode = 0,
        uint maxMip = 0)
    {
        const uint rgba8Unorm = 56;
        return
        [
            (uint)(TextureAddress >> 8),
            (rgba8Unorm << 20) | ((width - 1) << 30),
            ((width - 1) >> 2) | ((height - 1) << 14),
            (type << 28) | (tileMode << 20) | 0xFACu,
            lastArray,
            maxMip << 4,
            0,
            0,
        ];
    }
}
