// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // Sized from guest evidence, not SDK headers: titles keep the
    // SceAudioOut2ContextParam on the stack with the frame canary close behind
    // it (Quake's sits at param+0x60), and an oversized ResetParam write (the
    // earlier 0x80) zeroes that canary -> __stack_chk_fail kills audio init.
    // Stay well below 0x60 and only write the prefix we populate.
    private const int AudioOut2ContextParamSize = 0x30;
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const uint AudioOut2QueueCapacity = 4;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audioout2.{message}");
        }
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        Trace("initialize");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], AudioOut2ContextParamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 0x400);

        Trace($"context_reset_param param=0x{paramAddress:X}");
        return ctx.Memory.TryWrite(paramAddress, param)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var outMemorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || outMemorySizeAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller expects a single u64 (required memory size) written back
        // through rsi -- not a struct. The earlier 0x20-byte write overran the
        // caller's slot and smashed whatever followed it.
        Trace($"context_query_memory param=0x{paramAddress:X} size=0x{AudioOut2ContextMemorySize:X}");
        return ctx.TryWriteUInt64(outMemorySizeAddress, AudioOut2ContextMemorySize)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Trace($"context_create param=0x{paramAddress:X} mem=0x{memoryAddress:X} size=0x{memorySize:X} -> handle={handle}");
        return ctx.TryWriteUInt64(outContextAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        Trace($"context_destroy handle={ctx[CpuRegister.Rdi]}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var type = unchecked((int)ctx[CpuRegister.Rdi]);
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        var contextAddress = ctx[CpuRegister.Rcx];
        if (type < 0 || type > 255 || paramAddress == 0 || outPortAddress == 0 || contextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)(uint)type << 16) | portId;
        Trace($"port_create type={type} param=0x{paramAddress:X} context={contextAddress} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outPortAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var type = (int)((handle >> 16) & 0xFF);
        Span<byte> state = stackalloc byte[0x20];
        state.Clear();
        var output = type == 2 ? 0x40 : 0x01;
        var channels = type == 2 ? 1 : 2;
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        Trace($"port_get_state handle=0x{handle:X} type={type} output=0x{output:X} channels={channels}");
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[0x40];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 48000);

        Trace("get_speaker_info");
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        Trace($"port_destroy handle=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx)
    {
        Trace($"user_destroy handle={ctx[CpuRegister.Rdi]}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 255) || outUserAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        Trace($"user_create userId={userId} -> handle={handle}");
        return ctx.TryWriteUInt64(outUserAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        var portHandle = ctx[CpuRegister.Rdi];
        if (portHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Trace($"port_set_attributes handle=0x{portHandle:X} arg1=0x{ctx[CpuRegister.Rsi]:X} arg2=0x{ctx[CpuRegister.Rdx]:X} arg3=0x{ctx[CpuRegister.Rcx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Trace($"context_push handle={contextHandle} arg1=0x{ctx[CpuRegister.Rsi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var outLevelAddress = ctx[CpuRegister.Rsi];
        var outCapacityAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 || outLevelAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Real hardware drains this queue via DMA essentially instantly from
        // the CPU's perspective; reporting it as always-empty (never full)
        // matched observed behavior better than tracking a real depth here.
        if (!ctx.TryWriteUInt32(outLevelAddress, 0))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (outCapacityAddress != 0 && !ctx.TryWriteUInt32(outCapacityAddress, AudioOut2QueueCapacity))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Trace($"context_get_queue_level handle={contextHandle} -> level=0 capacity={AudioOut2QueueCapacity}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Trace($"context_advance handle={contextHandle}");
        return ctx.SetReturn(0);
    }
}
