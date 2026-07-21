// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpEntitlementAccessExports
{
    private const int NpEntitlementAccessErrorParameter = unchecked((int)0x817D0002);
    private const int NpEntitlementAccessErrorNoEntitlement = unchecked((int)0x817D0007);
    private const int BootParamSize = 32;
    private const int AddcontEntitlementInfoSize = 28;
    private const int EntitlementKeySize = 16;

    [SysAbiExport(
        Nid = "jO8DM8oyego",
        ExportName = "sceNpEntitlementAccessInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessInitialize(CpuContext ctx)
    {
        var initParam = ctx[CpuRegister.Rdi];
        var bootParam = ctx[CpuRegister.Rsi];

        if (initParam == 0 || bootParam == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> clear = stackalloc byte[BootParamSize];
        clear.Clear();
        if (!ctx.Memory.TryWrite(bootParam, clear))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNpEntitlementAccess($"initialize init=0x{initParam:X16} boot=0x{bootParam:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "TFyU+KFBv54",
        ExportName = "sceNpEntitlementAccessGetAddcontEntitlementInfoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetAddcontEntitlementInfoList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rsi];
        var listCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        var hitCountAddress = ctx[CpuRegister.Rcx];
        if (hitCountAddress == 0 || (listAddress == 0 && listCount != 0))
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        if (listCount > 4096)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        if (listAddress != 0 && listCount != 0 &&
            !ctx.Memory.TryWrite(listAddress, new byte[checked((int)listCount * AddcontEntitlementInfoSize)]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!ctx.TryWriteUInt32(hitCountAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNpEntitlementAccess(
            $"get_addcont_info_list service=0x{ctx[CpuRegister.Rdi]:X16} list=0x{listAddress:X16} " +
            $"max={listCount} hit_num=0x{hitCountAddress:X16} -> empty");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "lPDO62PpJIA",
        ExportName = "sceNpEntitlementAccessGetSkuFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetSkuFlag(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        return ctx.TryWriteUInt32(flagAddress, 3)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "xddD23+8TfQ",
        ExportName = "sceNpEntitlementAccessGetAddcontEntitlementInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetAddcontEntitlementInfo(CpuContext ctx)
    {
        var labelAddress = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        if (labelAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> label = stackalloc byte[20];
        Span<byte> info = stackalloc byte[AddcontEntitlementInfoSize];
        info.Clear();
        if (!ctx.Memory.TryRead(labelAddress, label) || !ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpEntitlementAccessErrorNoEntitlement);
    }

    [SysAbiExport(
        Nid = "5LiMEPuW0DQ",
        ExportName = "sceNpEntitlementAccessGetEntitlementKey",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetEntitlementKey(CpuContext ctx)
    {
        var labelAddress = ctx[CpuRegister.Rsi];
        var keyAddress = ctx[CpuRegister.Rdx];
        if (labelAddress == 0 || keyAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> label = stackalloc byte[20];
        Span<byte> key = stackalloc byte[EntitlementKeySize];
        key.Clear();
        if (!ctx.Memory.TryRead(labelAddress, label) || !ctx.Memory.TryWrite(keyAddress, key))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpEntitlementAccessErrorNoEntitlement);
    }

    private static void TraceNpEntitlementAccess(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.entitlement.{message}");
    }
}
