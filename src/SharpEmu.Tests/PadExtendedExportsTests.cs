// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Tests;

public sealed class PadExtendedExportsTests
{
    private const int PrimaryPadHandle = 1;
    private const ulong DataAddress = 0x10000;

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void GetHandle_PrimaryController_ReturnsStableHandle()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        PadExports.PadInit(ctx);
        ctx[CpuRegister.Rdi] = 1000;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;

        PadExports.PadGetHandle(ctx);

        Assert.Equal(PrimaryPadHandle, Result(ctx));
    }

    [Fact]
    public void DeviceClassExtendedInformation_StandardController_ClearsPackedStructure()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = PrimaryPadHandle;
        ctx[CpuRegister.Rsi] = DataAddress;
        Assert.True(memory.TryWrite(DataAddress, Enumerable.Repeat((byte)0xFF, 20).ToArray()));

        PadExports.PadDeviceClassGetExtendedInformation(ctx);

        Assert.Equal(0, Result(ctx));
        var information = new byte[20];
        Assert.True(memory.TryRead(DataAddress, information));
        Assert.All(information, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReadStateExt_ConnectedController_WritesNeutralPackedState()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = PrimaryPadHandle;
        ctx[CpuRegister.Rsi] = DataAddress;
        Assert.True(memory.TryWrite(DataAddress, Enumerable.Repeat((byte)0xFF, 0x78).ToArray()));

        PadExports.PadReadStateExt(ctx);

        Assert.Equal(0, Result(ctx));
        var data = new byte[0x78];
        Assert.True(memory.TryRead(DataAddress, data));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(new byte[] { 128, 128, 128, 128 }, data[4..8]);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x18)));
        Assert.Equal(1, data[0x4C]);
        Assert.Equal(1, data[0x68]);
    }

    [Fact]
    public void GetInfo_ConnectedController_WritesPrimaryHandleAndDefaultColor()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = unchecked((ulong)PrimaryPadHandle);
        ctx[CpuRegister.Rsi] = DataAddress;

        PadExports.PadGetInfo(ctx);

        Assert.Equal(0, Result(ctx));
        var information = new byte[0x98];
        Assert.True(memory.TryRead(DataAddress, information));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(0x00)));
        Assert.Equal(PrimaryPadHandle, BinaryPrimitives.ReadInt32LittleEndian(information.AsSpan(0x08)));
        Assert.Equal(0x00000101U, BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(0x0C)));
        Assert.Equal(0x00FF0000U, BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(0x18)));
    }
}
