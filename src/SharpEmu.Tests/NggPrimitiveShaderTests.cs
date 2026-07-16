// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Decode tests for NGG primitive-shader detection: the VGT_SHADER_STAGES_EN
/// bitfields that flag an NGG draw and the s_sendmsg SIMM16 payload the
/// primitive shader uses to request its per-subgroup allocation.
/// </summary>
public sealed class NggPrimitiveShaderTests
{
    [Fact]
    public void ShaderStages_PlainVertex_IsNotNggDraw()
    {
        // VS_EN set, PRIMGEN_EN clear: legacy vertex pipeline.
        var stages = NggShaderStages.Decode(0x0000_0040);

        Assert.False(stages.IsNggPrimitiveDraw);
        Assert.False(stages.PrimgenEn);
        Assert.True(stages.VsEn);
    }

    [Fact]
    public void ShaderStages_PrimgenEnabled_IsNggDraw()
    {
        // PRIMGEN_EN (bit 17) set: NGG geometry front-end.
        var stages = NggShaderStages.Decode(0x0002_0000);

        Assert.True(stages.IsNggPrimitiveDraw);
        Assert.True(stages.PrimgenEn);
    }

    [Fact]
    public void ShaderStages_DecodesMergedEsGsAndFastLaunch()
    {
        // ES_EN [4:3] + GS_EN [5] + PRIMGEN_EN [17] + GS_FAST_LAUNCH=2 [23:22].
        var value = 0x0000_0008u | 0x0000_0020u | 0x0002_0000u | (2u << 22);
        var stages = NggShaderStages.Decode(value);

        Assert.True(stages.EsEn);
        Assert.True(stages.GsEn);
        Assert.True(stages.PrimgenEn);
        Assert.Equal(2u, stages.GsFastLaunch);
        Assert.Equal(value, stages.Raw);
    }

    [Fact]
    public void ShaderStages_PrimgenPassthru_Decoded()
    {
        var stages = NggShaderStages.Decode(0x0002_0000u | 0x0004_0000u);

        Assert.True(stages.PrimgenEn);
        Assert.True(stages.PrimgenPassthruEn);
    }

    [Fact]
    public void SendMessage_GsAllocReq_Decoded()
    {
        // s_sendmsg with SIMM16 message id 9 (GS_ALLOC_REQ).
        var word = 0xBF90_0000u | 9u;
        var message = NggSendMessage.Decode(word);

        Assert.Equal(NggMessageId.GsAllocReq, message.Message);
        Assert.True(message.IsGsAllocReq);
    }

    [Fact]
    public void SendMessage_GsEmit_DecodesOperationAndStream()
    {
        // message id 2 (GS), operation 2 (EMIT) in [5:4], stream 1 in [9:8].
        var simm16 = 2u | (2u << 4) | (1u << 8);
        var message = NggSendMessage.Decode(0xBF90_0000u | simm16);

        Assert.Equal(NggMessageId.Gs, message.Message);
        Assert.Equal(NggGsOperation.Emit, message.GsOperation);
        Assert.Equal(1u, message.StreamId);
        Assert.False(message.IsGsAllocReq);
    }

    [Fact]
    public void SendMessage_GsCut_DecodesOperation()
    {
        var simm16 = 2u | (1u << 4); // GS message, CUT operation
        var message = NggSendMessage.Decode(0xBF90_0000u | simm16);

        Assert.Equal(NggMessageId.Gs, message.Message);
        Assert.Equal(NggGsOperation.Cut, message.GsOperation);
    }

    [Fact]
    public void SendMessage_UnknownMessageId_IsUnknown()
    {
        var message = NggSendMessage.Decode(0xBF90_0000u | 0x7u);

        Assert.Equal(NggMessageId.Unknown, message.Message);
    }

    [Fact]
    public void SendMessage_GsAllocReq_IsNotAmplification()
    {
        var message = NggSendMessage.Decode(0xBF90_0000u | 9u);

        Assert.False(message.IsGeometryAmplification);
    }

    [Fact]
    public void SendMessage_GsEmit_IsAmplification()
    {
        var simm16 = 2u | (2u << 4); // GS message, EMIT operation
        var message = NggSendMessage.Decode(0xBF90_0000u | simm16);

        Assert.True(message.IsGeometryAmplification);
    }

    [Fact]
    public void Classification_GsAllocReqOnly_IsPassThrough()
    {
        var messages = new[]
        {
            NggSendMessage.Decode(0xBF90_0000u | 9u), // GS_ALLOC_REQ
        };

        var result = NggEsGeometryClassification.FromSendMessages(messages);

        Assert.Equal(NggEsGeometryMode.PassThrough, result.Mode);
        Assert.False(result.IsAmplifying);
        Assert.Equal(1, result.AllocReqCount);
        Assert.Equal(0, result.EmitCount);
        Assert.Equal(0, result.CutCount);
    }

    [Fact]
    public void Classification_NoMessages_IsPassThrough()
    {
        var result = NggEsGeometryClassification.FromSendMessages(
            System.Array.Empty<NggSendMessage>());

        Assert.Equal(NggEsGeometryMode.PassThrough, result.Mode);
        Assert.False(result.IsAmplifying);
    }

    [Fact]
    public void Classification_WithEmit_IsAmplifying()
    {
        var messages = new[]
        {
            NggSendMessage.Decode(0xBF90_0000u | 9u),             // GS_ALLOC_REQ
            NggSendMessage.Decode(0xBF90_0000u | 2u | (2u << 4)), // GS EMIT
        };

        var result = NggEsGeometryClassification.FromSendMessages(messages);

        Assert.Equal(NggEsGeometryMode.Amplifying, result.Mode);
        Assert.True(result.IsAmplifying);
        Assert.Equal(1, result.AllocReqCount);
        Assert.Equal(1, result.EmitCount);
        Assert.Equal(0, result.CutCount);
    }

    [Fact]
    public void Classification_WithCut_IsAmplifying()
    {
        var messages = new[]
        {
            NggSendMessage.Decode(0xBF90_0000u | 2u | (1u << 4)), // GS CUT
        };

        var result = NggEsGeometryClassification.FromSendMessages(messages);

        Assert.Equal(NggEsGeometryMode.Amplifying, result.Mode);
        Assert.Equal(0, result.EmitCount);
        Assert.Equal(1, result.CutCount);
    }

    [Fact]
    public void Classification_EmitCut_CountsBoth()
    {
        var messages = new[]
        {
            NggSendMessage.Decode(0xBF90_0000u | 2u | (3u << 4)), // GS EMIT_CUT
        };

        var result = NggEsGeometryClassification.FromSendMessages(messages);

        Assert.Equal(NggEsGeometryMode.Amplifying, result.Mode);
        Assert.Equal(1, result.EmitCount);
        Assert.Equal(1, result.CutCount);
    }
}
