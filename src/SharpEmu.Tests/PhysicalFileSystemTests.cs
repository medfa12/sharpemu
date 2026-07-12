// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using Xunit;

namespace SharpEmu.Tests;

public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _root;
    private readonly PhysicalFileSystem _fs = new();

    public PhysicalFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Exists_And_TryReadAllBytes_RoundTrip()
    {
        var path = Path.Combine(_root, "eboot.bin");
        byte[] content = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        File.WriteAllBytes(path, content);

        Assert.True(_fs.Exists(path));
        Assert.True(_fs.TryReadAllBytes(path, out var data));
        Assert.Equal(content, data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespacePath_IsRejected(string? path)
    {
        Assert.False(_fs.Exists(path!));
        Assert.False(_fs.TryReadAllBytes(path!, out var data));
        Assert.Empty(data);
    }

    [Fact]
    public void MissingFile_ReturnsFalseWithEmptyData()
    {
        var path = Path.Combine(_root, "does-not-exist.bin");

        Assert.False(_fs.Exists(path));
        Assert.False(_fs.TryReadAllBytes(path, out var data));
        Assert.Empty(data);
    }

    [Fact]
    public void DirectoryPath_IsNotAFile()
    {
        Assert.False(_fs.Exists(_root));
        Assert.False(_fs.TryReadAllBytes(_root, out _));
    }
}
