// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Gfx10Detiler swizzle correctness. The expected element mappings are spelled
/// out bit-by-bit from the AMD addrlib GFX10 swizzle equations (16-pipe
/// patterns for the _X modes) rather than derived from the detiler's own mask
/// tables, so a transcription or evaluation bug in either side fails the
/// round-trip.
/// </summary>
public sealed class Gfx10DetilerTests
{
    private static uint Bit(uint value, int index) => (value >> index) & 1;

    // SW_64KB_R_X, 16 pipes, 4 bytes per element: 128x128-element blocks,
    // in-block bits (low to high above the two byte bits)
    // x0 x1 y0 y1 y2 x2 | x3^y3 x4^y4 x6^y5 x5^y6 | y3 x4 y6 x6.
    private static ulong Mode27TiledOffset4Bpe(uint x, uint y, uint pitchElements)
    {
        var blocksPerRow = (pitchElements + 127) / 128;
        var block = (ulong)(y / 128) * blocksPerRow + x / 128;
        var offset =
            (Bit(x, 0) << 2) | (Bit(x, 1) << 3) |
            (Bit(y, 0) << 4) | (Bit(y, 1) << 5) | (Bit(y, 2) << 6) |
            (Bit(x, 2) << 7) |
            ((Bit(x, 3) ^ Bit(y, 3)) << 8) |
            ((Bit(x, 4) ^ Bit(y, 4)) << 9) |
            ((Bit(x, 6) ^ Bit(y, 5)) << 10) |
            ((Bit(x, 5) ^ Bit(y, 6)) << 11) |
            (Bit(y, 3) << 12) | (Bit(x, 4) << 13) |
            (Bit(y, 6) << 14) | (Bit(x, 6) << 15);
        return (block << 16) | offset;
    }

    // SW_64KB_Z_X, 16 pipes, 4 bytes per element: same block geometry and pipe
    // bits as R_X, Morton-ordered micro tile x0 y0 x1 y1 x2 y2.
    private static ulong Mode24TiledOffset4Bpe(uint x, uint y, uint pitchElements)
    {
        var blocksPerRow = (pitchElements + 127) / 128;
        var block = (ulong)(y / 128) * blocksPerRow + x / 128;
        var offset =
            (Bit(x, 0) << 2) | (Bit(y, 0) << 3) |
            (Bit(x, 1) << 4) | (Bit(y, 1) << 5) |
            (Bit(x, 2) << 6) | (Bit(y, 2) << 7) |
            ((Bit(x, 3) ^ Bit(y, 3)) << 8) |
            ((Bit(x, 4) ^ Bit(y, 4)) << 9) |
            ((Bit(x, 6) ^ Bit(y, 5)) << 10) |
            ((Bit(x, 5) ^ Bit(y, 6)) << 11) |
            (Bit(y, 3) << 12) | (Bit(x, 4) << 13) |
            (Bit(y, 6) << 14) | (Bit(x, 6) << 15);
        return (block << 16) | offset;
    }

    // SW_64KB_R_X, 16 pipes, 8 bytes per element: 128x64-element blocks,
    // in-block bits x0 y0 x1 x2 y1 | pipe bits as at 4 bpe | y2 x3 y4 x6.
    private static ulong Mode27TiledOffset8Bpe(uint x, uint y, uint pitchElements)
    {
        var blocksPerRow = (pitchElements + 127) / 128;
        var block = (ulong)(y / 64) * blocksPerRow + x / 128;
        var offset =
            (Bit(x, 0) << 3) | (Bit(y, 0) << 4) |
            (Bit(x, 1) << 5) | (Bit(x, 2) << 6) | (Bit(y, 1) << 7) |
            ((Bit(x, 3) ^ Bit(y, 3)) << 8) |
            ((Bit(x, 4) ^ Bit(y, 4)) << 9) |
            ((Bit(x, 6) ^ Bit(y, 5)) << 10) |
            ((Bit(x, 5) ^ Bit(y, 6)) << 11) |
            (Bit(y, 2) << 12) | (Bit(x, 3) << 13) |
            (Bit(y, 4) << 14) | (Bit(x, 6) << 15);
        return (block << 16) | offset;
    }

