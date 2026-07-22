// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

// Contracts pinned by Astro Bot's SceSndzAudioOutMain thread (eboot
// 0x800EB2587/0x800EB3F4E port creation, 0x800EB3822+ state consumption) and
// KytyPS5's libAudio2: sceAudioOut2PortCreate is (context, const PortParam*,
// out u64*), with the port type u16 at param+0x00 and the data format u32 at
// param+0x04; sceAudioOut2PortGetState answers with that port's shape.
public sealed class AudioOut2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase;
    private const ulong OutPortAddress = MemoryBase + 0x100;
    private const ulong StateAddress = MemoryBase + 0x200;
    private const ulong InfoAddress = MemoryBase + 0x300;
    private const ulong OutContextAddress = MemoryBase + 0x380;
    private const ulong OutUserAddress = MemoryBase + 0x390;

    private static CpuContext CreateContext()
    {
        return new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
    }

    // Sndz audio-out is disabled by default (failing PortCreate parks Astro
    // Bot's SceSndzAudioOutMain thread away from its stomped mixer render);
    // these tests pin the enabled-path contract, so opt back in per call.
    private sealed class SndzEnabledScope : IDisposable
    {
        private const string Name = "SHARPEMU_DISABLE_SNDZ";
        private readonly string? _previous = Environment.GetEnvironmentVariable(Name);

        public SndzEnabledScope()
        {
            Environment.SetEnvironmentVariable(Name, "0");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Name, _previous);
        }
    }

    private static ulong CreatePort(CpuContext ctx, ushort portType, uint dataFormat)
    {
        using var scope = new SndzEnabledScope();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = OutUserAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2UserCreate(ctx));
        Assert.True(ctx.TryReadUInt64(OutUserAddress, out var user));

        Assert.True(ctx.Memory.TryWrite(ParamAddress, new byte[0x40]));
        Assert.True(ctx.TryWriteUInt32(ParamAddress + 0x00, 8));
        Assert.True(ctx.TryWriteUInt32(ParamAddress + 0x0C, 4));
        Assert.True(ctx.TryWriteUInt32(ParamAddress + 0x10, 512));
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x800;
        ctx[CpuRegister.Rdx] = 0x20000;
        ctx[CpuRegister.Rcx] = OutContextAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));
        Assert.True(ctx.TryReadUInt64(OutContextAddress, out var context));

        Span<byte> param = stackalloc byte[0x30];
        param.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(param, portType);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], dataFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x10..], user);
        Assert.True(ctx.Memory.TryWrite(ParamAddress, param));

        ctx[CpuRegister.Rdi] = context;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = OutPortAddress;
        ctx[CpuRegister.Rcx] = 0; // not an argument; must not be validated
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(ctx));

        Assert.True(ctx.TryReadUInt64(OutPortAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static byte[] GetState(CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = StateAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortGetState(ctx));

        var state = new byte[0x40];
        Assert.True(ctx.Memory.TryRead(StateAddress, state));
        return state;
    }

    [Fact]
    public void PortCreate_ReadsParamFromRsi_AndWritesHandleToRdx()
    {
        var ctx = CreateContext();
        var handle = CreatePort(ctx, portType: 0, dataFormat: 0x800);

        // Distinct ports must get distinct handles.
        var second = CreatePort(ctx, portType: 3, dataFormat: 0x100);
        Assert.NotEqual(handle, second);
    }

    [Fact]
    public void PortGetState_MainPort_ReportsChannelsFromDataFormat()
    {
        var ctx = CreateContext();
        var handle = CreatePort(ctx, portType: 0, dataFormat: 0x800);
        var state = GetState(ctx, handle);

        Assert.Equal(0x01, BinaryPrimitives.ReadUInt16LittleEndian(state)); // output: main
        Assert.Equal(8, state[0x02]);
        Assert.Equal(127, BinaryPrimitives.ReadInt16LittleEndian(state.AsSpan(0x04)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(0x08))); // flags
    }

    [Fact]
    public void PortGetState_PadSpeakerPort_ReportsPadSpeakerOutput()
    {
        var ctx = CreateContext();
        var handle = CreatePort(ctx, portType: 3, dataFormat: 0x100);
        var state = GetState(ctx, handle);

        // The Sndz main loop tests output bit 6 to detect the pad speaker.
        Assert.Equal(0x40, BinaryPrimitives.ReadUInt16LittleEndian(state));
        Assert.Equal(1, state[0x02]);
    }

    [Fact]
    public void PortGetState_ObjectPort_IsNotMistakenForPadSpeaker()
    {
        var ctx = CreateContext();
        // Object port bit (0x100) with main output in the low byte.
        var handle = CreatePort(ctx, portType: 0x100, dataFormat: 0x100);
        var state = GetState(ctx, handle);

        Assert.Equal(0x01, BinaryPrimitives.ReadUInt16LittleEndian(state));
        Assert.Equal(1, state[0x02]);
    }

    [Fact]
    public void PortGetState_UnknownHandle_ReturnsInvalidPort()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = 0xDEAD_BEEFUL;
        ctx[CpuRegister.Rsi] = StateAddress;

        Assert.Equal(unchecked((int)0x80268009), AudioOut2Exports.AudioOut2PortGetState(ctx));
    }

    [Fact]
    public void GetSpeakerInfo_KeepsStereoLayoutWithFourByteAngleStride()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = InfoAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2GetSpeakerInfo(ctx));

        var info = new byte[0x50];
        Assert.True(ctx.Memory.TryRead(InfoAddress, info));

        Assert.Equal(0, info[0x00]); // type: stereo
        Assert.Equal(0x3u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x04))); // FL|FR
        // The reconfig path (eboot 0x800DED210) reads i16 azimuths at
        // info+0x10 with a 4-byte stride: {i16 azimuth, i16 elevation}[16].
        Assert.Equal(-30, BinaryPrimitives.ReadInt16LittleEndian(info.AsSpan(0x10)));
        Assert.Equal(30, BinaryPrimitives.ReadInt16LittleEndian(info.AsSpan(0x14)));
    }
}
