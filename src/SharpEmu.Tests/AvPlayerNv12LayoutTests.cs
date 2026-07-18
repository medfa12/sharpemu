// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AvPlayerNv12LayoutTests
{
    [Theory]
    [InlineData(1920, 2048)]
    [InlineData(3840, 3840)]
    [InlineData(4097, 4352)]
    public void CalculateNv12Pitch_AlignsTo256Bytes(int width, int expectedPitch)
    {
        Assert.Equal(expectedPitch, AvPlayerExports.CalculateNv12Pitch(width));
    }

    [Fact]
    public void CalculateNv12BufferSize_IncludesBothPlanesAtTheAlignedPitch()
    {
        Assert.Equal(3_317_760, AvPlayerExports.CalculateNv12BufferSize(2048, 1080));
    }

    [Fact]
    public void CopyNv12ToGuestBuffer_UsesSourceStridesAndPitchedUvOffset()
    {
        const int width = 4;
        const int height = 4;
        const int sourceLumaStride = 6;
        const int sourceChromaStride = 8;
        const int destinationPitch = 8;
        var source = Enumerable.Repeat((byte)0xEE, 40).ToArray();
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                source[(row * sourceLumaStride) + column] = checked((byte)(1 + (row * 10) + column));
            }
        }
        var sourceChromaOffset = sourceLumaStride * height;
        for (var row = 0; row < height / 2; row++)
        {
            for (var column = 0; column < width; column++)
            {
                source[sourceChromaOffset + (row * sourceChromaStride) + column] =
                    checked((byte)(101 + (row * 10) + column));
            }
        }

        var destination = Enumerable.Repeat((byte)0xCC, 48).ToArray();
        AvPlayerExports.CopyNv12ToGuestBuffer(
            source,
            destination,
            width,
            height,
            sourceLumaStride,
            sourceChromaStride,
            destinationPitch);

        var expected = new byte[48];
        for (var row = 0; row < height; row++)
        {
            source.AsSpan(row * sourceLumaStride, width)
                .CopyTo(expected.AsSpan(row * destinationPitch, width));
        }
        var destinationChromaOffset = destinationPitch * height;
        for (var row = 0; row < height / 2; row++)
        {
            source.AsSpan(sourceChromaOffset + (row * sourceChromaStride), width)
                .CopyTo(expected.AsSpan(destinationChromaOffset + (row * destinationPitch), width));
        }
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void ConvertNv12ToBgra_MidGrayFrame_ProducesGrayBgraWithOpaqueAlpha()
    {
        // 2x2 luma at video mid, chroma at the neutral midpoint -> gray, no tint.
        var nv12 = new byte[] { 126, 126, 126, 126, 128, 128 };

        var bgra = AvPlayerExports.ConvertNv12ToBgra(nv12, 2, 2, 2, 4);

        var expected = new byte[16];
        for (var pixel = 0; pixel < 4; pixel++)
        {
            expected[(pixel * 4) + 0] = 128;
            expected[(pixel * 4) + 1] = 128;
            expected[(pixel * 4) + 2] = 128;
            expected[(pixel * 4) + 3] = 0xFF;
        }
        Assert.Equal(expected, bgra);
    }

    [Fact]
    public void ConvertNv12ToBgra_ColoredFrame_UsesBt709AndClampsToByte()
    {
        // Y=150 U=100 V=200 -> BT.709 limited-range: R saturates to 255.
        var nv12 = new byte[] { 150, 150, 150, 150, 100, 200 };

        var bgra = AvPlayerExports.ConvertNv12ToBgra(nv12, 2, 2, 2, 4);

        // BGRA per pixel: B=97 G=124 R=255 A=255.
        for (var pixel = 0; pixel < 4; pixel++)
        {
            Assert.Equal(97, bgra[(pixel * 4) + 0]);
            Assert.Equal(124, bgra[(pixel * 4) + 1]);
            Assert.Equal(255, bgra[(pixel * 4) + 2]);
            Assert.Equal(255, bgra[(pixel * 4) + 3]);
        }
    }

    [Fact]
    public void ConvertNv12ToBgra_UndersizedSource_ReturnsEmpty()
    {
        var tooSmall = new byte[] { 126, 126, 126, 126, 128 };

        Assert.Empty(AvPlayerExports.ConvertNv12ToBgra(tooSmall, 2, 2, 2, 4));
    }

    [Fact]
    public void ShouldDirectPresentVideoFrames_DefaultsOff_AndFollowsEnvGate()
    {
        var original = Environment.GetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT", null);
            Assert.False(AvPlayerExports.ShouldDirectPresentVideoFrames());

            Environment.SetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT", "0");
            Assert.False(AvPlayerExports.ShouldDirectPresentVideoFrames());

            Environment.SetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT", "1");
            Assert.True(AvPlayerExports.ShouldDirectPresentVideoFrames());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_AVPLAYER_PRESENT", original);
        }
    }
}
