// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong FontHandle = 0x1_0000_0000;
    private const ulong MetricsAddress = 0x1_0000_1000;
    private const ulong LayoutAddress = 0x1_0000_2000;

    private const uint ErrorInvalidParameter = 0x80460002;
    private const uint ErrorInvalidFontHandle = 0x80460005;
    private const uint ErrorNoSupportCode = 0x80460041;

    private readonly FakeCpuMemory _memory = new(0x1_0000_0000, 0x10000);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        FontExports.ResetScaleForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private void SetScalePixel(float w, float h)
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(w), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(h), 0);
        FontExports.FontSetScalePixel(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private float ReadFloat(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private void FillGarbage(ulong address, int size)
    {
        for (var offset = 0; offset < size; offset += sizeof(uint))
        {
            Assert.True(_ctx.TryWriteUInt32(address + (ulong)offset, 0xDEADBEEF));
        }
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_WritesAllFieldsAtAbiOffsets()
    {
        SetScalePixel(32f, 32f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0.55f * 32f, ReadFloat(MetricsAddress + 0x00)); // width
        Assert.Equal(0.72f * 32f, ReadFloat(MetricsAddress + 0x04)); // height
        Assert.Equal(0.04f * 32f, ReadFloat(MetricsAddress + 0x08)); // Horizontal.bearingX
        Assert.Equal(0.72f * 32f, ReadFloat(MetricsAddress + 0x0C)); // Horizontal.bearingY
        Assert.Equal(0.60f * 32f, ReadFloat(MetricsAddress + 0x10)); // Horizontal.advance
        Assert.Equal(0f, ReadFloat(MetricsAddress + 0x14)); // Vertical.bearingX
        Assert.Equal(0f, ReadFloat(MetricsAddress + 0x18)); // Vertical.bearingY
        Assert.Equal(0f, ReadFloat(MetricsAddress + 0x1C)); // Vertical.advance
    }

    [Fact]
    public void GetCharGlyphMetrics_FallbackVariant_MatchesRenderVariant()
    {
        SetScalePixel(24f, 24f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'g';
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetCharGlyphMetrics(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0.55f * 24f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(0.60f * 24f, ReadFloat(MetricsAddress + 0x10));
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_NoScaleSet_UsesDefaultPixelScale()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0.55f * 16f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(0.60f * 16f, ReadFloat(MetricsAddress + 0x10));
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_Space_HasNoInkButPositiveAdvance()
    {
        SetScalePixel(32f, 32f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = ' ';
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(0f, ReadFloat(MetricsAddress + 0x04));
        Assert.Equal(0.30f * 32f, ReadFloat(MetricsAddress + 0x10));
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_CodeZero_ZeroesStructAndReturnsNoSupportCode()
    {
        FillGarbage(MetricsAddress, 0x20);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(ErrorNoSupportCode, (uint)_ctx[CpuRegister.Rax]);
        for (var offset = 0; offset < 0x20; offset += sizeof(uint))
        {
            Assert.Equal(0f, ReadFloat(MetricsAddress + (ulong)offset));
        }
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_NullHandle_ZeroesStructAndReturnsInvalidHandle()
    {
        FillGarbage(MetricsAddress, 0x20);
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = MetricsAddress;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(ErrorInvalidFontHandle, (uint)_ctx[CpuRegister.Rax]);
        for (var offset = 0; offset < 0x20; offset += sizeof(uint))
        {
            Assert.Equal(0f, ReadFloat(MetricsAddress + (ulong)offset));
        }
    }

    [Fact]
    public void GetRenderCharGlyphMetrics_NullOutPointer_ReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = 0;

        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(ErrorInvalidParameter, (uint)_ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetHorizontalLayout_WritesThreeFloatsAtAbiOffsets()
    {
        SetScalePixel(32f, 32f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = LayoutAddress;

        FontExports.FontGetHorizontalLayout(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0.88f * 32f, ReadFloat(LayoutAddress + 0x00)); // baselineOffset
        Assert.Equal(1.18f * 32f, ReadFloat(LayoutAddress + 0x04)); // lineAdvance
        Assert.Equal(0f, ReadFloat(LayoutAddress + 0x08)); // decorationExtent
    }

    [Fact]
    public void GetHorizontalLayout_IsConsistentWithGlyphMetrics()
    {
        SetScalePixel(48f, 48f);
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'W';
        _ctx[CpuRegister.Rdx] = MetricsAddress;
        FontExports.FontGetRenderCharGlyphMetrics(_ctx);
        var height = ReadFloat(MetricsAddress + 0x04);
        var bearingY = ReadFloat(MetricsAddress + 0x0C);
        var advance = ReadFloat(MetricsAddress + 0x10);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = LayoutAddress;
        FontExports.FontGetHorizontalLayout(_ctx);
        var baselineOffset = ReadFloat(LayoutAddress + 0x00);
        var lineAdvance = ReadFloat(LayoutAddress + 0x04);

        // The layout loop only terminates if glyphs fit their line box and the
        // pen always moves forward.
        Assert.True(advance > 0f);
        Assert.True(bearingY <= baselineOffset);
        Assert.True(height - bearingY <= lineAdvance - baselineOffset);
        Assert.True(baselineOffset < lineAdvance);
    }

    [Fact]
    public void GetHorizontalLayout_NullHandle_ZeroesStructAndReturnsInvalidHandle()
    {
        FillGarbage(LayoutAddress, 0x0C);
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = LayoutAddress;

        FontExports.FontGetHorizontalLayout(_ctx);

        Assert.Equal(ErrorInvalidFontHandle, (uint)_ctx[CpuRegister.Rax]);
        for (var offset = 0; offset < 0x0C; offset += sizeof(uint))
        {
            Assert.Equal(0f, ReadFloat(LayoutAddress + (ulong)offset));
        }
    }

    [Fact]
    public void GetHorizontalLayout_NullOutPointer_ReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 0;

        FontExports.FontGetHorizontalLayout(_ctx);

        Assert.Equal(ErrorInvalidParameter, (uint)_ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetupRenderScalePixel_AlsoRecordsScale()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(20f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(20f), 0);
        FontExports.FontSetupRenderScalePixel(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = MetricsAddress;
        FontExports.FontGetRenderCharGlyphMetrics(_ctx);

        Assert.Equal(0.60f * 20f, ReadFloat(MetricsAddress + 0x10));
    }
}
