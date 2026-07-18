// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// The GFX10 unified IMG_FORMAT field (T#/V# word1[28:20]) must be converted
/// to the legacy dfmt/nfmt pair the rest of the pipeline speaks. Reading a
/// NUMBER_TYPE from bits [29:26] overlaps the top bits of the same field and
/// fabricates garbage for every unified value >= 64.
/// </summary>
public sealed class Gfx10UnifiedFormatTests
{
    [Theory]
    // The Astro Bot title HDR chain: RGBA16F intermediates, 10_11_11 bloom,
    // 32-bit float AO/depth, RG8, RGBA32F exposure, RGBA16 UNORM, RGBA8 sRGB.
    [InlineData(71u, 12u, 7u)]
    [InlineData(36u, 6u, 7u)]
    [InlineData(22u, 4u, 7u)]
    [InlineData(14u, 3u, 0u)]
    [InlineData(77u, 14u, 7u)]
    [InlineData(65u, 12u, 0u)]
    [InlineData(130u, 10u, 9u)]
    [InlineData(1u, 1u, 0u)]
    [InlineData(56u, 10u, 0u)]
    [InlineData(29u, 5u, 7u)]
    [InlineData(43u, 7u, 7u)]
    [InlineData(64u, 11u, 7u)]
    // Block-compressed encodings keep their unified identifier.
    [InlineData(169u, 169u, 0u)]
    [InlineData(182u, 182u, 9u)]
    public void TryDecode_MapsUnifiedFormatToLegacyPair(
        uint unified,
        uint expectedDataFormat,
        uint expectedNumberFormat)
    {
        Assert.True(Gfx10UnifiedFormat.TryDecode(unified, out var dataFormat, out var numberFormat));
        Assert.Equal(expectedDataFormat, dataFormat);
        Assert.Equal(expectedNumberFormat, numberFormat);
    }

    [Theory]
    // Reserved encodings (e.g. the integer spellings of 10_11_11 and the
    // 10_10_10_2 USCALED/SSCALED slots) must not decode.
    [InlineData(30u)]
    [InlineData(35u)]
    [InlineData(46u)]
    [InlineData(255u)]
    public void TryDecode_RejectsReservedEncodings(uint unified)
    {
        Assert.False(Gfx10UnifiedFormat.TryDecode(unified, out _, out _));
    }

    [Fact]
    public void TryDecode_ZeroIsInvalidButDecodable()
    {
        Assert.True(Gfx10UnifiedFormat.TryDecode(0, out var dataFormat, out var numberFormat));
        Assert.Equal(0u, dataFormat);
        Assert.Equal(0u, numberFormat);
    }

}
