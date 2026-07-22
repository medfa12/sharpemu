// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Audio;

public static class AudioInExports
{
    private const int InvalidHandle = unchecked((int)0x80260101);
    private const int InvalidSize = unchecked((int)0x80260102);
    private const int InvalidFrequency = unchecked((int)0x80260103);
    private const int InvalidPointer = unchecked((int)0x80260105);
    private const int InvalidParameter = unchecked((int)0x80260106);
    private const int PortFull = unchecked((int)0x80260107);
    private const int NotOpened = unchecked((int)0x80260109);
    private const int MaximumPorts = 7;
    private static readonly object PortGate = new();
    private static readonly ConcurrentDictionary<int, InputPort> Ports = new();
    private static readonly ConcurrentDictionary<int, byte> VirtualMicrophones = new();
    private static int _nextVirtualMicrophone;
    private static int _initialized;
    private static uint _rerouteCount;

    private sealed record InputPort(uint Type, uint Frames, uint Frequency, uint Channels)
    {
        public int ByteLength => checked((int)(Frames * Channels * 2));
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);

    private static int Open(CpuContext ctx)
    {
        var type = unchecked((uint)ctx[CpuRegister.Rsi]);
        var frames = unchecked((uint)ctx[CpuRegister.Rcx]);
        var frequency = unchecked((uint)ctx[CpuRegister.R8]);
        var format = unchecked((uint)ctx[CpuRegister.R9]);
        if (frames == 0 || frames > 2048) return ctx.SetReturn(InvalidSize);
        if (frequency is not (16000 or 48000)) return ctx.SetReturn(InvalidFrequency);
        if (format is not (0 or 2)) return ctx.SetReturn(InvalidParameter);

        Volatile.Write(ref _initialized, 1);
        lock (PortGate)
        {
            for (var index = 0; index < MaximumPorts; index++)
            {
                var handle = unchecked((int)(0x30000000u | (type << 16) | (uint)index));
                if (Ports.TryAdd(handle, new InputPort(type, frames, frequency, format == 0 ? 1u : 2u)))
                {
                    return ctx.SetReturn(handle);
                }
            }
        }
        return ctx.SetReturn(PortFull);
    }

    private static bool IsFormattedHandle(int handle) =>
        (unchecked((uint)handle) & 0x7F000000u) == 0x30000000u &&
        (unchecked((uint)handle) & 0xFFu) < MaximumPorts;

    private static int Input(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var destination = ctx[CpuRegister.Rsi];
        if (!IsFormattedHandle(handle)) return ctx.SetReturn(InvalidHandle);
        if (destination == 0) return ctx.SetReturn(InvalidPointer);
        if (!Ports.TryGetValue(handle, out var port)) return ctx.SetReturn(NotOpened);
        return ctx.Memory.TryWrite(destination, new byte[port.ByteLength])
            ? ctx.SetReturn(unchecked((int)port.Frames))
            : ctx.SetReturn(InvalidPointer);
    }

