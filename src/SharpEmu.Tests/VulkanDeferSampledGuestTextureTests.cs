// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests the sampled-input binding gate that decides whether a texture defers
/// to its GPU-produced VkImage (empty pixels, resolved at execution) or reads
/// guest memory. Default behavior is loose (any GPU-touched address defers);
/// under SHARPEMU_FLIP_COMPOSITE_FIX it is format-strict, mirroring the fork so
/// a format-mismatched or never-produced composite input reads real memory
/// instead of deferring to an alias that resolves black.
/// </summary>
[Collection(VulkanPresenterStateCollection.Name)]
public sealed class VulkanDeferSampledGuestTextureTests
{
    private const ulong RenderTarget = 0x00000005104A0000;

    [Fact]
    public void UnknownAddressNeverDefers()
    {
        Assert.False(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: false,
            gpuFormatKnown: false,
            gpuFormat: 0,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 56));

        Assert.False(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: true,
            gpuFormatKnown: false,
            gpuFormat: 0,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 56));
    }

    [Fact]
    public void LooseGateDefersRegardlessOfSampledFormat()
    {
        // Producer registered as guest format 56; sampled as a different format.
        Assert.True(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: false,
            gpuFormatKnown: true,
            gpuFormat: 56,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 12));
    }

    [Fact]
    public void StrictGateDefersOnlyOnExactFormatMatch()
    {
        Assert.True(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: true,
            gpuFormatKnown: true,
            gpuFormat: 56,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 56));

        Assert.False(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: true,
            gpuFormatKnown: true,
            gpuFormat: 56,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 12));
    }

    [Fact]
    public void StrictGateMatchesEitherRegistry()
    {
        Assert.True(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: true,
            gpuFormatKnown: false,
            gpuFormat: 0,
            renderTargetFormatKnown: true,
            renderTargetFormat: 12,
            sampledGuestFormat: 12));
    }

    [Fact]
    public void StrictGateRejectsZeroSampledFormat()
    {
        Assert.False(VulkanVideoPresenter.DeferSampledGuestTextureDecision(
            flipCompositeFix: true,
            gpuFormatKnown: true,
            gpuFormat: 0,
            renderTargetFormatKnown: false,
            renderTargetFormat: 0,
            sampledGuestFormat: 0));
    }

    [Fact]
    public void RegisteredRenderTargetDefersOnFormatMatchUnderStrictGate()
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX");
        try
        {
            VulkanVideoPresenter.ResetGuestImageTrackingForTests();
            // Rendered as guest format 56 (T# code 56 / CB code 10 canonicalize
            // to R8G8B8A8Unorm).
            VulkanVideoPresenter.PublishRenderedGuestImageForTests(
                RenderTarget, 56, 1920, 1080);

            Environment.SetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX", "1");

            // Sampled as T# code 56 -> guest format 56 -> exact match -> defers.
            Assert.True(VulkanVideoPresenter.ShouldDeferSampledGuestTexture(
                RenderTarget, format: 56, numberType: 0));

            // Sampled as a cross-class format the producer never rendered ->
            // reads guest memory instead of deferring to a black alias.
            Assert.False(VulkanVideoPresenter.ShouldDeferSampledGuestTexture(
                RenderTarget, format: 13, numberType: 0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX", previous);
            VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        }
    }

    [Fact]
    public void DefaultGateDefersForAnyKnownAddress()
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX");
        try
        {
            VulkanVideoPresenter.ResetGuestImageTrackingForTests();
            VulkanVideoPresenter.PublishRenderedGuestImageForTests(
                RenderTarget, 56, 1920, 1080);

            Environment.SetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX", null);

            // Loose default: even a cross-class sampled format defers (native
            // alias resolves it at execution time).
            Assert.True(VulkanVideoPresenter.ShouldDeferSampledGuestTexture(
                RenderTarget, format: 13, numberType: 0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX", previous);
            VulkanVideoPresenter.ResetGuestImageTrackingForTests();
        }
    }
}
