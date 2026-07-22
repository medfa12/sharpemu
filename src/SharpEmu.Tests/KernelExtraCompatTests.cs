// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

public sealed class KernelExtraCompatTests
{
    private static CpuContext NewContext() => new(new SparseGuestMemory(), Generation.Gen5);

    [Fact]
    public void AssignedSurface_ContainsExpectedLibraryCounts()
    {
        var exports = typeof(KernelExtraCompatExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<SysAbiExportAttribute>()
            .ToArray();

        Assert.Equal(226, exports.Length);
        Assert.Equal(158, exports.Count(export => export.LibraryName == "libkernel"));
        Assert.Equal(68, exports.Count(export => export.LibraryName == "libScePosix"));
        Assert.Equal(exports.Length, exports.Select(export => export.Nid).Distinct().Count());
        Assert.All(exports, export => Assert.Equal(Generation.Gen5, export.Target));
    }

    [Fact]
    public void PthreadSemaphore_TracksCountAndWritesOpaqueHandle()
    {
        const ulong semAddress = 0x1000;
        const ulong valueAddress = 0x2000;
        var ctx = NewContext();
        Assert.True(ctx.Memory.TryWrite(semAddress, new byte[16]));
        Assert.True(ctx.Memory.TryWrite(valueAddress, new byte[8]));

        ctx[CpuRegister.Rdi] = semAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, KernelExtraCompatExports.PthreadSemInit(ctx));
        Assert.True(ctx.TryReadUInt64(semAddress, out var handle));
        Assert.NotEqual(0UL, handle);

        ctx[CpuRegister.Rsi] = valueAddress;
        Assert.Equal(0, KernelExtraCompatExports.PthreadSemGetvalue(ctx));
        Assert.True(ctx.TryReadInt32(valueAddress, out var value));
        Assert.Equal(1, value);

        Assert.Equal(0, KernelExtraCompatExports.PthreadSemTrywait(ctx));
        Assert.Equal(-1, KernelExtraCompatExports.PthreadSemTrywait(ctx));
        Assert.Equal(0, KernelExtraCompatExports.PthreadSemPost(ctx));
        Assert.Equal(0, KernelExtraCompatExports.PthreadSemTrywait(ctx));
    }

    [Fact]
    public void AioSubmit_WritesIdAndPackedResult()
    {
        const ulong requestAddress = 0x3000;
        const ulong resultAddress = 0x4000;
        const ulong idAddress = 0x5000;
        const ulong stateAddress = 0x6000;
        var ctx = NewContext();
        Assert.True(ctx.Memory.TryWrite(requestAddress, new byte[64]));
        Assert.True(ctx.Memory.TryWrite(resultAddress, new byte[16]));
        Assert.True(ctx.Memory.TryWrite(idAddress, new byte[8]));
        Assert.True(ctx.Memory.TryWrite(stateAddress, new byte[8]));
        Assert.True(ctx.TryWriteUInt64(requestAddress + 24, resultAddress));

        ctx[CpuRegister.Rdi] = requestAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = idAddress;
        Assert.Equal(0, KernelExtraCompatExports.AioSubmitRead(ctx));
        Assert.True(ctx.TryReadInt32(idAddress, out var id));
        Assert.True(id > 0);
        Assert.True(ctx.TryReadUInt64(resultAddress, out var returnValue));
        Assert.Equal(0UL, returnValue);
        Assert.True(ctx.TryReadUInt32(resultAddress + 8, out var requestState));
        Assert.Equal(3u, requestState);

        ctx[CpuRegister.Rdi] = unchecked((uint)id);
        ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, KernelExtraCompatExports.AioPollRequest(ctx));
        Assert.True(ctx.TryReadInt32(stateAddress, out var state));
        Assert.Equal(3, state);
    }

    [Fact]
    public void SystemSoftwareVersion_UsesFirmwareEightPacking()
    {
        const ulong versionAddress = 0x7000;
        var ctx = NewContext();
        Assert.True(ctx.Memory.TryWrite(versionAddress, new byte[32]));
        ctx[CpuRegister.Rdi] = versionAddress;

        Assert.Equal(0, KernelExtraCompatExports.KernelGetSystemSwVersion(ctx));
        Assert.True(ctx.TryReadUInt32(versionAddress + 28, out var packedVersion));
        Assert.Equal(0x08000000u, packedVersion);
    }
}
