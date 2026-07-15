// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the flip-to-source redirect: a flip of a display buffer that was
/// registered through sceVideoOutRegisterBuffers but never written by the HLE
/// GPU must present the most recent full-screen render target instead of being
/// dropped. Astro Bot's menu flips 0x507410000/0x5093F0000 while its composite
/// passes render to internal 1080p targets.
/// </summary>
public sealed class VulkanPresenterFlipRedirectTests
{
    private const ulong DisplayBuffer0 = 0x0000000507410000;
    private const ulong DisplayBuffer1 = 0x00000005093F0000;
    private const ulong CompositeTarget = 0x0000000513560000;
    private const uint Rgba8Format = 0x10;

    [Fact]
    public void RegisteredDisplayBufferRedirectsToLatestPresentableRender()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer1, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source0));
        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer1, out var source1));
        Assert.Equal(CompositeTarget, source0);
        Assert.Equal(CompositeTarget, source1);
    }

    [Fact]
    public void UnregisteredAddressDoesNotRedirect()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080);

        Assert.False(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out _));
    }

    [Fact]
    public void SmallRenderTargetsAreNotPresentableSources()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        // Shadow maps, LUTs and 32x32 aux targets must never become the frame.
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x50740E000, Rgba8Format, 32, 32);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53D410000, Rgba8Format, 512, 512);

        Assert.False(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out _));
    }

    [Fact]
    public void LatestPresentablePublishWins()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 1920, 1080);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 3328, 1872);
        // A later small publish must not displace the full-screen source.
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x50740E000, Rgba8Format, 32, 32);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(CompositeTarget, source);
    }

    [Fact]
    public void EvictedSourceStopsRedirecting()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080);
        VulkanVideoPresenter.RemoveRenderedGuestImageForTests(CompositeTarget);

        Assert.False(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out _));
    }

    [Fact]
    public void CompositePassOutranksLaterIntermediatePass()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        // Astro Bot's menu: a 15-input-texture composite pass followed by
        // trailing 0-texture geometry/clear passes that publish last.
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 2432, 1368, inputTextureCount: 0);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(CompositeTarget, source);
    }

    [Fact]
    public void HighestInputTextureCountWins()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 1920, 1080, inputTextureCount: 7);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53D4A0000, Rgba8Format, 2432, 1368, inputTextureCount: 2);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(CompositeTarget, source);
    }

    [Fact]
    public void LatchedCompositePersistsIntoNextFrameWindow()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        // Flip closes the window; the composite must keep resolving until the
        // next window elects a candidate of its own.
        VulkanVideoPresenter.LatchFrameCompositeForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 2432, 1368, inputTextureCount: 0);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(CompositeTarget, source);
    }

    [Fact]
    public void NewFrameCompositeReplacesLatchedComposite()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        VulkanVideoPresenter.LatchFrameCompositeForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(CompositeTarget, source);
    }

    [Fact]
    public void ObservedCopyIntoDisplayBufferOverridesComposite()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 3840, 2160, inputTextureCount: 0);
        // A DMA copy into the flipped buffer names the authoritative source.
        VulkanVideoPresenter.NoteGuestMemoryCopy(
            DisplayBuffer0, 0x53B9F0000UL, 1024 * 1024);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(0x53B9F0000UL, source);
    }

    [Fact]
    public void EvictedCompositeFallsBackToLatestTarget()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterKnownDisplayBuffer(DisplayBuffer0, Rgba8Format);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            CompositeTarget, Rgba8Format, 1920, 1080, inputTextureCount: 15);
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            0x53B9F0000, Rgba8Format, 1920, 1080, inputTextureCount: 0);
        VulkanVideoPresenter.RemoveRenderedGuestImageForTests(CompositeTarget);

        Assert.True(VulkanVideoPresenter.TryResolveDisplayBufferSourceForTests(
            DisplayBuffer0, out var source));
        Assert.Equal(0x53B9F0000UL, source);
    }
}
