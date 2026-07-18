// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// CB_COLOR_CONTROL.MODE=RESOLVE detection. A resolve draw binds the
/// multisampled source in color slot 0 and the single-sample destination in
/// slot 1, but CB_TARGET_MASK enables only slot 0 — so the masked slot 1 must
/// be recovered explicitly or the draw rewrites the source and the destination
/// surface (the following composite's input) stays blank.
/// </summary>
public sealed class AgcHardwareColorResolveTests
{
    private const uint CbColorControl = 0x202;
    private const uint CbTargetMask = 0x8E;
    private const uint CbColor0Base = 0x318;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    private const uint CbColorRegisterStride = 15;
    private const uint ResolveMode = 3u << 4;

    private static void AddColorTarget(
        Dictionary<uint, uint> registers,
        uint slot,
        ulong address,
        uint width = 1920,
        uint height = 1080,
        uint format = 10,
        uint numberType = 0)
    {
        registers[CbColor0Base + slot * CbColorRegisterStride] = (uint)(address >> 8);
        registers[CbColor0BaseExt + slot] = (uint)(address >> 40) & 0xFFu;
        registers[CbColor0Attrib2 + slot] = ((width - 1) << 14) | (height - 1);
        registers[CbColor0Attrib3 + slot] = 0;
        registers[CbColor0Info + slot * CbColorRegisterStride] =
            (format << 2) | (numberType << 8);
    }

    private static Dictionary<uint, uint> ResolveRegisters()
    {
        var registers = new Dictionary<uint, uint>
        {
            [CbColorControl] = ResolveMode,
            [CbTargetMask] = 0xF, // slot 0 only; slot 1 is masked
        };
        AddColorTarget(registers, slot: 0, address: 0x1_0000_0000);
        AddColorTarget(registers, slot: 1, address: 0x2_0000_0000);
        return registers;
    }

    [Fact]
    public void ResolveMode_RecoversMaskedDestinationSlot()
    {
        var detected = AgcExports.TryGetHardwareColorResolveTargets(
            ResolveRegisters(),
            out var source,
            out var destination);

        Assert.True(detected);
        Assert.Equal(0x1_0000_0000UL, source.Address);
        Assert.Equal(0x2_0000_0000UL, destination.Address);
        Assert.Equal(source.Width, destination.Width);
        Assert.Equal(source.Height, destination.Height);
        Assert.Equal(source.Format, destination.Format);
    }

    [Fact]
    public void NormalMode_IsNotAResolveDraw()
    {
        var registers = ResolveRegisters();
        registers[CbColorControl] = 1u << 4; // CB_NORMAL

        Assert.False(AgcExports.TryGetHardwareColorResolveTargets(
            registers, out _, out _));
    }

    [Fact]
    public void MissingColorControl_IsNotAResolveDraw()
    {
        var registers = ResolveRegisters();
        registers.Remove(CbColorControl);

        Assert.False(AgcExports.TryGetHardwareColorResolveTargets(
            registers, out _, out _));
    }

    [Fact]
    public void MissingDestinationSlot_IsNotAResolveDraw()
    {
        var registers = ResolveRegisters();
        registers.Remove(CbColor0Base + CbColorRegisterStride);

        Assert.False(AgcExports.TryGetHardwareColorResolveTargets(
            registers, out _, out _));
    }

    [Fact]
    public void MismatchedExtents_AreNotAResolvePair()
    {
        var registers = ResolveRegisters();
        AddColorTarget(registers, slot: 1, address: 0x2_0000_0000, width: 960, height: 540);

        Assert.False(AgcExports.TryGetHardwareColorResolveTargets(
            registers, out _, out _));
    }

    [Fact]
    public void MismatchedFormats_AreNotAResolvePair()
    {
        var registers = ResolveRegisters();
        AddColorTarget(registers, slot: 1, address: 0x2_0000_0000, format: 12);

        Assert.False(AgcExports.TryGetHardwareColorResolveTargets(
            registers, out _, out _));
    }
}
