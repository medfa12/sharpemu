// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Stage 2 ordered-flip composite redirect. The game's final title composite is
/// a fullscreen pass that samples the frame's layers yet names no color render
/// target (hardware relies on the AGC flip to name the scanout surface). These
/// tests cover the pure retention criterion (which draws AgcExports retains as
/// the pending targetless composite, and that the SHARPEMU_ORDERED_FLIP gate
/// keeps default runs a no-op), the sce-pixel-format to render-target mapping,
/// and that the presenter redirect enqueues an offscreen draw into the flipped
/// display buffer and pre-registers that address so the ordered capture
/// snapshots the freshly rendered scanout surface. The GPU render itself needs a
/// Vulkan device and is exercised in the emulator, not here.
/// </summary>
public sealed class AgcDeferredCompositeRetentionTests
{
    [Fact]
    public void RetainsFullscreenCompositeWhenGateOnAndNoTargetButSamplesInputs()
    {
        Assert.True(AgcExports.ShouldRetainTargetlessComposite(
            orderedFlipEnabled: true,
            hasColorTarget: false,
            hasStorageTarget: false,
            sampledTextureCount: 15));
    }

    [Fact]
    public void GateOffIsAlwaysNoOp()
    {
        // Off: the exact composite that would be retained when on is dropped, so
        // default runs behave byte-for-byte as before.
        Assert.False(AgcExports.ShouldRetainTargetlessComposite(
            orderedFlipEnabled: false,
            hasColorTarget: false,
            hasStorageTarget: false,
            sampledTextureCount: 15));
    }

    [Fact]
    public void DrawsWithAColorTargetAreNeverRetained()
    {
        Assert.False(AgcExports.ShouldRetainTargetlessComposite(
            orderedFlipEnabled: true,
            hasColorTarget: true,
            hasStorageTarget: false,
            sampledTextureCount: 15));
    }

    [Fact]
    public void StorageSinkDrawsAreNeverRetained()
    {
        Assert.False(AgcExports.ShouldRetainTargetlessComposite(
            orderedFlipEnabled: true,
            hasColorTarget: false,
            hasStorageTarget: true,
            sampledTextureCount: 15));
    }

    [Fact]
    public void TargetlessDrawsThatSampleNothingAreNeverRetained()
    {
        // A geometry/clear pass with no target and no sampled input cannot be a
        // composite; nothing consumes it, so it is not retained.
        Assert.False(AgcExports.ShouldRetainTargetlessComposite(
            orderedFlipEnabled: true,
            hasColorTarget: false,
            hasStorageTarget: false,
            sampledTextureCount: 0));
    }
}

public sealed class DisplayBufferRenderTargetFormatTests
{
    private const ulong SceVideoOutPixelFormatA2R10G10B10 = 0x88060000;
    private const ulong SceVideoOutPixelFormatB8G8R8A8Unorm = 0x8100000000000000;
    private const ulong SceVideoOutPixelFormatR8G8B8A8Unorm = 0x8100000022000000;

    [Fact]
    public void A2R10G10B10MapsToFormat9AltSwap()
    {
        Assert.True(VideoOutExports.TryGetDisplayBufferRenderTargetFormat(
            SceVideoOutPixelFormatA2R10G10B10,
            out var format,
            out var numberType,
            out var componentSwap));

        // COLOR_2_10_10_10 with COMP_SWAP=ALT decodes to A2R10G10B10 (R high),
        // matching the 3840x2160 scanout surface the title flips.
        Assert.Equal(9u, format);
        Assert.Equal(0u, numberType);
        Assert.Equal(1u, componentSwap);
    }

    [Fact]
    public void Bgra8MapsToFormat10()
    {
        Assert.True(VideoOutExports.TryGetDisplayBufferRenderTargetFormat(
            SceVideoOutPixelFormatB8G8R8A8Unorm,
            out var format,
            out _,
            out _));
        Assert.Equal(10u, format);
    }

