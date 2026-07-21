// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.AppContent;

public static class AppContentExports
{
    private const int AppContentErrorParameter = unchecked((int)0x80D90002);
    private const int AppContentErrorDrmNoEntitlement = unchecked((int)0x80D90007);
    private const int BootParamSize = 40;
    private const int MountPointSize = 16;
    private const int AddcontInfoSize = 24;
    private const string Temp0MountPoint = "/temp0";
    private const uint AppParamSkuFlag = 0;
    private const int AppParamSkuFlagFull = 3;

    [SysAbiExport(
        Nid = "R9lA82OraNs",
        ExportName = "sceAppContentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentInitialize(CpuContext ctx)
    {
        var initParamAddress = ctx[CpuRegister.Rdi];
        var bootParamAddress = ctx[CpuRegister.Rsi];
        if (initParamAddress == 0 || bootParamAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        Span<byte> bootParam = stackalloc byte[BootParamSize];
        bootParam.Clear();
        if (!ctx.Memory.TryWrite(bootParamAddress, bootParam))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "xnd8BJzAxmk",
        ExportName = "sceAppContentGetAddcontInfoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentGetAddcontInfoList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rsi];
        var listCount = (uint)ctx[CpuRegister.Rdx];
        var hitCountAddress = ctx[CpuRegister.Rcx];

        if ((listAddress == 0 || listCount == 0) && hitCountAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        if (hitCountAddress != 0 && !ctx.TryWriteUInt32(hitCountAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "m47juOmH0VE",
        ExportName = "sceAppContentGetAddcontInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentGetAddcontInfo(CpuContext ctx)
    {
        var entitlementLabelAddress = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        if (entitlementLabelAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        Span<byte> info = stackalloc byte[AddcontInfoSize];
        info.Clear();
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(AppContentErrorDrmNoEntitlement);
    }

    [SysAbiExport(
        Nid = "99b82IKXpH4",
        ExportName = "sceAppContentAppParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentAppParamGetInt(CpuContext ctx)
    {
        var paramId = (uint)ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        int value;
        if (paramId == AppParamSkuFlag)
        {
            value = AppParamSkuFlagFull;
        }
        else if (!TryReadUserDefinedParam(paramId, out value))
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        if (!ctx.TryWriteInt32(valueAddress, value))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAppContent($"app_param_get_int id={paramId} value={value}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "buYbeLOGWmA",
        ExportName = "sceAppContentTemporaryDataMount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataMount2(CpuContext ctx)
    {
        var option = (uint)ctx[CpuRegister.Rdi];
        var mountPointAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        try
        {
            var root = ResolveTemp0Root();
            if ((option & 1) != 0)
            {
                FormatDirectory(root);
            }
            else
            {
                Directory.CreateDirectory(root);
            }
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
        }

        Span<byte> mountPoint = stackalloc byte[MountPointSize];
        mountPoint.Clear();
        Encoding.ASCII.GetBytes(Temp0MountPoint, mountPoint);
        if (!ctx.Memory.TryWrite(mountPointAddress, mountPoint))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "CN7EbEV7MFU",
        ExportName = "sceAppContentDownloadDataFormat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentDownloadDataFormat(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        if (!TryReadMountPoint(ctx, mountPointAddress, out var mountPoint) ||
            mountPoint is not ("/download0" or "/download1"))
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        try
        {
            FormatDirectory(Path.Combine(ResolveDownloadDataRoot(), mountPoint[1..]));
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static bool TryReadUserDefinedParam(uint paramId, out int value)
    {
        value = 0;
        if (paramId is < 1 or > 4)
        {
            return false;
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0Root))
        {
            return true;
        }

        var paramJsonPath = Path.Combine(app0Root, "sce_sys", "param.json");
        if (!File.Exists(paramJsonPath))
        {
            paramJsonPath = Path.Combine(app0Root, "param.json");
        }

        if (!File.Exists(paramJsonPath))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(paramJsonPath);
            using var document = JsonDocument.Parse(stream);
            var propertyName = $"userDefinedParam{paramId}";
            if (document.RootElement.TryGetProperty(propertyName, out var element) &&
                element.TryGetInt32(out var parsedValue))
            {
                value = parsedValue;
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool TryReadMountPoint(CpuContext ctx, ulong address, out string mountPoint)
    {
        mountPoint = string.Empty;
        if (address == 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[MountPointSize];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        var end = bytes.IndexOf((byte)0);
        if (end < 0)
        {
            end = bytes.Length;
        }

        mountPoint = Encoding.ASCII.GetString(bytes[..end]);
        return true;
    }

    private static void FormatDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        Directory.CreateDirectory(path);
    }

    private static void TraceAppContent(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_APP_CONTENT"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] app_content.{message}");
        }
    }

    private static string ResolveTemp0Root()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPEMU_TEMP0_DIR");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var root = Path.Combine(ResolveTitleDataRoot(), "temp0");
        Environment.SetEnvironmentVariable("SHARPEMU_TEMP0_DIR", root);
        return root;
    }

    private static string ResolveDownloadDataRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPEMU_DOWNLOAD_DATA_DIR");
        return !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.Combine(ResolveTitleDataRoot(), "download-data");
    }

    private static string ResolveTitleDataRoot()
    {
        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var appName = string.IsNullOrWhiteSpace(app0Root)
            ? "default"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "default";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        appName = new string(appName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(Path.GetTempPath(), "SharpEmu", appName);
    }
}