    // SW_4KB_S, 4 bytes per element: 32x32-element blocks, no pipe XOR,
    // in-block bits x0 x1 y0 y1 y2 x2 y3 x3 y4 x4.
    private static ulong Mode5TiledOffset4Bpe(uint x, uint y, uint pitchElements)
    {
        var blocksPerRow = (pitchElements + 31) / 32;
        var block = (ulong)(y / 32) * blocksPerRow + x / 32;
        var offset =
            (Bit(x, 0) << 2) | (Bit(x, 1) << 3) |
            (Bit(y, 0) << 4) | (Bit(y, 1) << 5) | (Bit(y, 2) << 6) |
            (Bit(x, 2) << 7) |
            (Bit(y, 3) << 8) | (Bit(x, 3) << 9) |
            (Bit(y, 4) << 10) | (Bit(x, 4) << 11);
        return (block << 12) | offset;
    }

    // SW_4KB_S, 1 byte per element: 64x64-element blocks,
    // in-block bits x0 x1 x2 x3 y0 y1 y2 y3 y4 x4 y5 x5.
    private static ulong Mode5TiledOffset1Bpe(uint x, uint y, uint pitchElements)
    {
        var blocksPerRow = (pitchElements + 63) / 64;
        var block = (ulong)(y / 64) * blocksPerRow + x / 64;
        var offset =
            Bit(x, 0) | (Bit(x, 1) << 1) | (Bit(x, 2) << 2) | (Bit(x, 3) << 3) |
            (Bit(y, 0) << 4) | (Bit(y, 1) << 5) | (Bit(y, 2) << 6) | (Bit(y, 3) << 7) |
            (Bit(y, 4) << 8) | (Bit(x, 4) << 9) |
            (Bit(y, 5) << 10) | (Bit(x, 5) << 11);
        return (block << 12) | offset;
    }

    private static byte ElementByte(uint x, uint y, int index) =>
        (byte)(x * 31 + y * 197 + index * 89 + 13);

