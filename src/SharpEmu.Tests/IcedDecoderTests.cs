// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

public sealed class IcedDecoderTests
{
    private const ulong Rip = 0x0000_0008_0000_0000UL;

    [Fact]
    public void TryDecode_SimpleInstruction_ReportsLengthAndMnemonic()
    {
        byte[] xorRaxRax = [0x48, 0x31, 0xC0];

        Assert.True(IcedDecoder.TryDecode(Rip, xorRaxRax, out var inst));
        Assert.Equal(Rip, inst.Rip);
        Assert.Equal(3, inst.Length);
        Assert.Equal("Xor", inst.Mnemonic);
        Assert.Equal(xorRaxRax, inst.Bytes);
        Assert.Null(inst.NearBranchTarget);
        Assert.Null(inst.MemoryAddress);
    }

    [Fact]
    public void TryDecode_EmptyInput_Fails()
    {
        Assert.False(IcedDecoder.TryDecode(Rip, ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void TryDecode_RelativeCall_ResolvesAbsoluteTarget()
    {
        // call rel32 +0x10: E8 10 00 00 00 → target = rip + 5 + 0x10
        byte[] call = [0xE8, 0x10, 0x00, 0x00, 0x00];

        Assert.True(IcedDecoder.TryDecode(Rip, call, out var inst));
        Assert.Equal(FlowControl.Call, inst.FlowControl);
        Assert.Equal(Rip + 5 + 0x10, inst.NearBranchTarget);
    }

    [Fact]
    public void TryDecode_ConditionalJump_ResolvesBackwardTarget()
    {
        // jne rel8 -2: 75 FE → target = rip (tight spin loop)
        byte[] jne = [0x75, 0xFE];

        Assert.True(IcedDecoder.TryDecode(Rip, jne, out var inst));
        Assert.Equal(FlowControl.ConditionalBranch, inst.FlowControl);
        Assert.Equal(Rip, inst.NearBranchTarget);
    }

    [Fact]
    public void TryDecode_RipRelativeLoad_ComputesEffectiveAddress()
    {
        // mov rax, [rip+0x100]: 48 8B 05 00 01 00 00
        byte[] mov = [0x48, 0x8B, 0x05, 0x00, 0x01, 0x00, 0x00];

        Assert.True(IcedDecoder.TryDecode(Rip, mov, out var inst));
        Assert.Equal(Rip + 7 + 0x100, inst.MemoryAddress);
    }

    [Fact]
    public void TryDecode_TruncatedInstruction_Fails()
    {
        // mov rax, [rip+...] cut off after the ModRM byte
        byte[] truncated = [0x48, 0x8B, 0x05];

        Assert.False(IcedDecoder.TryDecode(Rip, truncated, out _));
    }

    [Fact]
    public void TryDecode_UsesOnlyFirstInstruction()
    {
        // push rbp; mov rbp, rsp — decoder must stop after push (1 byte)
        byte[] prologue = [0x55, 0x48, 0x89, 0xE5];

        Assert.True(IcedDecoder.TryDecode(Rip, prologue, out var inst));
        Assert.Equal(1, inst.Length);
        Assert.Equal("Push", inst.Mnemonic);
        Assert.Equal(new byte[] { 0x55 }, inst.Bytes);
    }

    [Fact]
    public void TryReadGuestBytes_StopsAtUnmappedBoundary()
    {
        var memory = new VirtualMemory();
        byte[] code = [0x90, 0x90, 0x90, 0x90]; // 4 nops at the very end of a region
        memory.Map(Rip, 4, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        Assert.True(IcedDecoder.TryReadGuestBytes(memory, Rip, 15, out var bytes));
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public void TryReadGuestBytes_UnmappedStart_Fails()
    {
        var memory = new VirtualMemory();

        Assert.False(IcedDecoder.TryReadGuestBytes(memory, Rip, 15, out var bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void FormatBytes_EmptyAndNonEmpty()
    {
        Assert.Equal("??", IcedDecoder.FormatBytes(ReadOnlySpan<byte>.Empty));
        Assert.Equal("48 31 C0", IcedDecoder.FormatBytes([0x48, 0x31, 0xC0]));
    }
}