    [SysAbiExport(Nid = "IQtWgnrw6v8", ExportName = "sceAudioInChangeAppModuleState", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInChangeAppModuleState(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Jh6WbHhnI68", ExportName = "sceAudioInClose", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsFormattedHandle(handle)) return ctx.SetReturn(InvalidHandle);
        return ctx.SetReturn(Ports.TryRemove(handle, out _) ? 0 : NotOpened);
    }
    [SysAbiExport(Nid = "8mtcsG-Qp5E", ExportName = "sceAudioInCountPorts", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInCountPorts(CpuContext ctx) => ctx.SetReturn(Ports.Count);
    [SysAbiExport(Nid = "5qRVfxOmbno", ExportName = "sceAudioInDeviceHqOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInDeviceHqOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "gUNabrUkZNg", ExportName = "sceAudioInDeviceIdHqOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInDeviceIdHqOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "X-AQLtdxQOo", ExportName = "sceAudioInDeviceIdOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInDeviceIdOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "VoX9InuwwTg", ExportName = "sceAudioInDeviceOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInDeviceOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "48-miagyJ2I", ExportName = "sceAudioInDeviceOpenEx", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInDeviceOpenEx(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "kFKJ3MVcDuo", ExportName = "sceAudioInExtClose", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInExtClose(CpuContext ctx) => sceAudioInClose(ctx);
    [SysAbiExport(Nid = "mhAfefP9m2g", ExportName = "sceAudioInExtCtrl", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInExtCtrl(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "KpBKoHKVKEc", ExportName = "sceAudioInExtInput", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInExtInput(CpuContext ctx) => Input(ctx);
    [SysAbiExport(Nid = "YZ+3seW7CyY", ExportName = "sceAudioInExtOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInExtOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "FVGWf8JaHOE", ExportName = "sceAudioInExtSetAecMode", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInExtSetAecMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "S-rDUfQk9sg", ExportName = "sceAudioInGetGain", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInGetGain(CpuContext ctx) => WriteOptional(ctx, ctx[CpuRegister.Rsi], 0);
    [SysAbiExport(Nid = "NJam1-F7lNY", ExportName = "sceAudioInGetHandleStatusInfo", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInGetHandleStatusInfo(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Ports.TryGetValue(handle, out var port)) return ctx.SetReturn(IsFormattedHandle(handle) ? NotOpened : InvalidHandle);
        var output = ctx[CpuRegister.Rsi];
        if (output == 0) return ctx.SetReturn(InvalidPointer);
        Span<byte> info = stackalloc byte[0x20];
        info.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info, port.Type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info[4..], port.Frames);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info[8..], port.Frequency);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(info[12..], port.Channels);
        return ctx.Memory.TryWrite(output, info) ? ctx.SetReturn(0) : ctx.SetReturn(InvalidPointer);
    }
    [SysAbiExport(Nid = "3shKmTrTw6c", ExportName = "sceAudioInGetRerouteCount", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInGetRerouteCount(CpuContext ctx) => WriteOptional(ctx, ctx[CpuRegister.Rsi], Volatile.Read(ref _rerouteCount));
    [SysAbiExport(Nid = "BohEAQ7DlUE", ExportName = "sceAudioInGetSilentState", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInGetSilentState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(Ports.ContainsKey(handle) ? 1 : IsFormattedHandle(handle) ? NotOpened : InvalidHandle);
    }
    [SysAbiExport(Nid = "nya-R5gDYhM", ExportName = "sceAudioInHqOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInHqOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "CTh72m+IYbU", ExportName = "sceAudioInHqOpenEx", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInHqOpenEx(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "SxQprgjttKE", ExportName = "sceAudioInInit", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInInit(CpuContext ctx) { Volatile.Write(ref _initialized, 1); return Ok(ctx); }
    [SysAbiExport(Nid = "LozEOU8+anM", ExportName = "sceAudioInInput", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInInput(CpuContext ctx) => Input(ctx);
    [SysAbiExport(Nid = "rmgXsZ-2Tyk", ExportName = "sceAudioInInputs", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInInputs(CpuContext ctx)
    {
        var entries = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (entries == 0 || count == 0 || count > MaximumPorts) return ctx.SetReturn(InvalidParameter);
        for (uint i = 0; i < count; i++)
        {
            if (!ctx.TryReadUInt32(entries + i * 16, out var handle) || !ctx.TryReadUInt64(entries + i * 16 + 8, out var pointer)) return ctx.SetReturn(InvalidPointer);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = pointer;
            var result = Input(ctx);
            if (result < 0) return result;
        }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "6QP1MzdFWhs", ExportName = "sceAudioInIsSharedDevice", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInIsSharedDevice(CpuContext ctx) => ctx.SetReturn(0);
    [SysAbiExport(Nid = "5NE8Sjc7VC8", ExportName = "sceAudioInOpen", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInOpen(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "+DY07NwJb0s", ExportName = "sceAudioInOpenEx", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInOpenEx(CpuContext ctx) => Open(ctx);
    [SysAbiExport(Nid = "vYFsze1SqU8", ExportName = "sceAudioInSetAllMute", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetAllMute(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "vyh-T6sMqnw", ExportName = "sceAudioInSetCompressorPreGain", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetCompressorPreGain(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "YeBSNVAELe4", ExportName = "sceAudioInSetConnections", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetConnections(CpuContext ctx) { Interlocked.Increment(ref _rerouteCount); return Ok(ctx); }
    [SysAbiExport(Nid = "thLNHvkWSeg", ExportName = "sceAudioInSetConnectionsForUser", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetConnectionsForUser(CpuContext ctx) { Interlocked.Increment(ref _rerouteCount); return Ok(ctx); }
    [SysAbiExport(Nid = "rcgv2ciDrtc", ExportName = "sceAudioInSetDevConnection", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetDevConnection(CpuContext ctx) { Interlocked.Increment(ref _rerouteCount); return Ok(ctx); }
    [SysAbiExport(Nid = "iN3KqF-8R-w", ExportName = "sceAudioInSetFocusForUser", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetFocusForUser(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "VAzfxqDwbQ0", ExportName = "sceAudioInSetMode", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "CwBFvAlOv7k", ExportName = "sceAudioInSetMode2", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetMode2(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "tQpOPpYwv7o", ExportName = "sceAudioInSetPortConnections", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetPortConnections(CpuContext ctx) { Interlocked.Increment(ref _rerouteCount); return Ok(ctx); }
    [SysAbiExport(Nid = "NUWqWguYcNQ", ExportName = "sceAudioInSetPortStatuses", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetPortStatuses(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "U0ivfdKFZbA", ExportName = "sceAudioInSetSparkParam", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetSparkParam(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "hWMCAPpqzDo", ExportName = "sceAudioInSetSparkSideTone", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetSparkSideTone(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "nqXpw3MaN50", ExportName = "sceAudioInSetUsbGain", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetUsbGain(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "arJp991xk5k", ExportName = "sceAudioInSetUserMute", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInSetUserMute(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "DVTn+iMSpBM", ExportName = "sceAudioInVmicCreate", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInVmicCreate(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextVirtualMicrophone);
        VirtualMicrophones[handle] = 0;
        return ctx.SetReturn(handle);
    }
    [SysAbiExport(Nid = "3ULZGIl+Acc", ExportName = "sceAudioInVmicDestroy", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInVmicDestroy(CpuContext ctx) => ctx.SetReturn(VirtualMicrophones.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _) ? 0 : InvalidHandle);
    [SysAbiExport(Nid = "4kHw99LUG3A", ExportName = "sceAudioInVmicWrite", Target = Generation.Gen5, LibraryName = "libSceAudioIn")]
    public static int sceAudioInVmicWrite(CpuContext ctx) => ctx.SetReturn(VirtualMicrophones.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : InvalidHandle);

    private static int WriteOptional(CpuContext ctx, ulong address, uint value)
    {
        return address == 0 || ctx.TryWriteUInt32(address, value) ? ctx.SetReturn(0) : ctx.SetReturn(InvalidPointer);
    }

    internal static void ResetForTests()
    {
        Ports.Clear();
        VirtualMicrophones.Clear();
        _nextVirtualMicrophone = 0;
        _rerouteCount = 0;
        _initialized = 0;
    }
}
