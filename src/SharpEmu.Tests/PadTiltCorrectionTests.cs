// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// scePadSetTiltCorrectionState accepts the primary pad handle and rejects
/// everything else, matching the other pad state setters.
/// </summary>
public sealed class PadTiltCorrectionTests
{
    private const int PrimaryPadHandle = 1;
    private const int ErrorInvalidHandle = unchecked((int)0x80920003);

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void SetTiltCorrectionState_PrimaryHandle_Succeeds()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = PrimaryPadHandle;
        ctx[CpuRegister.Rsi] = 1;

        PadExports.PadSetTiltCorrectionState(ctx);
        Assert.Equal(0, Result(ctx));
    }

    [Fact]
    public void SetTiltCorrectionState_UnknownHandle_ReturnsInvalidHandle()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 42;
        ctx[CpuRegister.Rsi] = 1;

        PadExports.PadSetTiltCorrectionState(ctx);
        Assert.Equal(ErrorInvalidHandle, Result(ctx));
    }
}
