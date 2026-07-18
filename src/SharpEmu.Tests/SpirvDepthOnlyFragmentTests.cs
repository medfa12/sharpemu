// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// The fixed depth-only fragment stage backs guest passes that bind no pixel
/// shader (depth prepass / DB clear draws). It must be a structurally valid
/// fragment module with no color outputs: the guest exported none, and a
/// phantom output would bleed into the write-masked scratch attachment.
/// </summary>
public sealed class SpirvDepthOnlyFragmentTests
{
    private const uint SpirvMagic = 0x07230203;
    private const int OpEntryPoint = 15;
    private const int OpVariable = 59;
    private const uint StorageClassOutput = 3;

    [Fact]
    public void DepthOnlyFragmentIsOutputFreeFragmentModule()
    {
        var spirv = SpirvFixedShaders.CreateDepthOnlyFragment();

        Assert.True(spirv.Length >= 20);
        Assert.Equal(0, spirv.Length % 4);

        var words = new uint[spirv.Length / 4];
        System.Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        Assert.Equal(SpirvMagic, words[0]);

        var sawFragmentEntryPoint = false;
        var sawOutputVariable = false;
        for (var index = 5; index < words.Length;)
        {
            var wordCount = (int)(words[index] >> 16);
            var opcode = (int)(words[index] & 0xFFFF);
            Assert.True(wordCount > 0);
            if (opcode == OpEntryPoint)
            {
                // Word 1 is the execution model: 4 = Fragment.
                sawFragmentEntryPoint |= words[index + 1] == 4;
            }
            else if (opcode == OpVariable && wordCount >= 4)
            {
                sawOutputVariable |= words[index + 3] == StorageClassOutput;
            }

            index += wordCount;
        }

        Assert.True(sawFragmentEntryPoint);
        Assert.False(sawOutputVariable);
    }
}
