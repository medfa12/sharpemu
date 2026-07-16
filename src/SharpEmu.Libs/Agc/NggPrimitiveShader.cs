// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Decoded VGT_SHADER_STAGES_EN state used to detect RDNA NGG
/// primitive-shader draws (merged ES/GS launched through the geometry
/// pipeline rather than a plain vertex shader).
/// </summary>
/// <remarks>
/// Bit positions follow the GFX10.3 VGT_SHADER_STAGES_EN layout. If NGG
/// detection ever misfires on a specific title the masks below are the first
/// thing to re-verify against that title's register defaults.
/// </remarks>
internal readonly record struct NggShaderStages(
    uint Raw,
    bool LsEn,
    bool HsEn,
    bool EsEn,
    bool GsEn,
    bool VsEn,
    bool PrimgenEn,
    bool PrimgenPassthruEn,
    uint GsFastLaunch)
{
    // GFX10.3 VGT_SHADER_STAGES_EN field masks/shifts.
    private const uint LsEnMask = 0x0000_0003; // [1:0]
    private const uint HsEnMask = 0x0000_0004; // [2]
    private const uint EsEnMask = 0x0000_0018; // [4:3]
    private const uint GsEnMask = 0x0000_0020; // [5]
    private const uint VsEnMask = 0x0000_00C0; // [7:6]
    private const uint PrimgenEnMask = 0x0002_0000; // [17]
    private const uint PrimgenPassthruMask = 0x0004_0000; // [18]
    private const int GsFastLaunchShift = 22; // [23:22]
    private const uint GsFastLaunchFieldMask = 0x3;

    public static NggShaderStages Decode(uint value) => new(
        value,
        (value & LsEnMask) != 0,
        (value & HsEnMask) != 0,
        (value & EsEnMask) != 0,
        (value & GsEnMask) != 0,
        (value & VsEnMask) != 0,
        (value & PrimgenEnMask) != 0,
        (value & PrimgenPassthruMask) != 0,
        (value >> GsFastLaunchShift) & GsFastLaunchFieldMask);

    /// <summary>
    /// True when the draw runs the NGG geometry front-end (a merged ES/GS
    /// primitive shader) instead of a legacy vertex shader.
    /// </summary>
    public bool IsNggPrimitiveDraw => PrimgenEn;
}

/// <summary>
/// Message id decoded from the SIMM16 payload of an <c>s_sendmsg</c>
/// (SOPP opcode 0x10) instruction.
/// </summary>
internal enum NggMessageId
{
    Unknown = 0,
    Interrupt = 1,
    Gs = 2,
    GsDone = 3,
    GsAllocReq = 9,
    SysMsg = 15,
}

/// <summary>
/// Operation carried in the GS message (used with <see cref="NggMessageId.Gs"/>
/// / <see cref="NggMessageId.GsDone"/>).
/// </summary>
internal enum NggGsOperation
{
    Nop = 0,
    Cut = 1,
    Emit = 2,
    EmitCut = 3,
}

/// <summary>
/// Decoded <c>s_sendmsg</c> payload. The RDNA NGG primitive shader uses this
/// to request its per-subgroup vertex/primitive allocation
/// (<see cref="NggMessageId.GsAllocReq"/>) and, when it emits geometry through
/// the message path, to advance/cut the primitive stream
/// (<see cref="NggMessageId.Gs"/>).
/// </summary>
internal readonly record struct NggSendMessage(
    NggMessageId Message,
    NggGsOperation GsOperation,
    uint StreamId,
    ushort Simm16)
{
    /// <summary>
    /// Decodes the SIMM16 payload of an <c>s_sendmsg</c> SOPP word
    /// (the low 16 bits of the instruction word).
    /// </summary>
    public static NggSendMessage Decode(uint soppWord)
    {
        var simm16 = (ushort)(soppWord & 0xFFFF);
        var messageId = simm16 & 0xF;
        var operation = (simm16 >> 4) & 0x3;
        var streamId = (uint)((simm16 >> 8) & 0x3);
        var message = messageId switch
        {
            1 => NggMessageId.Interrupt,
            2 => NggMessageId.Gs,
            3 => NggMessageId.GsDone,
            9 => NggMessageId.GsAllocReq,
            15 => NggMessageId.SysMsg,
            _ => NggMessageId.Unknown,
        };
        return new NggSendMessage(
            message,
            (NggGsOperation)operation,
            streamId,
            simm16);
    }

    public bool IsGsAllocReq => Message == NggMessageId.GsAllocReq;

    /// <summary>
    /// True when the message advances or terminates a primitive stream
    /// (<c>GS_EMIT</c>/<c>GS_CUT</c>), i.e. the shader is amplifying geometry
    /// rather than passing vertices through 1:1.
    /// </summary>
    public bool IsGeometryAmplification =>
        (Message == NggMessageId.Gs || Message == NggMessageId.GsDone) &&
        GsOperation != NggGsOperation.Nop;
}

/// <summary>
/// How an NGG ES (merged ES/GS) primitive shader produces its output stream.
/// </summary>
internal enum NggEsGeometryMode
{
    /// <summary>
    /// The shader only requests its per-subgroup allocation
    /// (<c>GS_ALLOC_REQ</c>) — or issues no geometry message at all — and
    /// exports one output vertex per invocation. It can be run as a plain
    /// vertex shader (one thread == one output vertex).
    /// </summary>
    PassThrough,

    /// <summary>
    /// The shader issues <c>GS_EMIT</c>/<c>GS_CUT</c>: it amplifies geometry.
    /// Running it as a plain vertex shader drops the amplification and renders
    /// garbage; a real geometry backend is required.
    /// </summary>
    Amplifying,
}

/// <summary>
/// Result of scanning a decoded ES primitive-shader program for the
/// <c>s_sendmsg</c> messages that distinguish a pass-through NGG draw (safe to
/// run as a plain vertex shader) from an amplifying one (not).
/// </summary>
internal readonly record struct NggEsGeometryClassification(
    NggEsGeometryMode Mode,
    int AllocReqCount,
    int EmitCount,
    int CutCount)
{
    public bool IsAmplifying => Mode == NggEsGeometryMode.Amplifying;

    /// <summary>
    /// Classifies an ES program from its decoded <c>s_sendmsg</c> payloads.
    /// Presence of any <c>GS_EMIT</c>/<c>GS_CUT</c> marks the shader
    /// amplifying; otherwise it is treated as pass-through.
    /// </summary>
    public static NggEsGeometryClassification FromSendMessages(
        IEnumerable<NggSendMessage> messages)
    {
        var allocReq = 0;
        var emit = 0;
        var cut = 0;
        foreach (var message in messages)
        {
            if (message.IsGsAllocReq)
            {
                allocReq++;
                continue;
            }

            if (!message.IsGeometryAmplification)
            {
                continue;
            }

            if (message.GsOperation is NggGsOperation.Emit or NggGsOperation.EmitCut)
            {
                emit++;
            }

            if (message.GsOperation is NggGsOperation.Cut or NggGsOperation.EmitCut)
            {
                cut++;
            }
        }

        var mode = emit + cut > 0
            ? NggEsGeometryMode.Amplifying
            : NggEsGeometryMode.PassThrough;
        return new NggEsGeometryClassification(mode, allocReq, emit, cut);
    }
}
