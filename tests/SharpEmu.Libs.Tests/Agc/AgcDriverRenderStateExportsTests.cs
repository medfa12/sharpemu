// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDriverRenderStateExportsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;
    private const ulong CursorAddress = MemoryBase + 0x200;
    private const ulong CommandAddress = MemoryBase + 0x1000;
    private const ulong StackAddress = MemoryBase + 0x3000;
    private const ulong AllocationBase = MemoryBase + 0x10000;
    private const int AgcErrorQueue = unchecked((int)0x8A6D0000);
    private const int AgcErrorInvalidArgument = unchecked((int)0x8A6D0003);
    private const int VideoOutErrorInvalidIndex = unchecked((int)0x8029000A);

    [Fact]
    public void Exports_UseOracleNidsAndDriverLibrary()
    {
        var exports = typeof(AgcDriverRenderStateExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<SysAbiExportAttribute>()
            .ToDictionary(attribute => attribute.Nid);

        Assert.Equal(5, exports.Count);
        Assert.Equal("sceAgcDriverNotifyDefaultStates", exports["nR6xhiFsOoc"].ExportName);
        Assert.Equal("sceAgcDriverPatchClearState", exports["lYz7vbL4W4A"].ExportName);
        Assert.Equal("sceAgcDriverCreateQueue", exports["zP4ZNlXLBVg"].ExportName);
        Assert.Equal("sceAgcDriverSuspendPointSubmit", exports["QcmHLO2n7mk"].ExportName);
        Assert.Equal("sceAgcDriverSetFlip", exports["cwbxjPSJ7WQ"].ExportName);
        Assert.All(exports.Values, export => Assert.Equal("libSceAgcDriver", export.LibraryName));
    }

    [Fact]
    public void NotifyDefaultStates_AcceptsThreeRegisterLists()
    {
        var (ctx, memory) = NewContext();
        WriteRegisterValue(ctx, MemoryBase + 0x400, 0x8E, 0xF);
        WriteRegisterValue(ctx, MemoryBase + 0x500, 0x2FF, 0);
        WriteRegisterValue(ctx, MemoryBase + 0x600, 0x382, 0x40000040);
        ctx[CpuRegister.Rdi] = MemoryBase + 0x400;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x500;
        ctx[CpuRegister.Rdx] = MemoryBase + 0x600;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = 1;
        ctx[CpuRegister.R9] = 1;

        Assert.Equal(0, AgcDriverRenderStateExports.DriverNotifyDefaultStates(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(memory.TryRead(MemoryBase + 0x400, stackalloc byte[8]));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x400u, 0)]
    [InlineData(0x401u, AgcErrorInvalidArgument)]
    public void PatchClearState_PinsCountBoundary(uint count, int expected)
    {
        var (ctx, _) = NewContext();
        ctx[CpuRegister.Rdi] = MemoryBase + 0x400;
        ctx[CpuRegister.Rsi] = count;

        Assert.Equal(expected, AgcDriverRenderStateExports.DriverPatchClearState(ctx));
        Assert.Equal(unchecked((ulong)expected), ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, 0u)]
    [InlineData(OutputAddress, 0x60u)]
    public void CreateQueue_RejectsNullOutputAndQueueClassThree(ulong output, uint selector)
    {
        var (ctx, _) = NewContext();
        ctx[CpuRegister.Rdi] = selector;
        ctx[CpuRegister.Rsi] = output;

        Assert.Equal(AgcErrorQueue, AgcDriverRenderStateExports.DriverCreateQueue(ctx));
        Assert.Equal(unchecked((ulong)AgcErrorQueue), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void CreateQueue_WritesDefaultGraphicsDescriptorFields()
    {
        var (ctx, _) = NewContext();
        ctx[CpuRegister.Rdi] = 4;
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(0, AgcDriverRenderStateExports.DriverCreateQueue(ctx));
        var descriptor = ReadQword(ctx, OutputAddress);
        Assert.NotEqual(0UL, descriptor);
        Assert.Equal(0x30u, ReadDword(ctx, descriptor));
        Assert.Equal(4u, ReadDword(ctx, descriptor + 4));
        Assert.Equal(0x20000u, ReadDword(ctx, descriptor + 8));
        Assert.Equal(0u, ReadDword(ctx, descriptor + 0x10));
    }

    [Fact]
    public void CreateQueue_WritesAsyncDescriptorAtRecordPlusEight()
    {
        var (ctx, _) = NewContext();
        const uint selector = 0x20;
        ctx[CpuRegister.Rdi] = selector;
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(0, AgcDriverRenderStateExports.DriverCreateQueue(ctx));
        var descriptor = ReadQword(ctx, OutputAddress);
        Assert.Equal(selector, ReadDword(ctx, descriptor - 8));
        Assert.Equal(0x30u, ReadDword(ctx, descriptor));
        Assert.Equal(selector, ReadDword(ctx, descriptor + 4));
        Assert.Equal(descriptor - 8, ReadQword(ctx, descriptor + 0x38));
        Assert.Equal(1u, ReadDword(ctx, descriptor + 0x68));
        Assert.Equal(0u, ReadDword(ctx, descriptor + 0x78));
    }

    [Fact]
    public void SuspendPointSubmit_ConsumesSubmitDescriptor()
    {
        var (ctx, _) = NewContext();
        Assert.True(ctx.TryWriteUInt64(OutputAddress, CommandAddress));
        Assert.True(ctx.TryWriteUInt32(OutputAddress + 8, 0));
        ctx[CpuRegister.Rdi] = OutputAddress;

        Assert.Equal(0, AgcDriverRenderStateExports.DriverSuspendPointSubmit(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetFlip_EmitsConsumablePacketAndAdvancesCursorByRequestedSize()
    {
        var (ctx, _) = NewContext();
        const ulong flipArg = 0x1122334455667788;
        Assert.True(ctx.TryWriteUInt64(CursorAddress, CommandAddress));
        Assert.True(ctx.TryWriteUInt64(StackAddress + sizeof(ulong), flipArg));
        ctx[CpuRegister.Rdi] = CursorAddress;
        ctx[CpuRegister.Rsi] = 0x40;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 7;
        ctx[CpuRegister.R8] = 3;
        ctx[CpuRegister.R9] = 1;
        ctx[CpuRegister.Rsp] = StackAddress;

        Assert.Equal(0, AgcDriverRenderStateExports.DriverSetFlip(ctx));
        Assert.Equal(CommandAddress + 0x100, ReadQword(ctx, CursorAddress));
        Assert.Equal(0xC004105Cu, ReadDword(ctx, CommandAddress));
        Assert.Equal(7u, ReadDword(ctx, CommandAddress + 4));
        Assert.Equal(3u, ReadDword(ctx, CommandAddress + 8));
        Assert.Equal(1u, ReadDword(ctx, CommandAddress + 12));
        Assert.Equal(0x55667788u, ReadDword(ctx, CommandAddress + 16));
        Assert.Equal(0x11223344u, ReadDword(ctx, CommandAddress + 20));
        Assert.Equal(0xC0381000u, ReadDword(ctx, CommandAddress + 24));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(16)]
    public void SetFlip_RejectsDisplayIndexOutsideFirmwareRange(int displayBufferIndex)
    {
        var (ctx, _) = NewContext();
        Assert.True(ctx.TryWriteUInt64(CursorAddress, CommandAddress));
        ctx[CpuRegister.Rdi] = CursorAddress;
        ctx[CpuRegister.Rsi] = 0x40;
        ctx[CpuRegister.R8] = unchecked((uint)displayBufferIndex);

        Assert.Equal(VideoOutErrorInvalidIndex, AgcDriverRenderStateExports.DriverSetFlip(ctx));
        Assert.Equal(CommandAddress, ReadQword(ctx, CursorAddress));
    }

    [Fact]
    public void RegisterResource_ReturnsFirmwareNoPaDebugWithoutTouchingOutput()
    {
        var (ctx, _) = NewContext();
        const uint sentinel = 0xA5A55A5A;
        Assert.True(ctx.TryWriteUInt32(OutputAddress, sentinel));
        ctx[CpuRegister.Rdi] = OutputAddress;

        // Retail firmware body is `mov eax, 0x8A6C9018; ret`.
        Assert.Equal(unchecked((int)0x8A6C9018), AgcExports.DriverRegisterResource(ctx));
        Assert.Equal(sentinel, ReadDword(ctx, OutputAddress));
    }

    private static (CpuContext Context, FakeGuestMemory Memory) NewContext()
    {
        var memory = new FakeGuestMemory(MemoryBase, 0x20000, AllocationBase);
        return (new CpuContext(memory, Generation.Gen5), memory);
    }

    private static void WriteRegisterValue(CpuContext ctx, ulong address, uint offset, uint value)
    {
        Assert.True(ctx.TryWriteUInt32(address, offset));
        Assert.True(ctx.TryWriteUInt32(address + sizeof(uint), value));
    }

    private static uint ReadDword(CpuContext ctx, ulong address)
    {
        Assert.True(ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private static ulong ReadQword(CpuContext ctx, ulong address)
    {
        Assert.True(ctx.TryReadUInt64(address, out var value));
        return value;
    }
}
