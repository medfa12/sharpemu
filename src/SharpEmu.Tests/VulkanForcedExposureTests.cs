// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

public sealed class VulkanForcedExposureTests
{
    [Fact]
    public void AstroTonemapFix_UsesConservativeExposureFallback()
    {
        Assert.Equal(
            0.25f,
            VulkanVideoPresenter.ParseForcedExposureSetting(
                raw: null,
                astroTonemapFix: true));
    }

    [Fact]
    public void ExplicitExposure_OverridesAstroFallback()
    {
        Assert.Equal(
            0.5f,
            VulkanVideoPresenter.ParseForcedExposureSetting(
                "0.5",
                astroTonemapFix: true));
    }

    [Fact]
    public void ExposureFallback_IsDisabledByDefault()
    {
        Assert.Null(VulkanVideoPresenter.ParseForcedExposureSetting(
            raw: null,
            astroTonemapFix: false));
    }
}
