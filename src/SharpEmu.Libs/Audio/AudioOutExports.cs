// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Audio;

public static class AudioOutExports
{
    private static readonly ConcurrentDictionary<int, PortState> Ports = new();
    private static readonly ConcurrentDictionary<int, byte> PassThroughPorts = new();
    private static int _nextPortHandle;
    private static int _nextPassThroughHandle = 0x1000;
    private static int _masteringInitialized;

    private sealed class PortState : IDisposable
    {
        private readonly object _paceGate = new();
        private long _nextSilentOutput;

        public PortState(
            int userId,
            int type,
            uint bufferLength,
            uint frequency,
            int format,
            int channels,
            int bytesPerSample,
            bool isFloat,
            WinMmAudioPort? backend)
        {
            UserId = userId;
            Type = type;
            BufferLength = bufferLength;
            Frequency = frequency;
            Format = format;
            Channels = channels;
            BytesPerSample = bytesPerSample;
            IsFloat = isFloat;
            Backend = backend;
        }

        public int UserId { get; }
        public int Type { get; }
        public uint BufferLength { get; }
        public uint Frequency { get; }
        public int Format { get; }
        public int Channels { get; }
        public int BytesPerSample { get; }
        public bool IsFloat { get; }
        public WinMmAudioPort? Backend { get; }
        public int BufferByteLength =>
            checked((int)BufferLength * Channels * BytesPerSample);

        public void PaceSilence()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextSilentOutput < now)
                {
                    _nextSilentOutput = now;
                }

                delay = _nextSilentOutput - now;
                _nextSilentOutput += checked(
                    (long)Math.Ceiling(
                        Stopwatch.Frequency * (double)BufferLength / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }

        public void Dispose() => Backend?.Dispose();
    }

