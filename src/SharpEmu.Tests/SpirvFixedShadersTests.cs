// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Structural validation of the SPIR-V binaries emitted by
/// <see cref="SpirvFixedShaders"/> / <see cref="SpirvModuleBuilder"/>:
/// header layout, instruction word-count integrity, ID bounds, and the
/// invariants Vulkan drivers reject immediately when broken.
/// </summary>
public sealed class SpirvFixedShadersTests
{
    private const uint SpirvMagic = 0x0723_0203;
    private const ushort OpEntryPoint = 15;
    private const ushort OpCapability = 17;
    private const ushort OpFunction = 54;
    private const ushort OpFunctionEnd = 56;
    private const ushort OpLabel = 248;
    private const ushort OpReturn = 253;
    private const uint CapabilityShader = 1;
    private const uint ExecutionModelVertex = 0;
    private const uint ExecutionModelFragment = 4;

    private sealed record ParsedModule(
        uint Version,
        uint Bound,
        List<(ushort Opcode, uint[] Words)> Instructions);

    private static ParsedModule Parse(byte[] binary)
    {
        Assert.True(binary.Length >= 20, "SPIR-V module must have a 5-word header.");
        Assert.Equal(0, binary.Length % 4);

        var words = new uint[binary.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(i * 4));
        }

        Assert.Equal(SpirvMagic, words[0]);
        var version = words[1];
        var bound = words[3];
        Assert.Equal(0u, words[4]); // reserved schema word

        var instructions = new List<(ushort, uint[])>();
        var offset = 5;
        while (offset < words.Length)
        {
            var wordCount = (ushort)(words[offset] >> 16);
            var opcode = (ushort)(words[offset] & 0xFFFF);
            Assert.True(wordCount > 0, $"Zero word count at word offset {offset}.");
            Assert.True(
                offset + wordCount <= words.Length,
                $"Instruction 0x{opcode:X} at offset {offset} overruns the module.");
            instructions.Add((opcode, words[offset..(offset + wordCount)]));
            offset += wordCount;
        }

        Assert.Equal(words.Length, offset); // stream ends exactly at module end
        return new ParsedModule(version, bound, instructions);
    }

    public static TheoryData<uint> AttributeCounts => new() { 0u, 1u, 4u };

    [Theory]
    [MemberData(nameof(AttributeCounts))]
    public void FullscreenVertex_IsStructurallyValidSpirv(uint attributeCount)
    {
        var module = Parse(SpirvFixedShaders.CreateFullscreenVertex(attributeCount));

        AssertCommonInvariants(module, ExecutionModelVertex);

        // vertexIndex + position + one interface entry per attribute
        var entryPoint = Assert.Single(module.Instructions.Where(i => i.Opcode == OpEntryPoint));
        var interfaceCount = CountEntryPointInterfaces(entryPoint.Words);
        Assert.Equal(2 + attributeCount, interfaceCount);
    }

    [Fact]
    public void CopyFragment_IsStructurallyValidSpirv()
    {
        var module = Parse(SpirvFixedShaders.CreateCopyFragment());

        AssertCommonInvariants(module, ExecutionModelFragment);
    }

    private static void AssertCommonInvariants(ParsedModule module, uint executionModel)
    {
        Assert.Contains(
            module.Instructions,
            i => i.Opcode == OpCapability && i.Words[1] == CapabilityShader);

        var entryPoint = Assert.Single(module.Instructions.Where(i => i.Opcode == OpEntryPoint));
        Assert.Equal(executionModel, entryPoint.Words[1]);

        Assert.Equal(
            module.Instructions.Count(i => i.Opcode == OpFunction),
            module.Instructions.Count(i => i.Opcode == OpFunctionEnd));
        Assert.Contains(module.Instructions, i => i.Opcode == OpLabel);
        Assert.Contains(module.Instructions, i => i.Opcode == OpReturn);

        // Every result ID must stay below the declared bound; every used ID must be non-zero.
        foreach (var (opcode, words) in module.Instructions)
        {
            foreach (var word in ResultIds(opcode, words))
            {
                Assert.InRange(word, 1u, module.Bound - 1);
            }
        }
    }

    private static uint CountEntryPointInterfaces(uint[] entryPointWords)
    {
        // OpEntryPoint: [opcode] [execution model] [function id] [literal name...] [interfaces...]
        var index = 3;
        while (index < entryPointWords.Length)
        {
            var word = entryPointWords[index];
            index++;
            if (((word >> 24) & 0xFF) == 0 ||
                ((word >> 16) & 0xFF) == 0 ||
                ((word >> 8) & 0xFF) == 0 ||
                (word & 0xFF) == 0)
            {
                break; // NUL terminator of the literal name
            }
        }

        return (uint)(entryPointWords.Length - index);
    }

    private static IEnumerable<uint> ResultIds(ushort opcode, uint[] words)
    {
        // Opcodes used by the fixed shaders whose result ID is word 2 (type + result).
        var hasTypeAndResult = opcode is 43 or 44 or 54 or 61 or 77 or 79 or 82 or 87
            or 112 or 124 or 129 or 131 or 194 or 196;
        if (hasTypeAndResult && words.Length > 2)
        {
            yield return words[2];
        }
    }
}
