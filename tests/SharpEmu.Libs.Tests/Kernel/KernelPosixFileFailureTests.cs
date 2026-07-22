// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelPosixFileFailureTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong BufferAddress = MemoryBase + 0x400;
    private const ulong FsBase = MemoryBase + 0x1000;
    private const ulong ErrnoAddress = FsBase + 0x40;

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOneAndSetsEnoent()
    {
        var context = NewContext(out var memory);
        memory.WriteCString(PathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal(-1, KernelExports.Open(context));
        AssertPosixFailure(context, expectedErrno: 2);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOneAndSetsEbadf()
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0x80020002;
        context[CpuRegister.Rsi] = BufferAddress;

        Assert.Equal(-1, KernelExports.Fstat(context));
        AssertPosixFailure(context, expectedErrno: 9);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOneAndSetsEbadf()
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0x80020002;

        Assert.Equal(-1, KernelMemoryCompatExports.PosixClose(context));
        AssertPosixFailure(context, expectedErrno: 9);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOneAndSetsEbadf()
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0x80020002;
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = 64;

        Assert.Equal(-1, KernelMemoryCompatExports.PosixRead(context));
        AssertPosixFailure(context, expectedErrno: 9);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOneAndSetsEbadf()
    {
        var context = NewContext(out var memory);
        memory.WriteCString(BufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002;
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = 7;

        Assert.Equal(-1, KernelMemoryCompatExports.PosixWrite(context));
        AssertPosixFailure(context, expectedErrno: 9);
    }

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5)
        {
            FsBase = FsBase,
        };
    }

    private static void AssertPosixFailure(CpuContext context, int expectedErrno)
    {
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(ErrnoAddress, out var errno));
        Assert.Equal((uint)expectedErrno, errno);
    }
}
