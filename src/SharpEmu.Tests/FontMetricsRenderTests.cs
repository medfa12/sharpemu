// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Tests;

public sealed class FontMetricsRenderTests
{
    private const ulong FontHandle = 0x1234;
    private const ulong SurfaceAddress = 0x2000;
    private const ulong MetricsAddress = 0x2100;
    private const ulong ResultAddress = 0x2200;
    private const ulong LayoutAddress = 0x2300;
    private const ulong StyleFrameAddress = 0x2400;
    private const ulong BufferAddress = 0x4000;

    private readonly RangeMemory _memory = new(0x1000, 0x5000);
    private readonly CpuContext _ctx;

    public FontMetricsRenderTests()
    {
        FontExports.ResetScaleForTests();
        FontExports.ResetGlyphStateForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private void SetPixelScale(float width, float height)
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(width), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(height), 0);
        FontExports.FontSetScalePixel(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private float ReadFloat(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    [Fact]
    public void MetricsAndLayouts_AreQuantizedAndCoherent()
    {
        SetPixelScale(37f, 41f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'M';
        _ctx[CpuRegister.Rdx] = MetricsAddress;
        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        var width = ReadFloat(MetricsAddress + 0x00);
        var height = ReadFloat(MetricsAddress + 0x04);
        var horizontalBearingY = ReadFloat(MetricsAddress + 0x0C);
        var horizontalAdvance = ReadFloat(MetricsAddress + 0x10);
        var verticalAdvance = ReadFloat(MetricsAddress + 0x1C);
        Assert.Equal(MathF.Round(width * 64f), width * 64f);
        Assert.Equal(MathF.Round(height * 64f), height * 64f);
        Assert.True(horizontalAdvance > width);
        Assert.Equal(height, horizontalBearingY);
        Assert.True(verticalAdvance >= height);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = LayoutAddress;
        FontExports.FontGetHorizontalLayout(_ctx);
        var baseline = ReadFloat(LayoutAddress + 0x00);
        var lineAdvance = ReadFloat(LayoutAddress + 0x04);
        Assert.True(horizontalBearingY <= baseline);
        Assert.True(baseline < lineAdvance);
        Assert.Equal(0f, ReadFloat(LayoutAddress + 0x08));

        FontExports.FontGetVerticalLayout(_ctx);
        var verticalBaseline = ReadFloat(LayoutAddress + 0x00);
        var columnAdvance = ReadFloat(LayoutAddress + 0x04);
        Assert.True(verticalBaseline > 0f);
        Assert.True(verticalBaseline < columnAdvance);
        Assert.Equal(0f, ReadFloat(LayoutAddress + 0x08));
    }

    [Fact]
    public void PointScale_UsesPerHandleResolutionDpi()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 144;
        _ctx[CpuRegister.Rdx] = 72;
        FontExports.FontSetResolutionDpi(_ctx);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(10f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(10f), 0);
        FontExports.FontSetScalePoint(_ctx);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = MetricsAddress;
        FontExports.FontGetCharGlyphMetrics(_ctx);

        Assert.Equal(11f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(12f, ReadFloat(MetricsAddress + 0x10));
        Assert.Equal(MathF.Round(7.2f * 64f) / 64f, ReadFloat(MetricsAddress + 0x04));
    }

    [Fact]
    public void SurfaceStyleFrame_OverridesRenderScale()
    {
        SetPixelScale(32f, 32f);
        _ctx[CpuRegister.Rdi] = SurfaceAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 64;
        _ctx[CpuRegister.Rcx] = 1;
        _ctx[CpuRegister.R8] = 64;
        _ctx[CpuRegister.R9] = 32;
        FontExports.FontRenderSurfaceInit(_ctx);

        _ctx[CpuRegister.Rdi] = StyleFrameAddress;
        FontExports.FontStyleFrameInit(_ctx);
        _ctx[CpuRegister.Rdi] = StyleFrameAddress;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(8f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(12f), 0);
        FontExports.FontStyleFrameSetScalePixel(_ctx);
        _ctx[CpuRegister.Rdi] = SurfaceAddress;
        _ctx[CpuRegister.Rsi] = StyleFrameAddress;
        FontExports.FontRenderSurfaceSetStyleFrame(_ctx);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = SurfaceAddress;
        _ctx[CpuRegister.Rcx] = MetricsAddress;
        _ctx[CpuRegister.R8] = ResultAddress;
        _ctx.SetXmmRegister(0, 0, 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(12f), 0);
        FontExports.FontRenderCharGlyphImage(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(MathF.Round(4.4f * 64f) / 64f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(MathF.Round(8.64f * 64f) / 64f, ReadFloat(MetricsAddress + 0x04));
        Assert.Equal(MathF.Round(4.8f * 64f) / 64f, ReadFloat(MetricsAddress + 0x10));
    }

    [Fact]
    public void RenderCharGlyphImage_ClipsCoverageToSurfaceAndScissorBounds()
    {
        SetPixelScale(8f, 8f);
        _memory.Fill(BufferAddress - 16, 16 + 12 * 8 + 16, 0x5A);

        _ctx[CpuRegister.Rdi] = SurfaceAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 12;
        _ctx[CpuRegister.Rcx] = 1;
        _ctx[CpuRegister.R8] = 12;
        _ctx[CpuRegister.R9] = 8;
        FontExports.FontRenderSurfaceInit(_ctx);

        _ctx[CpuRegister.Rdi] = SurfaceAddress;
        _ctx[CpuRegister.Rsi] = 10;
        _ctx[CpuRegister.Rdx] = 1;
        _ctx[CpuRegister.Rcx] = 20;
        _ctx[CpuRegister.R8] = 2;
        FontExports.FontRenderSurfaceSetScissor(_ctx);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = SurfaceAddress;
        _ctx[CpuRegister.Rcx] = MetricsAddress;
        _ctx[CpuRegister.R8] = ResultAddress;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(9f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(4f), 0);
        FontExports.FontRenderCharGlyphImageHorizontal(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt32(ResultAddress + 0x18, out var x));
        Assert.True(_ctx.TryReadUInt32(ResultAddress + 0x1C, out var y));
        Assert.True(_ctx.TryReadUInt32(ResultAddress + 0x20, out var width));
        Assert.True(_ctx.TryReadUInt32(ResultAddress + 0x24, out var height));
        Assert.Equal(10u, x);
        Assert.Equal(1u, y);
        Assert.Equal(2u, width);
        Assert.Equal(2u, height);

        Assert.All(_memory.Read(BufferAddress - 16, 16), value => Assert.Equal(0x5A, value));
        Assert.All(_memory.Read(BufferAddress + 12 * 8, 16), value => Assert.Equal(0x5A, value));
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 12; column++)
            {
                var expected = row is 1 or 2 && column >= 10 ? (byte)0xFF : (byte)0x5A;
                Assert.Equal(expected, _memory.ReadByte(BufferAddress + (ulong)(row * 12 + column)));
            }
        }
    }

    private sealed class RangeMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _data = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryGetOffset(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _data.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryGetOffset(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_data.AsSpan(offset, source.Length));
            return true;
        }

        public void Fill(ulong address, int length, byte value)
        {
            Assert.True(TryGetOffset(address, length, out var offset));
            _data.AsSpan(offset, length).Fill(value);
        }

        public byte[] Read(ulong address, int length)
        {
            Assert.True(TryGetOffset(address, length, out var offset));
            return _data.AsSpan(offset, length).ToArray();
        }

        public byte ReadByte(ulong address)
        {
            Assert.True(TryGetOffset(address, 1, out var offset));
            return _data[offset];
        }

        private bool TryGetOffset(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || length < 0)
            {
                return false;
            }

            var relative = address - baseAddress;
            if (relative > int.MaxValue || relative + (ulong)length > (ulong)_data.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
