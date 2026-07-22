// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Tests;

public sealed class SystemServiceCompatExportTests
{
    private const ulong OutputAddress = 0x4000;

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void SystemService_StateAndStringQueriesAreCoherent()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 37;
        SystemServiceExports.SystemServiceSetGpuLoadEmulationMode(ctx);
        Assert.Equal(0, Result(ctx));

        SystemServiceExports.SystemServiceGetGpuLoadEmulationMode(ctx);
        Assert.Equal(37, Result(ctx));

        ctx[CpuRegister.Rdi] = 6;
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = 32;
        SystemServiceExports.SystemServiceParamGetString(ctx);
        Assert.Equal(0, Result(ctx));

        Span<byte> systemName = stackalloc byte[9];
        Assert.True(memory.TryRead(OutputAddress, systemName));
        Assert.Equal("SharpEmu\0"u8.ToArray(), systemName.ToArray());
    }

    [Fact]
    public void AppContent_EmptyDlcOutputsAreInitialized()
    {
        const int ErrorNoEntitlement = unchecked((int)0x80D90007);
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(OutputAddress, Enumerable.Repeat((byte)0xA5, 32).ToArray()));

        ctx[CpuRegister.Rsi] = 0x5000;
        ctx[CpuRegister.Rdx] = OutputAddress;
        AppContentExports.AppContentAddcontMount(ctx);
        Assert.Equal(ErrorNoEntitlement, Result(ctx));

        Span<byte> mountPoint = stackalloc byte[16];
        Assert.True(memory.TryRead(OutputAddress, mountPoint));
        Assert.True(mountPoint.SequenceEqual(new byte[16]));

        ctx[CpuRegister.Rsi] = OutputAddress;
        AppContentExports.AppContentDownloadDataGetAvailableSpaceKb(ctx);
        Assert.Equal(0, Result(ctx));
        Span<byte> availableSpace = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(OutputAddress, availableSpace));
        Assert.Equal(1024UL * 1024UL, BinaryPrimitives.ReadUInt64LittleEndian(availableSpace));
    }

    [Fact]
    public void Sysmodule_InternalLifecycleProvidesStableHandle()
    {
        const int ModuleId = 0x71234567;
        const int ErrorNotLoaded = unchecked((int)0x805A1001);
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = ModuleId;

        KernelRuntimeCompatExports.SysmoduleLoadModuleInternal(ctx);
        Assert.Equal(0, Result(ctx));

        KernelRuntimeCompatExports.SysmoduleIsLoadedInternal(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rsi] = OutputAddress;
        KernelRuntimeCompatExports.SysmoduleGetModuleHandleInternal(ctx);
        Assert.Equal(0, Result(ctx));
        Span<byte> handle = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(OutputAddress, handle));
        Assert.NotEqual(0U, BinaryPrimitives.ReadUInt32LittleEndian(handle));

        KernelRuntimeCompatExports.SysmoduleUnloadModuleInternal(ctx);
        Assert.Equal(0, Result(ctx));

        KernelRuntimeCompatExports.SysmoduleIsLoadedInternal(ctx);
        Assert.Equal(ErrorNotLoaded, Result(ctx));
    }
}
