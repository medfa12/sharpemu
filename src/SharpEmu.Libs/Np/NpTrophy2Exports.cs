// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpTrophy2Exports
{
    internal const int GameDetailsSize = 152;
    internal const int GameDataSize = 24;
    internal const int GroupDetailsSize = 152;
    internal const int GroupDataSize = 32;
    internal const int TrophyDetailsSize = 1312;
    internal const int TrophyDataSize = 32;

    private const int NpTrophy2ErrorInvalidArgument = unchecked((int)0x80553904);
    private const int NpTrophy2ErrorInvalidHandle = unchecked((int)0x80553908);
    private const int NpTrophy2ErrorInvalidContext = unchecked((int)0x80553909);
    private const int NpTrophy2ErrorInvalidTrophyId = unchecked((int)0x8055390A);
    private const int NpTrophy2ErrorInvalidGroupId = unchecked((int)0x8055390B);
    private const int NpTrophy2ErrorAlreadyUnlocked = unchecked((int)0x8055390C);
    private const int NpTrophy2ErrorNotRegistered = unchecked((int)0x8055390F);
    private const int NpTrophy2ErrorAlreadyRegistered = unchecked((int)0x80553910);
    private const int NpTrophy2ErrorIconFileNotFound = unchecked((int)0x80553911);
    private const int NpTrophyErrorInvalidArgument = unchecked((int)0x80551604);
    private const int NpTrophyErrorInvalidTrophyId = unchecked((int)0x8055160A);
    private const int NpTrophyErrorAlreadyUnlocked = unchecked((int)0x8055160C);
    private const int MaxTrophyId = 127;
    private const int BaseGameGroupId = 0;
    private const ulong UnixEpochRtcTick = 62_135_596_800_000_000;

    private static readonly object StateGate = new();
    private static readonly Dictionary<int, TrophyContextState> Contexts = [];
    private static readonly HashSet<int> Handles = [];
    private static int _nextContext = 1;
    private static int _nextHandle = 1;
    private static ulong _unlockCallback;
    private static ulong _unlockCallbackUserData;

    internal static string? TrophyStorageRootOverride;
    internal static string? TrophyTitleIdOverride;

    internal sealed record TrophyRecord(
        int Id,
        int Grade,
        int GroupId,
        bool Hidden,
        string Name,
        string Description,
        bool Unlocked,
        ulong TimestampTick);

    private sealed class TrophyRegistryFile
    {
        public int Version { get; set; } = 1;
        public string TitleId { get; set; } = "default";
        public List<TrophyRecord> Trophies { get; set; } = [];
    }

    private sealed class TrophyContextState(
        int userId,
        uint serviceLabel,
        string titleId,
        string registryPath)
    {
        public int UserId { get; } = userId;
        public uint ServiceLabel { get; } = serviceLabel;
        public string TitleId { get; } = titleId;
        public string RegistryPath { get; } = registryPath;
        public bool Registered { get; set; }
        public Dictionary<int, TrophyRecord> Trophies { get; } = [];
    }

    [SysAbiExport(
        Nid = "Bagshr7OQ6Q",
        ExportName = "sceNpTrophy2CreateContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateContext(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);
        var serviceLabel = unchecked((uint)ctx[CpuRegister.Rdx]);
        var options = ctx[CpuRegister.Rcx];
        if (outAddress == 0 || options != 0)
        {
            return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var id = _nextContext++;
            var titleId = ResolveTitleId();
            Contexts[id] = new TrophyContextState(
                userId,
                serviceLabel,
                titleId,
                ResolveRegistryPath(userId, titleId, serviceLabel));
            if (!ctx.TryWriteInt32(outAddress, id, checkNil: true))
            {
                Contexts.Remove(id);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "Gz1rmUZpROM",
        ExportName = "sceNpTrophy2CreateHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateHandle(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var id = _nextHandle++;
            Handles.Add(id);
            if (!ctx.TryWriteInt32(outAddress, id, checkNil: true))
            {
                Handles.Remove(id);
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "sysY2FHYff4",
        ExportName = "sceNpTrophy2DestroyContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyContext(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Contexts.Remove(unchecked((int)ctx[CpuRegister.Rdi]))
                ? 0
                : NpTrophy2ErrorInvalidContext);
        }
    }

    [SysAbiExport(
        Nid = "d8P11CI40KE",
        ExportName = "sceNpTrophy2DestroyHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyHandle(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Handles.Remove(unchecked((int)ctx[CpuRegister.Rdi]))
                ? 0
                : NpTrophy2ErrorInvalidHandle);
        }
    }

    [SysAbiExport(
        Nid = "fYapWA9xVmA",
        ExportName = "sceNpTrophy2AbortHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2AbortHandle(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Handles.Contains(unchecked((int)ctx[CpuRegister.Rdi]))
                ? 0
                : NpTrophy2ErrorInvalidHandle);
        }
    }

    [SysAbiExport(
        Nid = "bIDov3wBu5Q",
        ExportName = "sceNpTrophy2RegisterContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterContext(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextAndHandle(contextId, handleId, requireRegistered: false, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (state!.Registered)
            {
                return ctx.SetReturn(NpTrophy2ErrorAlreadyRegistered);
            }

            LoadRegistry(state);
            state.Registered = true;
            SaveRegistry(state);
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "sUXGfNMalIo",
        ExportName = "sceNpTrophy2RegisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterUnlockCallback(CpuContext ctx)
    {
        lock (StateGate)
        {
            _unlockCallback = ctx[CpuRegister.Rdi];
            _unlockCallbackUserData = ctx[CpuRegister.Rsi];
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "wVqxM58sIKs",
        ExportName = "sceNpTrophy2UnregisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2UnregisterUnlockCallback(CpuContext ctx)
    {
        lock (StateGate)
        {
            _unlockCallback = 0;
            _unlockCallbackUserData = 0;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "4IzqhhUQ3nk",
        ExportName = "sceNpTrophy2GetGameInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameInfo(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateQuery(ctx, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var detailsAddress = ctx[CpuRegister.Rdx];
            var dataAddress = ctx[CpuRegister.Rcx];
            if (detailsAddress == 0 || dataAddress == 0)
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
            }

            var queryState = state!;
            var details = PackGameDetails(queryState);
            var data = PackGameData(queryState);
            return ctx.Memory.TryWrite(detailsAddress, details) && ctx.Memory.TryWrite(dataAddress, data)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "DoZWauG8mu0",
        ExportName = "sceNpTrophy2GetGroupInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupInfo(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateQuery(ctx, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var groupId = NormalizeGroupId(unchecked((int)ctx[CpuRegister.Rdx]));
            var detailsAddress = ctx[CpuRegister.Rcx];
            var dataAddress = ctx[CpuRegister.R8];
            if (detailsAddress == 0 || dataAddress == 0)
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
            }

            var queryState = state!;
            if (!HasGroup(queryState, groupId))
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidGroupId);
            }

            return ctx.Memory.TryWrite(detailsAddress, PackGroupDetails(queryState, groupId)) &&
                   ctx.Memory.TryWrite(dataAddress, PackGroupData(queryState, groupId))
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "+PDSI6WgPRc",
        ExportName = "sceNpTrophy2GetGroupInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupInfoArray(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateQuery(ctx, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (!TryReadSeventhArgument(ctx, out var countAddress) || countAddress == 0)
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
            }

            var offset = unchecked((uint)ctx[CpuRegister.Rdx]);
            var limit = unchecked((uint)ctx[CpuRegister.Rcx]);
            var groupIds = state!.Trophies.Values.Select(trophy => trophy.GroupId)
                .Append(BaseGameGroupId).Distinct().Order().Skip(checked((int)offset)).Take(checked((int)limit)).ToArray();
            if (!ctx.TryWriteUInt32(countAddress, checked((uint)groupIds.Length)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            for (var index = 0; index < groupIds.Length; index++)
            {
                if ((ctx[CpuRegister.R8] != 0 && !ctx.Memory.TryWrite(
                        ctx[CpuRegister.R8] + (ulong)(index * GroupDetailsSize),
                        PackGroupDetails(state, groupIds[index]))) ||
                    (ctx[CpuRegister.R9] != 0 && !ctx.Memory.TryWrite(
                        ctx[CpuRegister.R9] + (ulong)(index * GroupDataSize),
                        PackGroupData(state, groupIds[index]))))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "EwNylPdWUTM",
        ExportName = "sceNpTrophy2GetTrophyInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfo(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateQuery(ctx, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            var trophyId = unchecked((int)ctx[CpuRegister.Rdx]);
            if (!state!.Trophies.TryGetValue(trophyId, out var trophy))
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidTrophyId);
            }

            var detailsAddress = ctx[CpuRegister.Rcx];
            var dataAddress = ctx[CpuRegister.R8];
            if (detailsAddress == 0 || dataAddress == 0)
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
            }

            return ctx.Memory.TryWrite(detailsAddress, PackTrophyDetails(trophy)) &&
                   ctx.Memory.TryWrite(dataAddress, PackTrophyData(trophy))
                ? ctx.SetReturn(0)
                : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "y3zHpdZO6ME",
        ExportName = "sceNpTrophy2GetTrophyInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfoArray(CpuContext ctx)
    {
        lock (StateGate)
        {
            var validation = ValidateQuery(ctx, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(validation);
            }

            if (!TryReadSeventhArgument(ctx, out var countAddress) || countAddress == 0)
            {
                return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
            }

            var offset = unchecked((uint)ctx[CpuRegister.Rdx]);
            var limit = unchecked((uint)ctx[CpuRegister.Rcx]);
            var trophies = state!.Trophies.Values.OrderBy(trophy => trophy.Id)
                .Skip(checked((int)offset)).Take(checked((int)limit)).ToArray();
            if (!ctx.TryWriteUInt32(countAddress, checked((uint)trophies.Length)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            for (var index = 0; index < trophies.Length; index++)
            {
                if ((ctx[CpuRegister.R8] != 0 && !ctx.Memory.TryWrite(
                        ctx[CpuRegister.R8] + (ulong)(index * TrophyDetailsSize),
                        PackTrophyDetails(trophies[index]))) ||
                    (ctx[CpuRegister.R9] != 0 && !ctx.Memory.TryWrite(
                        ctx[CpuRegister.R9] + (ulong)(index * TrophyDataSize),
                        PackTrophyData(trophies[index]))))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "28xmRUFao68",
        ExportName = "sceNpTrophyUnlockTrophy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyUnlockTrophy(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleId = unchecked((int)ctx[CpuRegister.Rsi]);
        var trophyId = unchecked((int)ctx[CpuRegister.Rdx]);
        var platinumIdAddress = ctx[CpuRegister.Rcx];
        if (platinumIdAddress == 0)
        {
            return ctx.SetReturn(NpTrophyErrorInvalidArgument);
        }

        lock (StateGate)
        {
            var validation = ValidateContextAndHandle(contextId, handleId, requireRegistered: true, out var state);
            if (validation != 0)
            {
                return ctx.SetReturn(ToLegacyTrophyError(validation));
            }

            if (trophyId is < 0 or > MaxTrophyId)
            {
                return ctx.SetReturn(NpTrophyErrorInvalidTrophyId);
            }

            if (!ctx.TryWriteInt32(platinumIdAddress, -1, checkNil: true))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var result = UnlockTrophy(state!, trophyId);
            return ctx.SetReturn(result == NpTrophy2ErrorAlreadyUnlocked
                ? NpTrophyErrorAlreadyUnlocked
                : result);
        }
    }

    [SysAbiExport(
        Nid = "2QgUy+xJqS0",
        ExportName = "sceNpTrophy2GetGameIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.Rcx]);

    [SysAbiExport(
        Nid = "6IjXJUy6ZnA",
        ExportName = "sceNpTrophy2GetGroupIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(
        Nid = "-9LLVU0uvs8",
        ExportName = "sceNpTrophy2GetTrophyIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(
        Nid = "BsE-m8JxIOg",
        ExportName = "sceNpTrophy2GetRewardIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetRewardIcon(CpuContext ctx) => ReturnMissingIcon(ctx, ctx[CpuRegister.R8]);

    [SysAbiExport(
        Nid = "EHQEDVXZ0TI",
        ExportName = "sceNpTrophy2ShowTrophyList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2ShowTrophyList(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Contexts.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var state) && state.Registered
                ? 0
                : NpTrophy2ErrorInvalidContext);
        }
    }

    internal static byte[] PackTrophyDetails(TrophyRecord trophy)
    {
        var bytes = new byte[TrophyDetailsSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00), trophy.Id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x04), trophy.Grade);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x08), trophy.GroupId);
        bytes[0x0C] = trophy.Hidden ? (byte)1 : (byte)0;
        WriteFixedUtf8(bytes.AsSpan(0x20, 128), trophy.Name);
        WriteFixedUtf8(bytes.AsSpan(0xA0, 1024), trophy.Description);
        return bytes;
    }

    internal static byte[] PackTrophyData(TrophyRecord trophy)
    {
        var bytes = new byte[TrophyDataSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00), trophy.Id);
        bytes[0x04] = trophy.Unlocked ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x18), trophy.TimestampTick);
        return bytes;
    }

    internal static void DefineTrophyForTests(int contextId, TrophyRecord trophy)
    {
        lock (StateGate)
        {
            var state = Contexts[contextId];
            state.Trophies[trophy.Id] = trophy;
            SaveRegistry(state);
        }
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            Contexts.Clear();
            Handles.Clear();
            _nextContext = 1;
            _nextHandle = 1;
            _unlockCallback = 0;
            _unlockCallbackUserData = 0;
        }
    }

    internal static int GetLegacyRegistrySnapshot(
        int contextId,
        int handleId,
        out string titleId,
        out TrophyRecord[] trophies)
    {
        lock (StateGate)
        {
            var validation = ValidateContextAndHandle(contextId, handleId, requireRegistered: true, out var state);
            if (validation != 0)
            {
                titleId = string.Empty;
                trophies = [];
                return ToLegacyTrophyError(validation);
            }

            titleId = state!.TitleId;
            trophies = state.Trophies.Values.OrderBy(trophy => trophy.Id).ToArray();
            return 0;
        }
    }

    private static int ValidateQuery(CpuContext ctx, out TrophyContextState? state) =>
        ValidateContextAndHandle(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((int)ctx[CpuRegister.Rsi]),
            requireRegistered: true,
            out state);

    private static int ValidateContextAndHandle(
        int contextId,
        int handleId,
        bool requireRegistered,
        out TrophyContextState? state)
    {
        if (!Contexts.TryGetValue(contextId, out state))
        {
            return NpTrophy2ErrorInvalidContext;
        }

        if (!Handles.Contains(handleId))
        {
            return NpTrophy2ErrorInvalidHandle;
        }

        return requireRegistered && !state.Registered ? NpTrophy2ErrorNotRegistered : 0;
    }

    private static int UnlockTrophy(TrophyContextState state, int trophyId)
    {
        if (state.Trophies.TryGetValue(trophyId, out var existing) && existing.Unlocked)
        {
            return NpTrophy2ErrorAlreadyUnlocked;
        }

        var record = existing ?? new TrophyRecord(
            trophyId,
            4,
            BaseGameGroupId,
            false,
            $"Trophy {trophyId}",
            string.Empty,
            false,
            0);
        state.Trophies[trophyId] = record with
        {
            Unlocked = true,
            TimestampTick = UnixEpochRtcTick + checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000)
        };
        SaveRegistry(state);
        return 0;
    }

    private static byte[] PackGameDetails(TrophyContextState state)
    {
        var trophies = state.Trophies.Values.ToArray();
        var bytes = new byte[GameDetailsSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x00), checked((uint)Math.Max(1, trophies.Select(trophy => trophy.GroupId).Distinct().Count())));
        WriteGradeCounts(bytes, 0x04, trophies);
        WriteFixedUtf8(bytes.AsSpan(0x18, 128), state.TitleId);
        return bytes;
    }

    private static byte[] PackGameData(TrophyContextState state)
    {
        var trophies = state.Trophies.Values.ToArray();
        var unlocked = trophies.Where(trophy => trophy.Unlocked).ToArray();
        var bytes = new byte[GameDataSize];
        WriteUnlockedCounts(bytes, unlocked);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(0x14),
            trophies.Length == 0 ? 0 : checked((uint)(unlocked.Length * 100 / trophies.Length)));
        return bytes;
    }

    private static byte[] PackGroupDetails(TrophyContextState state, int groupId)
    {
        var trophies = state.Trophies.Values.Where(trophy => trophy.GroupId == groupId).ToArray();
        var bytes = new byte[GroupDetailsSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00), groupId);
        WriteGradeCounts(bytes, 0x04, trophies);
        WriteFixedUtf8(bytes.AsSpan(0x18, 128), groupId == BaseGameGroupId ? "Base Game" : $"Group {groupId}");
        return bytes;
    }

    private static byte[] PackGroupData(TrophyContextState state, int groupId)
    {
        var trophies = state.Trophies.Values.Where(trophy => trophy.GroupId == groupId).ToArray();
        var unlocked = trophies.Where(trophy => trophy.Unlocked).ToArray();
        var bytes = new byte[GroupDataSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x00), groupId);
        WriteUnlockedCounts(bytes.AsSpan(0x04), unlocked);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(0x18),
            trophies.Length == 0 ? 0 : checked((uint)(unlocked.Length * 100 / trophies.Length)));
        return bytes;
    }

    private static void WriteGradeCounts(Span<byte> destination, int offset, IReadOnlyCollection<TrophyRecord> trophies)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], checked((uint)trophies.Count));
        for (var grade = 1; grade <= 4; grade++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination[(offset + (grade * 4))..],
                checked((uint)trophies.Count(trophy => trophy.Grade == grade)));
        }
    }

    private static void WriteUnlockedCounts(Span<byte> destination, IReadOnlyCollection<TrophyRecord> trophies)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, checked((uint)trophies.Count));
        for (var grade = 1; grade <= 4; grade++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination[(grade * 4)..],
                checked((uint)trophies.Count(trophy => trophy.Grade == grade)));
        }
    }

    private static bool HasGroup(TrophyContextState state, int groupId) =>
        groupId == BaseGameGroupId || state.Trophies.Values.Any(trophy => trophy.GroupId == groupId);

    private static int NormalizeGroupId(int groupId) => groupId < 0 ? BaseGameGroupId : groupId;

    private static int ReturnMissingIcon(CpuContext ctx, ulong sizeAddress)
    {
        if (sizeAddress == 0)
        {
            return ctx.SetReturn(NpTrophy2ErrorInvalidArgument);
        }

        return ctx.TryWriteUInt64(sizeAddress, 0)
            ? ctx.SetReturn(NpTrophy2ErrorIconFileNotFound)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ToLegacyTrophyError(int trophy2Error) => trophy2Error switch
    {
        NpTrophy2ErrorInvalidArgument => NpTrophyErrorInvalidArgument,
        NpTrophy2ErrorInvalidTrophyId => NpTrophyErrorInvalidTrophyId,
        _ => trophy2Error - 0x2300
    };

    private static bool TryReadSeventhArgument(CpuContext ctx, out ulong value) =>
        ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out value);

    private static void LoadRegistry(TrophyContextState state)
    {
        state.Trophies.Clear();
        if (!File.Exists(state.RegistryPath))
        {
            return;
        }

        try
        {
            var registry = JsonSerializer.Deserialize<TrophyRegistryFile>(File.ReadAllText(state.RegistryPath));
            if (registry?.Trophies == null)
            {
                return;
            }

            foreach (var trophy in registry.Trophies.Where(trophy => trophy.Id is >= 0 and <= MaxTrophyId))
            {
                state.Trophies[trophy.Id] = trophy;
            }
        }
        catch (JsonException)
        {
            Trace($"registry_invalid path='{state.RegistryPath}'");
        }
        catch (IOException exception)
        {
            Trace($"registry_read_failed type={exception.GetType().Name}");
        }
    }

    private static void SaveRegistry(TrophyContextState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(state.RegistryPath)!;
            Directory.CreateDirectory(directory);
            var registry = new TrophyRegistryFile
            {
                TitleId = state.TitleId,
                Trophies = state.Trophies.Values.OrderBy(trophy => trophy.Id).ToList()
            };
            var temporaryPath = state.RegistryPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(registry));
            File.Move(temporaryPath, state.RegistryPath, overwrite: true);
        }
        catch (IOException exception)
        {
            Trace($"registry_write_failed type={exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace($"registry_write_failed type={exception.GetType().Name}");
        }
    }

    private static string ResolveRegistryPath(int userId, string titleId, uint serviceLabel) =>
        Path.Combine(ResolveStorageRoot(), userId.ToString(), SanitizePathSegment(titleId), $"{serviceLabel:X8}.json");

    private static string ResolveStorageRoot()
    {
        if (!string.IsNullOrWhiteSpace(TrophyStorageRootOverride))
        {
            return Path.GetFullPath(TrophyStorageRootOverride);
        }

        var saveDataRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(saveDataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "user", "trophies")
            : Path.Combine(saveDataRoot, "trophies"));
    }

    private static string ResolveTitleId()
    {
        if (!string.IsNullOrWhiteSpace(TrophyTitleIdOverride))
        {
            return SanitizePathSegment(TrophyTitleIdOverride);
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var app0Name = string.IsNullOrWhiteSpace(app0Root)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        return SanitizePathSegment(app0Name?.Split('-', StringSplitOptions.RemoveEmptyEntries)[0] ?? "default");
    }

    private static string SanitizePathSegment(string value)
    {
        var chars = value.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "default" : sanitized;
    }

    private static void WriteFixedUtf8(Span<byte> destination, string value)
    {
        destination.Clear();
        if (destination.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] np.trophy2.{message}");
        }
    }
}
