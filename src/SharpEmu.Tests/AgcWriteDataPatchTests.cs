// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceAgcDcbWriteData / sceAgcWriteDataPatchSetAddressOrOffset patch-later flow.
/// Games (Astro Bot) build the GPU completion-label WriteData packet with a null
/// placeholder destination and patch the real label address in afterwards, so
/// the builder must accept destinationAddress == 0 and the patcher must fill in
/// the 64-bit destination at packet+8.
/// </summary>
public sealed class AgcWriteDataPatchTests
{
    private const ulong CommandBufferAddress = 0x1000;
    private const ulong CommandStart = 0x2000;
    private const ulong CommandEnd = 0x3000;
    private const ulong DataAddress = 0x4000;
    private const ulong StackAddress = 0x5000;
    private const ulong LabelAddress = 0x5_04E0_2638;
    private const uint ItNop = 0x10;
    private const uint RWriteData = 0x15;
    private const ulong CursorUpOffset = 0x10;
    private const ulong CursorDownOffset = 0x18;
    private const ulong CallbackOffset = 0x20;
    private const ulong ReservedDwOffset = 0x30;

    private static CpuContext NewContext()
    {
        var memory = new SparseGuestMemory();
        memory.WriteUInt64(CommandBufferAddress + CursorUpOffset, CommandStart);
        memory.WriteUInt64(CommandBufferAddress + CursorDownOffset, CommandEnd);
        memory.WriteUInt64(CommandBufferAddress + CallbackOffset, 0);
        memory.WriteUInt32(CommandBufferAddress + ReservedDwOffset, 0);
        memory.WriteUInt32(DataAddress, 0xCAFEBABE);
        memory.WriteUInt64(StackAddress + 8, 0); // increment
        memory.WriteUInt64(StackAddress + 16, 1); // writeConfirm
        return new CpuContext(memory, Generation.Gen5);
    }

    private static ulong WriteData(CpuContext ctx, ulong destinationAddress)
    {
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 5; // destination: memory
        ctx[CpuRegister.Rdx] = 0; // cache policy
        ctx[CpuRegister.Rcx] = destinationAddress;
        ctx[CpuRegister.R8] = DataAddress;
        ctx[CpuRegister.R9] = 1; // dword count
        ctx[CpuRegister.Rsp] = StackAddress;

        AgcExports.DcbWriteData(ctx);
        return ctx[CpuRegister.Rax];
    }

    private static OrbisGen2Result Patch(CpuContext ctx, ulong commandAddress, ulong address)
    {
        ctx[CpuRegister.Rdi] = commandAddress;
        ctx[CpuRegister.Rsi] = address;
        return (OrbisGen2Result)AgcExports.WriteDataPatchSetAddressOrOffset(ctx);
    }

    [Fact]
    public void DcbWriteData_NullDestination_BuildsPatchablePacket()
    {
        var ctx = NewContext();

        var packet = WriteData(ctx, destinationAddress: 0);

        Assert.Equal(CommandStart, packet);
        Assert.True(ctx.TryReadUInt32(packet, out var header));
        Assert.Equal(ItNop, (header >> 8) & 0xFF);
        Assert.Equal(RWriteData, (header >> 2) & 0x3F);
        Assert.True(ctx.TryReadUInt64(packet + 8, out var placeholder));
        Assert.Equal(0UL, placeholder);
        Assert.True(ctx.TryReadUInt32(packet + 16, out var payload));
        Assert.Equal(0xCAFEBABEu, payload);
    }

    [Fact]
    public void PatchSetAddressOrOffset_FillsInPlaceholderDestination()
    {
        var ctx = NewContext();
        var packet = WriteData(ctx, destinationAddress: 0);
        Assert.NotEqual(0UL, packet);

        var result = Patch(ctx, packet, LabelAddress);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(ctx.TryReadUInt64(packet + 8, out var patched));
        Assert.Equal(LabelAddress, patched);
    }

    [Fact]
    public void PatchSetAddressOrOffset_UnmappedCommandAddress_Rejected()
    {
        // A NULL packet pointer relocated by (dcbCursor - stagingBase) lands in
        // the non-canonical range; it must be rejected, never masked and written.
        var ctx = NewContext();

        var result = Patch(ctx, 0xFFFF_8005_32C0_34C4, LabelAddress);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void PatchSetAddressOrOffset_NonWriteDataPacket_Rejected()
    {
        var ctx = NewContext();
        var memory = (SparseGuestMemory)ctx.Memory;
        memory.WriteUInt32(CommandStart, 0xC0001000); // op=ItNop, register=0

        var result = Patch(ctx, CommandStart, LabelAddress);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }
}