    private static void AssertDetileMatches(
        uint tileMode,
        uint width,
        uint height,
        uint bytesPerElement,
        Func<uint, uint, uint, ulong> tiledOffset)
    {
        var tiledByteCount = Gfx10Detiler.GetTiledByteCount(
            tileMode, width, height, width, bytesPerElement);
        Assert.NotEqual(0UL, tiledByteCount);

        var tiled = new byte[tiledByteCount];
        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var offset = (long)tiledOffset(x, y, width);
                for (var i = 0; i < bytesPerElement; i++)
                {
                    tiled[offset + i] = ElementByte(x, y, i);
                }
            }
        }

        var linear = Gfx10Detiler.Detile(
            tiled, tileMode, width, height, width, bytesPerElement);
        Assert.Equal((int)(width * height * bytesPerElement), linear.Length);

        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var baseIndex = (long)(y * width + x) * bytesPerElement;
                for (var i = 0; i < bytesPerElement; i++)
                {
                    if (linear[baseIndex + i] != ElementByte(x, y, i))
                    {
                        Assert.Fail(
                            $"Mismatch at x={x} y={y} byte={i}: " +
                            $"got {linear[baseIndex + i]}, expected {ElementByte(x, y, i)}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Detile64KbRenderMatchesAddrlibEquation4Bpe()
    {
        // 320x160 at 4 bpe spans 3x2 blocks of 128x128 with pitch padding.
        AssertDetileMatches(27, 320, 160, 4, Mode27TiledOffset4Bpe);
    }

    [Fact]
    public void Detile64KbDepthMatchesAddrlibEquation4Bpe()
    {
        AssertDetileMatches(24, 320, 160, 4, Mode24TiledOffset4Bpe);
    }

    [Fact]
    public void Detile64KbRenderMatchesAddrlibEquation8Bpe()
    {
        // 256x96 at 8 bpe spans 2x2 blocks of 128x64.
        AssertDetileMatches(27, 256, 96, 8, Mode27TiledOffset8Bpe);
    }

    [Fact]
    public void Detile4KbStandardMatchesAddrlibEquation4Bpe()
    {
        // 100x50 at 4 bpe spans 4x2 blocks of 32x32 with pitch padding.
        AssertDetileMatches(5, 100, 50, 4, Mode5TiledOffset4Bpe);
    }

    [Fact]
    public void Detile4KbStandardMatchesAddrlibEquation1Bpe()
    {
        AssertDetileMatches(5, 130, 70, 1, Mode5TiledOffset1Bpe);
    }

    [Fact]
    public void TiledByteOffsetGoldenValues64KbRender()
    {
        // Hand-evaluated positions of the 16-pipe SW_64KB_R_X 4 bpe equation
        // at 1280 elements pitch (10 blocks per row).
        Assert.Equal(0UL, Gfx10Detiler.GetTiledByteOffset(27, 0, 0, 1280, 4));
        Assert.Equal(4UL, Gfx10Detiler.GetTiledByteOffset(27, 1, 0, 1280, 4));
        Assert.Equal(16UL, Gfx10Detiler.GetTiledByteOffset(27, 0, 1, 1280, 4));
        // x=8, y=8: the x3/y3 pipe terms cancel in bit 8, y3 sets bit 12.
        Assert.Equal(4096UL, Gfx10Detiler.GetTiledByteOffset(27, 8, 8, 1280, 4));
        // x=129, y=0: second block plus x0.
        Assert.Equal(65540UL, Gfx10Detiler.GetTiledByteOffset(27, 129, 0, 1280, 4));
        // y=128, second block row: 10 blocks * 64KB.
        Assert.Equal(655360UL, Gfx10Detiler.GetTiledByteOffset(27, 0, 128, 1280, 4));
    }

    [Fact]
    public void TiledByteCountPads720pRenderTargetTo768Rows()
    {
        // 1280x720 RGBA8 SW_64KB_R_X: 10x6 blocks of 64KB = 1280x768 elements.
        Assert.Equal(3_932_160UL, Gfx10Detiler.GetTiledByteCount(27, 1280, 720, 1280, 4));
    }

    [Fact]
    public void DetileZeroFillsElementsBeyondShortSource()
    {
        // A source truncated to the unpadded surface size must not throw, and
        // elements whose tiled offset lands beyond the source stay zero.
        const uint width = 320;
        const uint height = 160;
        var tiled = new byte[width * height * 4];
        Array.Fill(tiled, (byte)0xAB);
        var linear = Gfx10Detiler.Detile(tiled, 27, width, height, width, 4);

        var sawZeroed = false;
        for (var y = 0u; y < height && !sawZeroed; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                if (Mode27TiledOffset4Bpe(x, y, width) + 4 > (ulong)tiled.Length)
                {
                    var baseIndex = (long)(y * width + x) * 4;
                    Assert.Equal(0, linear[baseIndex]);
                    sawZeroed = true;
                    break;
                }
            }
        }

        Assert.True(sawZeroed, "Expected at least one element beyond the short source.");
    }

    [Fact]
    public void SupportedTileModes()
    {
        Assert.True(Gfx10Detiler.IsSupportedTileMode(5));
        Assert.True(Gfx10Detiler.IsSupportedTileMode(24));
        Assert.True(Gfx10Detiler.IsSupportedTileMode(27));
        Assert.False(Gfx10Detiler.IsSupportedTileMode(0));
        Assert.False(Gfx10Detiler.IsSupportedTileMode(9));
        Assert.False(Gfx10Detiler.IsSupportedTileMode(25));
    }
}
