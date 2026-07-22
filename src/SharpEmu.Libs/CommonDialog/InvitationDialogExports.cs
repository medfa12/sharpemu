// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class InvitationDialogExports
{
    private const int ResultSize = 56;
    private static readonly ImmediateDialogState State = new();
    private static ulong _callbackArgument;

    [SysAbiExport(Nid = "WWtCL5lzi7Y", ExportName = "sceInvitationDialogClose", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogClose(CpuContext ctx) => ctx.SetReturn(State.Close());

    [SysAbiExport(Nid = "8XKR6wa64iQ", ExportName = "sceInvitationDialogGetResult", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogGetResult(CpuContext ctx) => WriteResult(ctx);

    [SysAbiExport(Nid = "WuuUhuKOxwQ", ExportName = "sceInvitationDialogGetResultA", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogGetResultA(CpuContext ctx) => WriteResult(ctx);

    [SysAbiExport(Nid = "EiF92YDNHRA", ExportName = "sceInvitationDialogGetStatus", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogGetStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    [SysAbiExport(Nid = "XvA5KS56wcs", ExportName = "sceInvitationDialogInitialize", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogInitialize(CpuContext ctx) => ctx.SetReturn(State.Initialize());

    [SysAbiExport(Nid = "0zU0G+wiVLA", ExportName = "sceInvitationDialogOpen", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogOpen(CpuContext ctx) => Open(ctx);

    [SysAbiExport(Nid = "sAxbHhAWMXM", ExportName = "sceInvitationDialogOpenA", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogOpenA(CpuContext ctx) => Open(ctx);

    [SysAbiExport(Nid = "B6HVJtDYxEE", ExportName = "sceInvitationDialogTerminate", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogTerminate(CpuContext ctx)
    {
        _callbackArgument = 0;
        return ctx.SetReturn(State.Terminate());
    }

    [SysAbiExport(Nid = "9+g9iOq+7kg", ExportName = "sceInvitationDialogUpdateStatus", LibraryName = "libSceInvitationDialog", Target = Generation.Gen5)]
    public static int InvitationDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(State.Status);

    internal static void ResetForTests()
    {
        _callbackArgument = 0;
        State.Reset();
    }

    private static int Open(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var result = State.Open(paramAddress);
        if (result == 0)
        {
            _callbackArgument = ctx.TryReadUInt64(paramAddress + 0x40, out var callbackArgument)
                ? callbackArgument
                : 0;
        }

        return ctx.SetReturn(result);
    }

    private static int WriteResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        var validation = State.ValidateResult(resultAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (!ctx.TryReadUInt64(resultAddress + 0x10, out var selectedUsersAddress))
        {
            selectedUsersAddress = 0;
        }

        if (!DialogMemory.TryClear(ctx, resultAddress, ResultSize) ||
            !ctx.TryWriteUInt64(resultAddress, _callbackArgument) ||
            !ctx.TryWriteUInt64(resultAddress + 0x10, selectedUsersAddress) ||
            (selectedUsersAddress != 0 && !ctx.TryWriteUInt32(selectedUsersAddress, 0)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }
}
