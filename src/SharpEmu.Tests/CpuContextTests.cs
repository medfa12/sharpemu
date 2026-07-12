// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Tests for the guest CPU state abstraction: register file, typed memory
/// accessors, SSE/AVX register storage, stack push/pop, and the Rax return
/// convention used by every HLE export.
/// </summary>
public sealed class CpuContextTests
{
    private const ulong Base = 0x4000;

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    [Fact]
    public void Constructor_NullMemory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CpuContext(null!, Generation.Gen5));
    }

    [Fact]
    public void Registers_StoreAndRetrieveIndependently()
    {
        var ctx = NewContext(out _);

        ctx[CpuRegister.Rdi] = 0x1111;
        ctx[CpuRegister.Rsi] = 0x2222;
        ctx[CpuRegister.R15] = 0xFFFF_FFFF_FFFF_FFFF;

        Assert.Equal(0x1111UL, ctx[CpuRegister.Rdi]);
        Assert.Equal(0x2222UL, ctx[CpuRegister.Rsi]);
        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, ctx[CpuRegister.R15]);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]); // untouched
    }

    [Fact]
    public void RaxWriteFlag_TracksExplicitWrites()
    {
        var ctx = NewContext(out _);

        Assert.False(ctx.WasRaxWritten);

        ctx[CpuRegister.Rbx] = 5; // other registers do not set the flag
        Assert.False(ctx.WasRaxWritten);

        ctx[CpuRegister.Rax] = 42;
        Assert.True(ctx.WasRaxWritten);

        ctx.ClearRaxWriteFlag();
        Assert.False(ctx.WasRaxWritten);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0xFF)]
    public void ByteRoundTrip(byte value)
    {
        var ctx = NewContext(out _);
        Assert.True(ctx.Memory.TryWrite(Base, new[] { value }));
        Assert.True(ctx.TryReadByte(Base, out var read));
        Assert.Equal(value, read);
    }

    [Fact]
    public void TypedAccessors_RoundTripLittleEndian()
    {
        var ctx = NewContext(out _);

        Assert.True(ctx.TryWriteUInt16(Base, 0xABCD));
        Assert.True(ctx.TryWriteInt32(Base + 8, -12345));
        Assert.True(ctx.TryWriteUInt32(Base + 16, 0xDEAD_BEEF));
        Assert.True(ctx.TryWriteInt64(Base + 24, -1));
        Assert.True(ctx.TryWriteUInt64(Base + 32, 0x0123_4567_89AB_CDEF));

        Assert.True(ctx.TryReadUInt16(Base, out var u16));
        Assert.Equal(0xABCD, u16);
        Assert.True(ctx.TryReadInt32(Base + 8, out var i32));
        Assert.Equal(-12345, i32);
        Assert.True(ctx.TryReadUInt32(Base + 16, out var u32));
        Assert.Equal(0xDEAD_BEEFu, u32);
        Assert.True(ctx.TryReadUInt64(Base + 32, out var u64));
        Assert.Equal(0x0123_4567_89AB_CDEFUL, u64);
    }

    [Fact]
    public void LittleEndian_ByteOrderIsCorrect()
    {
        var ctx = NewContext(out var memory);
        ctx.TryWriteUInt32(Base, 0x11223344);

        var bytes = new byte[4];
        Assert.True(memory.TryRead(Base, bytes));
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, bytes);
    }

    [Fact]
    public void TypedReads_UnmappedAddress_ReturnFalseAndZero()
    {
        var ctx = NewContext(out _);

        Assert.False(ctx.TryReadByte(Base, out var b));
        Assert.Equal(0, b);
        Assert.False(ctx.TryReadUInt16(Base, out var s));
        Assert.Equal(0, s);
        Assert.False(ctx.TryReadUInt64(Base, out var q));
        Assert.Equal(0UL, q);
    }

    [Fact]
    public void TryWriteInt32_CheckNilRejectsNullAddress()
    {
        var ctx = NewContext(out _);

        Assert.False(ctx.TryWriteInt32(0, 1, checkNil: true));
        Assert.True(ctx.TryWriteInt32(Base, 1, checkNil: true));
    }

    [Fact]
    public void TryReadNullTerminatedUtf8_StopsAtTerminator()
    {
        var ctx = NewContext(out var memory);
        var bytes = Encoding.UTF8.GetBytes("libKernel");
        memory.TryWrite(Base, bytes);
        memory.TryWrite(Base + (ulong)bytes.Length, new byte[] { 0, (byte)'X' });

        Assert.True(ctx.TryReadNullTerminatedUtf8(Base, 32, out var value));
        Assert.Equal("libKernel", value);
    }

    [Fact]
    public void TryReadNullTerminatedUtf8_NoTerminatorWithinCapacity_ReturnsCappedString()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(Base, Encoding.UTF8.GetBytes("ABCDEF"));

        Assert.True(ctx.TryReadNullTerminatedUtf8(Base, 4, out var value));
        Assert.Equal("ABCD", value);
    }

    [Fact]
    public void TryReadNullTerminatedUtf8_NullAddressOrZeroCapacity_ReturnsFalse()
    {
        var ctx = NewContext(out _);

        Assert.False(ctx.TryReadNullTerminatedUtf8(0, 8, out _));
        Assert.False(ctx.TryReadNullTerminatedUtf8(Base, 0, out _));
    }

    [Fact]
    public void TryReadNullTerminatedUtf8_UnmappedByte_ReturnsFalse()
    {
        var ctx = NewContext(out _); // nothing mapped

        Assert.False(ctx.TryReadNullTerminatedUtf8(Base, 8, out _));
    }

    [Fact]
    public void XmmRegister_StoresLowAndHighLanes()
    {
        var ctx = NewContext(out _);

        ctx.SetXmmRegister(3, low: 0xAAAA, high: 0xBBBB);

        ctx.GetXmmRegister(3, out var low, out var high);
        Assert.Equal(0xAAAAUL, low);
        Assert.Equal(0xBBBBUL, high);
    }

    [Fact]
    public void XmmRegister_OutOfRange_Throws()
    {
        var ctx = NewContext(out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.SetXmmRegister(16, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.GetXmmRegister(16, out _, out _));
    }

    [Fact]
    public void Stack_PushThenPopIsLifoAndMovesRsp()
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(0x8000, 0); // ensure the stack region is mapped
        for (ulong a = 0x7F00; a <= 0x8000; a += 8)
        {
            memory.WriteUInt64(a, 0);
        }

        ctx[CpuRegister.Rsp] = 0x8000;

        Assert.True(ctx.PushUInt64(0x1111));
        Assert.True(ctx.PushUInt64(0x2222));
        Assert.Equal(0x8000UL - 16, ctx[CpuRegister.Rsp]);

        Assert.True(ctx.PopUInt64(out var first));
        Assert.Equal(0x2222UL, first); // last in, first out
        Assert.True(ctx.PopUInt64(out var second));
        Assert.Equal(0x1111UL, second);
        Assert.Equal(0x8000UL, ctx[CpuRegister.Rsp]);
    }

    [Fact]
    public void SetReturn_Int_SignExtendsToRaxAndReturnsInput()
    {
        var ctx = NewContext(out _);

        var returned = ctx.SetReturn(-5);

        Assert.Equal(-5, returned);
        // The default overload casts int->ulong, which sign-extends to 64 bits.
        Assert.Equal(unchecked((ulong)(long)-5), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetReturn_PositiveValue_ZeroFillsUpperBits()
    {
        var ctx = NewContext(out _);

        ctx.SetReturn(0x1234);

        Assert.Equal(0x1234UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetReturn_LongCast_SignExtendsToRax()
    {
        var ctx = NewContext(out _);

        ctx.SetReturn(-5, typeof(long));

        Assert.Equal(unchecked((ulong)(long)-5), ctx[CpuRegister.Rax]); // full 64-bit sign extension
    }

    [Fact]
    public void SetReturn_UnsupportedCast_Throws()
    {
        var ctx = NewContext(out _);

        Assert.Throws<NotSupportedException>(() => ctx.SetReturn(1, typeof(int)));
    }

    [Fact]
    public void SetReturn_OrbisResult_MapsToRax()
    {
        var ctx = NewContext(out _);

        var returned = ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, returned);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND),
            ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void TargetGeneration_IsExposed()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        Assert.Equal(Generation.Gen5, ctx.TargetGeneration);
    }
}
