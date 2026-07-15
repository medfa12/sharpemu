// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Regression tests for the glyph-bitmap base handed out by the libSceFont
/// HLE. Astro Bot's text engine reads the render surface's buffer field
/// (surface+0x00, echoed at OrbisFontRenderOutput.SurfaceImage.address,
/// result+0x08) as the SOURCE of its per-row glyph-atlas memcpy and adds an
/// in-surface pen offset (observed: 32*0x800 + 0x40A = 0x1040A, worst case
/// 0x18980 + 0x76E). When that base was left null the blit faulted on
/// null+0x1040A; these tests pin that the base is always non-null, readable,
/// zero-filled, and large enough for every observed offset.
/// </summary>
public sealed class FontGlyphBitmapTests
{
    private const ulong SurfAddress = 0x1000;
    private const ulong MetricsAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;
    private const ulong OutHandleAddress = 0x4000;
    private const ulong OutGlyphAddress = 0x4100;
    private const ulong GameBufferAddress = 0x50_0000;

    // The crash constants: src = base + penRow*0x800 + penCol, with 0x76E
    // bytes copied per row and 0x18980 the largest per-run offset observed.
    private const ulong CrashOffset = 0x1040A;
    private const int CrashLength = 0x76E;
    private const ulong WorstObservedOffset = 0x18980 + 0x76E;
    private const uint FallbackWidthByte = 0x800;

    public FontGlyphBitmapTests()
    {
        // The blank arena is cached process-wide but every test builds fresh
        // guest memory, so a stale cached address would be unmapped here.
        FontExports.ResetGlyphStateForTests();
        FontExports.ResetScaleForTests();
    }

