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
        if (Directory.Exists(_hostRoot))
        {
            Directory.Delete(_hostRoot, recursive: true);
        }
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

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/root/.ssh/id_rsa")]
    [InlineData("\\\\server\\share\\secret")]
    [InlineData("/proc/self/mem")]
    public void ResolveGuestPath_UnmappedAbsolutePathIsDenied(string guestPath)
    {
        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath(guestPath));
    }

    [Fact]
    public void ResolveGuestPath_RealNestedFileUnderMountResolves()
    {
        var nested = Path.Combine(_hostRoot, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "c.bin"), [9]);

        Assert.Equal(
            Path.Combine(nested, "c.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/path-test/a/b/c.bin"));
    }

    [Fact]
    public void ResolveGuestPath_ReparsePointInsideMountIsDenied()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var linkPath = Path.Combine(_hostRoot, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(outsideRoot);
            return;
        }

        try
        {
            Assert.Equal(
                string.Empty,
                KernelMemoryCompatExports.ResolveGuestPath("/path-test/link/secret.bin"));
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("/path-test/")]
    [InlineData("/path-test/bad\0name")]
    public void ResolveGuestPath_MalformedPathUnderMountFailsClosed(string prefix)
    {
        var guestPath = prefix.EndsWith('/')
            ? prefix + new string('a', 40_000)
            : prefix;

        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath(guestPath));
    }
}
