// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceKernelMapDirectMemory2 register plumbing: the memory-type argument is
/// inserted third, which pushes prot/flags/start down one register each and
/// moves alignment onto the guest stack. The wrapper must present the same
/// call to the sceKernelMapDirectMemory core.
/// </summary>
public sealed class KernelMapDirectMemory2Tests
{
    private const ulong InOutAddress = 0x1000;
    private const ulong StackAddress = 0x8000;

    private static CpuContext NewContext()
    {
        var memory = new SparseGuestMemory();
        memory.TryWrite(InOutAddress, new byte[0x100]);
        memory.TryWrite(StackAddress - 0x40, new byte[0x100]);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsp] = StackAddress;
        return ctx;
    }

    [Fact]
    public void MapDirectMemory2_MapsAndWritesOutAddress()
    {
        var ctx = NewContext();
        Assert.True(ctx.TryWriteUInt64(InOutAddress, 0x2000_0000)); // requested address
        ctx[CpuRegister.Rdi] = InOutAddress;
        ctx[CpuRegister.Rsi] = 0x10000;       // length
        ctx[CpuRegister.Rdx] = 3;             // memory type (consumed)
        ctx[CpuRegister.Rcx] = 0x2;           // prot
        ctx[CpuRegister.R8] = 0;              // flags
        ctx[CpuRegister.R9] = 0x4000_0000;    // direct memory start
        Assert.True(ctx.TryWriteUInt64(StackAddress + 8, 0x10000)); // alignment

        var result = KernelMemoryCompatExports.KernelMapDirectMemory2(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(ctx.TryReadUInt64(InOutAddress, out var mapped));
        Assert.Equal(0x2000_0000UL, mapped);
    }

    [Fact]
    public void MapDirectMemory2_NullInOutPointer_Fails()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x10000;

        var result = KernelMemoryCompatExports.KernelMapDirectMemory2(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }
}
