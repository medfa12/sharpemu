// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceKernelCreate/Set/Clear/PollEventFlag semantics driven through the guest
/// ABI (arguments in RDI/RSI/RDX/RCX/R8, result in RAX), exactly as the HLE
/// dispatcher invokes them. sceKernelWaitEventFlag is excluded — it blocks on
/// the guest scheduler.
/// </summary>
public sealed class KernelEventFlagTests
{
    private const ulong OutHandleAddress = 0x1000;
    private const ulong NameAddress = 0x2000;
    private const ulong ResultAddress = 0x3000;

    private const uint WaitAnd = 0x01;
    private const uint WaitOr = 0x02;
    private const uint ClearAll = 0x10;
    private const uint ClearPattern = 0x20;
    private const uint AttrMulti = 0x20;

    private static CpuContext NewContext(string name = "evf")
    {
        var memory = new SparseGuestMemory();
        var bytes = Encoding.UTF8.GetBytes(name);
        memory.TryWrite(NameAddress, bytes);
        memory.TryWrite(NameAddress + (ulong)bytes.Length, new byte[] { 0 });
        memory.WriteUInt64(OutHandleAddress, 0);
        memory.WriteUInt64(ResultAddress, 0);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static OrbisGen2Result Result(CpuContext ctx) =>
        (OrbisGen2Result)unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static ulong CreateFlag(CpuContext ctx, ulong initialBits, uint attributes = AttrMulti)
    {
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = attributes;
        ctx[CpuRegister.Rcx] = initialBits;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, (OrbisGen2Result)KernelEventFlagCompatExports.KernelCreateEventFlag(ctx));

        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static OrbisGen2Result Poll(CpuContext ctx, ulong handle, ulong pattern, uint waitMode)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = pattern;
        ctx[CpuRegister.Rdx] = waitMode;
        ctx[CpuRegister.Rcx] = ResultAddress;
        return (OrbisGen2Result)KernelEventFlagCompatExports.KernelPollEventFlag(ctx);
    }

    private static OrbisGen2Result Set(CpuContext ctx, ulong handle, ulong pattern)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = pattern;
        return (OrbisGen2Result)KernelEventFlagCompatExports.KernelSetEventFlag(ctx);
    }

    private static OrbisGen2Result Clear(CpuContext ctx, ulong handle, ulong mask)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = mask;
        return (OrbisGen2Result)KernelEventFlagCompatExports.KernelClearEventFlag(ctx);
    }

    private static OrbisGen2Result Delete(CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        return (OrbisGen2Result)KernelEventFlagCompatExports.KernelDeleteEventFlag(ctx);
    }

    [Fact]
    public void Create_WritesHandleAndSetsRax()
    {
        var ctx = NewContext();

        var handle = CreateFlag(ctx, initialBits: 0xF0);

        Assert.NotEqual(0UL, handle);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Result(ctx));
    }

    [Fact]
    public void Create_NullOutPointer_IsInvalidArgument()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = AttrMulti;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;

        var result = (OrbisGen2Result)KernelEventFlagCompatExports.KernelCreateEventFlag(ctx);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Create_InvalidAttributeBits_IsInvalidArgument()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = 0x40; // outside the 0x33 mask
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;

        var result = (OrbisGen2Result)KernelEventFlagCompatExports.KernelCreateEventFlag(ctx);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Create_NonZeroOptionPointer_IsInvalidArgument()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = AttrMulti;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0xDEAD; // options are not supported

        var result = (OrbisGen2Result)KernelEventFlagCompatExports.KernelCreateEventFlag(ctx);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Poll_AndMode_RequiresAllBits()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b0101);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 0b0111, WaitAnd));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0101, WaitAnd));
    }

    [Fact]
    public void Poll_OrMode_RequiresAnyBit()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b0100);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0110, WaitOr));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 0b1010, WaitOr));
    }

    [Fact]
    public void Poll_WritesCurrentBitsToResultPointer()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0xABCD);

        Poll(ctx, handle, 0xABCD, WaitAnd);

        Assert.True(ctx.TryReadUInt64(ResultAddress, out var observed));
        Assert.Equal(0xABCDUL, observed); // pre-clear snapshot
    }

    [Fact]
    public void Poll_ClearAll_ZeroesEveryBitOnSuccess()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b1111);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0001, WaitOr | ClearAll));

        // All bits gone, so a follow-up poll for any bit is busy.
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 0b1111, WaitOr));
    }

    [Fact]
    public void Poll_ClearPattern_ClearsOnlyMatchedBits()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b1111);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0011, WaitAnd | ClearPattern));

        Poll(ctx, handle, 0b1100, WaitAnd);
        Assert.True(ctx.TryReadUInt64(ResultAddress, out var observed));
        Assert.Equal(0b1100UL, observed); // low two bits cleared, high two retained
    }

    [Fact]
    public void Poll_FailedCondition_DoesNotClearBits()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b0001);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, handle, 0b0011, WaitAnd | ClearAll));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0001, WaitAnd));
    }

    [Fact]
    public void Poll_ZeroPatternOrBadWaitMode_IsInvalidArgument()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0xFF);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Poll(ctx, handle, 0, WaitAnd));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Poll(ctx, handle, 0xFF, 0x04));       // no And/Or
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Poll(ctx, handle, 0xFF, WaitAnd | WaitOr));
    }

    [Fact]
    public void Set_OrsBitsIntoPattern()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b0001);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Set(ctx, handle, 0b0110));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Poll(ctx, handle, 0b0111, WaitAnd));
    }

    [Fact]
    public void Clear_AndsBitsWithMask()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0b1111);

        // sceKernelClearEventFlag ANDs with the supplied mask (keeps masked bits).
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Clear(ctx, handle, 0b0011));

        Poll(ctx, handle, 0b0011, WaitAnd);
        Assert.True(ctx.TryReadUInt64(ResultAddress, out var observed));
        Assert.Equal(0b0011UL, observed);
    }

    [Fact]
    public void Delete_RemovesHandle()
    {
        var ctx = NewContext();
        var handle = CreateFlag(ctx, initialBits: 0);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Delete(ctx, handle));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Delete(ctx, handle));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Poll(ctx, handle, 1, WaitOr));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Set(ctx, handle, 1));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Clear(ctx, handle, 1));
    }

    [Fact]
    public void Handles_AreUniquePerFlag()
    {
        var ctx = NewContext();

        var first = CreateFlag(ctx, initialBits: 0);
        var second = CreateFlag(ctx, initialBits: 0);

        Assert.NotEqual(first, second);
    }
}
