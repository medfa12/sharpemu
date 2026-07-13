// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.ContentExport;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceContentExportInit/Init2/Term semantics driven through the guest ABI:
/// parameter block validation (callbacks required, version-2 reserved fields
/// and buffer size limits), double-init rejection, and term-before-init
/// rejection.
/// </summary>
public sealed class ContentExportTests : IDisposable
{
    private const ulong ParamAddress = 0x1000;
    private const ulong MallocFunc = 0x40_0000;
    private const ulong FreeFunc = 0x40_1000;

    private const int ErrorNoInit = unchecked((int)0x809D3004);
    private const int ErrorMultipleInit = unchecked((int)0x809D3005);
    private const int ErrorInvalidParam = unchecked((int)0x809D3016);

    public ContentExportTests()
    {
        ContentExportExports.ResetForTests();
    }

    public void Dispose()
    {
        ContentExportExports.ResetForTests();
    }

    private static CpuContext NewContext(
        ulong mallocFunc = MallocFunc,
        ulong freeFunc = FreeFunc,
        ulong bufferSize = 0,
        ulong reserved0 = 0,
        ulong reserved1 = 0)
    {
        var memory = new SparseGuestMemory();
        memory.WriteUInt64(ParamAddress + 0x00, mallocFunc);
        memory.WriteUInt64(ParamAddress + 0x08, freeFunc);
        memory.WriteUInt64(ParamAddress + 0x10, 0); // user data
        memory.WriteUInt64(ParamAddress + 0x18, bufferSize);
        memory.WriteUInt64(ParamAddress + 0x20, reserved0);
        memory.WriteUInt64(ParamAddress + 0x28, reserved1);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = ParamAddress;
        return ctx;
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void Init_ValidParam_Succeeds()
    {
        var ctx = NewContext();
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void Init2_ValidParam_Succeeds()
    {
        var ctx = NewContext();
        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void Init_NullParamPointer_ReturnsInvalidParam()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0;
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void Init_UnmappedParamPointer_ReturnsInvalidParam()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0xDEAD_0000;
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(ErrorInvalidParam, Result(ctx));
    }

    [Theory]
    [InlineData(0UL, FreeFunc)]
    [InlineData(MallocFunc, 0UL)]
    public void Init_MissingCallback_ReturnsInvalidParam(ulong mallocFunc, ulong freeFunc)
    {
        var ctx = NewContext(mallocFunc, freeFunc);
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void Init_SecondCall_ReturnsMultipleInit()
    {
        var ctx = NewContext();
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(0, Result(ctx));

        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(ErrorMultipleInit, Result(ctx));
    }

    [Theory]
    [InlineData(1UL, 0UL)]
    [InlineData(0UL, 1UL)]
    public void Init2_NonZeroReservedField_ReturnsInvalidParam(ulong reserved0, ulong reserved1)
    {
        var ctx = NewContext(reserved0: reserved0, reserved1: reserved1);
        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(ErrorInvalidParam, Result(ctx));
    }

    [Fact]
    public void Init2_BufferSizeBelowMinimum_ReturnsInvalidParam()
    {
        var ctx = NewContext(bufferSize: 0xFF);
        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(ErrorInvalidParam, Result(ctx));
    }

    [Theory]
    [InlineData(0x100UL)]
    [InlineData(0x10_0000UL)]
    public void Init2_BufferSizeAtOrAboveMinimum_Succeeds(ulong bufferSize)
    {
        var ctx = NewContext(bufferSize: bufferSize);
        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void Init_IgnoresVersion2Fields()
    {
        // The original init entry point predates the reserved fields; garbage
        // there must not fail validation.
        var ctx = NewContext(bufferSize: 1, reserved0: 7, reserved1: 9);
        ContentExportExports.ContentExportInit(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void Term_BeforeInit_ReturnsNoInit()
    {
        var ctx = NewContext();
        ContentExportExports.ContentExportTerm(ctx);
        Assert.Equal(ErrorNoInit, Result(ctx));
    }

    [Fact]
    public void Term_AfterInit_SucceedsAndAllowsReinit()
    {
        var ctx = NewContext();
        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(0, Result(ctx));

        ContentExportExports.ContentExportTerm(ctx);
        Assert.Equal(0, Result(ctx));

        ContentExportExports.ContentExportInit2(ctx);
        Assert.Equal(0, Result(ctx));
    }
}
