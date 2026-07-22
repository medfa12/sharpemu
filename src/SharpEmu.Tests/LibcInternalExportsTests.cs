// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs;
using Xunit;

namespace SharpEmu.Tests;

public sealed class LibcInternalExportsTests : IDisposable
{
    private const ulong DataAddress = 0x4000;
    private const ulong OutputAddress = 0x5000;

    public LibcInternalExportsTests()
    {
        LibcMspaceExports.ResetInternalForTests();
    }

    public void Dispose()
    {
        LibcMspaceExports.ResetInternalForTests();
    }

    [Fact]
    public void AssignedNids_AreAllExported()
    {
        string[] expected =
        [
            "xGT4Mc55ViQ", "jVDuvE3s5Bs", "sQL8D-jio7U", "dREVnZkAKRE", "9s3P+LCvWP8",
            "A+Y3xfrWLLo", "vZkmJmvqueY", "LaPaA6mYA38", "z7STeF6abuU", "pE4Ot3CffW0",
            "cMwgSSmpE5o", "Ss3108pBuZY", "0x7rx8TKy2Y", "JBcgYuW8lPU", "QI-x0SL8jhw",
            "7Ly52zaL44Q", "GZWjF-YIFFk", "OXmauLdQ8kY", "HUbZmOnT-Dg", "EH-x713A99c",
            "weDug8QD-lE", "2WE3BTYVwKM", "-P6FNMzk2Kc", "NVadfnzQhHQ", "dnaeGXbjP6E",
            "wuAQt-j+p4o", "8zsu04XNsZ4", "rtV7-jWC6Yg", "WuMbPBKN1TU", "lhpd6Wk6ccs",
            "RQXLbdT2lc4", "NFLs+dRJGNg", "9LCjpWyQ5Zc", "1D0H2KNjshE", "H8ya2H00jbI",
            "jMB7EFyu30Y", "pztV4AF18iI", "Q4rRL34CEeE", "K+gcnFFJKVc", "5Xa2ACNECdo",
            "YNzNkJzYqEg", "T7uyNqP7vQA", "ZE6RNL+eLbk",
        ];

        var actual = typeof(LibcMspaceExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Nid)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(43, expected.Length);
        Assert.All(expected, nid => Assert.Contains(nid, actual));
    }

    [Fact]
    public void MathExports_UseXmmArgumentsAndWriteSincosOutputs()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5)), 0);
        ctx[CpuRegister.Rdi] = DataAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, LibcMspaceExports.InternalSincos(ctx));
        Assert.True(ctx.TryReadUInt64(DataAddress, out var sinBits));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var cosBits));
        Assert.Equal(Math.Sin(0.5), BitConverter.Int64BitsToDouble(unchecked((long)sinBits)));
        Assert.Equal(Math.Cos(0.5), BitConverter.Int64BitsToDouble(unchecked((long)cosBits)));

        ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(3.0)), 0);
        ctx.SetXmmRegister(1, unchecked((ulong)BitConverter.DoubleToInt64Bits(4.0)), 0);
        Assert.Equal(0, LibcMspaceExports.InternalPow(ctx));
        ctx.GetXmmRegister(0, out var resultBits, out _);
        Assert.Equal(81.0, BitConverter.Int64BitsToDouble(unchecked((long)resultBits)));
    }

    [Fact]
    public void SecureMemoryAndStrings_CopyAndClearOnRangeErrors()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(DataAddress, [1, 2, 3, 4, 5]);
        memory.TryWrite(OutputAddress, [0xAA, 0xAA, 0xAA, 0xAA]);
        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = 4;
        ctx[CpuRegister.Rdx] = DataAddress;
        ctx[CpuRegister.Rcx] = 4;

        Assert.Equal(0, LibcMspaceExports.InternalMemcpyS(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Span<byte> copied = stackalloc byte[4];
        Assert.True(memory.TryRead(OutputAddress, copied));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, copied.ToArray());

        ctx[CpuRegister.Rcx] = 5;
        Assert.Equal(0, LibcMspaceExports.InternalMemcpyS(ctx));
        Assert.Equal(34UL, ctx[CpuRegister.Rax]);
        Assert.True(memory.TryRead(OutputAddress, copied));
        Assert.Equal(new byte[4], copied.ToArray());

        memory.TryWrite(DataAddress, "libc\0"u8);
        ctx[CpuRegister.Rsi] = 8;
        Assert.Equal(0, LibcMspaceExports.InternalStrcpyS(ctx));
        Span<byte> text = stackalloc byte[5];
        Assert.True(memory.TryRead(OutputAddress, text));
        Assert.Equal("libc\0"u8.ToArray(), text.ToArray());
    }

    [Fact]
    public void InternalMutex_IsRecursiveAndDestroyClearsMarker()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = DataAddress;

        Assert.Equal(0, LibcMspaceExports.InternalMtxinit(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, LibcMspaceExports.InternalMtxlock(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, LibcMspaceExports.InternalMtxlock(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, LibcMspaceExports.InternalMtxunlock(ctx));
        Assert.Equal(0, LibcMspaceExports.InternalMtxunlock(ctx));
        Assert.Equal(0, LibcMspaceExports.InternalMtxdst(ctx));
        Assert.True(ctx.TryReadUInt64(DataAddress, out var marker));
        Assert.Equal(0UL, marker);
    }

    [Fact]
    public void FofindAndFoprep_PackGuestFileObject()
    {
        var memory = new AllocatingMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.Equal(0, LibcMspaceExports.InternalFofind(ctx));
        var fileAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, fileAddress);
        Assert.True(ctx.TryReadUInt16(fileAddress, out var initialMode));
        Assert.True(ctx.TryReadByte(fileAddress + 2, out var index));
        Assert.Equal((ushort)0x80, initialMode);
        Assert.Equal((byte)5, index);

        memory.TryWrite(DataAddress, "rb\0"u8);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = DataAddress;
        ctx[CpuRegister.Rdx] = fileAddress;
        ctx[CpuRegister.Rcx] = 17;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = 0;
        Assert.Equal(0, LibcMspaceExports.InternalFoprep(ctx));
        Assert.Equal(fileAddress, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadInt32(fileAddress + 4, out var fd));
        Assert.True(ctx.TryReadUInt64(fileAddress + 8, out var buffer));
        Assert.Equal(17, fd);
        Assert.Equal(fileAddress + 126, buffer);

        ctx[CpuRegister.Rdi] = fileAddress;
        Assert.Equal(0, LibcMspaceExports.InternalFofree(ctx));
        Assert.True(ctx.TryReadUInt16(fileAddress, out var freedMode));
        Assert.True(ctx.TryReadInt32(fileAddress + 4, out var freedFd));
        Assert.Equal((ushort)0, freedMode);
        Assert.Equal(-1, freedFd);
    }

    private sealed class AllocatingMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly SparseGuestMemory _memory = new();
        private ulong _nextAddress = 0x10_0000;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = (_nextAddress + alignment - 1) & ~(alignment - 1);
            _nextAddress = address + size;
            return _memory.TryWrite(address, new byte[checked((int)size)]);
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _memory.TryWrite(virtualAddress, source);
    }
}
