// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AerolibTests
{
    [Fact]
    public void Instance_LoadsEmbeddedCatalog()
    {
        Assert.True(Aerolib.Instance.Count > 0, "Embedded aerolib.bin should yield a non-empty NID catalog.");
    }

    [Fact]
    public void Instance_NidAndExportNameLookupsAgree()
    {
        var catalog = Aerolib.Instance;
        var sample = catalog.GetAllNidNames().First();

        Assert.True(catalog.TryGetByNid(sample.Key, out var byNid));
        Assert.Equal(sample.Value, byNid.ExportName);

        Assert.True(catalog.TryGetByExportName(byNid.ExportName, out var byName));
        Assert.Equal(byName.ExportName, byNid.ExportName);
    }

    [Fact]
    public void Instance_SymbolsTargetGen5()
    {
        var catalog = Aerolib.Instance;
        var sample = catalog.GetAllNidNames().First();

        Assert.True(catalog.TryGetByNid(sample.Key, out var symbol));
        Assert.Equal(Generation.Gen5, symbol.Target);
    }

    [Fact]
    public void GetName_UnknownNid_ReturnsInputUnchanged()
    {
        Assert.Equal("ZZZZunknownNID", Aerolib.Instance.GetName("ZZZZunknownNID"));
    }

    [Fact]
    public void GetName_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Aerolib.Instance.GetName(null!));
        Assert.Equal(string.Empty, Aerolib.Instance.GetName(string.Empty));
    }

    [Fact]
    public void TryGetName_UnknownNid_ReturnsFalseWithEmptyName()
    {
        Assert.False(Aerolib.Instance.TryGetName("ZZZZunknownNID", out var name));
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void ContainsNid_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(Aerolib.Instance.ContainsNid(null!));
        Assert.False(Aerolib.Instance.ContainsNid(string.Empty));
    }

    [Fact]
    public void TryGetByNid_WhitespaceInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => Aerolib.Instance.TryGetByNid(" ", out _));
    }

    [Fact]
    public void Empty_CatalogHasNoEntries()
    {
        var empty = (Aerolib)Aerolib.Empty;

        Assert.Equal(0, empty.Count);
        Assert.False(empty.TryGetName("anything", out _));
    }
}