    [Fact]
    public void Rgba8MapsToFormat10()
    {
        Assert.True(VideoOutExports.TryGetDisplayBufferRenderTargetFormat(
            SceVideoOutPixelFormatR8G8B8A8Unorm,
            out var format,
            out _,
            out var componentSwap));
        Assert.Equal(10u, format);
        Assert.Equal(0u, componentSwap);
    }

    [Fact]
    public void UnknownFormatIsRejected()
    {
        Assert.False(VideoOutExports.TryGetDisplayBufferRenderTargetFormat(
            0xDEAD_BEEFUL, out _, out _, out _));
    }
}

[Collection(VulkanPresenterStateCollection.Name)]
public sealed class DisplayCompositeDrawTests : IDisposable
{
    // The A2R10G10B10 4K scanout surface Astro Bot's title flips.
    private const ulong DisplayBuffer = 0x0000000507410000;
    private const uint Width = 3840;
    private const uint Height = 2160;

    public DisplayCompositeDrawTests()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
    }

    public void Dispose()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
    }

    private static VulkanGuestRenderTarget A2R10G10B10Target() =>
        new(DisplayBuffer, Width, Height, Format: 9, NumberType: 0, ComponentSwap: 1);

    [Fact]
    public void EnqueuesOffscreenDrawAndPreRegistersScanoutAddress()
    {
        Assert.False(VulkanVideoPresenter.IsGpuGuestImageForTests(DisplayBuffer));

        var rendered = VulkanVideoPresenter.TrySubmitDisplayCompositeDraw(
            pixelSpirv: new byte[] { 1, 2, 3, 4 },
            textures: Array.Empty<VulkanGuestDrawTexture>(),
            globalMemoryBuffers: Array.Empty<VulkanGuestMemoryBuffer>(),
            attributeCount: 0,
            target: A2R10G10B10Target());

        Assert.True(rendered);
        // The offscreen draw is queued ahead of any flip capture, and the scanout
        // address is pre-registered as a GPU image so the following flip resolves
        // known=true and captures this address directly instead of an internal
        // composite target.
        Assert.Equal(1, VulkanVideoPresenter.PendingGuestWorkCountForTests());
        Assert.True(VulkanVideoPresenter.IsGpuGuestImageForTests(DisplayBuffer));
    }

    [Fact]
    public void RejectsEmptyPixelShaderWithoutTouchingState()
    {
        var rendered = VulkanVideoPresenter.TrySubmitDisplayCompositeDraw(
            pixelSpirv: Array.Empty<byte>(),
            textures: Array.Empty<VulkanGuestDrawTexture>(),
            globalMemoryBuffers: Array.Empty<VulkanGuestMemoryBuffer>(),
            attributeCount: 0,
            target: A2R10G10B10Target());

        Assert.False(rendered);
        Assert.Equal(0, VulkanVideoPresenter.PendingGuestWorkCountForTests());
        Assert.False(VulkanVideoPresenter.IsGpuGuestImageForTests(DisplayBuffer));
    }

    [Fact]
    public void RejectsUnmappedTargetFormat()
    {
        // Format 0 has no guest-texture mapping; the redirect must decline rather
        // than register an address the render path cannot produce.
        var rendered = VulkanVideoPresenter.TrySubmitDisplayCompositeDraw(
            pixelSpirv: new byte[] { 1, 2, 3, 4 },
            textures: Array.Empty<VulkanGuestDrawTexture>(),
            globalMemoryBuffers: Array.Empty<VulkanGuestMemoryBuffer>(),
            attributeCount: 0,
            target: new VulkanGuestRenderTarget(
                DisplayBuffer, Width, Height, Format: 0, NumberType: 0));

        Assert.False(rendered);
        Assert.False(VulkanVideoPresenter.IsGpuGuestImageForTests(DisplayBuffer));
    }
}
