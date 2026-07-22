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
    private const int EntitlementKeySize = 16;
    private const int DownloadProgressSize = 16;
    private const ulong DefaultAvailableSpaceKb = 1024UL * 1024UL;
    private const string Temp0MountPoint = "/temp0";
    private const string SmallSharedMountPoint = "/smallshared";
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

    [SysAbiExport(Nid = "ZiATpP9gEkA", ExportName = "sceAppContentAddcontDelete", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontDelete(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "7gxh+5QubhY", ExportName = "sceAppContentAddcontEnqueueDownload", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontEnqueueDownload(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "TVM-aYIsG9k", ExportName = "sceAppContentAddcontEnqueueDownloadSp", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontEnqueueDownloadSp(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "VANhIWcqYak", ExportName = "sceAppContentAddcontMount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontMount(CpuContext ctx)
    {
        var entitlementLabelAddress = ctx[CpuRegister.Rsi];
        var mountPointAddress = ctx[CpuRegister.Rdx];
        if (entitlementLabelAddress == 0 || mountPointAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        if (!TryWriteZeroes(ctx, mountPointAddress, MountPointSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(AppContentErrorDrmNoEntitlement);
    }

    [SysAbiExport(Nid = "D3H+cjfzzFY", ExportName = "sceAppContentAddcontShrink", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontShrink(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "3rHWaV-1KC4", ExportName = "sceAppContentAddcontUnmount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAddcontUnmount(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(AppContentErrorParameter)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "+OlXCu8qxUk", ExportName = "sceAppContentAppParamGetString", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentAppParamGetString(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        if (bufferAddress == 0 || bufferSize == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        ReadOnlySpan<byte> terminator = stackalloc byte[] { 0 };
        return ctx.Memory.TryWrite(bufferAddress, terminator)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "gpGZDB4ZlrI", ExportName = "sceAppContentDownload0Expand", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentDownload0Expand(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "S5eMvWnbbXg", ExportName = "sceAppContentDownload0Shrink", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentDownload0Shrink(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "B5gVeVurdUA", ExportName = "sceAppContentDownload1Expand", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentDownload1Expand(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "kUeYucqnb7o", ExportName = "sceAppContentDownload1Shrink", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentDownload1Shrink(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "Gl6w5i0JokY", ExportName = "sceAppContentDownloadDataGetAvailableSpaceKb", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentDownloadDataGetAvailableSpaceKb(CpuContext ctx) => WriteAvailableSpace(ctx, CpuRegister.Rsi);

    [SysAbiExport(Nid = "5bvvbUSiFs4", ExportName = "sceAppContentGetAddcontDownloadProgress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentGetAddcontDownloadProgress(CpuContext ctx)
    {
        var entitlementLabelAddress = ctx[CpuRegister.Rsi];
        var progressAddress = ctx[CpuRegister.Rdx];
        if (entitlementLabelAddress == 0 || progressAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        return TryWriteZeroes(ctx, progressAddress, DownloadProgressSize)
            ? ctx.SetReturn(AppContentErrorDrmNoEntitlement)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "XTWR0UXvcgs", ExportName = "sceAppContentGetEntitlementKey", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentGetEntitlementKey(CpuContext ctx)
    {
        var entitlementLabelAddress = ctx[CpuRegister.Rsi];
        var keyAddress = ctx[CpuRegister.Rdx];
        if (entitlementLabelAddress == 0 || keyAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        return TryWriteZeroes(ctx, keyAddress, EntitlementKeySize)
            ? ctx.SetReturn(AppContentErrorDrmNoEntitlement)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "74-1x3lyZK8", ExportName = "sceAppContentGetRegion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentGetRegion(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "bVtF7v2uqT0", ExportName = "sceAppContentRequestPatchInstall", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentRequestPatchInstall(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "9Gq5rOkWzNU", ExportName = "sceAppContentSmallSharedDataFormat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentSmallSharedDataFormat(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "xhb-r8etmAA", ExportName = "sceAppContentSmallSharedDataGetAvailableSpaceKb", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentSmallSharedDataGetAvailableSpaceKb(CpuContext ctx) => WriteAvailableSpace(ctx, CpuRegister.Rsi);

    [SysAbiExport(Nid = "QuApZnMo9MM", ExportName = "sceAppContentSmallSharedDataMount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentSmallSharedDataMount(CpuContext ctx) => WriteMountPoint(ctx, ctx[CpuRegister.Rdi], SmallSharedMountPoint);

    [SysAbiExport(Nid = "EqMtBHWu-5M", ExportName = "sceAppContentSmallSharedDataUnmount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentSmallSharedDataUnmount(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(AppContentErrorParameter)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "a5N7lAG0y2Q", ExportName = "sceAppContentTemporaryDataFormat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataFormat(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(AppContentErrorParameter)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(Nid = "SaKib2Ug0yI", ExportName = "sceAppContentTemporaryDataGetAvailableSpaceKb", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataGetAvailableSpaceKb(CpuContext ctx) => WriteAvailableSpace(ctx, CpuRegister.Rsi);

    [SysAbiExport(Nid = "7bOLX66Iz-U", ExportName = "sceAppContentTemporaryDataMount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataMount(CpuContext ctx) => WriteMountPoint(ctx, ctx[CpuRegister.Rdi], Temp0MountPoint);

    [SysAbiExport(Nid = "bcolXMmp6qQ", ExportName = "sceAppContentTemporaryDataUnmount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataUnmount(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(AppContentErrorParameter)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    private static int WriteAvailableSpace(CpuContext ctx, CpuRegister outputRegister)
    {
        var availableSpaceAddress = ctx[outputRegister];
        if (availableSpaceAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        return ctx.TryWriteUInt64(availableSpaceAddress, DefaultAvailableSpaceKb)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int WriteMountPoint(CpuContext ctx, ulong mountPointAddress, string value)
    {
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(AppContentErrorParameter);
        }

        Span<byte> mountPoint = stackalloc byte[MountPointSize];
        mountPoint.Clear();
        Encoding.ASCII.GetBytes(value, mountPoint);
        return ctx.Memory.TryWrite(mountPointAddress, mountPoint)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryWriteZeroes(CpuContext ctx, ulong address, int size)
    {
        Span<byte> zeroes = stackalloc byte[size];
        zeroes.Clear();
        return ctx.Memory.TryWrite(address, zeroes);
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
