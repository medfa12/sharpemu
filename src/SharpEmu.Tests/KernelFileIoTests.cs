// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceKernelOpen/Pread/Fsync/Close over a registered guest mount: pread reads
/// at an explicit offset without moving the descriptor position, and both
/// calls reject unknown descriptors.
/// </summary>
public sealed class KernelFileIoTests : IDisposable
{
    private const ulong PathAddress = 0x1000;
    private const ulong BufferAddress = 0x2000;

    private readonly string _hostRoot;

    public KernelFileIoTests()
    {
        _hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-pread-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_hostRoot);
        File.WriteAllBytes(Path.Combine(_hostRoot, "data.bin"), Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        KernelMemoryCompatExports.RegisterGuestPathMount("/preadtest", _hostRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hostRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CpuContext NewContext()
    {
        var memory = new SparseGuestMemory();
        // The guest-string reader scans in 256-byte chunks; keep the whole
        // chunk mapped so the read never leaves guest memory.
        memory.TryWrite(PathAddress, new byte[0x1000]);
        var path = Encoding.UTF8.GetBytes("/preadtest/data.bin\0");
        memory.TryWrite(PathAddress, path);
        memory.TryWrite(BufferAddress, new byte[64]);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int OpenDataFile(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = PathAddress;
        ctx[CpuRegister.Rsi] = 0; // O_RDONLY
        ctx[CpuRegister.Rdx] = 0;
        KernelMemoryCompatExports.KernelOpenUnderscore(ctx);
        var fd = unchecked((int)ctx[CpuRegister.Rax]);
        Assert.True(fd > 0);
        return fd;
    }

    private static void CloseFd(CpuContext ctx, int fd)
    {
        ctx[CpuRegister.Rdi] = (ulong)fd;
        KernelMemoryCompatExports.KernelCloseUnderscore(ctx);
    }

    [Fact]
    public void Pread_ReadsAtOffsetWithoutMovingPosition()
    {
        var ctx = NewContext();
        var fd = OpenDataFile(ctx);
        try
        {
            ctx[CpuRegister.Rdi] = (ulong)fd;
            ctx[CpuRegister.Rsi] = BufferAddress;
            ctx[CpuRegister.Rdx] = 6;
            ctx[CpuRegister.Rcx] = 10; // offset of 'A'
            KernelMemoryCompatExports.KernelPread(ctx);
            Assert.Equal(6UL, ctx[CpuRegister.Rax]);

            Span<byte> read = stackalloc byte[6];
            Assert.True(ctx.Memory.TryRead(BufferAddress, read));
            Assert.Equal("ABCDEF", Encoding.ASCII.GetString(read));

            // A sequential read afterwards still starts at position 0.
            ctx[CpuRegister.Rdi] = (ulong)fd;
            ctx[CpuRegister.Rsi] = BufferAddress;
            ctx[CpuRegister.Rdx] = 4;
            KernelMemoryCompatExports.KernelRead(ctx);
            Assert.Equal(4UL, ctx[CpuRegister.Rax]);

            Span<byte> sequential = stackalloc byte[4];
            Assert.True(ctx.Memory.TryRead(BufferAddress, sequential));
            Assert.Equal("0123", Encoding.ASCII.GetString(sequential));
        }
        finally
        {
            CloseFd(ctx, fd);
        }
    }

    [Fact]
    public void Pread_PastEndOfFile_ReturnsZero()
    {
        var ctx = NewContext();
        var fd = OpenDataFile(ctx);
        try
        {
            ctx[CpuRegister.Rdi] = (ulong)fd;
            ctx[CpuRegister.Rsi] = BufferAddress;
            ctx[CpuRegister.Rdx] = 8;
            ctx[CpuRegister.Rcx] = 0x100;
            KernelMemoryCompatExports.KernelPread(ctx);
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseFd(ctx, fd);
        }
    }

    [Fact]
    public void Pread_UnknownFd_ReturnsNotFound()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0x7FFF;
        ctx[CpuRegister.Rsi] = BufferAddress;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = 0;
        var result = KernelMemoryCompatExports.KernelPread(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void Fsync_OpenFdSucceeds_UnknownFdFails()
    {
        var ctx = NewContext();
        var fd = OpenDataFile(ctx);
        try
        {
            ctx[CpuRegister.Rdi] = (ulong)fd;
            var result = KernelMemoryCompatExports.KernelFsync(ctx);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        }
        finally
        {
            CloseFd(ctx, fd);
        }

        ctx[CpuRegister.Rdi] = 0x7FFF;
        var missing = KernelMemoryCompatExports.KernelFsync(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, missing);
    }
}
