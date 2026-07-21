// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.DiscMap;

public static class DiscMapExports
{
    private const int DiscMapErrorInvalidArgument = unchecked((int)0x81100001);
    private const int DiscMapErrorNoBitmapInfo = unchecked((int)0x81100004);

    [SysAbiExport(
        Nid = "fl1eoDnwQ4s",
        ExportName = "sceDiscMapGetPackageSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapGetPackageSize(CpuContext ctx)
    {
        var lowAddress = ctx[CpuRegister.Rsi];
        var highAddress = ctx[CpuRegister.Rdx];
        if (lowAddress == 0 || highAddress == 0)
        {
            return ctx.SetReturn(DiscMapErrorInvalidArgument);
        }

        var size = GetMountedAppSize();
        if (!ctx.TryWriteUInt32(lowAddress, (uint)size) ||
            !ctx.TryWriteUInt32(highAddress, (uint)(size >> 32)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "lbQKqsERhtE",
        ExportName = "sceDiscMapIsRequestOnHDD",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapIsRequestOnHDD(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rcx];
        if (pathAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(DiscMapErrorInvalidArgument);
        }

        if (!ctx.TryReadNullTerminatedUtf8(pathAddress, 1024, out var path))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!ctx.TryWriteInt32(resultAddress, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceDiscMap("sceDiscMapIsRequestOnHDD", path, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "fJgP+wqifno",
        ExportName = "sceDiscMapMapRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapMapRequest(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var flagsAddress = ctx[CpuRegister.Rcx];
        var firstResultAddress = ctx[CpuRegister.R8];
        var secondResultAddress = ctx[CpuRegister.R9];
        if (pathAddress == 0 || flagsAddress == 0 || firstResultAddress == 0 || secondResultAddress == 0)
        {
            return ctx.SetReturn(DiscMapErrorInvalidArgument);
        }

        if (!ctx.TryReadNullTerminatedUtf8(pathAddress, 1024, out var path))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!ctx.TryWriteInt32(flagsAddress, 0) ||
            !ctx.TryWriteInt32(firstResultAddress, 0) ||
            !ctx.TryWriteInt32(secondResultAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceDiscMap("sceDiscMapMapRequest", path, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ioKMruft1ek",
        ExportName = "sceDiscMapUnknownIoKM",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapUnknownIoKM(CpuContext ctx) => ctx.SetReturn(DiscMapErrorNoBitmapInfo);

    [SysAbiExport(
        Nid = "5+vOlukvkfg",
        ExportName = "sceDiscMapUnknownE7EB",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapUnknownE7EB(CpuContext ctx) => ctx.SetReturn(DiscMapErrorNoBitmapInfo);

    internal static ulong GetMountedAppSize()
    {
        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0Root) || !Directory.Exists(app0Root))
        {
            return 0;
        }

        ulong total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(app0Root, "*", SearchOption.AllDirectories))
            {
                total = checked(total + (ulong)new FileInfo(path).Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return 0;
        }

        return total;
    }

    private static void TraceDiscMap(string exportName, string path, ulong offset, ulong size)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISCMAP"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[HLE][DISCMAP] {exportName} path={path} offset=0x{offset:X} size=0x{size:X}");
        }
    }
}
