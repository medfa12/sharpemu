// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Serializes test classes that mutate VulkanVideoPresenter's static guest
/// image registries, which xunit would otherwise run in parallel.
/// </summary>
[CollectionDefinition(VulkanPresenterStateCollection.Name, DisableParallelization = true)]
public sealed class VulkanPresenterStateCollection
{
    public const string Name = "VulkanPresenterState";
}

/// <summary>
/// Tests for render-target-to-sampled-texture aliasing: a draw that samples an
/// address rendered by an earlier pass must be captured as an
/// address-referencing texture (resolved against the rendered VkImage at
/// execution time) instead of snapshotting guest memory, which the GPU never
/// writes. Astro Bot's menu composite samples targets such as 0x5104A0000
/// rendered earlier in the same submit.
/// </summary>
[Collection(VulkanPresenterStateCollection.Name)]
public sealed class VulkanPresenterRenderTargetAliasTests
{
    private const ulong RenderTarget = 0x00000005104A0000;
    // CB register data format 10 (8_8_8_8) with numberType 0 renders the
    // target; T# code 56 samples it. Both canonicalize to R8G8B8A8Unorm.
    private const uint Rgba8RenderFormat = 10;
    private const uint Rgba8SampleFormat = 56;
    private const uint Rgba8GuestFormat = 56;

    [Fact]
    public void PendingRenderTargetIsAvailableBeforeItExecutes()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        // Enqueue-time pre-registration only: the producing draw has not run
        // on the render thread yet, so nothing is in the GPU-written set.
        VulkanVideoPresenter.RegisterPendingRenderTargetForTests(
            RenderTarget, Rgba8RenderFormat, numberType: 0);

        Assert.True(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }

    [Fact]
    public void PublishedRenderTargetIsAvailable()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            RenderTarget, Rgba8GuestFormat, 1920, 1080);

        Assert.True(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }

    [Fact]
    public void ClassCompatibleSampleFormatAliasesRenderTarget()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            RenderTarget, Rgba8GuestFormat, 1920, 1080);

        // R16G16Sfloat (dfmt 5, numberType 7) shares the 32-bit compatibility
        // class with R8G8B8A8Unorm; a mutable-format view reinterprets it.
        Assert.True(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, format: 5, numberType: 7));
    }

    [Fact]
    public void CrossClassSampleFormatStillAliasesRenderedImage()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            RenderTarget, Rgba8GuestFormat, 1920, 1080);

        // The game reuses a guest address for surfaces of different formats, so
        // a rendered target is often sampled in an incompatible format. The
        // rendered VkImage still holds real content while the guest memory
        // behind the address was never written by the GPU and reads back black,
        // so the sample defers to the rendered image (resolution binds a
        // native-format view) rather than uploading empty memory.
        Assert.True(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, format: 12, numberType: 7));
        Assert.True(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, format: 13, numberType: 0));
    }

    [Fact]
    public void CpuUploadedAddressDoesNotAlias()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        // A genuine CPU asset registers as available (for flips and refresh)
        // but must never be captured as an address-referencing texture.
        VulkanVideoPresenter.RegisterCpuGuestImageForTests(
            RenderTarget, Rgba8GuestFormat);

        Assert.False(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }

    [Fact]
    public void CpuUploadDemotesFormerRenderTarget()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.RegisterPendingRenderTargetForTests(
            RenderTarget, Rgba8RenderFormat, numberType: 0);
        // The address is re-purposed by a CPU texture upload; render-target
        // provenance must not survive it.
        VulkanVideoPresenter.RegisterCpuGuestImageForTests(
            RenderTarget, Rgba8GuestFormat);

        Assert.False(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }

    [Fact]
    public void RemovedRenderTargetDoesNotAlias()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        VulkanVideoPresenter.PublishRenderedGuestImageForTests(
            RenderTarget, Rgba8GuestFormat, 1920, 1080);
        VulkanVideoPresenter.RemoveRenderedGuestImageForTests(RenderTarget);

        Assert.False(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }

    [Fact]
    public void UnknownAddressDoesNotAlias()
    {
        VulkanVideoPresenter.ResetGuestImageTrackingForTests();

        Assert.False(VulkanVideoPresenter.IsGpuGuestImageAvailable(
            RenderTarget, Rgba8SampleFormat, numberType: 0));
    }
}
