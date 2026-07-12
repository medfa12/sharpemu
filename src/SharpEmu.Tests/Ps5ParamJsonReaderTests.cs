// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Tests;

public sealed class Ps5ParamJsonReaderTests
{
    private static (string? Title, string? TitleId, string? Version) Parse(string json)
        => Ps5ParamJsonReader.TryReadPs5Param(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void FullParamJson_ExtractsTitleTitleIdAndVersion()
    {
        var (title, titleId, version) = Parse("""
            {
                "titleId": "PPSA01341",
                "contentVersion": "01.000.000",
                "localizedParameters": {
                    "defaultLanguage": "en-US",
                    "en-US": { "titleName": "Demon's Souls" }
                }
            }
            """);

        Assert.Equal("Demon's Souls", title);
        Assert.Equal("PPSA01341", titleId);
        Assert.Equal("01.000.000", version);
    }

    [Fact]
    public void Version_FallsBackToMasterVersion()
    {
        var (_, _, version) = Parse("""{ "masterVersion": "02.000.000" }""");

        Assert.Equal("02.000.000", version);
    }

    [Fact]
    public void Version_FallsBackToTargetContentVersion()
    {
        var (_, _, version) = Parse("""{ "targetContentVersion": "03.000.000" }""");

        Assert.Equal("03.000.000", version);
    }

    [Fact]
    public void Version_PrefersContentVersionOverFallbacks()
    {
        var (_, _, version) = Parse("""
            {
                "contentVersion": "01.000.000",
                "masterVersion": "02.000.000",
                "targetContentVersion": "03.000.000"
            }
            """);

        Assert.Equal("01.000.000", version);
    }

    [Fact]
    public void Title_UsesDefaultLanguageEntry()
    {
        var (title, _, _) = Parse("""
            {
                "localizedParameters": {
                    "defaultLanguage": "ja-JP",
                    "ja-JP": { "titleName": "デモンズソウル" },
                    "en-US": { "titleName": "Demon's Souls" }
                }
            }
            """);

        Assert.Equal("デモンズソウル", title);
    }

    [Fact]
    public void Title_MissingDefaultLanguageEntry_FallsBackToEnUs()
    {
        var (title, _, _) = Parse("""
            {
                "localizedParameters": {
                    "defaultLanguage": "de-DE",
                    "en-US": { "titleName": "Fallback Title" }
                }
            }
            """);

        Assert.Equal("Fallback Title", title);
    }

    [Fact]
    public void Title_FallsBackToDiscLocalizedParameters()
    {
        var (title, _, _) = Parse("""
            {
                "disc": {
                    "localizedParameters": {
                        "defaultLanguage": "en-US",
                        "en-US": { "titleName": "Disc Title" }
                    }
                }
            }
            """);

        Assert.Equal("Disc Title", title);
    }

    [Fact]
    public void Title_NoLocalizedParametersAnywhere_ReturnsNull()
    {
        var (title, titleId, version) = Parse("""{ "titleId": "PPSA00000" }""");

        Assert.Null(title);
        Assert.Equal("PPSA00000", titleId);
        Assert.Null(version);
    }

    [Fact]
    public void InvalidJson_ReturnsAllNulls()
    {
        var result = Parse("{ not json");

        Assert.Equal((null, null, null), result);
    }

    [Fact]
    public void EmptyData_ReturnsAllNulls()
    {
        var result = Ps5ParamJsonReader.TryReadPs5Param(Array.Empty<byte>());

        Assert.Equal((null, null, null), result);
    }
}
