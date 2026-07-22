// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelPathResolverCollection.Name, DisableParallelization = true)]
public sealed class KernelPathResolverCollection
{
    public const string Name = "KernelPathResolver";
}

[Collection(KernelPathResolverCollection.Name)]
public sealed class KernelPathResolverTests : IDisposable
{
    private readonly string _hostRoot;

    public KernelPathResolverTests()
    {
        _hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_hostRoot);
        KernelMemoryCompatExports.RegisterGuestPathMount("/path-test", _hostRoot);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.TryUnregisterGuestPathMount("/path-test");
        Directory.Delete(_hostRoot, recursive: true);
    }

    [Theory]
    [InlineData("/path-test/../../content/./packs", "content/packs")]
    [InlineData("/path-test/bin/platform/../../../movies", "movies")]
    public void ResolveGuestPath_ClampsTraversalAtMountRoot(string guestPath, string expectedRelativePath)
    {
        var expected = Path.Combine(
            _hostRoot,
            expectedRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, KernelMemoryCompatExports.ResolveGuestPath(guestPath));
    }
}
