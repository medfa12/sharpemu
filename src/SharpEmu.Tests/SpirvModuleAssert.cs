// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>Minimal structural SPIR-V parser shared by shader tests.</summary>
internal static class SpirvModuleAssert
{
    public const uint Magic = 0x0723_0203;
    public const ushort OpEntryPoint = 15;
    public const ushort OpCapability = 17;
    public const ushort OpFunction = 54;
    public const ushort OpFunctionEnd = 56;
    public const ushort OpLabel = 248;
    public const ushort OpReturn = 253;
    public const uint CapabilityShader = 1;

    internal sealed record ParsedModule(
        uint Version,
        uint Bound,
        List<(ushort Opcode, uint[] Words)> Instructions);

    public static ParsedModule Parse(byte[] binary)
    {
        Assert.True(binary.Length >= 20, "SPIR-V module must have a 5-word header.");
        Assert.Equal(0, binary.Length % 4);

        var words = new uint[binary.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(i * 4));
        }

        Assert.Equal(Magic, words[0]);
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

    /// <summary>Common invariants every valid shader module must satisfy.</summary>
    public static (ushort Opcode, uint[] Words) AssertShaderModule(ParsedModule module, uint executionModel)
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
        return entryPoint;
    }
}
