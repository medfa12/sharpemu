// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Depth target decode from the GFX10 DB context register block (register byte
/// address minus 0x28000, / 4): DB_DEPTH_SIZE_XY at 0x007, DB_Z_INFO at 0x010,
/// DB_Z_READ_BASE/DB_Z_WRITE_BASE at 0x012/0x014 and their HI words at
/// 0x01A/0x01C. The GFX9-style offsets used previously read DB_Z_READ_BASE_HI
/// where DB_DEPTH_SIZE_XY lives, so real streams decoded garbage extents and
/// the depth attachment silently never bound. These tests pin the layout with
/// raw register indices rather than the decoder's own constants.
/// </summary>
public sealed class AgcDepthTargetDecodeTests
{
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbZInfo = 0x010;
    private const uint DbZReadBase = 0x012;
    private const uint DbZWriteBase = 0x014;
    private const uint DbZReadBaseHi = 0x01A;
    private const uint DbZWriteBaseHi = 0x01C;
    private const uint DbDepthControl = 0x200;

    private static Dictionary<uint, uint> Gfx10DepthRegisters() => new()
    {
        // Z_ENABLE | Z_WRITE_ENABLE | ZFUNC=LESS(1)
        [DbDepthControl] = 0x2u | 0x4u | (1u << 4),
        [DbDepthSizeXy] = 1919u | (1079u << 16),
        // FORMAT=Z32_FLOAT(3), SW_MODE=24
        [DbZInfo] = 3u | (24u << 4),
        [DbZReadBase] = 0x0123_4567u,
        [DbZWriteBase] = 0x0123_4567u,
        [DbZReadBaseHi] = 2u,
        [DbZWriteBaseHi] = 2u,
    };

    [Fact]
    public void DecodesGfx10DepthRegisterLayout()
    {
        var depth = AgcExports.GetDepthTarget(Gfx10DepthRegisters());

        Assert.NotNull(depth);
        Assert.Equal(0x0000_0201_2345_6700UL, depth.Value.Address);
        Assert.Equal(1920u, depth.Value.Width);
        Assert.Equal(1080u, depth.Value.Height);
        Assert.Equal(3u, depth.Value.Format);
        Assert.Equal(24u, depth.Value.TileMode);
        Assert.True(depth.Value.TestEnable);
        Assert.True(depth.Value.WriteEnable);
        Assert.Equal(1u, depth.Value.CompareOp);
    }

    [Fact]
    public void ReadOnlyDepthFallsBackToReadBase()
    {
        var registers = Gfx10DepthRegisters();
        registers.Remove(DbZWriteBase);
        registers.Remove(DbZWriteBaseHi);
        // Z_ENABLE only, ZFUNC=LEQUAL(3): a read-only depth test binds just
        // DB_Z_READ_BASE, exactly the uninitialized-first-use title draw shape.
        registers[DbDepthControl] = 0x2u | (3u << 4);

        var depth = AgcExports.GetDepthTarget(registers);

        Assert.NotNull(depth);
        Assert.Equal(0x0000_0201_2345_6700UL, depth.Value.Address);
        Assert.True(depth.Value.TestEnable);
        Assert.False(depth.Value.WriteEnable);
        Assert.Equal(3u, depth.Value.CompareOp);
    }

    [Fact]
    public void StaleClearStateSizeStillDecodes()
    {
        // Some streams leave DB_DEPTH_SIZE_XY at its clear-state value 0
        // (decodes as 1x1); the presenter repairs the extent later, so the
        // decoder must still surface the target instead of dropping it.
        var registers = Gfx10DepthRegisters();
        registers[DbDepthSizeXy] = 0;

        var depth = AgcExports.GetDepthTarget(registers);

        Assert.NotNull(depth);
        Assert.Equal(1u, depth.Value.Width);
        Assert.Equal(1u, depth.Value.Height);
    }

    [Fact]
    public void InvalidFormatDecodesAsNoDepthTarget()
    {
        var registers = Gfx10DepthRegisters();
        registers[DbZInfo] = 24u << 4; // FORMAT=Z_INVALID(0)

        Assert.Null(AgcExports.GetDepthTarget(registers));
    }

    [Fact]
    public void DisabledDepthControlDecodesAsNoDepthTarget()
    {
        var registers = Gfx10DepthRegisters();
        registers[DbDepthControl] = 0;

        Assert.Null(AgcExports.GetDepthTarget(registers));
    }
}
