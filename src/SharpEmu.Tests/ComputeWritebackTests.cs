// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Tests;

public sealed class ComputeWritebackTests
{
    private const ulong GuestAddress = 0x0000000532830000;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void Gate_IsEnabledOnlyByExactOne(string? value, bool expected)
    {
        Assert.Equal(expected, VulkanVideoPresenter.IsComputeWritebackEnabled(value));
    }

    [Fact]
    public void Sink_IsCapturedOnlyWhenEnabled()
    {
        var memory = new SparseGuestMemory();

        Assert.Null(VulkanVideoPresenter.SelectComputeWritebackMemory(memory, enabled: false));
        Assert.Same(
            memory,
            VulkanVideoPresenter.SelectComputeWritebackMemory(memory, enabled: true));
    }

    [Fact]
    public void R16FloatOneByOne_HasTwoByteWriteback()
    {
        Assert.True(VulkanVideoPresenter.TryGetComputeImageWritebackSize(
            Format.R16Sfloat,
            width: 1,
            height: 1,
            out var byteCount));
        Assert.Equal(2UL, byteCount);
    }

    [Theory]
    [InlineData(Format.R16Sfloat, 2u, 1u)]
    [InlineData(Format.R16Sfloat, 1u, 2u)]
    [InlineData(Format.Undefined, 1u, 1u)]
    public void ImageWriteback_RejectsNonInvariantLayouts(
        Format format,
        uint width,
        uint height)
    {
        Assert.False(VulkanVideoPresenter.TryGetComputeImageWritebackSize(
            format,
            width,
            height,
            out var byteCount));
        Assert.Equal(0UL, byteCount);
    }

    [Fact]
    public void SmallBuffer_RequiresWritableMappedGuestRange()
    {
        Assert.True(VulkanVideoPresenter.IsSmallWritableComputeBuffer(
            new VulkanGuestMemoryBuffer(
                GuestAddress,
                new byte[VulkanVideoPresenter.MaxComputeWritebackBytes],
                IsWritable: true)));
        Assert.False(VulkanVideoPresenter.IsSmallWritableComputeBuffer(
            new VulkanGuestMemoryBuffer(GuestAddress, [1], IsWritable: false)));
        Assert.False(VulkanVideoPresenter.IsSmallWritableComputeBuffer(
            new VulkanGuestMemoryBuffer(0, [1], IsWritable: true)));
        Assert.False(VulkanVideoPresenter.IsSmallWritableComputeBuffer(
            new VulkanGuestMemoryBuffer(
                GuestAddress,
                new byte[VulkanVideoPresenter.MaxComputeWritebackBytes + 1],
                IsWritable: true)));
    }

    [Fact]
    public void CompletedOutput_IsWrittenToCapturedGuestAddress()
    {
        var memory = new SparseGuestMemory();
        byte[] exposure = [0x00, 0x3C];

        Assert.True(VulkanVideoPresenter.TryWriteComputeOutput(
            memory,
            GuestAddress,
            exposure));

        var actual = new byte[exposure.Length];
        Assert.True(memory.TryRead(GuestAddress, actual));
        Assert.Equal(exposure, actual);
    }

    [Theory]
    [InlineData("BufferStoreDword", true)]
    [InlineData("BufferAtomicAdd", true)]
    [InlineData("BufferLoadDword", false)]
    public void GlobalBinding_IsWritableOnlyForMatchedWriteInstruction(
        string opcode,
        bool expected)
    {
        var binding = new Gen5GlobalMemoryBinding(
            ScalarAddress: 4,
            BaseAddress: GuestAddress,
            InstructionPcs: [0x20],
            Data: [0, 0, 0, 0]);
        var program = new Gen5ShaderProgram(
            Address: 0x1000,
            Instructions:
            [
                new Gen5ShaderInstruction(
                    Pc: 0x20,
                    Encoding: Gen5ShaderEncoding.Mubuf,
                    Opcode: opcode,
                    Words: [],
                    Sources: [],
                    Destinations: [],
                    Control: null),
            ]);

        Assert.Equal(expected, AgcExports.IsGlobalMemoryBindingWritable(binding, program));
    }

    [Fact]
    public void GlobalBinding_IgnoresWriteAtDifferentProgramCounter()
    {
        var binding = new Gen5GlobalMemoryBinding(4, GuestAddress, [0x20], [0, 0, 0, 0]);
        var program = new Gen5ShaderProgram(
            0x1000,
            [new Gen5ShaderInstruction(
                0x24,
                Gen5ShaderEncoding.Mubuf,
                "BufferStoreDword",
                [],
                [],
                [],
                null)]);

        Assert.False(AgcExports.IsGlobalMemoryBindingWritable(binding, program));
    }
}
