// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// scePadGetTriggerEffectState reports both adaptive triggers in the neutral
/// "no effect active" state: trigger effects decode straight to rumble in
/// scePadSetTriggerEffect and never latch a state machine, so a title polling
/// this per frame (Astro Bot) sees a stable answer instead of an unresolved
/// import.
/// </summary>
public sealed class PadTriggerEffectStateTests
{
    private const int PrimaryPadHandle = 1;
    private const int ErrorInvalidHandle = unchecked((int)0x80920003);
    private const ulong StateAddress = 0x10000;

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void GetTriggerEffectState_PrimaryHandle_WritesNeutralState()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = PrimaryPadHandle;
        ctx[CpuRegister.Rsi] = StateAddress;
        // Pre-fill so the zero write is observable.
        Assert.True(ctx.Memory.TryWrite(StateAddress, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));

        PadExports.PadGetTriggerEffectState(ctx);

        Assert.Equal(0, Result(ctx));
        var state = new byte[8];
        Assert.True(ctx.Memory.TryRead(StateAddress, state));
        Assert.All(state, value => Assert.Equal(0, value));
    }

    [Fact]
    public void GetTriggerEffectState_UnknownHandle_ReturnsInvalidHandle()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 42;
        ctx[CpuRegister.Rsi] = StateAddress;

        PadExports.PadGetTriggerEffectState(ctx);

        Assert.Equal(ErrorInvalidHandle, Result(ctx));
    }

    [Fact]
    public void GetTriggerEffectState_NullStatePointer_ReturnsInvalidArgument()
    {
        var ctx = new CpuContext(new SparseGuestMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = PrimaryPadHandle;
        ctx[CpuRegister.Rsi] = 0;

        PadExports.PadGetTriggerEffectState(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }
}