    [SysAbiExport(
        Nid = "JfEPXVxhFqA",
        ExportName = "sceAudioOutInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "ekNvsT22rsY",
        ExportName = "sceAudioOutOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var bufferLength = unchecked((uint)ctx[CpuRegister.Rcx]);
        var frequency = unchecked((uint)ctx[CpuRegister.R8]);
        var format = unchecked((int)ctx[CpuRegister.R9]);
        if (bufferLength == 0 || frequency == 0 ||
            !TryGetFormat(format, out var channels, out var bytesPerSample, out var isFloat))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        WinMmAudioPort? backend = null;
        string backendName;
        try
        {
            backend = new WinMmAudioPort(frequency);
            backendName = "winmm";
        }
        catch (Exception exception)
        {
            backendName = "silent";
            Console.Error.WriteLine(
                $"[LOADER][WARN] AudioOut host backend unavailable: {exception.Message}");
        }

        var handle = Interlocked.Increment(ref _nextPortHandle);
        Ports[handle] = new PortState(
            userId,
            type,
            bufferLength,
            frequency,
            format,
            channels,
            bytesPerSample,
            isFloat,
            backend);
        Console.Error.WriteLine(
            $"[LOADER][INFO] AudioOut port {handle}: {frequency} Hz, " +
            $"{channels} ch, {(isFloat ? "float32" : "s16")}, " +
            $"{bufferLength} frames, backend={backendName}");
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "s1--uE9mBFw",
        ExportName = "sceAudioOutClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Ports.TryRemove(handle, out var port))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        port.Dispose();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "QOQtbeDqsT4",
        ExportName = "sceAudioOutOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOutput(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (!Ports.TryGetValue(handle, out var port))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (sourceAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(port.BufferByteLength);
        try
        {
            var source = buffer.AsSpan(0, port.BufferByteLength);
            if (!ctx.Memory.TryRead(sourceAddress, source))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (port.Backend is null ||
                !port.Backend.Submit(
                    source,
                    port.BufferLength,
                    port.Channels,
                    port.BytesPerSample,
                    port.IsFloat))
            {
                port.PaceSilence();
            }

            return ctx.SetReturn(0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [SysAbiExport(
        Nid = "b+uAV89IlxE",
        ExportName = "sceAudioOutSetVolume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutSetVolume(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(
            Ports.ContainsKey(handle)
                ? 0
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    private static int Ok(CpuContext ctx) => ctx.SetReturn(0);

    private static int OpenPassThrough(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextPassThroughHandle);
        PassThroughPorts[handle] = 0;
        return ctx.SetReturn(handle);
    }

    private static int ClosePassThrough(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(PassThroughPorts.TryRemove(handle, out _)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    private static int GetPassThroughTime(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var output = ctx[CpuRegister.Rsi];
        if (!PassThroughPorts.ContainsKey(handle) || output == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var micros = (ulong)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency);
        return ctx.TryWriteUInt64(output, micros)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "Iz9X7ISldhs", ExportName = "sceAudioOutA3dControl", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutA3dControl(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "9RVIoocOVAo", ExportName = "sceAudioOutA3dExit", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutA3dExit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "n7KgxE8rOuE", ExportName = "sceAudioOutA3dInit", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutA3dInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "WBAO6-n0-4M", ExportName = "sceAudioOutAttachToApplicationByPid", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutAttachToApplicationByPid(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "O3FM2WXIJaI", ExportName = "sceAudioOutChangeAppModuleState", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutChangeAppModuleState(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ol4LbeTG8mc", ExportName = "sceAudioOutDetachFromApplicationByPid", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutDetachFromApplicationByPid(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "r1V9IFEE+Ts", ExportName = "sceAudioOutExConfigureOutputMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExConfigureOutputMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "wZakRQsWGos", ExportName = "sceAudioOutExGetSystemInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExGetSystemInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], 0x20);
    [SysAbiExport(Nid = "xjjhT5uw08o", ExportName = "sceAudioOutExPtClose", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExPtClose(CpuContext ctx) => ClosePassThrough(ctx);
    [SysAbiExport(Nid = "DsST7TNsyfo", ExportName = "sceAudioOutExPtGetLastOutputTime", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExPtGetLastOutputTime(CpuContext ctx) => GetPassThroughTime(ctx);
    [SysAbiExport(Nid = "4UlW3CSuCa4", ExportName = "sceAudioOutExPtOpen", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExPtOpen(CpuContext ctx) => OpenPassThrough(ctx);
    [SysAbiExport(Nid = "Xcj8VTtnZw0", ExportName = "sceAudioOutExSystemInfoIsSupportedAudioOutExMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutExSystemInfoIsSupportedAudioOutExMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "I3Fwcmkg5Po", ExportName = "sceAudioOutGetFocusEnablePid", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetFocusEnablePid(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 0);
    [SysAbiExport(Nid = "Y3lXfCFEWFY", ExportName = "sceAudioOutGetHandleStatusInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetHandleStatusInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rsi], 0x20);
    [SysAbiExport(Nid = "-00OAutAw+c", ExportName = "sceAudioOutGetInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rsi], 0x20);
    [SysAbiExport(Nid = "RqmKxBqB8B4", ExportName = "sceAudioOutGetInfoOpenNum", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetInfoOpenNum(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], (uint)Ports.Count);
    [SysAbiExport(Nid = "Ptlts326pds", ExportName = "sceAudioOutGetLastOutputTime", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetLastOutputTime(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var output = ctx[CpuRegister.Rsi];
        if (!Ports.ContainsKey(handle) || output == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        var micros = (ulong)(Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency);
        return ctx.TryWriteUInt64(output, micros) ? ctx.SetReturn(0) : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    [SysAbiExport(Nid = "GrQ9s4IrNaQ", ExportName = "sceAudioOutGetPortState", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetPortState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var output = ctx[CpuRegister.Rsi];
        if (!Ports.TryGetValue(handle, out var port) || output == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        Span<byte> state = stackalloc byte[0x20];
        state.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(state, port.Type is 2 or 3 ? (ushort)2 : port.Type == 4 ? (ushort)4 : (ushort)1);
        state[2] = (byte)Math.Min(port.Channels, 2);
        BinaryPrimitives.WriteInt16LittleEndian(state[4..], port.Type == 4 ? (short)127 : (short)-1);
        return ctx.Memory.TryWrite(output, state) ? ctx.SetReturn(0) : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    [SysAbiExport(Nid = "c7mVozxJkPU", ExportName = "sceAudioOutGetSimulatedBusUsableStatusByBusType", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetSimulatedBusUsableStatusByBusType(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 1);
    [SysAbiExport(Nid = "pWmS7LajYlo", ExportName = "sceAudioOutGetSimulatedHandleStatusInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetSimulatedHandleStatusInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rsi], 0x20);
    [SysAbiExport(Nid = "oPLghhAWgMM", ExportName = "sceAudioOutGetSimulatedHandleStatusInfo2", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetSimulatedHandleStatusInfo2(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rsi], 0x20);
    [SysAbiExport(Nid = "5+r7JYHpkXg", ExportName = "sceAudioOutGetSparkVss", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetSparkVss(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 0);
    [SysAbiExport(Nid = "R5hemoKKID8", ExportName = "sceAudioOutGetSystemState", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutGetSystemState(CpuContext ctx) => ClearRequired(ctx, ctx[CpuRegister.Rdi], 0x20);
    [SysAbiExport(Nid = "n16Kdoxnvl0", ExportName = "sceAudioOutInitIpmiGetSession", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutInitIpmiGetSession(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 1);
    [SysAbiExport(Nid = "r+qKw+ueD+Q", ExportName = "sceAudioOutMasteringGetState", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutMasteringGetState(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], (uint)Volatile.Read(ref _masteringInitialized));
    [SysAbiExport(Nid = "xX4RLegarbg", ExportName = "sceAudioOutMasteringInit", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutMasteringInit(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] != 0) return ctx.SetReturn(unchecked((int)0x80260201));
        Volatile.Write(ref _masteringInitialized, 1);
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "4055yaUg3EY", ExportName = "sceAudioOutMasteringSetParam", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutMasteringSetParam(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "RVWtUgoif5o", ExportName = "sceAudioOutMasteringTerm", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutMasteringTerm(CpuContext ctx) { Volatile.Write(ref _masteringInitialized, 0); return Ok(ctx); }
    [SysAbiExport(Nid = "-LXhcGARw3k", ExportName = "sceAudioOutMbusInit", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutMbusInit(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "qLpSK75lXI4", ExportName = "sceAudioOutOpenEx", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutOpenEx(CpuContext ctx) => AudioOutOpen(ctx);
    [SysAbiExport(Nid = "w3PdaSTSwGE", ExportName = "sceAudioOutOutputs", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutOutputs(CpuContext ctx)
    {
        var entries = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (entries == 0 || count == 0 || count > 25) return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        for (uint i = 0; i < count; i++)
        {
            if (!ctx.TryReadUInt32(entries + i * 16, out var handle) || !ctx.TryReadUInt64(entries + i * 16 + 8, out var pointer))
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = pointer;
            var result = AudioOutOutput(ctx);
            if (result < 0) return result;
        }
        return Ok(ctx);
    }
    [SysAbiExport(Nid = "MapHTgeogbk", ExportName = "sceAudioOutPtClose", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutPtClose(CpuContext ctx) => ClosePassThrough(ctx);
    [SysAbiExport(Nid = "YZaq+UKbriQ", ExportName = "sceAudioOutPtGetLastOutputTime", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutPtGetLastOutputTime(CpuContext ctx) => GetPassThroughTime(ctx);
    [SysAbiExport(Nid = "xyT8IUCL3CI", ExportName = "sceAudioOutPtOpen", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutPtOpen(CpuContext ctx) => OpenPassThrough(ctx);
    [SysAbiExport(Nid = "o4OLQQqqA90", ExportName = "sceAudioOutSetConnections", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetConnections(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "QHq2ylFOZ0k", ExportName = "sceAudioOutSetConnectionsForUser", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetConnectionsForUser(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "r9KGqGpwTpg", ExportName = "sceAudioOutSetDevConnection", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetDevConnection(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "08MKi2E-RcE", ExportName = "sceAudioOutSetHeadphoneOutMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetHeadphoneOutMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "18IVGrIQDU4", ExportName = "sceAudioOutSetJediJackVolume", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetJediJackVolume(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "h0o+D4YYr1k", ExportName = "sceAudioOutSetJediSpkVolume", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetJediSpkVolume(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "KI9cl22to7E", ExportName = "sceAudioOutSetMainOutput", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetMainOutput(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "wVwPU50pS1c", ExportName = "sceAudioOutSetMixLevelPadSpk", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetMixLevelPadSpk(CpuContext ctx) => ctx.SetReturn(Ports.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    [SysAbiExport(Nid = "eeRsbeGYe20", ExportName = "sceAudioOutSetMorpheusParam", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetMorpheusParam(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "IZrItPnflBM", ExportName = "sceAudioOutSetMorpheusWorkingMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetMorpheusWorkingMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Gy0ReOgXW00", ExportName = "sceAudioOutSetPortConnections", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetPortConnections(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "oRBFflIrCg0", ExportName = "sceAudioOutSetPortStatuses", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetPortStatuses(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "ae-IVPMSWjU", ExportName = "sceAudioOutSetRecMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetRecMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "d3WL2uPE1eE", ExportName = "sceAudioOutSetSparkParam", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetSparkParam(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "X7Cfsiujm8Y", ExportName = "sceAudioOutSetUsbVolume", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetUsbVolume(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "rho9DH-0ehs", ExportName = "sceAudioOutSetVolumeDown", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSetVolumeDown(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "I91P0HAPpjw", ExportName = "sceAudioOutStartAuxBroadcast", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutStartAuxBroadcast(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "uo+eoPzdQ-s", ExportName = "sceAudioOutStartSharePlay", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutStartSharePlay(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "AImiaYFrKdc", ExportName = "sceAudioOutStopAuxBroadcast", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutStopAuxBroadcast(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "teCyKKZPjME", ExportName = "sceAudioOutStopSharePlay", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutStopSharePlay(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "95bdtHdNUic", ExportName = "sceAudioOutSuspendResume", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSuspendResume(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "oRJZnXxok-M", ExportName = "sceAudioOutSysConfigureOutputMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSysConfigureOutputMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "Tf9-yOJwF-A", ExportName = "sceAudioOutSysGetHdmiMonitorInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSysGetHdmiMonitorInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], 0x40);
    [SysAbiExport(Nid = "y2-hP-KoTMI", ExportName = "sceAudioOutSysGetSystemInfo", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSysGetSystemInfo(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdi], 0x20);
    [SysAbiExport(Nid = "YV+bnMvMfYg", ExportName = "sceAudioOutSysHdmiMonitorInfoIsSupportedAudioOutMode", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSysHdmiMonitorInfoIsSupportedAudioOutMode(CpuContext ctx) => Ok(ctx);
    [SysAbiExport(Nid = "JEHhANREcLs", ExportName = "sceAudioOutSystemControlGet", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSystemControlGet(CpuContext ctx) => ClearOptional(ctx, ctx[CpuRegister.Rdx], 0x20);
    [SysAbiExport(Nid = "9CHWVv6r3Dg", ExportName = "sceAudioOutSystemControlSet", Target = Generation.Gen5, LibraryName = "libSceAudioOut")]
    public static int sceAudioOutSystemControlSet(CpuContext ctx) => Ok(ctx);

    private static int WriteOptionalUInt32(CpuContext ctx, ulong address, uint value)
    {
        return address == 0 || ctx.TryWriteUInt32(address, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ClearOptional(CpuContext ctx, ulong address, int size)
    {
        if (address == 0) return ctx.SetReturn(0);
        return ctx.Memory.TryWrite(address, new byte[size])
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ClearRequired(CpuContext ctx, ulong address, int size)
    {
        if (address == 0) return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return ClearOptional(ctx, address, size);
    }

    public static void ShutdownAllPorts()
    {
        foreach (var handle in Ports.Keys)
        {
            if (Ports.TryRemove(handle, out var port))
            {
                port.Dispose();
            }
        }
    }

    private static bool TryGetFormat(
        int rawFormat,
        out int channels,
        out int bytesPerSample,
        out bool isFloat)
    {
        var format = rawFormat & 0xFF;
        channels = format switch
        {
            0 or 3 => 1,
            1 or 4 => 2,
            2 or 5 or 6 or 7 => 8,
            _ => 0,
        };
        bytesPerSample = format is >= 3 and <= 5 or 7 ? 4 : 2;
        isFloat = bytesPerSample == 4;
        return channels != 0;
    }
}
