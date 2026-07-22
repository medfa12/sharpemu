// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Tests;

public sealed class UserServiceExportsTests
{
    private const int PrimaryUserId = 1000;
    private const ulong OutputAddress = 0x2000;

    private static CpuContext CreateContext()
    {
        return new CpuContext(new SparseGuestMemory(), Generation.Gen5);
    }

    [Fact]
    public void ForegroundAndRegisteredUsers_UseThePrimaryProfile()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = OutputAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetForegroundUser(ctx));
        Assert.True(ctx.TryReadInt32(OutputAddress, out var foregroundUser));
        Assert.Equal(PrimaryUserId, foregroundUser);

        Assert.Equal(0, UserServiceExports.UserServiceGetRegisteredUserIdList(ctx));
        for (var index = 0; index < 16; index++)
        {
            Assert.True(ctx.TryReadInt32(OutputAddress + (ulong)(index * sizeof(int)), out var userId));
            Assert.Equal(index == 0 ? PrimaryUserId : -1, userId);
        }
    }

    [Fact]
    public void ScalarSetting_RoundTripsThroughSetAndGet()
    {
        var ctx = CreateContext();
        Assert.Equal(0, UserServiceExports.UserServiceInitialize2(ctx));

        ctx[CpuRegister.Rdi] = PrimaryUserId;
        ctx[CpuRegister.Rsi] = 37;
        Assert.Equal(0, UserServiceExports.UserServiceSetVolumeForController(ctx));

        ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, UserServiceExports.UserServiceGetVolumeForController(ctx));
        Assert.True(ctx.TryReadInt32(OutputAddress, out var volume));
        Assert.Equal(37, volume);
    }

    [Fact]
    public void DateOfBirth_WritesPackedYearMonthAndDay()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = PrimaryUserId;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetNpDateOfBirth(ctx));
        Assert.True(ctx.TryReadInt32(OutputAddress, out var year));
        Assert.True(ctx.TryReadInt32(OutputAddress + 4, out var month));
        Assert.True(ctx.TryReadInt32(OutputAddress + 8, out var day));
        Assert.Equal(1990, year);
        Assert.Equal(1, month);
        Assert.Equal(1, day);
    }
}
