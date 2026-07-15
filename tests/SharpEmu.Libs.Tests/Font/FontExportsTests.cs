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
    private const ulong GlyphSlotAddress = 0x1_0000_3000;
    private const ulong SurfaceAddress = 0x1_0000_4000;
    private const ulong SurfaceBufferAddress = 0x1_0000_5000;
    private const ulong RenderResultAddress = 0x1_0000_6000;
    private const ulong GenerateParamsAddress = 0x1_0000_7000;
    private const ulong GlyphAllocationBase = 0x1_0000_8000;

    private const uint ErrorInvalidParameter = 0x80460002;
    private const uint ErrorInvalidFontHandle = 0x80460005;
    private const uint ErrorInvalidGlyph = 0x80460006;
    private const uint ErrorNoSupportCode = 0x80460041;

    // The glyph exports allocate scratch objects through the HLE data
    // allocator, which needs an IGuestAddressSpace behind ctx.Memory; the
    // fake hands out pages from GlyphAllocationBase upward.
    private readonly FakeGuestMemory _memory = new(0x1_0000_0000, 0x10000, GlyphAllocationBase);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        FontExports.ResetScaleForTests();
        FontExports.ResetGlyphStateForTests();
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

    // (handle, codepoint, params, outGlyph) -> RDI, ESI, RDX, RCX.
    private ulong GenerateGlyph(uint code, ulong paramsAddress = 0)
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = code;
        _ctx[CpuRegister.Rdx] = paramsAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;
        FontExports.FontGenerateCharGlyph(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(GlyphSlotAddress, out var glyph));
        Assert.NotEqual(0UL, glyph);
        return glyph;
    }

    // (const OrbisFontMem* memory, OrbisFontGlyph* glyph) -> RDI, RSI.
    private void DeleteGlyph(ulong slotAddress)
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = slotAddress;
        FontExports.FontDeleteGlyph(_ctx);
    }

    // OrbisFontRenderSurface: buffer @+0x00, widthByte @+0x08,
    // pixelSizeByte @+0x0C, width @+0x10, height @+0x14.
    private void InitRenderSurface(uint widthByte = 256, uint pixelSize = 1, uint width = 64, uint height = 16)
    {
        Assert.True(_ctx.TryWriteUInt64(SurfaceAddress + 0x00, SurfaceBufferAddress));
        Assert.True(_ctx.TryWriteUInt32(SurfaceAddress + 0x08, widthByte));
        Assert.True(_ctx.TryWriteUInt32(SurfaceAddress + 0x0C, pixelSize));
        Assert.True(_ctx.TryWriteUInt32(SurfaceAddress + 0x10, width));
        Assert.True(_ctx.TryWriteUInt32(SurfaceAddress + 0x14, height));
    }

    // (handle, code, surf, x, y, metrics, result) -> RDI, ESI, RDX,
    // XMM0, XMM1, RCX, R8; the floats do not shift the integer slots.
    private void CallRender(ulong handle, uint code, ulong surf, ulong metrics, ulong result, float x = 0f, float y = 0f)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = code;
        _ctx[CpuRegister.Rdx] = surf;
        _ctx[CpuRegister.Rcx] = metrics;
        _ctx[CpuRegister.R8] = result;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(x), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(y), 0);
        FontExports.FontRenderCharGlyphImageHorizontal(_ctx);
    }

    private void AssertRenderOutputsZeroed()
    {
        for (var offset = 0; offset < 0x20; offset += sizeof(uint))
        {
            Assert.Equal(0f, ReadFloat(MetricsAddress + (ulong)offset));
        }

        for (var offset = 0; offset < 0x40; offset += sizeof(uint))
        {
            Assert.True(_ctx.TryReadUInt32(RenderResultAddress + (ulong)offset, out var word));
            Assert.Equal(0u, word);
        }
    }

    [Fact]
    public void GenerateCharGlyph_ValidArgs_ReturnsNonNullGlyphWithHeader()
    {
        SetScalePixel(32f, 32f);

        var glyph = GenerateGlyph('A');

        // OrbisFontGlyphOpaque header: magic @+0x00, em_size @+0x06,
        // baseline @+0x08, height_px @+0x0A, origin_y @+0x0E,
        // scale_x @+0x10, base_scale @+0x14.
        Assert.True(_ctx.TryReadUInt32(glyph + 0x00, out var word0));
        Assert.Equal(0x0F03u, word0 & 0xFFFF);
        Assert.True(_ctx.TryReadUInt32(glyph + 0x04, out var word1));
        Assert.Equal(32u, word1 >> 16); // em_size
        Assert.True(_ctx.TryReadUInt32(glyph + 0x08, out var word2));
        Assert.Equal(23u, word2 & 0xFFFF); // baseline = 0.72 * 32
        Assert.Equal(23u, word2 >> 16); // height_px, the pre-fix crash read
        Assert.True(_ctx.TryReadUInt32(glyph + 0x0C, out var word3));
        Assert.Equal(23u, word3 >> 16); // origin_y = baseline
        Assert.Equal(32f, ReadFloat(glyph + 0x10)); // scale_x
        Assert.Equal(32f, ReadFloat(glyph + 0x14)); // base_scale
    }

    [Fact]
    public void GenerateCharGlyph_ScratchCoversDeepGuestFieldReads()
    {
        var glyph = GenerateGlyph('A');

        // The game's renderer reads fields far past the public header (the
        // pre-fix fault was at glyph+0x40C); the whole scratch page must be
        // mapped and zeroed.
        Assert.True(_ctx.TryReadUInt32(glyph + 0x40C, out var deep));
        Assert.Equal(0u, deep);
        Assert.True(_ctx.TryReadUInt32(glyph + 0x1000 - sizeof(uint), out var last));
        Assert.Equal(0u, last);
    }

    [Fact]
    public void GenerateCharGlyph_CopiesParamsFormsAndMemory()
    {
        const ulong memPointer = 0x1_0000_0F00;
        // OrbisFontGenerateGlyphParams: id @+0x00, formOptions @+0x04,
        // glyphForm @+0x06, metricsForm @+0x07, OrbisFontMem* @+0x08.
        Assert.True(_ctx.TryWriteUInt32(GenerateParamsAddress + 0x00, 0x0FD3));
        Assert.True(_ctx.TryWriteUInt32(GenerateParamsAddress + 0x04, 0x0101_0011));
        Assert.True(_ctx.TryWriteUInt64(GenerateParamsAddress + 0x08, memPointer));

        var glyph = GenerateGlyph('A', GenerateParamsAddress);

        Assert.True(_ctx.TryReadUInt32(glyph + 0x00, out var word0));
        Assert.Equal(0x0F03u, word0 & 0xFFFF);
        Assert.Equal(0x0011u, word0 >> 16); // flags = formOptions
        Assert.True(_ctx.TryReadUInt32(glyph + 0x04, out var word1));
        Assert.Equal(1u, word1 & 0xFF); // glyph_form
        Assert.Equal(1u, (word1 >> 8) & 0xFF); // metrics_form
        Assert.True(_ctx.TryReadUInt64(glyph + 0x18, out var glyphMemory));
        Assert.Equal(memPointer, glyphMemory);
    }

    [Fact]
    public void GenerateCharGlyph_NullOutPointer_ReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;

        FontExports.FontGenerateCharGlyph(_ctx);

        Assert.Equal(ErrorInvalidParameter, (uint)_ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GenerateCharGlyph_NullHandle_ClearsSlotAndReturnsInvalidHandle()
    {
        Assert.True(_ctx.TryWriteUInt64(GlyphSlotAddress, 0xDEAD_BEEF_DEAD_BEEF));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        FontExports.FontGenerateCharGlyph(_ctx);

        Assert.Equal(ErrorInvalidFontHandle, (uint)_ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(GlyphSlotAddress, out var slot));
        Assert.Equal(0UL, slot);
    }

    [Fact]
    public void GenerateCharGlyph_CodeZero_ClearsSlotAndReturnsNoSupportCode()
    {
        Assert.True(_ctx.TryWriteUInt64(GlyphSlotAddress, 0xDEAD_BEEF_DEAD_BEEF));
        _ctx[CpuRegister.Rdi] = FontHandle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        FontExports.FontGenerateCharGlyph(_ctx);

        Assert.Equal(ErrorNoSupportCode, (uint)_ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(GlyphSlotAddress, out var slot));
        Assert.Equal(0UL, slot);
    }

    [Fact]
    public void GlyphDefineAttribute_ReturnsOk()
    {
        _ctx[CpuRegister.Rax] = 0xDEAD;

        FontExports.FontGlyphDefineAttribute(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_ValidArgs_FillsMetricsAndResult()
    {
        SetScalePixel(32f, 32f);
        InitRenderSurface(widthByte: 256, pixelSize: 1);
        FillGarbage(MetricsAddress, 0x20);
        FillGarbage(RenderResultAddress, 0x40);

        CallRender(FontHandle, 'A', SurfaceAddress, MetricsAddress, RenderResultAddress, 12f, 24f);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        // Metrics agree with the layout queries so pen advances stay coherent.
        Assert.Equal(0.55f * 32f, ReadFloat(MetricsAddress + 0x00));
        Assert.Equal(0.60f * 32f, ReadFloat(MetricsAddress + 0x10));
        // OrbisFontRenderOutput: stage null, SurfaceImage echoing the caller's
        // surface, empty UpdateRect (nothing drawn), metrics-derived advance.
        Assert.True(_ctx.TryReadUInt64(RenderResultAddress + 0x00, out var stage));
        Assert.Equal(0UL, stage);
        Assert.True(_ctx.TryReadUInt64(RenderResultAddress + 0x08, out var image));
        Assert.Equal(SurfaceBufferAddress, image);
        Assert.True(_ctx.TryReadUInt32(RenderResultAddress + 0x10, out var pitch));
        Assert.Equal(256u, pitch);
        Assert.True(_ctx.TryReadUInt32(RenderResultAddress + 0x14, out var pixelSize));
        Assert.Equal(1u, pixelSize);
        for (var offset = 0x18; offset < 0x28; offset += sizeof(uint))
        {
            Assert.True(_ctx.TryReadUInt32(RenderResultAddress + (ulong)offset, out var rect));
            Assert.Equal(0u, rect);
        }

        Assert.Equal(0.04f * 32f, ReadFloat(RenderResultAddress + 0x28)); // bearingX
        Assert.Equal(0.72f * 32f, ReadFloat(RenderResultAddress + 0x2C)); // bearingY
        Assert.Equal(0.60f * 32f, ReadFloat(RenderResultAddress + 0x30)); // advance > 0
        Assert.Equal(0.60f * 32f, ReadFloat(RenderResultAddress + 0x34)); // stride
        Assert.True(_ctx.TryReadUInt64(RenderResultAddress + 0x38, out var imageDims));
        Assert.Equal(0UL, imageDims);
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_LeavesSurfaceBufferUntouched()
    {
        SetScalePixel(32f, 32f);
        InitRenderSurface();
        FillGarbage(SurfaceBufferAddress, 0x100);

        CallRender(FontHandle, 'A', SurfaceAddress, MetricsAddress, RenderResultAddress, 4f, 12f);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        for (var offset = 0; offset < 0x100; offset += sizeof(uint))
        {
            Assert.True(_ctx.TryReadUInt32(SurfaceBufferAddress + (ulong)offset, out var pixel));
            Assert.Equal(0xDEADBEEFu, pixel);
        }
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_NullHandle_ZeroesOutputsAndReturnsInvalidHandle()
    {
        FillGarbage(MetricsAddress, 0x20);
        FillGarbage(RenderResultAddress, 0x40);

        CallRender(0, 'A', SurfaceAddress, MetricsAddress, RenderResultAddress);

        Assert.Equal(ErrorInvalidFontHandle, (uint)_ctx[CpuRegister.Rax]);
        AssertRenderOutputsZeroed();
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_CodeZero_ZeroesOutputsAndReturnsNoSupportCode()
    {
        FillGarbage(MetricsAddress, 0x20);
        FillGarbage(RenderResultAddress, 0x40);

        CallRender(FontHandle, 0, SurfaceAddress, MetricsAddress, RenderResultAddress);

        Assert.Equal(ErrorNoSupportCode, (uint)_ctx[CpuRegister.Rax]);
        AssertRenderOutputsZeroed();
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_NullSurface_ZeroesOutputsAndReturnsInvalidParameter()
    {
        FillGarbage(MetricsAddress, 0x20);
        FillGarbage(RenderResultAddress, 0x40);

        CallRender(FontHandle, 'A', 0, MetricsAddress, RenderResultAddress);

        Assert.Equal(ErrorInvalidParameter, (uint)_ctx[CpuRegister.Rax]);
        AssertRenderOutputsZeroed();
    }

    [Fact]
    public void DeleteGlyph_ValidGlyph_NullsSlotAndReturnsOk()
    {
        _ = GenerateGlyph('A');

        DeleteGlyph(GlyphSlotAddress);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(GlyphSlotAddress, out var slot));
        Assert.Equal(0UL, slot);
    }

    [Fact]
    public void DeleteGlyph_NullSlot_ReturnsInvalidParameter()
    {
        DeleteGlyph(0);

        Assert.Equal(ErrorInvalidParameter, (uint)_ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DeleteGlyph_NullGlyphInSlot_ReturnsInvalidGlyph()
    {
        Assert.True(_ctx.TryWriteUInt64(GlyphSlotAddress, 0));

        DeleteGlyph(GlyphSlotAddress);

        Assert.Equal(ErrorInvalidGlyph, (uint)_ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DeleteGlyph_RecyclesScratch_NextGenerateHandsBackZeroedObject()
    {
        var first = GenerateGlyph('A');
        // The guest may scribble anywhere in the object; recycling must not
        // leak that state into the next glyph.
        Assert.True(_ctx.TryWriteUInt32(first + 0x200, 0xDEADBEEF));
        DeleteGlyph(GlyphSlotAddress);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        var second = GenerateGlyph('B');

        Assert.Equal(first, second);
        Assert.True(_ctx.TryReadUInt32(second + 0x200, out var scribble));
        Assert.Equal(0u, scribble);
    }
}
