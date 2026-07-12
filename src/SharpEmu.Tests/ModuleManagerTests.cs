// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class ModuleManagerTests
{
    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private static class TestExports
    {
        [SysAbiExport(Nid = "TEST+ret42+xx", ExportName = "sharpemuTestReturn42", LibraryName = "libTest", Target = Generation.Gen5)]
        internal static int Return42(CpuContext ctx) => 42;

        [SysAbiExport(Nid = "TEST+setsrax+x", ExportName = "sharpemuTestSetsRax", Target = Generation.Gen5)]
        internal static int SetsRaxExplicitly(CpuContext ctx)
        {
            ctx[CpuRegister.Rax] = 0x1234_5678;
            return -1; // must NOT overwrite the explicit Rax value
        }

        [SysAbiExport(Nid = "TEST+noargs+xxx", ExportName = "sharpemuTestNoArgs", Target = Generation.Gen5)]
        internal static int NoArguments() => 7;

        [SysAbiExport(Nid = "TEST+gen4only+x", ExportName = "sharpemuTestGen4Only", Target = Generation.Gen4)]
        internal static int Gen4Only(CpuContext ctx) => 0;
    }

    private static ModuleManager CreateRegistered(Generation generation = Generation.Gen4 | Generation.Gen5)
    {
        var manager = new ModuleManager();
        var count = manager.RegisterFromAssembly(typeof(ModuleManagerTests).Assembly, generation);
        Assert.True(count >= 4, $"Expected at least the 4 test exports, registered {count}.");
        return manager;
    }

    private static CpuContext Gen5Context() => new(new NullMemory(), Generation.Gen5);

    [Fact]
    public void RegisterFromAssembly_ExposesExportsByNidAndName()
    {
        var manager = CreateRegistered();

        Assert.True(manager.TryGetExport("TEST+ret42+xx", out var export));
        Assert.Equal("sharpemuTestReturn42", export.Name);
        Assert.Equal("libTest", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);

        Assert.True(manager.TryGetExportByName("sharpemuTestReturn42", out var byName));
        Assert.Equal(export.Nid, byName.Nid);
        Assert.True(manager.TryGetFunction("TEST+ret42+xx", out _));
    }

    [Fact]
    public void Dispatch_CopiesReturnValueIntoRax()
    {
        var manager = CreateRegistered();
        var context = Gen5Context();

        var result = manager.Dispatch("TEST+ret42+xx", context);

        Assert.Equal((OrbisGen2Result)42, result);
        Assert.Equal(42UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Dispatch_PreservesRaxWrittenByHandler()
    {
        var manager = CreateRegistered();
        var context = Gen5Context();

        manager.Dispatch("TEST+setsrax+x", context);

        Assert.Equal(0x1234_5678UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Dispatch_SupportsParameterlessExports()
    {
        var manager = CreateRegistered();
        var context = Gen5Context();

        var result = manager.Dispatch("TEST+noargs+xxx", context);

        Assert.Equal((OrbisGen2Result)7, result);
        Assert.Equal(7UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void TryDispatch_UnknownNid_ReturnsNotFoundAndSetsRax()
    {
        var manager = CreateRegistered();
        var context = Gen5Context();

        Assert.False(manager.TryDispatch("TEST+missing+xx", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND), context[CpuRegister.Rax]);
    }

    [Fact]
    public void TryDispatch_GenerationMismatch_ReturnsNotImplemented()
    {
        var manager = CreateRegistered();
        var context = Gen5Context(); // Gen4-only export dispatched from a Gen5 context

        Assert.False(manager.TryDispatch("TEST+gen4only+x", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, result);
    }

    [Fact]
    public void RegisterFromAssembly_SkipsExportsOutsideRequestedGeneration()
    {
        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(ModuleManagerTests).Assembly, Generation.Gen5);

        Assert.False(manager.TryGetExport("TEST+gen4only+x", out _));
        Assert.True(manager.TryGetExport("TEST+ret42+xx", out _));
    }

    [Fact]
    public void Freeze_BlocksFurtherRegistration()
    {
        var manager = CreateRegistered();
        manager.Freeze();

        Assert.Throws<InvalidOperationException>(
            () => manager.RegisterFromAssembly(typeof(ModuleManagerTests).Assembly, Generation.Gen5));
    }

    [Fact]
    public void TryGetFunction_WhitespaceNid_Throws()
    {
        var manager = new ModuleManager();

        Assert.Throws<ArgumentException>(() => manager.TryGetFunction(" ", out _));
    }
}