    private static CpuContext NewContext(out AllocatingGuestMemory memory)
    {
        memory = new AllocatingGuestMemory();
        memory.TryWrite(SurfAddress, new byte[0x80]);
        memory.TryWrite(MetricsAddress, new byte[0x20]);
        memory.TryWrite(ResultAddress, new byte[0x40]);
        memory.TryWrite(OutHandleAddress, new byte[0x08]);
        memory.TryWrite(OutGlyphAddress, new byte[0x08]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static ulong OpenFontHandle(CpuContext ctx)
    {
        // sceFontOpenFontMemory(library, memory, size, options, out) with the
        // out slot in R8, as at the game's call site.
        ctx[CpuRegister.Rdi] = 0x1234;
        ctx[CpuRegister.R8] = OutHandleAddress;
        FontExports.FontOpenFontMemory(ctx);
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static void AssertReadableZeroRange(CpuContext ctx, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(ctx.Memory.TryRead(address, bytes));
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void RenderSurfaceInit_StoresBufferAndGeometry()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = SurfAddress;
        ctx[CpuRegister.Rsi] = GameBufferAddress;
        ctx[CpuRegister.Rdx] = FallbackWidthByte; // bufWidthByte
        ctx[CpuRegister.Rcx] = 1;                 // pixelSizeByte
        ctx[CpuRegister.R8] = 2048;               // widthPixel
        ctx[CpuRegister.R9] = 1024;               // heightPixel

        FontExports.FontRenderSurfaceInit(ctx);

        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(SurfAddress + 0x00, out var buffer));
        Assert.Equal(GameBufferAddress, buffer);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x08, out var widthByte));
        Assert.Equal(FallbackWidthByte, widthByte);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x0C, out var pixelSize));
        Assert.Equal(1u, pixelSize);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x10, out var width));
        Assert.Equal(2048u, width);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x14, out var height));
        Assert.Equal(1024u, height);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x18, out var scX0));
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x1C, out var scY0));
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x20, out var scX1));
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x24, out var scY1));
        Assert.Equal(0u, scX0);
        Assert.Equal(0u, scY0);
        Assert.Equal(2048u, scX1);
        Assert.Equal(1024u, scY1);
    }

    [Fact]
    public void RenderSurfaceInit_NullBuffer_SubstitutesZeroedArena()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = SurfAddress;
        ctx[CpuRegister.Rsi] = 0; // no backing store from the title
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 2048;
        ctx[CpuRegister.R9] = 32;

        FontExports.FontRenderSurfaceInit(ctx);

        Assert.True(ctx.TryReadUInt64(SurfAddress + 0x00, out var buffer));
        Assert.NotEqual(0UL, buffer);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x08, out var widthByte));
        Assert.Equal(FallbackWidthByte, widthByte);
        Assert.True(ctx.TryReadUInt32(SurfAddress + 0x0C, out var pixelSize));
        Assert.Equal(1u, pixelSize);

        // The exact crash read: base + 32*0x800 + 0x40A, 0x76E bytes.
        AssertReadableZeroRange(ctx, buffer + CrashOffset, CrashLength);
    }

    [Fact]
    public void GenerateCharGlyph_ThenRenderImage_HandsOutReadableZeroedBitmapBase()
    {
        var ctx = NewContext(out _);
        var handle = OpenFontHandle(ctx);

        // sceFontGenerateCharGlyph(handle, code, params, outGlyph).
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 'A';
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = OutGlyphAddress;
        FontExports.FontGenerateCharGlyph(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(OutGlyphAddress, out var glyph));
        Assert.NotEqual(0UL, glyph);
        Assert.True(ctx.TryReadUInt16(glyph + 0x00, out var magic));
        Assert.Equal((ushort)0x0F03, magic);
        // Fields past the known header read as mapped zeros (the pre-fix
        // crash was a read at glyph+0x40C).
        AssertReadableZeroRange(ctx, glyph + 0x40C, 0x10);

        // sceFontRenderCharGlyphImageHorizontal with a surface the title never
        // ran through sceFontRenderSurfaceInit (buffer and stride still zero).
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 'A';
        ctx[CpuRegister.Rdx] = SurfAddress;
        ctx[CpuRegister.Rcx] = MetricsAddress;
        ctx[CpuRegister.R8] = ResultAddress;
        ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(1034f), 0); // pen x
        ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(32f), 0);   // pen y
        FontExports.FontRenderCharGlyphImageHorizontal(ctx);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        // SurfaceImage.address (result+0x08) is the base the game's atlas
        // blit reads from; it must be non-null with a sane stride and pixel
        // size beside it.
        Assert.True(ctx.TryReadUInt64(ResultAddress + 0x08, out var bitmapBase));
        Assert.NotEqual(0UL, bitmapBase);
        Assert.True(ctx.TryReadUInt32(ResultAddress + 0x10, out var widthByte));
        Assert.Equal(FallbackWidthByte, widthByte);
        Assert.True(ctx.TryReadUInt32(ResultAddress + 0x14, out var pixelSize));
        Assert.Equal(1u, pixelSize);

        // The deterministic faulting read and the worst offset observed
        // across boots must both land in mapped, zero-filled memory.
        AssertReadableZeroRange(ctx, bitmapBase + CrashOffset, CrashLength);
        AssertReadableZeroRange(ctx, bitmapBase + WorstObservedOffset - CrashLength, CrashLength);
        AssertReadableZeroRange(ctx, bitmapBase + 0x20000, 0x100); // headroom

        // The metrics the game sizes its copy with stay non-zero so its
        // width*height length (0x76E at the crash) remains consistent.
        Assert.True(ctx.TryReadUInt32(MetricsAddress + 0x00, out var glyphWidthBits));
        Assert.True(ctx.TryReadUInt32(MetricsAddress + 0x10, out var advanceBits));
        Assert.True(BitConverter.UInt32BitsToSingle(glyphWidthBits) > 0f);
        Assert.True(BitConverter.UInt32BitsToSingle(advanceBits) > 0f);
    }

    /// <summary>
    /// Sparse guest memory that also satisfies the virtual-range allocator so
    /// KernelMemoryCompatExports.TryAllocateHleData (the scratch allocator
    /// behind font handles, glyph objects, and the blank glyph arena) works
    /// under test. Allocations report the aligned desired address; the
    /// allocator's own zero-fill then maps the bytes.
    /// </summary>
    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly Dictionary<ulong, byte> _bytes = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                if (!_bytes.TryGetValue(virtualAddress + (ulong)i, out var value))
                {
                    return false;
                }

                destination[i] = value;
            }

            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            for (var i = 0; i < source.Length; i++)
            {
                _bytes[virtualAddress + (ulong)i] = source[i];
            }

            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
            => desiredAddress;

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress)
        {
            var effectiveAlignment = Math.Max(alignment, 1UL);
            actualAddress = (desiredAddress + effectiveAlignment - 1) & ~(effectiveAlignment - 1);
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }

        public bool TryFreeGuestMemory(ulong address) => false;
    }
}
