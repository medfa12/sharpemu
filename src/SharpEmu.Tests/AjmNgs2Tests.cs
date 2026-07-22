// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ajm;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AjmNgs2Tests
{
    private const int AjmErrorMalformedBatch = unchecked((int)0x80930011);
    private const int Ngs2InvalidGrainSamples = unchecked((int)0x804A0051);
    private const int Ngs2InvalidSampleRate = unchecked((int)0x804A0201);
    private const int Ngs2InvalidBufferAddress = unchecked((int)0x804A0207);
    private const int Ngs2InvalidVoiceHandle = unchecked((int)0x804A0300);
    private const int Ngs2InvalidGeomFlag = unchecked((int)0x804A0923);

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static uint CreateAjmLpcmInstance(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1000;
        AjmExports.Initialize(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x1000, out var contextId));

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = 23;
        ctx[CpuRegister.Rdx] = 0;
        AjmExports.ModuleRegister(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = 23;
        ctx[CpuRegister.Rdx] = 2;
        ctx[CpuRegister.Rcx] = 0x1010;
        AjmExports.InstanceCreate(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x1010, out var instanceId));
        return instanceId;
    }

    [Fact]
    public void AjmBatchStartBuffer_ParsesLpcmJobAndPacksStreamSideband()
    {
        var ctx = NewContext(out var memory);
        var instanceId = CreateAjmLpcmInstance(ctx);
        Assert.True(ctx.TryReadUInt32(0x1000, out var contextId));

        byte[] pcm = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        Assert.True(memory.TryWrite(0x2000, pcm));

        var batch = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(batch, instanceId << 6);
        BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(4), 56);
        WriteBufferChunk(batch.AsSpan(8), 1, pcm.Length, 0x2000);
        var flags = 1UL << 47;
        BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(24), 4u | ((uint)(flags >> 32) << 6));
        BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(28), (uint)flags);
        WriteBufferChunk(batch.AsSpan(32), 17, pcm.Length, 0x3000);
        WriteBufferChunk(batch.AsSpan(48), 18, 24, 0x4000);
        Assert.True(memory.TryWrite(0x5000, batch));

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = 0x5000;
        ctx[CpuRegister.Rdx] = (uint)batch.Length;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0x6000;
        ctx[CpuRegister.R9] = 0x6100;
        AjmExports.BatchStartBuffer(ctx);

        Assert.Equal(0, Result(ctx));
        var output = new byte[pcm.Length];
        Assert.True(memory.TryRead(0x3000, output));
        Assert.Equal(pcm, output);
        Assert.True(ctx.TryReadInt32(0x4000, out var sidebandResult));
        Assert.Equal(0, sidebandResult);
        Assert.True(ctx.TryReadInt32(0x4008, out var consumed));
        Assert.Equal(pcm.Length, consumed);
        Assert.True(ctx.TryReadInt32(0x400C, out var produced));
        Assert.Equal(pcm.Length, produced);
        Assert.True(ctx.TryReadUInt64(0x4010, out var decodedSamples));
        Assert.Equal(2UL, decodedSamples);
        Assert.True(ctx.TryReadUInt32(0x6100, out var batchId));
        Assert.NotEqual(0u, batchId);
    }

    [Fact]
    public void AjmBatchStartBuffer_RejectsJobWithoutFlags()
    {
        var ctx = NewContext(out var memory);
        _ = CreateAjmLpcmInstance(ctx);
        Assert.True(ctx.TryReadUInt32(0x1000, out var contextId));

        var batch = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(batch, 0);
        Assert.True(memory.TryWrite(0x5000, batch));
        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = 0x5000;
        ctx[CpuRegister.Rdx] = (uint)batch.Length;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0x6000;
        ctx[CpuRegister.R9] = 0x6100;
        AjmExports.BatchStartBuffer(ctx);

        Assert.Equal(AjmErrorMalformedBatch, Result(ctx));
        Assert.True(ctx.TryReadInt32(0x6000, out var batchError));
        Assert.Equal(AjmErrorMalformedBatch, batchError);
    }

    [Fact]
    public void Ngs2VoiceControl_TransitionsSetupPlayAndStopAcrossRenders()
    {
        var memory = new AllocatingGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var system = CreateNgs2System(ctx, memory);
        var rack = CreateNgs2Rack(ctx, memory, system);
        var voice = GetNgs2Voice(ctx, rack);

        WriteVoiceParameter(memory, 0x5000, 0x28, 0, 0x1000_0000, 0x12);
        Assert.True(ctx.TryWriteUInt32(0x500C, 2));
        Assert.True(ctx.TryWriteUInt32(0x5010, 48000));
        ctx[CpuRegister.Rdi] = voice;
        ctx[CpuRegister.Rsi] = 0x5000;
        Ngs2Exports.Ngs2VoiceControl(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(1u, GetVoiceFlags(ctx, voice));

        WriteVoiceParameter(memory, 0x5100, 12, 0, 6, 1);
        ctx[CpuRegister.Rdi] = voice;
        ctx[CpuRegister.Rsi] = 0x5100;
        Ngs2Exports.Ngs2VoiceControl(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(1u, GetVoiceFlags(ctx, voice));

        Assert.True(ctx.TryWriteUInt64(0x6000, 0x7000));
        Assert.True(ctx.TryWriteUInt64(0x6008, 0x400));
        Assert.True(ctx.TryWriteUInt32(0x6010, 0x12));
        Assert.True(ctx.TryWriteUInt32(0x6014, 2));
        Assert.True(memory.TryWrite(0x7000, Enumerable.Repeat((byte)0xCC, 0x400).ToArray()));
        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 1;
        Ngs2Exports.Ngs2SystemRender(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(3u, GetVoiceFlags(ctx, voice));

        var rendered = new byte[0x400];
        Assert.True(memory.TryRead(0x7000, rendered));
        Assert.All(rendered, value => Assert.Equal(0, value));
        ctx[CpuRegister.Rdi] = voice;
        ctx[CpuRegister.Rsi] = 0x7200;
        ctx[CpuRegister.Rdx] = 8;
        Ngs2Exports.Ngs2VoiceGetState(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x7200, out var stateFlags));
        Assert.Equal(3u, stateFlags);
        Assert.True(ctx.TryReadUInt32(0x7204, out var stateError));
        Assert.Equal(0u, stateError);

        WriteVoiceParameter(memory, 0x5100, 12, 0, 6, 2);
        ctx[CpuRegister.Rdi] = voice;
        ctx[CpuRegister.Rsi] = 0x5100;
        Ngs2Exports.Ngs2VoiceControl(ctx);
        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = 1;
        Ngs2Exports.Ngs2SystemRender(ctx);
        Assert.Equal(9u, GetVoiceFlags(ctx, voice));
    }

    [Fact]
    public void Ngs2CompatibilityOptionsAndSystemInfo_MatchFirmwareLayout()
    {
        var memory = new AllocatingGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 0x8000;
        Ngs2Exports.Ngs2SystemResetOption(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x8000, out var optionSize));
        Assert.Equal(0x40UL, optionSize);
        Assert.True(ctx.TryReadUInt32(0x801C, out var maximumGrain));
        Assert.Equal(512u, maximumGrain);
        Assert.True(ctx.TryReadUInt32(0x8020, out var grain));
        Assert.Equal(256u, grain);
        Assert.True(ctx.TryReadUInt32(0x8024, out var sampleRate));
        Assert.Equal(48000u, sampleRate);

        var system = CreateNgs2System(ctx, memory);
        _ = CreateNgs2Rack(ctx, memory, system);
        Assert.True(memory.TryWrite(0x9000, new byte[0x88]));
        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 0x9000;
        ctx[CpuRegister.Rdx] = 0x88;
        Ngs2Exports.Ngs2SystemGetInfo(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x905C, out var minimumGrain));
        Assert.Equal(64u, minimumGrain);
        Assert.True(ctx.TryReadUInt32(0x9064, out var systemFlags));
        Assert.Equal(1u, systemFlags);
        Assert.True(ctx.TryReadUInt32(0x9068, out var rackCount));
        Assert.Equal(1u, rackCount);
    }

    [Fact]
    public void Ngs2SettersAndRender_UseDiscreteFirmwareValidation()
    {
        var memory = new AllocatingGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var system = CreateNgs2System(ctx, memory);

        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 65;
        Ngs2Exports.Ngs2SystemSetGrainSamples(ctx);
        Assert.Equal(Ngs2InvalidGrainSamples, Result(ctx));
        ctx[CpuRegister.Rsi] = 64;
        Ngs2Exports.Ngs2SystemSetGrainSamples(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 32000;
        Ngs2Exports.Ngs2SystemSetSampleRate(ctx);
        Assert.Equal(Ngs2InvalidSampleRate, Result(ctx));
        ctx[CpuRegister.Rsi] = 44100;
        Ngs2Exports.Ngs2SystemSetSampleRate(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        Ngs2Exports.Ngs2SystemRender(ctx);
        Assert.Equal(Ngs2InvalidBufferAddress, Result(ctx));
    }

    [Fact]
    public void Ngs2VoiceFlagsAndGeomApply_ReturnOracleErrorsAndWrites()
    {
        var memory = new AllocatingGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(0xA000, uint.MaxValue));
        ctx[CpuRegister.Rdi] = 0xDEAD;
        ctx[CpuRegister.Rsi] = 0xA000;
        Ngs2Exports.Ngs2VoiceGetStateFlags(ctx);
        Assert.Equal(Ngs2InvalidVoiceHandle, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0xA000, out var invalidFlags));
        Assert.Equal(0u, invalidFlags);

        Assert.True(memory.TryWrite(0xB000, new byte[0x60]));
        Assert.True(memory.TryWrite(0xB100, new byte[0x68]));
        Assert.True(memory.TryWrite(0xB200, new byte[0x134]));
        ctx[CpuRegister.Rdi] = 0xB000;
        ctx[CpuRegister.Rsi] = 0xB100;
        ctx[CpuRegister.Rdx] = 0xB200;
        ctx[CpuRegister.Rcx] = 0;
        Ngs2Exports.Ngs2GeomApply(ctx);
        Assert.Equal(Ngs2InvalidGeomFlag, Result(ctx));
    }

    private static ulong CreateNgs2System(CpuContext ctx, ICpuMemory memory)
    {
        var option = new byte[0x40];
        BinaryPrimitives.WriteUInt64LittleEndian(option, 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x1C), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x20), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x24), 48000);
        Assert.True(memory.TryWrite(0x1000, option));
        Assert.True(ctx.TryWriteUInt64(0x2000, 1));
        Assert.True(ctx.TryWriteUInt64(0x2008, 2));
        Assert.True(ctx.TryWriteUInt64(0x2010, 0));
        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = 0x2000;
        ctx[CpuRegister.Rdx] = 0x2100;
        Ngs2Exports.Ngs2SystemCreateWithAllocator(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x2100, out var system));
        return system;
    }

    private static ulong CreateNgs2Rack(CpuContext ctx, ICpuMemory memory, ulong system)
    {
        var option = new byte[0xA4];
        BinaryPrimitives.WriteUInt64LittleEndian(option, 0xA4);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x1C), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(option.AsSpan(0x2C), 8);
        Assert.True(memory.TryWrite(0x3000, option));
        Assert.True(ctx.TryWriteUInt64(0x4000, 1));
        Assert.True(ctx.TryWriteUInt64(0x4008, 2));
        Assert.True(ctx.TryWriteUInt64(0x4010, 0));
        ctx[CpuRegister.Rdi] = system;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 0x3000;
        ctx[CpuRegister.Rcx] = 0x4000;
        ctx[CpuRegister.R8] = 0x4100;
        Ngs2Exports.Ngs2RackCreateWithAllocator(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x4100, out var rack));
        return rack;
    }

    private static ulong GetNgs2Voice(CpuContext ctx, ulong rack)
    {
        ctx[CpuRegister.Rdi] = rack;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0x4200;
        Ngs2Exports.Ngs2RackGetVoiceHandle(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(0x4200, out var voice));
        return voice;
    }

    private static uint GetVoiceFlags(CpuContext ctx, ulong voice)
    {
        ctx[CpuRegister.Rdi] = voice;
        ctx[CpuRegister.Rsi] = 0x4300;
        Ngs2Exports.Ngs2VoiceGetStateFlags(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(0x4300, out var flags));
        return flags;
    }

    private static void WriteBufferChunk(Span<byte> chunk, uint id, int size, ulong address)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, id);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], (uint)size);
        BinaryPrimitives.WriteUInt64LittleEndian(chunk[8..], address);
    }

    private static void WriteVoiceParameter(
        ICpuMemory memory,
        ulong address,
        ushort size,
        short next,
        uint id,
        uint value)
    {
        Span<byte> parameter = stackalloc byte[size];
        parameter.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(parameter, size);
        BinaryPrimitives.WriteInt16LittleEndian(parameter[2..], next);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[4..], id);
        if (size >= 12)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(parameter[8..], value);
        }
        Assert.True(memory.TryWrite(address, parameter));
    }

    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly Dictionary<ulong, byte> _bytes = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(virtualAddress + (ulong)index, out destination[index]))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
            {
                _bytes[virtualAddress + (ulong)index] = source[index];
            }
            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) =>
            desiredAddress;

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = desiredAddress;
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }
    }
}
