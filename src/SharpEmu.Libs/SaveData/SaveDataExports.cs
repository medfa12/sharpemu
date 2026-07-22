// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataExports
{
    private const int ErrorParameter = unchecked((int)0x809F0000);
    private const int ErrorBusy = unchecked((int)0x809F0003);
    private const int ErrorNotMounted = unchecked((int)0x809F0004);
    private const int ErrorExists = unchecked((int)0x809F0007);
    private const int ErrorNotFound = unchecked((int)0x809F0008);
    private const int ErrorInternal = unchecked((int)0x809F000B);
    private const int ErrorMountFull = unchecked((int)0x809F000C);
    private const int ErrorBadMounted = unchecked((int)0x809F000D);
    private const int ErrorInvalidLoginUser = unchecked((int)0x809F0011);
    private const int ErrorMemoryNotReady = unchecked((int)0x809F0012);
    private const int MemoryFault = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;

    private const int TitleIdSize = 10;
    private const int DirNameSize = 32;
    private const int MountPointSize = 16;
    private const int MountResultSize = 0x40;
    private const int ParamSize = 0x530;
    private const int SearchInfoSize = 0x30;
    private const int MemoryDataSize = 0x40;
    private const uint BlockSize = 32768;
    private const ulong DefaultBlocks = 96;
    private const ulong MaximumMemorySize = 64UL * 1024 * 1024;
    private const ulong MaximumIconSize = 16UL * 1024 * 1024;
    private const uint MountModeReadOnly = 1u << 0;
    private const uint MountModeCreate = 1u << 2;
    private const uint MountModeCreate2 = 1u << 5;
    private const int MaximumMounts = 16;

    private static readonly object StateGate = new();
    private static readonly Dictionary<string, MountedSave> Mounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<(int UserId, uint SlotId)> ReadyMemorySlots = [];
    private static readonly HashSet<int> TransactionResources = [];
    private static readonly HashSet<int> PreparedTransactionResources = [];
    private static readonly Queue<SaveDataEventValue> PendingEvents = [];
    private static string? _titleId;
    private static int _nextTransactionResource;
    private static int _saveDataLibraryUser;
    private static uint _autoUploadSetting;
    private static float _backupProgress;
    private static ulong _eventCallback;

    internal readonly record struct SaveDataParamValue(
        string Title,
        string Subtitle,
        string Detail,
        uint UserParam,
        long MTime);

    private readonly record struct MountedSave(
        string MountPoint,
        string HostPath,
        ulong Blocks,
        bool ReadOnly);

    private readonly record struct SearchCond(
        int UserId,
        ulong TitleIdAddress,
        string Pattern,
        uint SortKey,
        uint SortOrder);

    private readonly record struct SearchResult(
        ulong DirNamesAddress,
        uint DirNamesNum,
        ulong ParamsAddress,
        ulong InfosAddress);

    private readonly record struct SaveEntry(
        string Name,
        string Path,
        SaveDataParamValue Param,
        ulong Blocks,
        ulong FreeBlocks);

    private readonly record struct MemoryData(ulong BufferAddress, ulong BufferSize, long Offset);

    private readonly record struct SaveDataEventValue(uint Type, int UserId, string TitleId, string DirName);

    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (StateGate)
        {
            foreach (var mountPoint in Mounts.Keys)
            {
                KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
            }

            Mounts.Clear();
            ReadyMemorySlots.Clear();
            TransactionResources.Clear();
            PreparedTransactionResources.Clear();
            _nextTransactionResource = 0;
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId);
        }
    }

    [SysAbiExport(
        Nid = "TywrFKCoLGY",
        ExportName = "sceSaveDataInitialize3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize3(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "yKDy8S5yLA0",
        ExportName = "sceSaveDataTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataTerminate(CpuContext ctx)
    {
        lock (StateGate)
        {
            foreach (var mountPoint in Mounts.Keys)
            {
                KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
            }

            Mounts.Clear();
            ReadyMemorySlots.Clear();
            TransactionResources.Clear();
            PreparedTransactionResources.Clear();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "32HQAQdwM2o",
        ExportName = "sceSaveDataMount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataMount(CpuContext ctx)
    {
        var mountAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (mountAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x28, out var blocks) ||
            !ctx.TryReadUInt32(mountAddress + 0x30, out var mountMode))
        {
            return ctx.SetReturn(MemoryFault);
        }

        return Mount(ctx, resultAddress, userId, dirNameAddress, blocks, mountMode, ResolveConfiguredTitleId());
    }

    [SysAbiExport(
        Nid = "0z45PIH+SNI",
        ExportName = "sceSaveDataMount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataMount2(CpuContext ctx)
    {
        var mountAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (mountAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x08, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var blocks) ||
            !ctx.TryReadUInt32(mountAddress + 0x18, out var mountMode))
        {
            return ctx.SetReturn(MemoryFault);
        }

        return Mount(ctx, resultAddress, userId, dirNameAddress, blocks, mountMode, ResolveConfiguredTitleId());
    }

    [SysAbiExport(
        Nid = "ZP4e7rlzOUk",
        ExportName = "sceSaveDataMount3",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataMount3(CpuContext ctx)
    {
        var mountAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (mountAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x08, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var blocks) ||
            !ctx.TryReadUInt64(mountAddress + 0x18, out var systemBlocks) ||
            !ctx.TryReadUInt32(mountAddress + 0x20, out var mountMode) ||
            !ctx.TryReadUInt32(mountAddress + 0x28, out var resource))
        {
            return ctx.SetReturn(MemoryFault);
        }

        Trace($"mount3 system_blocks={systemBlocks} resource={resource}");
        return Mount(ctx, resultAddress, userId, dirNameAddress, blocks, mountMode, ResolveConfiguredTitleId());
    }

    private static int Mount(
        CpuContext ctx,
        ulong resultAddress,
        int userId,
        ulong dirNameAddress,
        ulong blocks,
        uint mountMode,
        string titleId)
    {
        if (userId < 0)
        {
            return ctx.SetReturn(ErrorInvalidLoginUser);
        }

        if (dirNameAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, dirNameAddress, DirNameSize, out var dirName))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        var create = (mountMode & MountModeCreate) != 0;
        var createIfMissing = (mountMode & MountModeCreate2) != 0;
        if (create && createIfMissing)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            var savePath = BuildSaveDirectoryPath(ResolveSaveDataRoot(), userId, titleId, dirName);
            var existed = Directory.Exists(savePath);
            if (!existed && !create && !createIfMissing)
            {
                return ctx.SetReturn(ErrorNotFound);
            }

            if (existed && create)
            {
                return ctx.SetReturn(ErrorExists);
            }

            if (!existed)
            {
                CreateSaveLayout(savePath, dirName, blocks);
            }

            var storedBlocks = ReadBlocks(savePath, blocks == 0 ? DefaultBlocks : blocks);
            string mountPoint;
            lock (StateGate)
            {
                if (Mounts.Values.Any(mount => PathsEqual(mount.HostPath, savePath)))
                {
                    return ctx.SetReturn(ErrorBusy);
                }

                mountPoint = Enumerable.Range(0, MaximumMounts)
                    .Select(index => $"/savedata{index}")
                    .FirstOrDefault(candidate => !Mounts.ContainsKey(candidate)) ?? string.Empty;
                if (mountPoint.Length == 0)
                {
                    return ctx.SetReturn(ErrorMountFull);
                }

                Mounts.Add(
                    mountPoint,
                    new MountedSave(
                        mountPoint,
                        savePath,
                        storedBlocks,
                        (mountMode & MountModeReadOnly) != 0));
            }

            try
            {
                KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, savePath);
                Span<byte> result = stackalloc byte[MountResultSize];
                result.Clear();
                WriteAscii(result[..MountPointSize], mountPoint);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result[0x1C..],
                    createIfMissing && !existed ? 1u : 0u);
                if (!ctx.Memory.TryWrite(resultAddress, result))
                {
                    KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
                    lock (StateGate)
                    {
                        Mounts.Remove(mountPoint);
                    }

                    return ctx.SetReturn(MemoryFault);
                }
            }
            catch
            {
                KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
                lock (StateGate)
                {
                    Mounts.Remove(mountPoint);
                }

                throw;
            }

            Trace(
                $"mount user={userId} title={titleId} dir={dirName} mode=0x{mountMode:X} " +
                $"mount_point={mountPoint} created={!existed} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "BMR4F-Uek3E",
        ExportName = "sceSaveDataUmount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUmount(CpuContext ctx) => Umount(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "uW4vfTwMQVo",
        ExportName = "sceSaveDataUmount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUmount2(CpuContext ctx)
    {
        Trace($"umount2 mode={unchecked((uint)ctx[CpuRegister.Rdi])}");
        return Umount(ctx, ctx[CpuRegister.Rsi]);
    }

    private static int Umount(CpuContext ctx, ulong mountPointAddress)
    {
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, MountPointSize, out var mountPoint))
        {
            return ctx.SetReturn(MemoryFault);
        }

        lock (StateGate)
        {
            if (!Mounts.Remove(mountPoint))
            {
                return ctx.SetReturn(ErrorNotFound);
            }
        }

        KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
        Trace($"umount mount_point={mountPoint}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "65VH0Qaaz6s",
        ExportName = "sceSaveDataGetMountInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetMountInfo(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, MountPointSize, out var mountPoint))
        {
            return ctx.SetReturn(MemoryFault);
        }

        MountedSave mounted;
        lock (StateGate)
        {
            if (!Mounts.TryGetValue(mountPoint, out mounted))
            {
                return ctx.SetReturn(ErrorNotMounted);
            }
        }

        try
        {
            var usedBlocks = BytesToBlocks(GetDirectorySize(mounted.HostPath));
            Span<byte> info = stackalloc byte[0x30];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info, mounted.Blocks);
            BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], SaturatingSubtract(mounted.Blocks, usedBlocks));
            return ctx.Memory.TryWrite(infoAddress, info)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(MemoryFault);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "S1GkePI17zQ",
        ExportName = "sceSaveDataDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDelete(CpuContext ctx)
    {
        var deleteAddress = ctx[CpuRegister.Rdi];
        if (deleteAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(deleteAddress, out var userId) ||
            !ctx.TryReadUInt64(deleteAddress + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(deleteAddress + 0x10, out var dirNameAddress))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (userId < 0 || dirNameAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadOptionalTitleId(ctx, titleIdAddress, out var titleId) ||
            !TryReadFixedAscii(ctx, dirNameAddress, DirNameSize, out var dirName))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            var path = BuildSaveDirectoryPath(ResolveSaveDataRoot(), userId, titleId, dirName);
            lock (StateGate)
            {
                if (Mounts.Values.Any(mount => PathsEqual(mount.HostPath, path)))
                {
                    return ctx.SetReturn(ErrorBusy);
                }
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            Trace($"delete user={userId} title={titleId} dir={dirName}");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "YbCO38BOOl4",
        ExportName = "sceSaveDataCopy5",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCopy5(CpuContext ctx)
    {
        // Public references identify this export but do not publish its argument layout.
        // Failing safely avoids copying or overwriting an unintended directory.
        return ctx.SetReturn(ctx[CpuRegister.Rdi] == 0 ? ErrorParameter : ErrorNotFound);
    }

    [SysAbiExport(
        Nid = "dyIhnXq-0SM",
        ExportName = "sceSaveDataDirNameSearch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDirNameSearch(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (condAddress == 0 || resultAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadSearchCond(ctx, condAddress, out var cond) ||
            !TryReadSearchResult(ctx, resultAddress, out var result))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (cond.UserId < 0 || cond.SortKey > 5 || cond.SortKey == 4 || cond.SortOrder > 1)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            if (!TryReadOptionalTitleId(ctx, cond.TitleIdAddress, out var titleId))
            {
                return ctx.SetReturn(MemoryFault);
            }

            var root = BuildTitleSaveRoot(ResolveSaveDataRoot(), cond.UserId, titleId);
            Directory.CreateDirectory(root);
            var entries = EnumerateSaveDirectories(root, cond.Pattern);
            entries = SortEntries(entries, cond.SortKey, cond.SortOrder);
            var setNum = Math.Min((int)result.DirNamesNum, entries.Count);

            if (!ctx.TryWriteUInt32(resultAddress, checked((uint)entries.Count)) ||
                !ctx.TryWriteUInt32(resultAddress + 0x14, checked((uint)setNum)))
            {
                return ctx.SetReturn(MemoryFault);
            }

            if (setNum != 0 && result.DirNamesAddress == 0)
            {
                return ctx.SetReturn(ErrorParameter);
            }

            for (var index = 0; index < setNum; index++)
            {
                var entry = entries[index];
                if (!TryWriteFixedAscii(
                        ctx,
                        result.DirNamesAddress + ((ulong)index * DirNameSize),
                        DirNameSize,
                        entry.Name) ||
                    (result.ParamsAddress != 0 &&
                     !ctx.Memory.TryWrite(
                         result.ParamsAddress + ((ulong)index * ParamSize),
                         PackSaveDataParam(entry.Param))) ||
                    (result.InfosAddress != 0 &&
                     !TryWriteSearchInfo(
                         ctx,
                         result.InfosAddress + ((ulong)index * SearchInfoSize),
                         entry)))
                {
                    return ctx.SetReturn(MemoryFault);
                }
            }

            Trace($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set={setNum}");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "XgvSuIdnMlw",
        ExportName = "sceSaveDataGetParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetParam(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var paramType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var bufferAddress = ctx[CpuRegister.Rdx];
        var bufferSize = ctx[CpuRegister.Rcx];
        var gotSizeAddress = ctx[CpuRegister.R8];
        if (mountPointAddress == 0 || bufferAddress == 0 || paramType > 5 || bufferSize == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryGetMountedSave(ctx, mountPointAddress, out var mounted, out var error))
        {
            return ctx.SetReturn(error);
        }

        try
        {
            var value = LoadParam(mounted.HostPath, Path.GetFileName(mounted.HostPath));
            byte[] output;
            switch (paramType)
            {
                case 0:
                    output = PackSaveDataParam(value);
                    break;
                case 1:
                    output = EncodeAsciiString(value.Title, bufferSize);
                    break;
                case 2:
                    output = EncodeAsciiString(value.Subtitle, bufferSize);
                    break;
                case 3:
                    output = EncodeAsciiString(value.Detail, bufferSize);
                    break;
                case 4:
                    output = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(output, value.UserParam);
                    break;
                default:
                    output = new byte[sizeof(long)];
                    BinaryPrimitives.WriteInt64LittleEndian(output, value.MTime);
                    break;
            }

            if ((ulong)output.Length > bufferSize)
            {
                return ctx.SetReturn(ErrorParameter);
            }

            if (!ctx.Memory.TryWrite(bufferAddress, output) ||
                (gotSizeAddress != 0 && !ctx.TryWriteUInt64(gotSizeAddress, checked((ulong)output.Length))))
            {
                return ctx.SetReturn(MemoryFault);
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "85zul--eGXs",
        ExportName = "sceSaveDataSetParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetParam(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var paramType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var bufferAddress = ctx[CpuRegister.Rdx];
        var bufferSize = ctx[CpuRegister.Rcx];
        if (mountPointAddress == 0 || bufferAddress == 0 || paramType > 4)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryGetMountedSave(ctx, mountPointAddress, out var mounted, out var error))
        {
            return ctx.SetReturn(error);
        }

        if (mounted.ReadOnly)
        {
            return ctx.SetReturn(ErrorBadMounted);
        }

        try
        {
            var current = LoadParam(mounted.HostPath, Path.GetFileName(mounted.HostPath));
            SaveDataParamValue value;
            if (paramType == 0)
            {
                if (bufferSize < ParamSize)
                {
                    return ctx.SetReturn(ErrorParameter);
                }

                var packed = new byte[ParamSize];
                if (!ctx.Memory.TryRead(bufferAddress, packed))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                value = UnpackSaveDataParam(packed) with { MTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            }
            else if (paramType == 4)
            {
                if (bufferSize < sizeof(uint) || !ctx.TryReadUInt32(bufferAddress, out var userParam))
                {
                    return ctx.SetReturn(bufferSize < sizeof(uint) ? ErrorParameter : MemoryFault);
                }

                value = current with
                {
                    UserParam = userParam,
                    MTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
            }
            else
            {
                if (bufferSize == 0 || bufferSize > 1024)
                {
                    return ctx.SetReturn(ErrorParameter);
                }

                if (!TryReadAscii(ctx, bufferAddress, checked((int)bufferSize), out var text))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                value = paramType switch
                {
                    1 => current with { Title = text, MTime = now },
                    2 => current with { Subtitle = text, MTime = now },
                    _ => current with { Detail = text, MTime = now },
                };
            }

            SaveParam(mounted.HostPath, value);
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "cGjO3wM3V28",
        ExportName = "sceSaveDataLoadIcon",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataLoadIcon(CpuContext ctx)
    {
        if (!TryGetMountedSave(ctx, ctx[CpuRegister.Rdi], out var mounted, out var error))
        {
            return ctx.SetReturn(error);
        }

        return LoadIcon(ctx, ctx[CpuRegister.Rsi], GetIconPath(mounted.HostPath));
    }

    [SysAbiExport(
        Nid = "c88Yy54Mx0w",
        ExportName = "sceSaveDataSaveIcon",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSaveIcon(CpuContext ctx)
    {
        if (!TryGetMountedSave(ctx, ctx[CpuRegister.Rdi], out var mounted, out var error))
        {
            return ctx.SetReturn(error);
        }

        if (mounted.ReadOnly)
        {
            return ctx.SetReturn(ErrorBadMounted);
        }

        return SaveIcon(ctx, ctx[CpuRegister.Rsi], GetIconPath(mounted.HostPath));
    }

    [SysAbiExport(
        Nid = "v7AAAMo0Lz4",
        ExportName = "sceSaveDataSetupSaveDataMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetupSaveDataMemory(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var memorySize = ctx[CpuRegister.Rsi];
        var paramAddress = ctx[CpuRegister.Rdx];
        return SetupMemory(ctx, userId, 0, memorySize, 0, paramAddress, 0);
    }

    [SysAbiExport(
        Nid = "oQySEUfgXRA",
        ExportName = "sceSaveDataSetupSaveDataMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetupSaveDataMemory2(CpuContext ctx)
    {
        var setupAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (setupAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(setupAddress + 0x04, out var userId) ||
            !ctx.TryReadUInt64(setupAddress + 0x08, out var memorySize) ||
            !ctx.TryReadUInt64(setupAddress + 0x10, out var iconMemorySize) ||
            !ctx.TryReadUInt64(setupAddress + 0x18, out var paramAddress) ||
            !ctx.TryReadUInt64(setupAddress + 0x20, out var iconAddress) ||
            !ctx.TryReadUInt32(setupAddress + 0x28, out var slotId))
        {
            return ctx.SetReturn(MemoryFault);
        }

        return SetupMemory(ctx, userId, slotId, memorySize, resultAddress, paramAddress, iconAddress, iconMemorySize);
    }

    private static int SetupMemory(
        CpuContext ctx,
        int userId,
        uint slotId,
        ulong memorySize,
        ulong resultAddress,
        ulong paramAddress,
        ulong iconAddress,
        ulong iconMemorySize = 0)
    {
        if (userId < 0 || memorySize > MaximumMemorySize || iconMemorySize > MaximumIconSize)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            var directory = ResolveMemoryDirectory(userId, slotId);
            var memoryPath = Path.Combine(directory, "memory.dat");
            lock (StateGate)
            {
                var existedSize = File.Exists(memoryPath) ? checked((ulong)new FileInfo(memoryPath).Length) : 0;
                if (resultAddress != 0 && !ctx.TryWriteUInt64(resultAddress, existedSize))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                EnsureMemoryFile(memoryPath, memorySize);
                ReadyMemorySlots.Add((userId, slotId));
            }

            if (paramAddress != 0)
            {
                var packed = new byte[ParamSize];
                if (!ctx.Memory.TryRead(paramAddress, packed))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                SaveParam(directory, UnpackSaveDataParam(packed));
            }

            if (iconAddress != 0)
            {
                var iconResult = SaveIcon(ctx, iconAddress, GetIconPath(directory));
                if (iconResult != 0)
                {
                    return iconResult;
                }
            }

            Trace($"memory_setup user={userId} slot={slotId} size={memorySize} icon_size={iconMemorySize}");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "7Bt5pBC-Aco",
        ExportName = "sceSaveDataGetSaveDataMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory(CpuContext ctx) =>
        TransferLegacyMemory(ctx, write: false);

    [SysAbiExport(
        Nid = "h3YURzXGSVQ",
        ExportName = "sceSaveDataSetSaveDataMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory(CpuContext ctx) =>
        TransferLegacyMemory(ctx, write: true);

    private static int TransferLegacyMemory(CpuContext ctx, bool write)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var data = new MemoryData(
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            unchecked((long)ctx[CpuRegister.Rcx]));
        if (userId < 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        return TransferMemoryData(ctx, userId, 0, data, write);
    }

    [SysAbiExport(
        Nid = "QwOO7vegnV8",
        ExportName = "sceSaveDataGetSaveDataMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory2(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var dataAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x10, out var paramAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x18, out var iconAddress) ||
            !ctx.TryReadUInt32(requestAddress + 0x20, out var slotId))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!IsMemoryReady(userId, slotId))
        {
            return ctx.SetReturn(ErrorMemoryNotReady);
        }

        var directory = ResolveMemoryDirectory(userId, slotId);
        if (dataAddress != 0)
        {
            if (!TryReadMemoryData(ctx, dataAddress, out var data))
            {
                return ctx.SetReturn(MemoryFault);
            }

            var transferResult = TransferMemoryData(ctx, userId, slotId, data, write: false);
            if (transferResult != 0)
            {
                return transferResult;
            }
        }

        try
        {
            if (paramAddress != 0 &&
                !ctx.Memory.TryWrite(paramAddress, PackSaveDataParam(LoadParam(directory, "Saved Data Memory"))))
            {
                return ctx.SetReturn(MemoryFault);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }

        if (iconAddress != 0)
        {
            var iconResult = LoadIcon(ctx, iconAddress, GetIconPath(directory));
            if (iconResult == ErrorNotFound)
            {
                if (!ctx.TryWriteUInt64(iconAddress + 0x10, 0))
                {
                    return ctx.SetReturn(MemoryFault);
                }
            }
            else if (iconResult != 0)
            {
                return iconResult;
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cduy9v4YmT4",
        ExportName = "sceSaveDataSetSaveDataMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory2(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var dataAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x10, out var paramAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x18, out var iconAddress) ||
            !ctx.TryReadUInt32(requestAddress + 0x20, out var dataNum) ||
            !ctx.TryReadUInt32(requestAddress + 0x24, out var slotId))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!IsMemoryReady(userId, slotId))
        {
            return ctx.SetReturn(ErrorMemoryNotReady);
        }

        if (dataNum > 1024)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (dataAddress != 0)
        {
            var count = Math.Max(dataNum, 1u);
            for (var index = 0u; index < count; index++)
            {
                if (!TryReadMemoryData(ctx, dataAddress + ((ulong)index * MemoryDataSize), out var data))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                var transferResult = TransferMemoryData(ctx, userId, slotId, data, write: true);
                if (transferResult != 0)
                {
                    return transferResult;
                }
            }
        }

        var directory = ResolveMemoryDirectory(userId, slotId);
        try
        {
            if (paramAddress != 0)
            {
                var packed = new byte[ParamSize];
                if (!ctx.Memory.TryRead(paramAddress, packed))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                SaveParam(directory, UnpackSaveDataParam(packed));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }

        if (iconAddress != 0)
        {
            var iconResult = SaveIcon(ctx, iconAddress, GetIconPath(directory));
            if (iconResult != 0)
            {
                return iconResult;
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "wiT9jeC7xPw",
        ExportName = "sceSaveDataSyncSaveDataMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSyncSaveDataMemory(CpuContext ctx)
    {
        var syncAddress = ctx[CpuRegister.Rdi];
        if (syncAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(syncAddress, out var userId) ||
            !ctx.TryReadUInt32(syncAddress + 0x04, out var slotId))
        {
            return ctx.SetReturn(MemoryFault);
        }

        return ctx.SetReturn(IsMemoryReady(userId, slotId) ? 0 : ErrorMemoryNotReady);
    }

    [SysAbiExport(
        Nid = "gjRZNnw0JPE",
        ExportName = "sceSaveDataCreateTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCreateTransactionResource(CpuContext ctx)
    {
        int resource;
        lock (StateGate)
        {
            resource = ++_nextTransactionResource;
            TransactionResources.Add(resource);
        }

        Trace($"create_transaction_resource memory_size={ctx[CpuRegister.Rdi]} resource={resource}");
        return ctx.SetReturn(resource);
    }

    [SysAbiExport(
        Nid = "lJUQuaKqoKY",
        ExportName = "sceSaveDataDeleteTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteTransactionResource(CpuContext ctx)
    {
        var resource = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (StateGate)
        {
            TransactionResources.Remove(resource);
            PreparedTransactionResources.Remove(resource);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "sDCBrmc61XU",
        ExportName = "sceSaveDataPrepare",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataPrepare(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var resource = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!TryGetMountedSave(ctx, mountPointAddress, out _, out var error))
        {
            return ctx.SetReturn(error);
        }

        lock (StateGate)
        {
            if (resource != 0 && !TransactionResources.Contains(resource))
            {
                return ctx.SetReturn(ErrorParameter);
            }

            if (resource != 0)
            {
                PreparedTransactionResources.Add(resource);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ie7qhZ4X0Cc",
        ExportName = "sceSaveDataCommit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCommit(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        lock (StateGate)
        {
            PreparedTransactionResources.Clear();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "WAzWTZm1H+I",
        ExportName = "sceSaveDataTransferringMount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataTransferringMount(CpuContext ctx) => ctx.SetReturn(ErrorNotFound);

    internal static string BuildTitleSaveRoot(string root, int userId, string titleId) =>
        Path.Combine(
            Path.GetFullPath(root),
            userId.ToString(CultureInfo.InvariantCulture),
            SanitizePathSegment(titleId));

    internal static string BuildSaveDirectoryPath(
        string root,
        int userId,
        string titleId,
        string dirName) =>
        Path.Combine(BuildTitleSaveRoot(root, userId, titleId), SanitizePathSegment(dirName));

    internal static byte[] PackSaveDataParam(SaveDataParamValue value)
    {
        var packed = new byte[ParamSize];
        WriteAscii(packed.AsSpan(0x00, 128), value.Title);
        WriteAscii(packed.AsSpan(0x80, 128), value.Subtitle);
        WriteAscii(packed.AsSpan(0x100, 1024), value.Detail);
        BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(0x500), value.UserParam);
        BinaryPrimitives.WriteInt64LittleEndian(packed.AsSpan(0x508), value.MTime);
        return packed;
    }

    internal static SaveDataParamValue UnpackSaveDataParam(ReadOnlySpan<byte> packed)
    {
        if (packed.Length < ParamSize)
        {
            throw new ArgumentException("Save data parameter buffer is too small.", nameof(packed));
        }

        return new SaveDataParamValue(
            DecodeAscii(packed.Slice(0x00, 128)),
            DecodeAscii(packed.Slice(0x80, 128)),
            DecodeAscii(packed.Slice(0x100, 1024)),
            BinaryPrimitives.ReadUInt32LittleEndian(packed[0x500..]),
            BinaryPrimitives.ReadInt64LittleEndian(packed[0x508..]));
    }

    internal static void EnsureMemoryFile(string path, ulong size)
    {
        if (size > MaximumMemorySize)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if ((ulong)stream.Length < size)
        {
            stream.SetLength(checked((long)size));
        }
    }

    internal static void WriteMemoryFile(string path, long offset, ReadOnlySpan<byte> data)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        ValidateFileRange(stream.Length, offset, data.Length);
        stream.Position = offset;
        stream.Write(data);
        stream.Flush();
    }

    internal static byte[] ReadMemoryFile(string path, long offset, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ValidateFileRange(stream.Length, offset, count);
        var data = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(data);
        return data;
    }

    internal static void CopySaveDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(source);
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int TransferMemoryData(
        CpuContext ctx,
        int userId,
        uint slotId,
        MemoryData data,
        bool write)
    {
        if (!IsMemoryReady(userId, slotId))
        {
            return ctx.SetReturn(ErrorMemoryNotReady);
        }

        if (data.Offset < 0 || data.BufferSize > int.MaxValue ||
            (data.BufferSize != 0 && data.BufferAddress == 0))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            var path = Path.Combine(ResolveMemoryDirectory(userId, slotId), "memory.dat");
            var fileLength = new FileInfo(path).Length;
            if ((ulong)data.Offset > (ulong)fileLength || data.BufferSize > (ulong)fileLength - (ulong)data.Offset)
            {
                return ctx.SetReturn(ErrorParameter);
            }

            var count = checked((int)data.BufferSize);
            if (write)
            {
                var buffer = new byte[count];
                if (count != 0 && !ctx.Memory.TryRead(data.BufferAddress, buffer))
                {
                    return ctx.SetReturn(MemoryFault);
                }

                lock (StateGate)
                {
                    WriteMemoryFile(path, data.Offset, buffer);
                }
            }
            else
            {
                byte[] buffer;
                lock (StateGate)
                {
                    buffer = ReadMemoryFile(path, data.Offset, count);
                }

                if (count != 0 && !ctx.Memory.TryWrite(data.BufferAddress, buffer))
                {
                    return ctx.SetReturn(MemoryFault);
                }
            }

            Trace($"memory_{(write ? "set" : "get")} user={userId} slot={slotId} offset={data.Offset} size={count}");
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    private static bool IsMemoryReady(int userId, uint slotId)
    {
        if (userId < 0)
        {
            return false;
        }

        lock (StateGate)
        {
            return ReadyMemorySlots.Contains((userId, slotId));
        }
    }

    private static string ResolveMemoryDirectory(int userId, uint slotId)
    {
        var name = slotId == 0 ? "sce_sdmemory" : $"sce_sdmemory{slotId}";
        return Path.Combine(BuildTitleSaveRoot(ResolveSaveDataRoot(), userId, ResolveConfiguredTitleId()), name);
    }

    private static bool TryReadMemoryData(CpuContext ctx, ulong address, out MemoryData data)
    {
        data = default;
        if (!ctx.TryReadUInt64(address, out var bufferAddress) ||
            !ctx.TryReadUInt64(address + 0x08, out var bufferSize) ||
            !ctx.TryReadUInt64(address + 0x10, out var rawOffset))
        {
            return false;
        }

        data = new MemoryData(bufferAddress, bufferSize, unchecked((long)rawOffset));
        return true;
    }

    private static int LoadIcon(CpuContext ctx, ulong iconAddress, string path)
    {
        if (iconAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadUInt64(iconAddress, out var bufferAddress) ||
            !ctx.TryReadUInt64(iconAddress + 0x08, out var bufferSize))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (bufferSize > MaximumIconSize || (bufferSize != 0 && bufferAddress == 0))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        try
        {
            if (!File.Exists(path))
            {
                return ctx.SetReturn(ErrorNotFound);
            }

            var length = new FileInfo(path).Length;
            if ((ulong)length > MaximumIconSize)
            {
                return ctx.SetReturn(ErrorInternal);
            }

            var icon = File.ReadAllBytes(path);
            var copySize = Math.Min(icon.Length, checked((int)bufferSize));
            if ((copySize != 0 && !ctx.Memory.TryWrite(bufferAddress, icon.AsSpan(0, copySize))) ||
                !ctx.TryWriteUInt64(iconAddress + 0x10, checked((ulong)icon.Length)))
            {
                return ctx.SetReturn(MemoryFault);
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    private static int SaveIcon(CpuContext ctx, ulong iconAddress, string path)
    {
        if (iconAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadUInt64(iconAddress, out var bufferAddress) ||
            !ctx.TryReadUInt64(iconAddress + 0x08, out var bufferSize) ||
            !ctx.TryReadUInt64(iconAddress + 0x10, out var dataSize))
        {
            return ctx.SetReturn(MemoryFault);
        }

        var size = Math.Min(bufferSize, dataSize);
        if (size > MaximumIconSize || (size != 0 && bufferAddress == 0))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        var icon = new byte[checked((int)size)];
        if (icon.Length != 0 && !ctx.Memory.TryRead(bufferAddress, icon))
        {
            return ctx.SetReturn(MemoryFault);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, icon);
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    private static bool TryGetMountedSave(
        CpuContext ctx,
        ulong mountPointAddress,
        out MountedSave mounted,
        out int error)
    {
        mounted = default;
        if (mountPointAddress == 0)
        {
            error = ErrorParameter;
            return false;
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, MountPointSize, out var mountPoint))
        {
            error = MemoryFault;
            return false;
        }

        lock (StateGate)
        {
            if (!Mounts.TryGetValue(mountPoint, out mounted))
            {
                error = ErrorNotMounted;
                return false;
            }
        }

        error = 0;
        return true;
    }

    private static void CreateSaveLayout(string savePath, string dirName, ulong blocks)
    {
        Directory.CreateDirectory(savePath);
        Directory.CreateDirectory(GetSystemPath(savePath));
        SaveParam(
            savePath,
            new SaveDataParamValue(
                "Saved Data",
                string.Empty,
                dirName,
                0,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        File.WriteAllBytes(Path.Combine(GetSystemPath(savePath), "keystone"), new byte[0x60]);
        WriteBlocks(savePath, blocks == 0 ? DefaultBlocks : blocks);
    }

    private static SaveDataParamValue LoadParam(string savePath, string fallbackDetail)
    {
        var path = GetParamPath(savePath);
        if (File.Exists(path))
        {
            var packed = File.ReadAllBytes(path);
            if (packed.Length >= ParamSize)
            {
                return UnpackSaveDataParam(packed);
            }
        }

        var mtime = Directory.Exists(savePath)
            ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(savePath)).ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new SaveDataParamValue("Saved Data", string.Empty, fallbackDetail, 0, mtime);
    }

    private static void SaveParam(string savePath, SaveDataParamValue value)
    {
        Directory.CreateDirectory(GetSystemPath(savePath));
        File.WriteAllBytes(GetParamPath(savePath), PackSaveDataParam(value));
    }

    private static string GetSystemPath(string savePath) => Path.Combine(savePath, "sce_sys");

    private static string GetParamPath(string savePath) => Path.Combine(GetSystemPath(savePath), "param.bin");

    private static string GetIconPath(string savePath) => Path.Combine(GetSystemPath(savePath), "icon0.png");

    private static string GetBlocksPath(string savePath) => Path.Combine(GetSystemPath(savePath), "blocks.bin");

    private static void WriteBlocks(string savePath, ulong blocks)
    {
        Span<byte> packed = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(packed, Math.Max(blocks, DefaultBlocks));
        File.WriteAllBytes(GetBlocksPath(savePath), packed);
    }

    private static ulong ReadBlocks(string savePath, ulong fallback)
    {
        var path = GetBlocksPath(savePath);
        if (!File.Exists(path))
        {
            return Math.Max(fallback, DefaultBlocks);
        }

        var packed = File.ReadAllBytes(path);
        return packed.Length >= sizeof(ulong)
            ? Math.Max(BinaryPrimitives.ReadUInt64LittleEndian(packed), DefaultBlocks)
            : Math.Max(fallback, DefaultBlocks);
    }

    private static List<SaveEntry> EnumerateSaveDirectories(string root, string pattern)
    {
        var entries = new List<SaveEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("sce_", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(pattern) && !MatchPattern(name, pattern)))
            {
                continue;
            }

            var usedBlocks = BytesToBlocks(GetDirectorySize(directory));
            var blocks = ReadBlocks(directory, Math.Max(DefaultBlocks, usedBlocks));
            entries.Add(new SaveEntry(
                name,
                directory,
                LoadParam(directory, name),
                blocks,
                SaturatingSubtract(blocks, usedBlocks)));
        }

        return entries;
    }

    private static List<SaveEntry> SortEntries(List<SaveEntry> entries, uint sortKey, uint sortOrder)
    {
        IOrderedEnumerable<SaveEntry> sorted = sortKey switch
        {
            1 => entries.OrderBy(entry => entry.Param.UserParam),
            2 => entries.OrderBy(entry => entry.Blocks),
            3 => entries.OrderBy(entry => entry.Param.MTime),
            5 => entries.OrderBy(entry => entry.FreeBlocks),
            _ => entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };
        return sortOrder == 0 ? sorted.ToList() : sorted.Reverse().ToList();
    }

    private static bool TryWriteSearchInfo(CpuContext ctx, ulong address, SaveEntry entry)
    {
        Span<byte> info = stackalloc byte[SearchInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, entry.Blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], entry.FreeBlocks);
        return ctx.Memory.TryWrite(address, info);
    }

    private static bool TryReadSearchCond(CpuContext ctx, ulong address, out SearchCond cond)
    {
        cond = default;
        if (!ctx.TryReadInt32(address, out var userId) ||
            !ctx.TryReadUInt64(address + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(address + 0x10, out var dirNameAddress) ||
            !ctx.TryReadUInt32(address + 0x18, out var sortKey) ||
            !ctx.TryReadUInt32(address + 0x1C, out var sortOrder))
        {
            return false;
        }

        var pattern = string.Empty;
        if (dirNameAddress != 0 && !TryReadFixedAscii(ctx, dirNameAddress, DirNameSize, out pattern))
        {
            return false;
        }

        cond = new SearchCond(userId, titleIdAddress, pattern, sortKey, sortOrder);
        return true;
    }

    private static bool TryReadSearchResult(CpuContext ctx, ulong address, out SearchResult result)
    {
        result = default;
        if (!ctx.TryReadUInt64(address + 0x08, out var dirNamesAddress) ||
            !ctx.TryReadUInt32(address + 0x10, out var dirNamesNum) ||
            !ctx.TryReadUInt64(address + 0x18, out var paramsAddress) ||
            !ctx.TryReadUInt64(address + 0x20, out var infosAddress))
        {
            return false;
        }

        result = new SearchResult(dirNamesAddress, dirNamesNum, paramsAddress, infosAddress);
        return true;
    }

    private static bool TryReadOptionalTitleId(CpuContext ctx, ulong address, out string titleId)
    {
        if (address == 0)
        {
            titleId = ResolveConfiguredTitleId();
            return true;
        }

        return TryReadFixedAscii(ctx, address, TitleIdSize, out titleId);
    }

    private static string ResolveSaveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "user", "savedata")
            : configured);
    }

    private static string ResolveConfiguredTitleId()
    {
        lock (StateGate)
        {
            if (!string.IsNullOrWhiteSpace(_titleId))
            {
                return _titleId;
            }
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var app0Name = string.IsNullOrWhiteSpace(app0Root)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (!string.IsNullOrWhiteSpace(app0Name))
        {
            var candidate = app0Name.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return SanitizePathSegment(candidate);
            }
        }

        return "default";
    }

    private static string SanitizePathSegment(string value)
    {
        var trimmed = value.Trim();
        var chars = trimmed.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "default" : sanitized;
    }

    [SysAbiExport(Nid = "dQ2GohUHXzk", ExportName = "sceSaveDataAbort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataAbort(CpuContext ctx)
    {
        lock (StateGate)
        {
            _backupProgress = 0;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "z1JA8-iJt3k", ExportName = "sceSaveDataBackup", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataBackup(CpuContext ctx) => BackupOrRestore(ctx, restore: false);

    [SysAbiExport(Nid = "kLJQ3XioYiU", ExportName = "sceSaveDataBindPsnAccount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataBindPsnAccount(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "hHHCPRqA3+g", ExportName = "sceSaveDataBindPsnAccountForSystemBackup", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataBindPsnAccountForSystemBackup(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "ykwIZfVD08s", ExportName = "sceSaveDataChangeDatabase", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataChangeDatabase(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "G0hFeOdRCUs", ExportName = "sceSaveDataChangeInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataChangeInternal(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "RQOqDbk3bSU", ExportName = "sceSaveDataCheckBackupData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckBackupData(CpuContext ctx) => CheckBackupData(ctx);

    [SysAbiExport(Nid = "rYvLW1z2poM", ExportName = "sceSaveDataCheckBackupDataForCdlg", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckBackupDataForCdlg(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "v1TrX+3ZB10", ExportName = "sceSaveDataCheckBackupDataInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckBackupDataInternal(CpuContext ctx) => CheckBackupData(ctx);

    [SysAbiExport(Nid = "-eczr5e4dsI", ExportName = "sceSaveDataCheckCloudData", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckCloudData(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x20);

    [SysAbiExport(Nid = "4OPOZxfVkHA", ExportName = "sceSaveDataCheckIpmiIfSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckIpmiIfSize(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "1i0rfc+mfa8", ExportName = "sceSaveDataCheckSaveDataBroken", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckSaveDataBroken(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "p6A1adyQi3E", ExportName = "sceSaveDataCheckSaveDataVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckSaveDataVersion(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "S49B+I96kpk", ExportName = "sceSaveDataCheckSaveDataVersionLatest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCheckSaveDataVersionLatest(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 1);

    [SysAbiExport(Nid = "Wz-4JZfeO9g", ExportName = "sceSaveDataClearProgress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataClearProgress(CpuContext ctx)
    {
        lock (StateGate)
        {
            _backupProgress = 0;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "kbIIP9aXK9A", ExportName = "sceSaveDataCreateUploadData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataCreateUploadData(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x20);

    [SysAbiExport(Nid = "gW6G4HxBBXA", ExportName = "sceSaveDataDebug", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebug(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "bYCnxLexU7M", ExportName = "sceSaveDataDebugCleanMount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugCleanMount(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "hVDqYB8+jkk", ExportName = "sceSaveDataDebugCompiledSdkVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugCompiledSdkVersion(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 0x08000000);

    [SysAbiExport(Nid = "K9gXXlrVLNI", ExportName = "sceSaveDataDebugCreateSaveDataRoot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugCreateSaveDataRoot(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(Nid = "5yHFvMwZX2o", ExportName = "sceSaveDataDebugGetThreadId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugGetThreadId(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "UGTldPVEdB4", ExportName = "sceSaveDataDebugRemoveSaveDataRoot", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugRemoveSaveDataRoot(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "AYBQmnRplrg", ExportName = "sceSaveDataDebugTarget", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDebugTarget(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "SQWusLoK8Pw", ExportName = "sceSaveDataDelete5", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDelete5(CpuContext ctx) => SaveDataDelete(ctx);

    [SysAbiExport(Nid = "pJrlpCgR8h4", ExportName = "sceSaveDataDeleteAllUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteAllUser(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "fU43mJUgKcM", ExportName = "sceSaveDataDeleteCloudData", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteCloudData(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "uZqc4JpFdeY", ExportName = "sceSaveDataDeleteUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteUser(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "xJ5NFWC3m+k", ExportName = "sceSaveDataDirNameSearchInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDirNameSearchInternal(CpuContext ctx) => SaveDataDirNameSearch(ctx);

    [SysAbiExport(Nid = "h1nP9EYv3uc", ExportName = "sceSaveDataDownload", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDownload(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "A1ThglSGUwA", ExportName = "sceSaveDataGetAllSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAllSize(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x20);

    [SysAbiExport(Nid = "KuXcrMAQIMQ", ExportName = "sceSaveDataGetAppLaunchedUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAppLaunchedUser(CpuContext ctx)
    {
        int userId;
        lock (StateGate)
        {
            userId = _saveDataLibraryUser;
        }

        return WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], unchecked((uint)userId));
    }

    [SysAbiExport(Nid = "itZ46iH14Vs", ExportName = "sceSaveDataGetAutoUploadConditions", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAutoUploadConditions(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x20);

    [SysAbiExport(Nid = "PL20kjAXZZ4", ExportName = "sceSaveDataGetAutoUploadRequestInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAutoUploadRequestInfo(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x20);

    [SysAbiExport(Nid = "G12foE0S77E", ExportName = "sceSaveDataGetAutoUploadSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAutoUploadSetting(CpuContext ctx)
    {
        uint setting;
        lock (StateGate)
        {
            setting = _autoUploadSetting;
        }

        return WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], setting);
    }

    [SysAbiExport(Nid = "PzDtD6eBXIM", ExportName = "sceSaveDataGetBoundPsnAccountCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetBoundPsnAccountCount(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "tu0SDPl+h88", ExportName = "sceSaveDataGetClientThreadPriority", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetClientThreadPriority(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 700);

    [SysAbiExport(Nid = "6lZYZqQPfkY", ExportName = "sceSaveDataGetCloudQuotaInfo", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetCloudQuotaInfo(CpuContext ctx) => ZeroOptionalOutput(ctx, ctx[CpuRegister.Rsi], 0x30);

    [SysAbiExport(Nid = "CWlBd2Ay1M4", ExportName = "sceSaveDataGetDataBaseFilePath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetDataBaseFilePath(CpuContext ctx) =>
        WriteOptionalPath(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], Path.Combine(ResolveSaveDataRoot(), "savedata.db"));

    [SysAbiExport(Nid = "eBSSNIG6hMk", ExportName = "sceSaveDataGetEventInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetEventInfo(CpuContext ctx)
    {
        uint count;
        lock (StateGate)
        {
            count = checked((uint)PendingEvents.Count);
        }

        return WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], count);
    }

    [SysAbiExport(Nid = "j8xKtiFj0SY", ExportName = "sceSaveDataGetEventResult", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetEventResult(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rsi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        SaveDataEventValue value;
        lock (StateGate)
        {
            if (!PendingEvents.TryDequeue(out value))
            {
                return ctx.SetReturn(ErrorNotFound);
            }
        }

        var result = PackSaveDataEvent(value.Type, value.UserId, value.TitleId, value.DirName);
        return ctx.Memory.TryWrite(eventAddress, result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(MemoryFault);
    }

    [SysAbiExport(Nid = "UMpxor4AlKQ", ExportName = "sceSaveDataGetFormat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetFormat(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], 0);

    [SysAbiExport(Nid = "pc4guaUPVqA", ExportName = "sceSaveDataGetMountedSaveDataCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetMountedSaveDataCount(CpuContext ctx)
    {
        uint count;
        lock (StateGate)
        {
            count = checked((uint)Mounts.Count);
        }

        return WriteOptionalUInt32(ctx, ctx[CpuRegister.Rdi], count);
    }

    [SysAbiExport(Nid = "ANmSWUiyyGQ", ExportName = "sceSaveDataGetProgress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetProgress(CpuContext ctx)
    {
        var progressAddress = ctx[CpuRegister.Rdi];
        if (progressAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        float progress;
        lock (StateGate)
        {
            progress = _backupProgress;
        }

        return ctx.TryWriteUInt32(progressAddress, unchecked((uint)BitConverter.SingleToInt32Bits(progress)))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(MemoryFault);
    }

    [SysAbiExport(Nid = "SN7rTPHS+Cg", ExportName = "sceSaveDataGetSaveDataCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataCount(CpuContext ctx)
    {
        var countAddress = ctx[CpuRegister.Rsi];
        if (countAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        var userId = unchecked((int)(uint)ctx[CpuRegister.Rdi]);
        uint count = 0;
        try
        {
            var root = BuildTitleSaveRoot(ResolveSaveDataRoot(), userId, ResolveConfiguredTitleId());
            if (Directory.Exists(root))
            {
                count = checked((uint)EnumerateSaveDirectories(root, "%").Count);
            }

            return WriteOptionalUInt32(ctx, countAddress, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(Nid = "+bRDRotfj0Y", ExportName = "sceSaveDataGetSaveDataRootDir", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataRootDir(CpuContext ctx) =>
        WriteOptionalPath(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ResolveSaveDataRoot());

    [SysAbiExport(Nid = "3luF0xq0DkQ", ExportName = "sceSaveDataGetSaveDataRootPath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataRootPath(CpuContext ctx) =>
        WriteOptionalPath(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ResolveSaveDataRoot());

    [SysAbiExport(Nid = "DwAvlQGvf1o", ExportName = "sceSaveDataGetSaveDataRootUsbPath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataRootUsbPath(CpuContext ctx) =>
        WriteOptionalPath(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], Path.Combine(ResolveSaveDataRoot(), "usb"));

    [SysAbiExport(Nid = "kb24-4DLyNo", ExportName = "sceSaveDataGetSavePoint", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSavePoint(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "OYmnApJ9q+U", ExportName = "sceSaveDataGetUpdatedDataCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetUpdatedDataCount(CpuContext ctx) => WriteOptionalUInt32(ctx, ctx[CpuRegister.Rsi], 0);

    [SysAbiExport(Nid = "ZkZhskCPXFw", ExportName = "sceSaveDataInitialize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize(CpuContext ctx) => SaveDataInitialize3(ctx);

    [SysAbiExport(Nid = "l1NmDeDpNGU", ExportName = "sceSaveDataInitialize2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize2(CpuContext ctx) => SaveDataInitialize3(ctx);

    [SysAbiExport(Nid = "g9uwUI3BlQ8", ExportName = "sceSaveDataInitializeForCdlg", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataInitializeForCdlg(CpuContext ctx) => SaveDataInitialize3(ctx);

    [SysAbiExport(Nid = "voAQW45oKuo", ExportName = "sceSaveDataIsDeletingUsbDb", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataIsDeletingUsbDb(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "ieP6jP138Qo", ExportName = "sceSaveDataIsMounted", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataIsMounted(CpuContext ctx)
    {
        if (!TryReadFixedAscii(ctx, ctx[CpuRegister.Rdi], MountPointSize, out var mountPoint))
        {
            return ctx.SetReturn(0);
        }

        lock (StateGate)
        {
            return ctx.SetReturn(Mounts.ContainsKey(mountPoint) ? 1 : 0);
        }
    }

    [SysAbiExport(Nid = "xz0YMi6BfNk", ExportName = "sceSaveDataMount5", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMount5(CpuContext ctx) => SaveDataMount3(ctx);

    [SysAbiExport(Nid = "msCER7Iibm8", ExportName = "sceSaveDataMountInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMountInternal(CpuContext ctx) => SaveDataMount3(ctx);

    [SysAbiExport(Nid = "-XYmdxjOqyA", ExportName = "sceSaveDataMountSys", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMountSys(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        return ZeroOptionalOutput(ctx, resultAddress, MountResultSize);
    }

    [SysAbiExport(Nid = "uNu7j3pL2mQ", ExportName = "sceSaveDataPromote5", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataPromote5(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "SgIY-XYA2Xg", ExportName = "sceSaveDataRebuildDatabase", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataRebuildDatabase(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(Nid = "hsKd5c21sQc", ExportName = "sceSaveDataRegisterEventCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataRegisterEventCallback(CpuContext ctx)
    {
        lock (StateGate)
        {
            _eventCallback = ctx[CpuRegister.Rdi];
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "lU9YRFsgwSU", ExportName = "sceSaveDataRestoreBackupData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataRestoreBackupData(CpuContext ctx) => BackupOrRestore(ctx, restore: true);

    [SysAbiExport(Nid = "HuToUt1GQ8w", ExportName = "sceSaveDataRestoreBackupDataForCdlg", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataRestoreBackupDataForCdlg(CpuContext ctx) => BackupOrRestore(ctx, restore: true);

    [SysAbiExport(Nid = "aoZKKNjlq3Y", ExportName = "sceSaveDataRestoreLoadSaveDataMemory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataRestoreLoadSaveDataMemory(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "0VFHv-Fa4w8", ExportName = "sceSaveDataSetAutoUploadSetting", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetAutoUploadSetting(CpuContext ctx)
    {
        lock (StateGate)
        {
            _autoUploadSetting = unchecked((uint)ctx[CpuRegister.Rsi]);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "52pL2GKkdjA", ExportName = "sceSaveDataSetEventInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetEventInfo(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "v3vg2+cooYw", ExportName = "sceSaveDataSetSaveDataLibraryUser", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataLibraryUser(CpuContext ctx)
    {
        lock (StateGate)
        {
            _saveDataLibraryUser = unchecked((int)(uint)ctx[CpuRegister.Rdi]);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "zMgXM79jRhw", ExportName = "sceSaveDataShutdownStart", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataShutdownStart(CpuContext ctx)
    {
        lock (StateGate)
        {
            _eventCallback = 0;
            PendingEvents.Clear();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "+orZm32HB1s", ExportName = "sceSaveDataSupportedFakeBrokenStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSupportedFakeBrokenStatus(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "LMSQUTxmGVg", ExportName = "sceSaveDataSyncCloudList", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSyncCloudList(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "2-8NWLS8QSA", ExportName = "sceSaveDataUmountSys", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataUmountSys(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        return mountPointAddress == 0 ? ctx.SetReturn(0) : Umount(ctx, mountPointAddress);
    }

    [SysAbiExport(Nid = "VwadwBBBJ80", ExportName = "sceSaveDataUmountWithBackup", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataUmountWithBackup(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        if (!TryGetMountedSave(ctx, mountPointAddress, out var mounted, out var error))
        {
            return ctx.SetReturn(error);
        }

        try
        {
            CreateSaveBackup(mounted.HostPath);
            lock (StateGate)
            {
                _backupProgress = 1.0f;
            }

            return Umount(ctx, mountPointAddress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    [SysAbiExport(Nid = "v-AK1AxQhS0", ExportName = "sceSaveDataUnregisterEventCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataUnregisterEventCallback(CpuContext ctx)
    {
        lock (StateGate)
        {
            _eventCallback = 0;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "COwz3WBj+5s", ExportName = "sceSaveDataUpload", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataUpload(CpuContext ctx) => ctx.SetReturn(0);

    private static int BackupOrRestore(CpuContext ctx, bool restore)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x10, out var dirNameAddress))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (userId < 0 || dirNameAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadOptionalTitleId(ctx, titleIdAddress, out var titleId) ||
            !TryReadFixedAscii(ctx, dirNameAddress, DirNameSize, out var dirName))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(ErrorParameter);
        }

        var savePath = BuildSaveDirectoryPath(ResolveSaveDataRoot(), userId, titleId, dirName);
        lock (StateGate)
        {
            if (Mounts.Values.Any(mount => PathsEqual(mount.HostPath, savePath)))
            {
                return ctx.SetReturn(ErrorBusy);
            }
        }

        try
        {
            if (!Directory.Exists(savePath))
            {
                return ctx.SetReturn(ErrorNotFound);
            }

            if (restore)
            {
                if (!RestoreSaveBackup(savePath))
                {
                    return ctx.SetReturn(ErrorNotFound);
                }
            }
            else
            {
                CreateSaveBackup(savePath);
            }

            lock (StateGate)
            {
                _backupProgress = 1.0f;
                PendingEvents.Enqueue(new SaveDataEventValue(restore ? 0u : 2u, userId, titleId, dirName));
                while (PendingEvents.Count > 20)
                {
                    PendingEvents.Dequeue();
                }
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    private static int CheckBackupData(CpuContext ctx)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!ctx.TryReadInt32(requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x10, out var dirNameAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x18, out var paramAddress) ||
            !ctx.TryReadUInt64(requestAddress + 0x20, out var iconAddress))
        {
            return ctx.SetReturn(MemoryFault);
        }

        if (userId < 0 || dirNameAddress == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        if (!TryReadOptionalTitleId(ctx, titleIdAddress, out var titleId) ||
            !TryReadFixedAscii(ctx, dirNameAddress, DirNameSize, out var dirName))
        {
            return ctx.SetReturn(MemoryFault);
        }

        try
        {
            var savePath = BuildSaveDirectoryPath(ResolveSaveDataRoot(), userId, titleId, dirName);
            var backupPath = GetSaveBackupPath(savePath);
            if (!Directory.Exists(backupPath))
            {
                return ctx.SetReturn(ErrorNotFound);
            }

            if (paramAddress != 0)
            {
                var packed = PackSaveDataParam(LoadParam(backupPath, dirName));
                if (!ctx.Memory.TryWrite(paramAddress, packed))
                {
                    return ctx.SetReturn(MemoryFault);
                }
            }

            if (iconAddress != 0 && File.Exists(GetIconPath(backupPath)))
            {
                return LoadIcon(ctx, iconAddress, GetIconPath(backupPath));
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(ErrorInternal);
        }
    }

    internal static string GetSaveBackupPath(string savePath) => Path.Combine(savePath, "sce_backup");

    internal static void CreateSaveBackup(string savePath)
    {
        var backupPath = GetSaveBackupPath(savePath);
        var temporaryPath = Path.Combine(savePath, "sce_backup_tmp");
        var previousPath = Path.Combine(savePath, "sce_backup_old");
        if (Directory.Exists(temporaryPath))
        {
            Directory.Delete(temporaryPath, recursive: true);
        }

        if (Directory.Exists(previousPath))
        {
            Directory.Delete(previousPath, recursive: true);
        }

        Directory.CreateDirectory(temporaryPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(savePath))
        {
            var name = Path.GetFileName(entry);
            if (name is "sce_backup" or "sce_backup_tmp" or "sce_backup_old")
            {
                continue;
            }

            var destination = Path.Combine(temporaryPath, name);
            if (Directory.Exists(entry))
            {
                CopySaveDirectory(entry, destination);
            }
            else
            {
                File.Copy(entry, destination, overwrite: true);
            }
        }

        if (Directory.Exists(backupPath))
        {
            Directory.Move(backupPath, previousPath);
        }

        Directory.Move(temporaryPath, backupPath);
        if (Directory.Exists(previousPath))
        {
            Directory.Delete(previousPath, recursive: true);
        }
    }

    internal static bool RestoreSaveBackup(string savePath)
    {
        var backupPath = GetSaveBackupPath(savePath);
        if (!Directory.Exists(backupPath))
        {
            return false;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(savePath).ToArray())
        {
            if (PathsEqual(entry, backupPath))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(backupPath))
        {
            var destination = Path.Combine(savePath, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopySaveDirectory(entry, destination);
            }
            else
            {
                File.Copy(entry, destination, overwrite: true);
            }
        }

        return true;
    }

    internal static byte[] PackSaveDataEvent(uint type, int userId, string titleId, string dirName)
    {
        var result = new byte[0x68];
        BinaryPrimitives.WriteUInt32LittleEndian(result, type);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x08), userId);
        WriteAscii(result.AsSpan(0x10, TitleIdSize), titleId);
        WriteAscii(result.AsSpan(0x1A, DirNameSize), dirName);
        return result;
    }

    private static int ZeroOptionalOutput(CpuContext ctx, ulong address, int size)
    {
        if (address == 0)
        {
            return ctx.SetReturn(0);
        }

        return ctx.Memory.TryWrite(address, new byte[size])
            ? ctx.SetReturn(0)
            : ctx.SetReturn(MemoryFault);
    }

    private static int WriteOptionalUInt32(CpuContext ctx, ulong address, uint value)
    {
        if (address == 0)
        {
            return ctx.SetReturn(0);
        }

        return ctx.TryWriteUInt32(address, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(MemoryFault);
    }

    private static int WriteOptionalPath(CpuContext ctx, ulong address, ulong bufferSize, string value)
    {
        if (address == 0)
        {
            return ctx.SetReturn(0);
        }

        if (bufferSize == 0)
        {
            return ctx.SetReturn(ErrorParameter);
        }

        var encoded = EncodeAsciiString(value, bufferSize);
        return ctx.Memory.TryWrite(address, encoded)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(MemoryFault);
    }

    private static bool TryReadFixedAscii(CpuContext ctx, ulong address, int length, out string value) =>
        TryReadAscii(ctx, address, length, out value);

    private static bool TryReadAscii(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        var buffer = new byte[length];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        value = DecodeAscii(buffer);
        return true;
    }

    private static bool TryWriteFixedAscii(CpuContext ctx, ulong address, int length, string value)
    {
        Span<byte> buffer = stackalloc byte[length];
        buffer.Clear();
        WriteAscii(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static byte[] EncodeAsciiString(string value, ulong bufferSize)
    {
        if (bufferSize == 0 || bufferSize > int.MaxValue)
        {
            return [];
        }

        var output = new byte[Math.Min(checked((int)bufferSize), Encoding.ASCII.GetByteCount(value) + 1)];
        WriteAscii(output, value);
        return output;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? value : value[..length]);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        var encoded = Encoding.ASCII.GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static bool MatchPattern(string value, string pattern) =>
        MatchPattern(value.AsSpan(), pattern.AsSpan());

    private static bool MatchPattern(ReadOnlySpan<char> value, ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty)
        {
            return value.IsEmpty;
        }

        if (pattern[0] == '%')
        {
            for (var index = 0; index <= value.Length; index++)
            {
                if (MatchPattern(value[index..], pattern[1..]))
                {
                    return true;
                }
            }

            return false;
        }

        return !value.IsEmpty &&
            (pattern[0] == '_' || char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(value[0])) &&
            MatchPattern(value[1..], pattern[1..]);
    }

    private static void ValidateFileRange(long length, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > length || count > length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private static long GetDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }

    private static ulong BytesToBlocks(long bytes) => checked((ulong)((bytes + BlockSize - 1) / BlockSize));

    private static ulong SaturatingSubtract(ulong left, ulong right) => left > right ? left - right : 0;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] savedata.{message}");
        }
    }
}
