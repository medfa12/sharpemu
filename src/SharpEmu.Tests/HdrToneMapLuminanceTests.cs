// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sceSystemServiceGetHdrToneMapLuminance writes the three-float
/// {max, maxFrameAverage, min} block and rejects a null out-pointer.
/// </summary>
public sealed class HdrToneMapLuminanceTests
{
    private const ulong LuminanceAddress = 0x3000;
    private const int ErrorParameter = unchecked((int)0x80A10003);

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void GetHdrToneMapLuminance_WritesDefaultLuminances()
    {
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = LuminanceAddress;

        SystemServiceExports.SystemServiceGetHdrToneMapLuminance(ctx);
        Assert.Equal(0, Result(ctx));

        var luminance = new byte[3 * sizeof(float)];
        Assert.True(memory.TryRead(LuminanceAddress, luminance));
        Assert.Equal(1000.0f, BitConverter.ToSingle(luminance, 0));
        Assert.Equal(1000.0f, BitConverter.ToSingle(luminance, sizeof(float)));
        Assert.Equal(0.01f, BitConverter.ToSingle(luminance, 2 * sizeof(float)));
    }

    [Fact]
    public void GetHdrToneMapLuminance_NullPointer_ReturnsParameterError()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;

        SystemServiceExports.SystemServiceGetHdrToneMapLuminance(ctx);
        Assert.Equal(ErrorParameter, Result(ctx));
    }
}
