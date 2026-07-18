// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Ordered-flip capture (SHARPEMU_ORDERED_FLIP). On a display-buffer flip the
/// presenter must freeze the resolved source render target into an immutable,
/// version-tagged snapshot in guest-queue order and present that, instead of
/// lazily sampling the mutable render target after guest work drains (which
/// reads the next frame's overwritten double-buffered contents). These tests
/// cover the version sequence, the enqueue payload, and that the feature gate
/// leaves the old composite-heuristic Presentation shape byte-for-byte intact
/// when off. The GPU capture itself (ExecuteOrderedGuestFlip) needs a Vulkan
/// device and is exercised in the emulator, not here.
/// </summary>
[Collection(VulkanPresenterStateCollection.Name)]
public sealed class VulkanOrderedFlipTests : IDisposable
{
    private const ulong SourceAddress = 0x0000000513560000;
    private const uint Width = 3840;
    private const uint Height = 2160;

    private readonly string? _previousGate;

    public VulkanOrderedFlipTests()
    {
        _previousGate = Environment.GetEnvironmentVariable("SHARPEMU_ORDERED_FLIP");
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", _previousGate);
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
    }

    [Fact]
    public void GateDefaultsOffAndFollowsEnvironment()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", null);
        Assert.False(VulkanVideoPresenter.ShouldUseOrderedGuestFlip());

        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", "0");
        Assert.False(VulkanVideoPresenter.ShouldUseOrderedGuestFlip());

        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", "1");
        Assert.True(VulkanVideoPresenter.ShouldUseOrderedGuestFlip());
    }

    [Fact]
    public void OrderedFlipOffPreservesOldPresentationShape()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", null);

        var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            SourceAddress, Width, Height);

        // Off: no flip work is enqueued and the presentation carries the address
        // with GuestImageVersion == 0 (the existing composite-heuristic shape).
        Assert.Equal(0, version);
        Assert.False(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out _, out _, out _, out _));
        Assert.True(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out var address, out var presentationVersion));
        Assert.Equal(SourceAddress, address);
        Assert.Equal(0, presentationVersion);
    }

    [Fact]
    public void OrderedFlipOnEnqueuesCaptureAndDefersPresentation()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", "1");

        var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            SourceAddress, Width, Height);

        // On: a capture is enqueued (non-zero version) and NO immediate
        // presentation is built -- ExecuteOrderedGuestFlip publishes it later.
        Assert.Equal(1, version);
        Assert.False(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out _, out _));
        Assert.True(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out var workVersion, out var workAddress, out var workWidth, out var workHeight));
        Assert.Equal(version, workVersion);
        Assert.Equal(SourceAddress, workAddress);
        Assert.Equal(Width, workWidth);
        Assert.Equal(Height, workHeight);
    }

    [Fact]
    public void VersionsIncrementMonotonicallyPerFlip()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", "1");

        var first = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            SourceAddress, Width, Height);
        var second = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            SourceAddress + 0x10000, 1920, 1080);
        var third = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            SourceAddress, Width, Height);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);

        // FIFO order is preserved: each capture carries its own version/extent.
        Assert.True(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out var v1, out _, out var w1, out var h1));
        Assert.Equal(1, v1);
        Assert.Equal(Width, w1);
        Assert.Equal(Height, h1);

        Assert.True(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out var v2, out var a2, out var w2, out var h2));
        Assert.Equal(2, v2);
        Assert.Equal(SourceAddress + 0x10000, a2);
        Assert.Equal(1920u, w2);
        Assert.Equal(1080u, h2);

        Assert.True(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out var v3, out _, out _, out _));
        Assert.Equal(3, v3);

        Assert.False(VulkanVideoPresenter.TryDequeueOrderedGuestFlipForTests(
            out _, out _, out _, out _));
    }
}
