// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataExports
{
    private const int OrbisSaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int OrbisSaveDataErrorExists = unchecked((int)0x809F0007);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const int OrbisSaveDataErrorInternal = unchecked((int)0x809F000B);
    private const int SaveDataTitleIdSize = 10;
    private const int SaveDataDirNameSize = 32;
    private const int SaveDataParamSize = 0x530;
    private const int SaveDataSearchInfoSize = 0x30;
    private const ulong ResultHitNumOffset = 0x00;
    private const ulong ResultDirNamesOffset = 0x08;
    private const ulong ResultDirNamesNumOffset = 0x10;
    private const ulong ResultSetNumOffset = 0x14;
    private const ulong ResultParamsOffset = 0x18;
    private const ulong ResultInfosOffset = 0x20;
    private const uint SortKeyFreeBlocks = 5;
    private const uint SortOrderDescent = 1;
    private const uint MountModeCreate = 1u << 2;
    private const uint MountModeCreate2 = 1u << 5;
    private const int MountResultSize = 0x40;
    private static readonly object _stateGate = new();
    private static readonly HashSet<int> _transactionResources = [];
    private static readonly HashSet<int> _preparedTransactionResources = [];
    private static string? _titleId;
    private static int _nextTransactionResource;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (_stateGate)
        {
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId.Trim());
            _transactionResources.Clear();
            _preparedTransactionResources.Clear();
            _nextTransactionResource = 0;
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
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
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
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadSearchCond(ctx, condAddress, out var cond) ||
            !TryReadSearchResult(ctx, resultAddress, out var result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (cond.UserId < 0 || cond.SortKey > SortKeyFreeBlocks || cond.SortOrder > SortOrderDescent)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            string titleId;
            if (cond.TitleIdAddress == 0)
            {
                titleId = ResolveConfiguredTitleId();
            }
            else if (!TryReadFixedAscii(ctx, cond.TitleIdAddress, SaveDataTitleIdSize, out titleId))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var root = ResolveTitleSaveRoot(cond.UserId, titleId);
            var entries = Directory.Exists(root)
                ? EnumerateSaveDirectories(root, cond.Pattern)
                : [];

            entries = SortEntries(entries, cond.SortKey, cond.SortOrder);
            var setNum = result.DirNamesNum == 0
                ? 0
                : Math.Min(result.DirNamesNum, entries.Count);
            if (!ctx.TryWriteUInt32(resultAddress + ResultHitNumOffset, checked((uint)entries.Count)) ||
                !ctx.TryWriteUInt32(resultAddress + ResultSetNumOffset, checked((uint)setNum)))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (setNum == 0)
            {
                TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set=0 root='{root}'");
                return ctx.SetReturn(0);
            }

            if (result.DirNamesAddress == 0)
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            for (var i = 0; i < setNum; i++)
            {
                var entry = entries[i];
                if (!TryWriteFixedAscii(
                        ctx,
                        result.DirNamesAddress + ((ulong)i * SaveDataDirNameSize),
                        SaveDataDirNameSize,
                        entry.Name) ||
                    (result.ParamsAddress != 0 &&
                     !TryWriteParam(ctx, result.ParamsAddress + ((ulong)i * SaveDataParamSize), entry)) ||
                    (result.InfosAddress != 0 &&
                     !TryWriteSearchInfo(ctx, result.InfosAddress + ((ulong)i * SaveDataSearchInfoSize), entry)))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set={setNum} root='{root}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
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
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x08, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var blocks) ||
            !ctx.TryReadUInt64(mountAddress + 0x18, out var systemBlocks) ||
            !ctx.TryReadUInt32(mountAddress + 0x20, out var mountMode) ||
            !ctx.TryReadUInt32(mountAddress + 0x24, out var resource) ||
            !ctx.TryReadUInt32(mountAddress + 0x28, out var mode) ||
            dirNameAddress == 0 ||
            !TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out var dirName))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || string.IsNullOrWhiteSpace(dirName))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var titleId = ResolveConfiguredTitleId();
            var savePath = Path.Combine(
                ResolveTitleSaveRoot(userId, titleId),
                SanitizePathSegment(dirName));
            var existed = Directory.Exists(savePath);
            var create = (mountMode & MountModeCreate) != 0;
            var createIfMissing = (mountMode & MountModeCreate2) != 0;

            if (!existed && !create && !createIfMissing)
            {
                return ctx.SetReturn(OrbisSaveDataErrorNotFound);
            }

            if (existed && create)
            {
                return ctx.SetReturn(OrbisSaveDataErrorExists);
            }

            if (!existed)
            {
                Directory.CreateDirectory(savePath);
            }

            const string mountPoint = "/savedata0";
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, savePath);

            Span<byte> result = stackalloc byte[MountResultSize];
            result.Clear();
            WriteAscii(result[..16], mountPoint);
            BinaryPrimitives.WriteUInt32LittleEndian(result[0x1C..], createIfMissing && !existed ? 1u : 0u);
            if (!ctx.Memory.TryWrite(resultAddress, result))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceSaveData(
                $"mount3 user={userId} title={titleId} dir={dirName} blocks={blocks} " +
                $"system_blocks={systemBlocks} mount_mode=0x{mountMode:X} resource={resource} mode={mode} " +
                $"mount_point={mountPoint} created={!existed} root='{savePath}'");
            return ctx.SetReturn(0);
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (ArgumentException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }
    }

    // --- Save data memory (sceSaveData*SaveDataMemory2) ---
    // Struct layouts verified against shadPS4 savedata.cpp and Astro Bot's call
    // sites at 0x8010823A7 (Setup2), 0x801082846 (Get2), and 0x801082A9E (Set2).
    //
    // OrbisSaveDataMemorySetup2 (64 bytes, rdi):
    //   +0x00 u32 option, +0x04 s32 userId, +0x08 u64 memorySize,
    //   +0x10 u64 iconMemorySize, +0x18 initParam*, +0x20 initIcon*, +0x28 u32 slotId
    // OrbisSaveDataMemorySetupResult (rsi, may be NULL): +0x00 u64 existedMemorySize
    // OrbisSaveDataMemoryGet2 (64 bytes, rdi):
    //   +0x00 s32 userId, +0x08 OrbisSaveDataMemoryData* data, +0x10 param*,
    //   +0x18 icon*, +0x20 u32 slotId
    // OrbisSaveDataMemorySet2 (72 bytes, rdi):
    //   +0x00 s32 userId, +0x08 OrbisSaveDataMemoryData* data, +0x10 param*,
    //   +0x18 icon*, +0x20 u32 dataNum, +0x24 u32 slotId
    // OrbisSaveDataMemoryData (64 bytes):
    //   +0x00 void* buf, +0x08 u64 bufSize, +0x10 s64 offset
    private const int OrbisSaveDataErrorMemoryNotReady = unchecked((int)0x809F0012);
    private const ulong SaveDataMemoryMaxSize = 64UL * 1024 * 1024;
    private const int SaveDataMemoryDataSize = 0x40;
    private static readonly Dictionary<(int UserId, uint SlotId), byte[]> _memorySlots = [];

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
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadUInt32(setupAddress + 0x00, out var option) ||
            !ctx.TryReadInt32(setupAddress + 0x04, out var userId) ||
            !ctx.TryReadUInt64(setupAddress + 0x08, out var memorySize) ||
            !ctx.TryReadUInt64(setupAddress + 0x10, out var iconMemorySize) ||
            !ctx.TryReadUInt32(setupAddress + 0x28, out var slotId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || memorySize > SaveDataMemoryMaxSize)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        ulong existedSize;
        lock (_stateGate)
        {
            var key = (userId, slotId);
            if (_memorySlots.TryGetValue(key, out var existing))
            {
                existedSize = (ulong)existing.LongLength;
            }
            else
            {
                var persisted = TryLoadSaveDataMemory(userId, slotId);
                existedSize = persisted is null ? 0 : (ulong)persisted.LongLength;
                var buffer = new byte[Math.Max(memorySize, existedSize)];
                persisted?.CopyTo(buffer, 0);
                _memorySlots[key] = buffer;
            }
        }

        if (resultAddress != 0 && !ctx.TryWriteUInt64(resultAddress, existedSize))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceSaveData(
            $"setup_save_data_memory2 user={userId} slot={slotId} option=0x{option:X} " +
            $"memory_size={memorySize} icon_memory_size={iconMemorySize} existed_size={existedSize}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "QwOO7vegnV8",
        ExportName = "sceSaveDataGetSaveDataMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory2(CpuContext ctx)
    {
        var getAddress = ctx[CpuRegister.Rdi];
        if (getAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(getAddress + 0x00, out var userId) ||
            !ctx.TryReadUInt64(getAddress + 0x08, out var dataAddress) ||
            !ctx.TryReadUInt64(getAddress + 0x10, out var paramAddress) ||
            !ctx.TryReadUInt64(getAddress + 0x18, out var iconAddress) ||
            !ctx.TryReadUInt32(getAddress + 0x20, out var slotId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        byte[]? slot;
        lock (_stateGate)
        {
            _memorySlots.TryGetValue((userId, slotId), out slot);
        }

        if (slot is null)
        {
            return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
        }

        if (dataAddress != 0)
        {
            if (!TryReadMemoryData(ctx, dataAddress, out var data))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!TryCheckMemoryDataRange(data, slot, out var offset, out var count))
            {
                return ctx.SetReturn(OrbisSaveDataErrorParameter);
            }

            byte[] chunk;
            lock (_stateGate)
            {
                chunk = slot.AsSpan(offset, count).ToArray();
            }

            if (count > 0 && !ctx.Memory.TryWrite(data.BufAddress, chunk))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        // Astro Bot passes param == NULL and icon == NULL here; we do not
        // synthesize either to avoid writing fields we cannot justify.
        TraceSaveData(
            $"get_save_data_memory2 user={userId} slot={slotId} data=0x{dataAddress:X} " +
            $"param=0x{paramAddress:X} icon=0x{iconAddress:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cduy9v4YmT4",
        ExportName = "sceSaveDataSetSaveDataMemory2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory2(CpuContext ctx)
    {
        var setAddress = ctx[CpuRegister.Rdi];
        if (setAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!ctx.TryReadInt32(setAddress + 0x00, out var userId) ||
            !ctx.TryReadUInt64(setAddress + 0x08, out var dataAddress) ||
            !ctx.TryReadUInt32(setAddress + 0x20, out var dataNum) ||
            !ctx.TryReadUInt32(setAddress + 0x24, out var slotId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        byte[]? slot;
        lock (_stateGate)
        {
            _memorySlots.TryGetValue((userId, slotId), out slot);
        }

        if (slot is null)
        {
            return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
        }

        if (dataAddress != 0)
        {
            var entryCount = Math.Max(dataNum, 1u);
            for (var i = 0u; i < entryCount; i++)
            {
                var entryAddress = dataAddress + ((ulong)i * SaveDataMemoryDataSize);
                if (!TryReadMemoryData(ctx, entryAddress, out var data))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (!TryCheckMemoryDataRange(data, slot, out var offset, out var count))
                {
                    return ctx.SetReturn(OrbisSaveDataErrorParameter);
                }

                if (count == 0)
                {
                    continue;
                }

                var chunk = new byte[count];
                if (!ctx.Memory.TryRead(data.BufAddress, chunk))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                lock (_stateGate)
                {
                    chunk.CopyTo(slot.AsSpan(offset, count));
                }
            }

            PersistSaveDataMemory(userId, slotId, slot);
        }

        TraceSaveData(
            $"set_save_data_memory2 user={userId} slot={slotId} data=0x{dataAddress:X} data_num={dataNum}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "WAzWTZm1H+I",
        ExportName = "sceSaveDataTransferringMount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataTransferringMount(CpuContext ctx)
    {
        // No transferable (PS4) save data exists in the emulator: report
        // NOT_FOUND so the game takes its no-transfer path instead of a
        // kernel-space 0x80020002 its libSceSaveData wrappers were not
        // written to interpret. The mount-result output (rsi) is
        // intentionally left unwritten on this failure path.
        TraceSaveData(
            $"transferring_mount mount=0x{ctx[CpuRegister.Rdi]:X} result=0x{ctx[CpuRegister.Rsi]:X} -> NOT_FOUND");
        return ctx.SetReturn(OrbisSaveDataErrorNotFound);
    }

    private static bool TryReadMemoryData(CpuContext ctx, ulong address, out MemoryData data)
    {
        data = default;
        if (!ctx.TryReadUInt64(address + 0x00, out var bufAddress) ||
            !ctx.TryReadUInt64(address + 0x08, out var bufSize) ||
            !ctx.TryReadUInt64(address + 0x10, out var offset))
        {
            return false;
        }

        data = new MemoryData(bufAddress, bufSize, unchecked((long)offset));
        return true;
    }

    private static bool TryCheckMemoryDataRange(MemoryData data, byte[] slot, out int offset, out int count)
    {
        offset = 0;
        count = 0;
        if (data.Offset < 0 ||
            data.Offset > slot.LongLength ||
            data.BufSize > (ulong)(slot.LongLength - data.Offset))
        {
            return false;
        }

        if (data.BufSize > 0 && data.BufAddress == 0)
        {
            return false;
        }

        offset = checked((int)data.Offset);
        count = checked((int)data.BufSize);
        return true;
    }

    private static string ResolveSaveDataMemoryPath(int userId, uint slotId)
    {
        var directory = Path.Combine(
            ResolveTitleSaveRoot(userId, ResolveConfiguredTitleId()),
            slotId == 0 ? "sce_sdmemory" : $"sce_sdmemory{slotId}");
        return Path.Combine(directory, "memory.dat");
    }

    private static byte[]? TryLoadSaveDataMemory(int userId, uint slotId)
    {
        try
        {
            var path = ResolveSaveDataMemoryPath(userId, slotId);
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return (ulong)bytes.LongLength <= SaveDataMemoryMaxSize ? bytes : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void PersistSaveDataMemory(int userId, uint slotId, byte[] slot)
    {
        try
        {
            var path = ResolveSaveDataMemoryPath(userId, slotId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] snapshot;
            lock (_stateGate)
            {
                snapshot = (byte[])slot.Clone();
            }

            File.WriteAllBytes(path, snapshot);
        }
        catch (IOException)
        {
            // Guest memory copy already succeeded; persistence is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct MemoryData(ulong BufAddress, ulong BufSize, long Offset);

    [SysAbiExport(
        Nid = "gjRZNnw0JPE",
        ExportName = "sceSaveDataCreateTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCreateTransactionResource(CpuContext ctx)
    {
        var memorySize = ctx[CpuRegister.Rdi];
        int resource;
        lock (_stateGate)
        {
            resource = ++_nextTransactionResource;
            _transactionResources.Add(resource);
        }

        TraceSaveData($"create_transaction_resource memory_size=0x{memorySize:X} resource={resource}");
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
        lock (_stateGate)
        {
            _transactionResources.Remove(resource);
            _preparedTransactionResources.Remove(resource);
        }

        TraceSaveData($"delete_transaction_resource resource={resource}");
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
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            if (resource != 0)
            {
                _preparedTransactionResources.Add(resource);
            }
        }

        TraceSaveData($"prepare mount_point={mountPoint} resource={resource}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ie7qhZ4X0Cc",
        ExportName = "sceSaveDataCommit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCommit(CpuContext ctx)
    {
        var commitAddress = ctx[CpuRegister.Rdi];
        if (commitAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            _preparedTransactionResources.Clear();
        }

        TraceSaveData($"commit commit=0x{commitAddress:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "uW4vfTwMQVo",
        ExportName = "sceSaveDataUmount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUmount2(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        if (mountPointAddress == 0)
        {
            mountPointAddress = ctx[CpuRegister.Rsi];
        }

        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        var unmounted = KernelMemoryCompatExports.TryUnregisterGuestPathMount(mountPoint);
        TraceSaveData($"umount2 mount_point={mountPoint} unregistered={unmounted}");
        return ctx.SetReturn(0);
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

        string pattern;
        if (dirNameAddress == 0)
        {
            pattern = string.Empty;
        }
        else if (!TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out pattern))
        {
            return false;
        }

        cond = new SearchCond(userId, titleIdAddress, pattern, sortKey, sortOrder);
        return true;
    }

    private static bool TryReadSearchResult(CpuContext ctx, ulong address, out SearchResult result)
    {
        result = default;
        if (!ctx.TryReadUInt64(address + ResultDirNamesOffset, out var dirNamesAddress) ||
            !ctx.TryReadUInt32(address + ResultDirNamesNumOffset, out var dirNamesNum) ||
            !ctx.TryReadUInt64(address + ResultParamsOffset, out var paramsAddress) ||
            !ctx.TryReadUInt64(address + ResultInfosOffset, out var infosAddress))
        {
            return false;
        }

        result = new SearchResult(dirNamesAddress, dirNamesNum, paramsAddress, infosAddress);
        return true;
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

            var info = new DirectoryInfo(directory);
            entries.Add(new SaveEntry(name, directory, info.LastWriteTimeUtc));
        }

        return entries;
    }

    private static List<SaveEntry> SortEntries(List<SaveEntry> entries, uint sortKey, uint sortOrder)
    {
        IOrderedEnumerable<SaveEntry> sorted = sortKey switch
        {
            3 => entries.OrderBy(entry => entry.LastWriteUtc),
            _ => entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = sorted.ToList();
        if (sortOrder == SortOrderDescent)
        {
            list.Reverse();
        }

        return list;
    }

    private static bool TryWriteParam(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var param = new byte[SaveDataParamSize];
        WriteAscii(param.AsSpan(0x00, 128), "Saved Data");
        WriteAscii(param.AsSpan(0x100, 1024), entry.Name);
        BinaryPrimitives.WriteInt64LittleEndian(
            param.AsSpan(0x508, sizeof(long)),
            new DateTimeOffset(entry.LastWriteUtc).ToUnixTimeSeconds());
        return ctx.Memory.TryWrite(address, param);
    }

    private static bool TryWriteSearchInfo(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var size = GetDirectorySize(entry.Path);
        var usedBlocks = checked((ulong)((size + 32767) / 32768));
        var blocks = Math.Max(96UL, usedBlocks);
        Span<byte> info = stackalloc byte[SaveDataSearchInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x00..], blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], blocks - usedBlocks);
        return ctx.Memory.TryWrite(address, info);
    }

    private static long GetDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
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
            for (var i = 0; i <= value.Length; i++)
            {
                if (MatchPattern(value[i..], pattern[1..]))
                {
                    return true;
                }
            }

            return false;
        }

        if (value.IsEmpty)
        {
            return false;
        }

        if (pattern[0] == '_' ||
            char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(value[0]))
        {
            return MatchPattern(value[1..], pattern[1..]);
        }

        return false;
    }

    private static string ResolveTitleSaveRoot(int userId, string titleId) =>
        Path.Combine(ResolveSaveDataRoot(), userId.ToString(), SanitizePathSegment(titleId));

    private static string ResolveSaveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "user", "savedata")
            : configured;
        return Path.GetFullPath(root);
    }

    private static string ResolveConfiguredTitleId()
    {
        lock (_stateGate)
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
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static bool TryReadFixedAscii(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        Span<byte> buffer = stackalloc byte[length];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        var stringLength = buffer.IndexOf((byte)0);
        if (stringLength < 0)
        {
            stringLength = buffer.Length;
        }

        value = Encoding.ASCII.GetString(buffer[..stringLength]);
        return true;
    }

    private static bool TryWriteFixedAscii(CpuContext ctx, ulong address, int length, string value)
    {
        Span<byte> buffer = stackalloc byte[length];
        buffer.Clear();
        WriteAscii(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        var count = Math.Min(value.Length, Math.Max(0, destination.Length - 1));
        for (var i = 0; i < count; i++)
        {
            var ch = value[i];
            destination[i] = ch <= 0x7F ? (byte)ch : (byte)'?';
        }
    }

    private static void TraceSaveData(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] savedata.{message}");
        }
    }

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

    private readonly record struct SaveEntry(string Name, string Path, DateTime LastWriteUtc);
}
