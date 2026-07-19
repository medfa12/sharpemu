// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Present arbitration while an AvPlayer intro clip is playing. A decoded video
/// frame reaches the swapchain through TryPresentVideoFrame, which marks the
/// video active. While that window is fresh the game's concurrent display-buffer
/// flips and translated composites must NOT overwrite the clip -- otherwise the
/// intro loses the race to the game's loading frames and, because each new video
/// frame is immediately clobbered, only an occasional frame ever survives instead
/// of a stream. These tests pin the VideoPresentIsActive gate and that it
/// suppresses the default (non-ordered) flip-publish path for the whole window
/// yet lets the game resume once the window lapses. The GPU present itself needs
/// a Vulkan device and is exercised in the emulator, not here.
/// </summary>
[Collection(VulkanPresenterStateCollection.Name)]
public sealed class VulkanVideoPresentArbitrationTests : IDisposable
{
    private const ulong GuestFlipAddress = 0x0000000513560000;
    private const uint Width = 3840;
    private const uint Height = 2160;

    private readonly string? _previousOrderedGate;

    public VulkanVideoPresentArbitrationTests()
    {
        _previousOrderedGate =
            Environment.GetEnvironmentVariable("SHARPEMU_ORDERED_FLIP");
        // Exercise the default (non-ordered) flip-publish path.
        Environment.SetEnvironmentVariable("SHARPEMU_ORDERED_FLIP", null);
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
        VulkanVideoPresenter.ClearVideoPresentActiveForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "SHARPEMU_ORDERED_FLIP", _previousOrderedGate);
        VulkanVideoPresenter.ResetOrderedFlipStateForTests();
        VulkanVideoPresenter.ClearVideoPresentActiveForTests();
    }

    [Fact]
    public void VideoPresentGateInactiveByDefault()
    {
        Assert.False(VulkanVideoPresenter.VideoPresentIsActiveForTests());
    }

    [Fact]
    public void MarkVideoPresentActivatesGate()
    {
        VulkanVideoPresenter.MarkVideoPresentActiveForTests();
        Assert.True(VulkanVideoPresenter.VideoPresentIsActiveForTests());

        VulkanVideoPresenter.ClearVideoPresentActiveForTests();
        Assert.False(VulkanVideoPresenter.VideoPresentIsActiveForTests());
    }

    [Fact]
    public void GuestFlipPresentsWhenNoVideoActive()
    {
        // No video playing: the flip publishes normally (the pre-existing shape,
        // GuestImageVersion == 0 carrying the flipped address).
        var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            GuestFlipAddress, Width, Height);

        Assert.Equal(0, version);
        Assert.True(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out var address, out var presentationVersion));
        Assert.Equal(GuestFlipAddress, address);
        Assert.Equal(0, presentationVersion);
    }

    [Fact]
    public void GuestFlipSuppressedWhileVideoActive()
    {
        VulkanVideoPresenter.MarkVideoPresentActiveForTests();

        var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            GuestFlipAddress, Width, Height);

        // The clip owns the swapchain: the game's flip builds no presentation and
        // enqueues no work, so the last video frame stays latched.
        Assert.Equal(0, version);
        Assert.False(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out _, out _));
    }

    [Fact]
    public void EveryGuestFlipSuppressedAcrossTheVideoWindow()
    {
        // The intro is a stream: over the whole active window every one of the
        // game's interleaved loading flips must lose, not just the first, so the
        // decoded frames form an uninterrupted sequence on the swapchain.
        VulkanVideoPresenter.MarkVideoPresentActiveForTests();

        for (var frame = 0; frame < 16; frame++)
        {
            var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
                GuestFlipAddress + ((ulong)frame << 16), Width, Height);
            Assert.Equal(0, version);
            Assert.False(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
                out _, out _));
        }
    }

    [Fact]
    public void GuestFlipResumesAfterVideoWindowLapses()
    {
        // While playing: suppressed.
        VulkanVideoPresenter.MarkVideoPresentActiveForTests();
        VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            GuestFlipAddress, Width, Height);
        Assert.False(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out _, out _));

        // Clip ended (window lapsed): the game reclaims the swapchain.
        VulkanVideoPresenter.ClearVideoPresentActiveForTests();
        var version = VulkanVideoPresenter.BuildDisplayFlipPresentationForTests(
            GuestFlipAddress, Width, Height);

        Assert.Equal(0, version);
        Assert.True(VulkanVideoPresenter.TryGetLatestFlipPresentationForTests(
            out var address, out _));
        Assert.Equal(GuestFlipAddress, address);
    }
}
