// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the built-in PNG splash decoder. PNGs are synthesized in-memory
/// (the decoder does not verify chunk CRCs) and loaded through the
/// SHARPEMU_APP0_DIR / sce_sys/pic0.png path used at runtime.
/// </summary>
public sealed class PngSplashLoaderTests : IDisposable
{
    private readonly string _app0Dir;

    public PngSplashLoaderTests()
    {
        _app0Dir = Path.Combine(Path.GetTempPath(), $"sharpemu-png-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_app0Dir, "sce_sys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", null);
        try
        {
            Directory.Delete(_app0Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (byte[] Pixels, uint Width, uint Height, bool Ok) Load(byte[] png)
    {
        File.WriteAllBytes(Path.Combine(_app0Dir, "sce_sys", "pic0.png"), png);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Dir);
        var ok = PngSplashLoader.TryLoad(out var pixels, out var width, out var height);
        return (pixels, width, height, ok);
    }

    private static byte[] BuildPng(
        uint width,
        uint height,
        byte colorType,
        byte[] rawScanlines,
        byte bitDepth = 8,
        byte interlace = 0)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlace;
        WriteChunk(stream, "IHDR", ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(rawScanlines);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(MemoryStream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        foreach (var c in type)
        {
            stream.WriteByte((byte)c);
        }

        stream.Write(data);
        stream.Write([0, 0, 0, 0]); // CRC — not validated by the decoder
    }

    [Fact]
    public void TryLoad_Rgb_ConvertsToBgraWithOpaqueAlpha()
    {
        // 2x1, filter 0: red pixel then blue pixel
        byte[] scanlines = [0, 255, 0, 0, 0, 0, 255];
        var (pixels, width, height, ok) = Load(BuildPng(2, 1, colorType: 2, scanlines));

        Assert.True(ok);
        Assert.Equal(2u, width);
        Assert.Equal(1u, height);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);   // red -> B,G,R,A
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[4..8]); // blue -> B,G,R,A
    }

    [Fact]
    public void TryLoad_Rgba_PreservesAlpha()
    {
        // 1x1 RGBA, filter 0: half-transparent green
        byte[] scanlines = [0, 0, 200, 0, 128];
        var (pixels, _, _, ok) = Load(BuildPng(1, 1, colorType: 6, scanlines));

        Assert.True(ok);
        Assert.Equal(new byte[] { 0, 200, 0, 128 }, pixels);
    }

    [Fact]
    public void TryLoad_SubFilter_ReconstructsFromLeftNeighbor()
    {
        // 2x1 RGB, filter 1 (Sub): first pixel raw (10,20,30), second stores deltas (+1,+2,+3)
        byte[] scanlines = [1, 10, 20, 30, 1, 2, 3];
        var (pixels, _, _, ok) = Load(BuildPng(2, 1, colorType: 2, scanlines));

        Assert.True(ok);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 33, 22, 11, 255 }, pixels[4..8]); // 11,22,33 -> BGRA
    }

    [Fact]
    public void TryLoad_UpFilter_ReconstructsFromRowAbove()
    {
        // 1x2 RGB: row0 filter 0 = (100,100,100); row1 filter 2 (Up) stores deltas (+5,+6,+7)
        byte[] scanlines = [0, 100, 100, 100, 2, 5, 6, 7];
        var (pixels, _, _, ok) = Load(BuildPng(1, 2, colorType: 2, scanlines));

        Assert.True(ok);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 107, 106, 105, 255 }, pixels[4..8]);
    }

    [Theory]
    [InlineData(16)] // 16-bit depth unsupported
    [InlineData(1)]
    public void TryLoad_UnsupportedBitDepth_Fails(byte bitDepth)
    {
        byte[] scanlines = [0, 1, 2, 3];
        var (_, _, _, ok) = Load(BuildPng(1, 1, colorType: 2, scanlines, bitDepth: bitDepth));

        Assert.False(ok);
    }

    [Fact]
    public void TryLoad_PaletteColorType_Fails()
    {
        byte[] scanlines = [0, 0];
        var (_, _, _, ok) = Load(BuildPng(1, 1, colorType: 3, scanlines));

        Assert.False(ok);
    }

    [Fact]
    public void TryLoad_InterlacedImage_Fails()
    {
        byte[] scanlines = [0, 1, 2, 3];
        var (_, _, _, ok) = Load(BuildPng(1, 1, colorType: 2, scanlines, interlace: 1));

        Assert.False(ok);
    }

    [Fact]
    public void TryLoad_CorruptSignature_Fails()
    {
        var png = BuildPng(1, 1, colorType: 2, [0, 1, 2, 3]);
        png[0] = 0x00;

        var (_, _, _, ok) = Load(png);
        Assert.False(ok);
    }

    [Fact]
    public void TryLoad_MissingFile_Fails()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Dir);
        File.Delete(Path.Combine(_app0Dir, "sce_sys", "pic0.png"));

        Assert.False(PngSplashLoader.TryLoad(out _, out _, out _));
    }

    [Fact]
    public void TryLoad_NoAppDirConfigured_Fails()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", null);

        Assert.False(PngSplashLoader.TryLoad(out _, out _, out _));
    }
}
