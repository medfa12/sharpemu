// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// libc CRT shims (memset/strlen/strcmp/strncmp) driven through the guest ABI.
/// Guest pointers must be in the canonical user range [0x1000, 0x800000000000).
/// </summary>
public sealed class KernelLibcTests
{
    private const ulong A = 0x10_0000;
    private const ulong B = 0x20_0000;
    private const ulong Dest = 0x30_0000;

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void WriteCString(SparseGuestMemory memory, ulong address, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        memory.TryWrite(address, bytes);
        memory.TryWrite(address + (ulong)bytes.Length, new byte[] { 0 });
    }

    [Fact]
    public void Memset_FillsRegionAndReturnsDestination()
    {
        var ctx = NewContext(out var memory);
        for (ulong i = 0; i < 16; i++)
        {
            memory.TryWrite(Dest + i, new byte[] { 0x11 });
        }

        ctx[CpuRegister.Rdi] = Dest;
        ctx[CpuRegister.Rsi] = 0xAB;
        ctx[CpuRegister.Rdx] = 8;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Memset(ctx));

        Assert.Equal(Dest, ctx[CpuRegister.Rax]); // memset returns dst
        for (ulong i = 0; i < 8; i++)
        {
            Assert.True(ctx.TryReadByte(Dest + i, out var b));
            Assert.Equal(0xAB, b);
        }

        Assert.True(ctx.TryReadByte(Dest + 8, out var beyond));
        Assert.Equal(0x11, beyond); // byte past length untouched
    }

    [Fact]
    public void Memset_TruncatesValueToLowByte()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(Dest, new byte[] { 0 });

        ctx[CpuRegister.Rdi] = Dest;
        ctx[CpuRegister.Rsi] = 0x12FF; // only 0xFF should be written
        ctx[CpuRegister.Rdx] = 1;
        KernelMemoryCompatExports.Memset(ctx);

        Assert.True(ctx.TryReadByte(Dest, out var b));
        Assert.Equal(0xFF, b);
    }

    [Fact]
    public void Memset_ZeroLength_ReturnsDestinationWithoutWriting()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(Dest, new byte[] { 0x55 });

        ctx[CpuRegister.Rdi] = Dest;
        ctx[CpuRegister.Rsi] = 0x00;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Memset(ctx));

        Assert.Equal(Dest, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadByte(Dest, out var b));
        Assert.Equal(0x55, b); // unchanged
    }

    // Note: strlen's read path performs a kernel32 page-accessibility check and is
    // therefore Windows-only; it is exercised on the CI runner, not in these
    // cross-platform unit tests.

    [Fact]
    public void Strcmp_EqualStrings_ReturnsZero()
    {
        var ctx = NewContext(out var memory);
        WriteCString(memory, A, "sceKernel");
        WriteCString(memory, B, "sceKernel");

        ctx[CpuRegister.Rdi] = A;
        ctx[CpuRegister.Rsi] = B;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Strcmp(ctx));
        Assert.Equal(0, unchecked((int)ctx[CpuRegister.Rax]));
    }

    [Fact]
    public void Strcmp_ReturnsSignOfFirstDifferingByte()
    {
        var ctx = NewContext(out var memory);
        WriteCString(memory, A, "abc");
        WriteCString(memory, B, "abd"); // 'c'(0x63) - 'd'(0x64) = -1

        ctx[CpuRegister.Rdi] = A;
        ctx[CpuRegister.Rsi] = B;
        KernelMemoryCompatExports.Strcmp(ctx);
        Assert.True(unchecked((int)ctx[CpuRegister.Rax]) < 0);

        // Reverse operands -> positive.
        ctx[CpuRegister.Rdi] = B;
        ctx[CpuRegister.Rsi] = A;
        KernelMemoryCompatExports.Strcmp(ctx);
        Assert.True(unchecked((int)ctx[CpuRegister.Rax]) > 0);
    }

    [Fact]
    public void Strcmp_PrefixIsLessThanLongerString()
    {
        var ctx = NewContext(out var memory);
        WriteCString(memory, A, "lib");
        WriteCString(memory, B, "libc");

        ctx[CpuRegister.Rdi] = A;
        ctx[CpuRegister.Rsi] = B;
        KernelMemoryCompatExports.Strcmp(ctx);
        Assert.True(unchecked((int)ctx[CpuRegister.Rax]) < 0); // 0 - 'c'
    }

    [Fact]
    public void Strncmp_StopsAtLimit()
    {
        var ctx = NewContext(out var memory);
        WriteCString(memory, A, "abcXXX");
        WriteCString(memory, B, "abcYYY");

        // First 3 bytes equal -> 0 within the limit.
        ctx[CpuRegister.Rdi] = A;
        ctx[CpuRegister.Rsi] = B;
        ctx[CpuRegister.Rdx] = 3;
        KernelMemoryCompatExports.Strncmp(ctx);
        Assert.Equal(0, unchecked((int)ctx[CpuRegister.Rax]));

        // Extending the limit exposes the difference.
        ctx[CpuRegister.Rdx] = 4;
        KernelMemoryCompatExports.Strncmp(ctx);
        Assert.True(unchecked((int)ctx[CpuRegister.Rax]) < 0); // 'X' < 'Y'
    }

    [Fact]
    public void Strcmp_NullPointer_IsMemoryFault()
    {
        var ctx = NewContext(out var memory);
        WriteCString(memory, A, "x");

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = A;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.Strcmp(ctx));
    }

}
