// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Semantics tests for the scalar (SALU) constant evaluator used to recover
/// resource descriptors from Gen5 shader preambles: RDNA carry/borrow flags,
/// inline constant decoding, immediate sign extension, and control flow.
/// </summary>
public sealed class Gen5ShaderScalarEvaluatorTests
{
    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private sealed class ArrayMemory(ulong baseAddress, byte[] data) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress < baseAddress ||
                virtualAddress - baseAddress + (ulong)destination.Length > (ulong)data.Length)
            {
                return false;
            }

            data.AsSpan((int)(virtualAddress - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private static Gen5ShaderInstruction Sop2(uint pc, string opcode, uint dest, Gen5Operand src0, Gen5Operand src1)
        => new(pc, Gen5ShaderEncoding.Sop2, opcode, [0u], [src0, src1], [Gen5Operand.Scalar(dest)], null);

    private static Gen5ShaderInstruction Sop1(uint pc, string opcode, uint dest, Gen5Operand src0)
        => new(pc, Gen5ShaderEncoding.Sop1, opcode, [0u], [src0], [Gen5Operand.Scalar(dest)], null);

    private static Gen5ShaderInstruction Sopk(uint pc, string opcode, uint dest, uint immediate)
        => new(pc, Gen5ShaderEncoding.Sopk, opcode, [0u],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, immediate)], [Gen5Operand.Scalar(dest)], null);

    private static Gen5ShaderInstruction EndPgm(uint pc)
        => new(pc, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null);

    private static Gen5ShaderInstruction Branch(uint pc, short dwordOffset)
        => new(pc, Gen5ShaderEncoding.Sopp, "SBranch", [(uint)(ushort)dwordOffset], [], [], null);

    private static Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);

    private static Gen5ShaderInstruction ImageSampleLz(uint pc, uint scalarResource, uint scalarSampler)
        => new(pc, Gen5ShaderEncoding.Mimg, "ImageSampleLz", [0u], [], [],
            new Gen5ImageControl(
                Dmask: 0xF,
                VectorAddress: 0,
                AddressRegisters: [0u, 1u],
                VectorData: 0,
                ScalarResource: scalarResource,
                ScalarSampler: scalarSampler,
                Dimension: 1,
                IsArray: false,
                Glc: false,
                Slc: false));

    private static Gen5ShaderInstruction SLoadDwordx8(uint pc, uint baseRegister, uint destBase)
        => new(pc, Gen5ShaderEncoding.Smem, "SLoadDwordx8", [0u],
            [Gen5Operand.Scalar(baseRegister)],
            [.. Enumerable.Range(0, 8).Select(index => Gen5Operand.Scalar(destBase + (uint)index))],
            new Gen5ScalarMemoryControl(
                DestinationCount: 8,
                ImmediateOffsetBytes: 0,
                DynamicOffsetRegister: null));

    private static Gen5ShaderEvaluation Evaluate(
        IReadOnlyList<uint> userData,
        uint userDataBase,
        params Gen5ShaderInstruction[] instructions)
        => Evaluate(new NullMemory(), userData, userDataBase, instructions);

    private static Gen5ShaderEvaluation Evaluate(
        ICpuMemory memory,
        IReadOnlyList<uint> userData,
        uint userDataBase,
        params Gen5ShaderInstruction[] instructions)
    {
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x10000, instructions),
            userData,
            Metadata: null,
            UserDataScalarRegisterBase: userDataBase);
        var ok = Gen5ShaderScalarEvaluator.TryEvaluate(
            new CpuContext(memory, Generation.Gen5),
            state,
            out var evaluation,
            out var error);
        Assert.True(ok, $"TryEvaluate failed: {error}");
        return evaluation;
    }

    [Fact]
    public void Evaluate_SeedsUserDataAndExecMask()
    {
        var evaluation = Evaluate([11, 22, 33], userDataBase: 4, EndPgm(0));

        Assert.Equal(11u, evaluation.ScalarRegisters[4]);
        Assert.Equal(22u, evaluation.ScalarRegisters[5]);
        Assert.Equal(33u, evaluation.ScalarRegisters[6]);
        Assert.Equal(0xFFFF_FFFFu, evaluation.ScalarRegisters[126]); // EXEC lo (wave32)
        Assert.Equal(0u, evaluation.ScalarRegisters[127]);           // EXEC hi
        Assert.Equal(0u, evaluation.ScalarRegisters[106]);           // VCC lo
    }

    [Fact]
    public void SMovB32_WritesLiteralAndSnapshotsPerPc()
    {
        var evaluation = Evaluate(
            [],
            0,
            Sop1(0, "SMovB32", dest: 0, Literal(0xDEAD_BEEF)),
            EndPgm(4));

        Assert.Equal(0xDEAD_BEEFu, evaluation.ScalarRegisters[0]);
        // Snapshots are taken before executing the instruction at that PC.
        Assert.Equal(0u, evaluation.ScalarRegistersByPc[0][0]);
        Assert.Equal(0xDEAD_BEEFu, evaluation.ScalarRegistersByPc[4][0]);
    }

    [Fact]
    public void SAddU32_SetsCarry_AndSAddcU32ConsumesIt()
    {
        var evaluation = Evaluate(
            [0xFFFF_FFFF, 1],
            0,
            Sop2(0, "SAddU32", dest: 2, Gen5Operand.Scalar(0), Gen5Operand.Scalar(1)),
            Sop2(4, "SAddcU32", dest: 3, Gen5Operand.Scalar(1), Gen5Operand.Scalar(1)),
            EndPgm(8));

        Assert.Equal(0u, evaluation.ScalarRegisters[2]);  // wrapped
        Assert.Equal(3u, evaluation.ScalarRegisters[3]);  // 1 + 1 + carry
    }

    [Fact]
    public void SSubU32_SetsBorrow_AndSCselectB32ReadsScc()
    {
        var evaluation = Evaluate(
            [1, 2, 0xAAAA_AAAA, 0xBBBB_BBBB],
            0,
            Sop2(0, "SSubU32", dest: 4, Gen5Operand.Scalar(0), Gen5Operand.Scalar(1)),
            Sop2(4, "SCselectB32", dest: 5, Gen5Operand.Scalar(2), Gen5Operand.Scalar(3)),
            EndPgm(8));

        Assert.Equal(0xFFFF_FFFFu, evaluation.ScalarRegisters[4]); // 1 - 2 wraps
        Assert.Equal(0xAAAA_AAAAu, evaluation.ScalarRegisters[5]); // SCC (borrow) selects first source
    }

    [Fact]
    public void SMovkI32_SignExtendsSixteenBitImmediate()
    {
        var evaluation = Evaluate(
            [],
            0,
            Sopk(0, "SMovkI32", dest: 0, immediate: 0x8000),
            Sopk(4, "SMovkI32", dest: 1, immediate: 0x7FFF),
            EndPgm(8));

        Assert.Equal(0xFFFF_8000u, evaluation.ScalarRegisters[0]);
        Assert.Equal(0x0000_7FFFu, evaluation.ScalarRegisters[1]);
    }

    [Theory]
    [InlineData(125u, 0u)]           // SGPR-NULL reads as zero
    [InlineData(129u, 1u)]           // inline 1..64
    [InlineData(192u, 64u)]
    [InlineData(193u, 0xFFFF_FFFFu)] // inline -1..-16
    [InlineData(208u, unchecked((uint)-16))]
    public void SMovB32_DecodesInlineConstants(uint encoded, uint expected)
    {
        var evaluation = Evaluate(
            [],
            0,
            Sop1(0, "SMovB32", dest: 0, new Gen5Operand(Gen5OperandKind.EncodedConstant, encoded)),
            EndPgm(4));

        Assert.Equal(expected, evaluation.ScalarRegisters[0]);
    }

    [Fact]
    public void SBcnt1I32B32_CountsBits()
    {
        var evaluation = Evaluate(
            [0xF0F0],
            0,
            Sop1(0, "SBcnt1I32B32", dest: 1, Gen5Operand.Scalar(0)),
            EndPgm(4));

        Assert.Equal(8u, evaluation.ScalarRegisters[1]);
    }

    [Fact]
    public void SBranch_SkipsForwardToTarget()
    {
        var evaluation = Evaluate(
            [],
            0,
            Sop1(0, "SMovB32", dest: 0, Literal(1)),
            Branch(4, dwordOffset: 1), // next pc 8, +4 bytes -> lands at 12
            Sop1(8, "SMovB32", dest: 0, Literal(2)),
            EndPgm(12));

        Assert.Equal(1u, evaluation.ScalarRegisters[0]);
    }

    [Fact]
    public void SEndpgm_StopsExecution()
    {
        var evaluation = Evaluate(
            [],
            0,
            EndPgm(0),
            Sop1(4, "SMovB32", dest: 0, Literal(5)));

        Assert.Equal(0u, evaluation.ScalarRegisters[0]);
    }

    [Fact]
    public void SLshlB64_ShiftsAcrossDwordBoundary()
    {
        var evaluation = Evaluate(
            [1, 0],
            0,
            Sop2(0, "SLshlB64", dest: 2, Gen5Operand.Scalar(0), Literal(36)),
            EndPgm(4));

        Assert.Equal(0u, evaluation.ScalarRegisters[2]);        // low dword
        Assert.Equal(0x10u, evaluation.ScalarRegisters[3]);     // high dword: 1 << 36
    }

    [Fact]
    public void SAndB32_SetsSccFromResult()
    {
        var evaluation = Evaluate(
            [0xFF00, 0x00FF, 0xAAAA_AAAA, 0xBBBB_BBBB],
            0,
            Sop2(0, "SAndB32", dest: 4, Gen5Operand.Scalar(0), Gen5Operand.Scalar(1)),
            Sop2(4, "SCselectB32", dest: 5, Gen5Operand.Scalar(2), Gen5Operand.Scalar(3)),
            EndPgm(8));

        Assert.Equal(0u, evaluation.ScalarRegisters[4]);
        Assert.Equal(0xBBBB_BBBBu, evaluation.ScalarRegisters[5]); // SCC=0 selects second source
    }

    [Fact]
    public void SBranch_SkippedImageSample_ResolvesBindingFromBranchState()
    {
        // T# in s8..s15 and S# in s16..s19 are user data; the image sample sits
        // in a branched-over block that the SPIR-V translator still emits.
        var evaluation = Evaluate(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            userDataBase: 8,
            Branch(0, dwordOffset: 1), // next pc 4, +4 bytes -> lands at 8
            ImageSampleLz(4, scalarResource: 8, scalarSampler: 16),
            EndPgm(8));

        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(4u, binding.Pc);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }, binding.ResourceDescriptor);
        Assert.Equal(new uint[] { 9, 10, 11, 12 }, binding.SamplerDescriptor);
    }

    [Fact]
    public void SBranch_SkippedSLoadDwordx8_ResolvesImageDescriptorFromMemory()
    {
        // The skipped block loads its own T# from memory before sampling; the
        // shadow walk must execute the load so the binding carries the
        // descriptor bytes read from the guest address in s0:s1.
        const ulong descriptorAddress = 0x2_1000_0000;
        var descriptorWords = new uint[]
        {
            0xAABB_0001, 0xAABB_0002, 0xAABB_0003, 0xAABB_0004,
            0xAABB_0005, 0xAABB_0006, 0xAABB_0007, 0xAABB_0008,
        };
        var memory = new byte[descriptorWords.Length * sizeof(uint)];
        Buffer.BlockCopy(descriptorWords, 0, memory, 0, memory.Length);

        var evaluation = Evaluate(
            new ArrayMemory(descriptorAddress, memory),
            [
                unchecked((uint)descriptorAddress),
                (uint)(descriptorAddress >> 32),
                0, 0,
                101, 102, 103, 104, // S# in s4..s7
            ],
            userDataBase: 0,
            Branch(0, dwordOffset: 2), // next pc 4, +8 bytes -> lands at 12
            SLoadDwordx8(4, baseRegister: 0, destBase: 8),
            ImageSampleLz(8, scalarResource: 8, scalarSampler: 4),
            EndPgm(12));

        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(8u, binding.Pc);
        Assert.Equal(descriptorWords, binding.ResourceDescriptor);
        Assert.Equal(new uint[] { 101, 102, 103, 104 }, binding.SamplerDescriptor);
        // The shadow walk must not leak into the main register file.
        Assert.Equal(0u, evaluation.ScalarRegisters[8]);
    }

    [Fact]
    public void UnreachedImageSample_FallsBackToDonorBinding()
    {
        // The op past SEndpgm has an unresolvable T# register range; it still
        // needs a binding (the translator emits every block), so it borrows the
        // resolved donor descriptor instead of failing the translation.
        var evaluation = Evaluate(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            userDataBase: 8,
            ImageSampleLz(0, scalarResource: 8, scalarSampler: 16),
            EndPgm(4),
            ImageSampleLz(8, scalarResource: 250, scalarSampler: 16));

        Assert.Equal(2, evaluation.ImageBindings.Count);
        Assert.Equal(8u, evaluation.ImageBindings[1].Pc);
        Assert.Equal(
            evaluation.ImageBindings[0].ResourceDescriptor,
            evaluation.ImageBindings[1].ResourceDescriptor);
        Assert.Equal(new uint[] { 9, 10, 11, 12 }, evaluation.ImageBindings[1].SamplerDescriptor);
    }
}
