// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static class AgcDriverRenderStateExports
{
    private const int AgcErrorQueue = unchecked((int)0x8A6D0000);
    private const int AgcErrorInvalidArgument = unchecked((int)0x8A6D0003);
    private const int AgcErrorOutOfMemory = unchecked((int)0x8A6D0005);
    private const int VideoOutErrorInvalidIndex = unchecked((int)0x8029000A);
    private const uint MaximumRegisterCount = 0x400;
    private const uint MaximumPacketDwords = 0x4000;
    private const uint ItNop = 0x10;
    private const uint RZero = 0x00;
    private const uint RFlip = 0x17;
    private const uint FlipPacketDwords = 6;
    private const ulong QueueAllocationSize = 0x1000;

    private static readonly ConditionalWeakTable<object, DriverRenderState> _states = new();

    [SysAbiExport(
        Nid = "nR6xhiFsOoc",
        ExportName = "sceAgcDriverNotifyDefaultStates",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverNotifyDefaultStates(CpuContext ctx)
    {
        var cxAddress = ctx[CpuRegister.Rdi];
        var shAddress = ctx[CpuRegister.Rsi];
        var ucAddress = ctx[CpuRegister.Rdx];
        var cxCount = (uint)ctx[CpuRegister.Rcx];
        var shCount = (uint)ctx[CpuRegister.R8];
        var ucCount = (uint)ctx[CpuRegister.R9];

        try
        {
            var intent = new DefaultStateIntent(
                ReadRegisterValues(ctx, cxAddress, cxCount),
                ReadRegisterValues(ctx, shAddress, shCount),
                ReadRegisterValues(ctx, ucAddress, ucCount));
            var state = _states.GetValue(ctx.Memory, static _ => new DriverRenderState());
            lock (state.Gate)
            {
                state.DefaultStates = intent;
            }
        }
        catch (OutOfMemoryException)
        {
            // Firmware returns this only when one of its three range allocations fails
            // (libSceAgcDriver 0x3499-0x3571).
            return ctx.SetReturn(AgcErrorOutOfMemory);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "lYz7vbL4W4A",
        ExportName = "sceAgcDriverPatchClearState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverPatchClearState(CpuContext ctx)
    {
        var valuesAddress = ctx[CpuRegister.Rdi];
        var valueCount = (uint)ctx[CpuRegister.Rsi];
        if (valueCount > MaximumRegisterCount)
        {
            // Firmware rejects counts above the 0x400-register hardware space
            // before reading the array (libSceAgcDriver 0x2FAA-0x2FB5).
            return ctx.SetReturn(AgcErrorInvalidArgument);
        }

        var patches = ReadRegisterValues(ctx, valuesAddress, valueCount, filterRegisterSpace: true);
        var state = _states.GetValue(ctx.Memory, static _ => new DriverRenderState());
        lock (state.Gate)
        {
            state.ClearStatePatches = patches;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "zP4ZNlXLBVg",
        ExportName = "sceAgcDriverCreateQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverCreateQueue(CpuContext ctx)
    {
        var queueSelector = (uint)ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        var optionsAddress = ctx[CpuRegister.Rdx];
        var queueClass = (queueSelector >> 5) & 0x3;
        if (outputAddress == 0 || queueClass == 3)
        {
            // The oracle extracts bits [6:5] with BEXTR and rejects class 3
            // using the same result as a null output (0x1ED8-0x1EF2).
            return ctx.SetReturn(AgcErrorQueue);
        }

        var state = _states.GetValue(ctx.Memory, static _ => new DriverRenderState());
        ulong descriptorAddress;
        lock (state.Gate)
        {
            if (!state.Queues.TryGetValue(queueSelector, out descriptorAddress))
            {
                if (!KernelMemoryCompatExports.TryAllocateHleData(
                        ctx,
                        QueueAllocationSize,
                        16,
                        out var allocationAddress) ||
                    !InitializeQueueDescriptor(
                        ctx,
                        allocationAddress,
                        queueSelector,
                        queueClass,
                        optionsAddress,
                        out descriptorAddress))
                {
                    return ctx.SetReturn(AgcErrorQueue);
                }

                state.Queues.Add(queueSelector, descriptorAddress);
            }
        }

        return ctx.TryWriteUInt64(outputAddress, descriptorAddress)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(AgcErrorQueue);
    }

    [SysAbiExport(
        Nid = "QcmHLO2n7mk",
        ExportName = "sceAgcDriverSuspendPointSubmit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSuspendPointSubmit(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !ctx.TryReadUInt64(packetAddress, out _) ||
            !ctx.TryReadUInt32(packetAddress + 8, out _))
        {
            return ctx.SetReturn(AgcErrorQueue);
        }

        // The firmware copies the packet's address, DWORD count, and queue byte from
        // offsets 0, 8, and 0xC before submitting it (0x1C45-0x1C5A). SharpEmu's DCB
        // submit path consumes the same first two fields and applies their GPU effects.
        return AgcExports.DriverSubmitDcb(ctx);
    }

    [SysAbiExport(
        Nid = "cwbxjPSJ7WQ",
        ExportName = "sceAgcDriverSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetFlip(CpuContext ctx)
    {
        var cursorAddress = ctx[CpuRegister.Rdi];
        var packetDwords = (uint)ctx[CpuRegister.Rsi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rcx];
        var displayBufferIndex = unchecked((int)(uint)ctx[CpuRegister.R8]);
        var flipMode = (uint)ctx[CpuRegister.R9];

        if (unchecked((uint)(displayBufferIndex + 2)) >= 18)
        {
            // Firmware accepts exactly [-2, 15] (0x6DBB-0x6DF0).
            return ctx.SetReturn(VideoOutErrorInvalidIndex);
        }

        if (packetDwords < FlipPacketDwords ||
            packetDwords > MaximumPacketDwords ||
            packetDwords - FlipPacketDwords == 1 ||
            !ctx.TryReadUInt64(cursorAddress, out var commandAddress) ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var flipArg))
        {
            return ctx.SetReturn(AgcErrorQueue);
        }

        var packet = new byte[checked((int)packetDwords * sizeof(uint))];
        WriteDword(packet, 0, Pm4(FlipPacketDwords, ItNop, RFlip));
        WriteDword(packet, 1, videoOutHandle);
        WriteDword(packet, 2, unchecked((uint)displayBufferIndex));
        WriteDword(packet, 3, flipMode);
        WriteDword(packet, 4, (uint)flipArg);
        WriteDword(packet, 5, (uint)(flipArg >> 32));

        var paddingDwords = packetDwords - FlipPacketDwords;
        if (paddingDwords >= 2)
        {
            WriteDword(packet, FlipPacketDwords, Pm4(paddingDwords, ItNop, RZero));
        }

        if (!ctx.Memory.TryWrite(commandAddress, packet) ||
            !ctx.TryWriteUInt64(cursorAddress, commandAddress + packetDwords * sizeof(uint)))
        {
            return ctx.SetReturn(AgcErrorQueue);
        }

        return ctx.SetReturn(0);
    }

    private static bool InitializeQueueDescriptor(
        CpuContext ctx,
        ulong allocationAddress,
        uint queueSelector,
        uint queueClass,
        ulong optionsAddress,
        out ulong descriptorAddress)
    {
        if (queueClass == 0)
        {
            descriptorAddress = allocationAddress;
            if (optionsAddress != 0)
            {
                return CopyQueueOptions(ctx, optionsAddress, descriptorAddress);
            }

            // Default graphics descriptor fields written at 0x1F74-0x1F86 and 0x20A4.
            return ctx.TryWriteUInt32(descriptorAddress, 0x30) &&
                ctx.TryWriteUInt32(descriptorAddress + 4, queueSelector) &&
                ctx.TryWriteUInt32(descriptorAddress + 8, 0x20000) &&
                ctx.TryWriteUInt32(descriptorAddress + 0x10, 0);
        }

        // Async queue handles point eight bytes into the driver's 0x88-byte record
        // (0x2251 and 0x2345).
        descriptorAddress = allocationAddress + 8;
        if (optionsAddress != 0)
        {
            if (!CopyQueueOptions(ctx, optionsAddress, descriptorAddress))
            {
                return false;
            }
        }
        else if (!ctx.TryWriteUInt32(descriptorAddress, 0x30) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x14, 0))
        {
            return false;
        }

        return ctx.TryWriteUInt32(allocationAddress, queueSelector) &&
            ctx.TryWriteUInt32(descriptorAddress + 4, queueSelector) &&
            ctx.TryWriteUInt64(descriptorAddress + 0x38, allocationAddress) &&
            ctx.TryWriteUInt32(descriptorAddress + 0x68, 1) &&
            ctx.TryWriteUInt32(descriptorAddress + 0x78, 0);
    }

    private static bool CopyQueueOptions(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        Span<byte> options = stackalloc byte[0x20];
        return ctx.Memory.TryRead(sourceAddress, options) &&
            ctx.Memory.TryWrite(destinationAddress, options);
    }

    private static RegisterValue[] ReadRegisterValues(
        CpuContext ctx,
        ulong address,
        uint count,
        bool filterRegisterSpace = false)
    {
        var readableCount = Math.Min(count, MaximumRegisterCount);
        var values = new List<RegisterValue>(checked((int)readableCount));
        for (uint i = 0; i < readableCount; i++)
        {
            var entryAddress = address + i * sizeof(ulong);
            if (!ctx.TryReadUInt32(entryAddress, out var registerOffset) ||
                !ctx.TryReadUInt32(entryAddress + sizeof(uint), out var value))
            {
                break;
            }

            if (!filterRegisterSpace || registerOffset < MaximumRegisterCount)
            {
                values.Add(new RegisterValue(registerOffset, value));
            }
        }

        return values.ToArray();
    }

    private static void WriteDword(Span<byte> packet, uint dwordOffset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet[(int)(dwordOffset * sizeof(uint))..], value);

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private sealed class DriverRenderState
    {
        public object Gate { get; } = new();
        public Dictionary<uint, ulong> Queues { get; } = new();
        public DefaultStateIntent? DefaultStates { get; set; }
        public RegisterValue[] ClearStatePatches { get; set; } = [];
    }

    private sealed record DefaultStateIntent(
        RegisterValue[] Cx,
        RegisterValue[] Sh,
        RegisterValue[] Uc);

    private readonly record struct RegisterValue(uint Offset, uint Value);
}
