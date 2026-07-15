// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
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

/// <summary>
/// Tests for the multi-surface guest render-image cache: games reuse one guest
/// address for surfaces of different formats within a frame (Astro Bot renders
/// 0x53B9F0000 as R8G8Unorm and samples it as R32G32B32A32Sfloat), so the
/// cache must retain a surface per (address, format) instead of destroying the
/// earlier surface when a different-format render reuses the address.
/// </summary>
public sealed class VulkanPresenterGuestSurfaceCacheTests
{
    private const ulong Address = 0x000000053B9F0000;

    private static VulkanVideoPresenter.GuestImageResource Surface(
        ulong address,
        Format format,
        uint width,
        uint height,
        bool initialized = true,
        bool cpuBacked = false,
        ulong stamp = 0) => new()
    {
        Address = address,
        Width = width,
        Height = height,
        MipLevels = 1,
        Format = format,
        Initialized = initialized,
        IsCpuBacked = cpuBacked,
        LastUseStamp = stamp,
    };

    private static VulkanGuestDrawTexture Texture(ulong address, uint width, uint height) =>
        new(address, width, height, Format: 0, NumberType: 0, [], IsFallback: false, IsStorage: false);

    [Fact]
    public void DifferentFormatSurfacesCoexistAtOneAddress()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var rg8 = Surface(Address, Format.R8G8Unorm, 1920, 1080, stamp: 1);
        var rgba32f = Surface(Address, Format.R32G32B32A32Sfloat, 1920, 1080, stamp: 2);
        cache.Add(rg8);
        cache.Add(rgba32f);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetExact(Address, Format.R8G8Unorm, out var first));
        Assert.Same(rg8, first);
        Assert.True(cache.TryGetExact(Address, Format.R32G32B32A32Sfloat, out var second));
        Assert.Same(rgba32f, second);
    }

    [Fact]
    public void SampleBindsExactFormatSurfaceNotSibling()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var rgba32f = Surface(Address, Format.R32G32B32A32Sfloat, 1920, 1080, stamp: 1);
        // The sibling was rendered later; recency must not beat an exact hit.
        var rg8 = Surface(Address, Format.R8G8Unorm, 1920, 1080, stamp: 2);
        cache.Add(rgba32f);
        cache.Add(rg8);

        var texture = Texture(Address, 1920, 1080);
        Assert.True(cache.TryFindSampleAlias(
            texture, Format.R32G32B32A32Sfloat, out var chosen));
        Assert.Same(rgba32f, chosen);
        Assert.True(cache.TryFindSampleAlias(texture, Format.R8G8Unorm, out chosen));
        Assert.Same(rg8, chosen);
    }

    [Fact]
    public void ClassCompatibleSiblingAliasesWhenExactFormatIsMissing()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var rgba8 = Surface(Address, Format.R8G8B8A8Unorm, 1920, 1080);
        cache.Add(rgba8);

        // R16G16Sfloat shares the 32-bit texel class with R8G8B8A8Unorm; a
        // mutable-format view reinterprets it.
        Assert.True(cache.TryFindSampleAlias(
            Texture(Address, 1920, 1080), Format.R16G16Sfloat, out var chosen));
        Assert.Same(rgba8, chosen);
    }

    [Fact]
    public void CrossClassSampleFallsBackToNativeAlias()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var rgba8 = Surface(Address, Format.R8G8B8A8Unorm, 1920, 1080);
        cache.Add(rgba8);

        var texture = Texture(Address, 1920, 1080);
        // 128-bit texels cannot view a 32-bit image; the strict alias misses
        // but the rendered content is still bound through its own format.
        Assert.False(cache.TryFindSampleAlias(
            texture, Format.R32G32B32A32Sfloat, out _));
        Assert.True(cache.TryFindNativeAlias(texture, out var native));
        Assert.Same(rgba8, native);
    }

    [Fact]
    public void NativeAliasPrefersGpuRenderedSurfaceOverCpuUpload()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var cpu = Surface(
            Address, Format.R8G8B8A8Unorm, 1920, 1080, cpuBacked: true, stamp: 5);
        var gpu = Surface(Address, Format.R16G16B16A16Sfloat, 1920, 1080, stamp: 1);
        cache.Add(cpu);
        cache.Add(gpu);

        Assert.True(cache.TryFindNativeAlias(Texture(Address, 1920, 1080), out var chosen));
        Assert.Same(gpu, chosen);
    }

    [Fact]
    public void PresentResolvesMostRecentlyRenderedSurface()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var older = Surface(Address, Format.R8G8B8A8Unorm, 1920, 1080, stamp: 1);
        var latest = Surface(Address, Format.R16G16B16A16Sfloat, 1920, 1080, stamp: 2);
        cache.Add(older);
        cache.Add(latest);

        Assert.True(cache.TryFindPresentable(Address, out var presented));
        Assert.Same(latest, presented);
    }

    [Fact]
    public void PresentFallsBackToLargestInitializedSibling()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var small = Surface(Address, Format.R8G8Unorm, 256, 256, stamp: 2);
        var large = Surface(Address, Format.R8G8B8A8Unorm, 1920, 1080, stamp: 1);
        var uninitialized = Surface(
            Address, Format.R16G16B16A16Sfloat, 3840, 2160, initialized: false, stamp: 3);
        cache.Add(small);
        cache.Add(large);
        // The most recent render never completed; the flip must still find a
        // real surface and prefer the full-size one over the aux target.
        cache.Add(uninitialized);

        Assert.True(cache.TryFindPresentable(Address, out var presented));
        Assert.Same(large, presented);
    }

    [Fact]
    public void SameFormatReplaceKeepsDifferentFormatSibling()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var sibling = Surface(Address, Format.R32G32B32A32Sfloat, 1920, 1080, stamp: 1);
        var outgoing = Surface(Address, Format.R8G8B8A8Unorm, 960, 540, stamp: 2);
        cache.Add(sibling);
        cache.Add(outgoing);

        // Mirrors GetOrCreateGuestImage's same-key resize: only the matching
        // (address, format) entry is replaced and provenance survives.
        Assert.False(cache.Remove(outgoing));
        Assert.True(cache.ContainsAddress(Address));
        var replacement = Surface(Address, Format.R8G8B8A8Unorm, 1920, 1080, stamp: 3);
        cache.Add(replacement);

        Assert.True(cache.TryGetExact(Address, Format.R32G32B32A32Sfloat, out var kept));
        Assert.Same(sibling, kept);
        Assert.True(cache.TryGetExact(Address, Format.R8G8B8A8Unorm, out var current));
        Assert.Same(replacement, current);
    }

    [Fact]
    public void RemoveForgetsAddressOnlyWhenLastSurfaceGoes()
    {
        var cache = new VulkanVideoPresenter.GuestSurfaceCache();
        var first = Surface(Address, Format.R8G8Unorm, 1920, 1080, stamp: 1);
        var second = Surface(Address, Format.R32G32B32A32Sfloat, 1920, 1080, stamp: 2);
        cache.Add(first);
        cache.Add(second);

        Assert.False(cache.Remove(second));
        Assert.True(cache.ContainsAddress(Address));
        Assert.True(cache.TryFindPresentable(Address, out var promoted));
        Assert.Same(first, promoted);

        Assert.True(cache.Remove(first));
        Assert.False(cache.ContainsAddress(Address));
        Assert.False(cache.TryFindPresentable(Address, out _));
    }
}
