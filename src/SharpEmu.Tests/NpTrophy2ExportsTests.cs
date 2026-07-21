// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NpTrophy2ExportsTests : IDisposable
{
    private const ulong ContextAddress = 0x1000;
    private const ulong HandleAddress = 0x1010;
    private const ulong PlatinumAddress = 0x1020;
    private const ulong DetailsAddress = 0x2000;
    private const ulong DataAddress = 0x3000;

    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-trophies-{Guid.NewGuid():N}");
    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpTrophy2ExportsTests()
    {
        Directory.CreateDirectory(_storageRoot);
        NpTrophy2Exports.ResetForTests();
        NpTrophy2Exports.TrophyStorageRootOverride = _storageRoot;
        NpTrophy2Exports.TrophyTitleIdOverride = "PPSA12345";
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void TrophyRegistry_UnlockAndReload_RoundTripsQueriesAndProgress()
    {
        var (contextId, handleId) = CreateRegisteredContext();
        NpTrophy2Exports.DefineTrophyForTests(
            contextId,
            new NpTrophy2Exports.TrophyRecord(7, 2, 0, false, "Gold Star", "Complete the route", false, 0));
        NpTrophy2Exports.DefineTrophyForTests(
            contextId,
            new NpTrophy2Exports.TrophyRecord(8, 4, 0, true, "Hidden Route", "Find the hidden route", false, 0));

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        _ctx[CpuRegister.Rsi] = (ulong)handleId;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = PlatinumAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophyUnlockTrophy(_ctx));
        Assert.True(_ctx.TryReadInt32(PlatinumAddress, out var platinumId));
        Assert.Equal(-1, platinumId);

        var registryPath = Path.Combine(_storageRoot, "1000", "PPSA12345", "00000007.json");
        Assert.True(File.Exists(registryPath));
        Assert.Contains("\"Unlocked\":true", File.ReadAllText(registryPath), StringComparison.Ordinal);

        NpTrophy2Exports.ResetForTests();
        (contextId, handleId) = CreateRegisteredContext();

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        _ctx[CpuRegister.Rsi] = (ulong)handleId;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = DetailsAddress;
        _ctx[CpuRegister.R8] = DataAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfo(_ctx));

        var details = Read(DetailsAddress, NpTrophy2Exports.TrophyDetailsSize);
        var data = Read(DataAddress, NpTrophy2Exports.TrophyDataSize);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(details));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(4)));
        Assert.Equal("Gold Star", ReadFixedUtf8(details.AsSpan(0x20, 128)));
        Assert.Equal((byte)1, data[4]);
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x18)));

        _ctx[CpuRegister.Rdx] = DetailsAddress;
        _ctx[CpuRegister.Rcx] = DataAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));
        var gameDetails = Read(DetailsAddress, NpTrophy2Exports.GameDetailsSize);
        var gameData = Read(DataAddress, NpTrophy2Exports.GameDataSize);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(gameDetails.AsSpan(4)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(gameData));
        Assert.Equal(50U, BinaryPrimitives.ReadUInt32LittleEndian(gameData.AsSpan(0x14)));

        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = DetailsAddress;
        _ctx[CpuRegister.R8] = DataAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfo(_ctx));
        var groupData = Read(DataAddress, NpTrophy2Exports.GroupDataSize);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(groupData.AsSpan(4)));
        Assert.Equal(50U, BinaryPrimitives.ReadUInt32LittleEndian(groupData.AsSpan(0x18)));
    }

    [Fact]
    public void TrophyInfoPacking_MatchesPs5SdkOffsetsAndSizes()
    {
        var trophy = new NpTrophy2Exports.TrophyRecord(
            42,
            3,
            5,
            true,
            "Packed trophy",
            "Packed description",
            true,
            0x1122_3344_5566_7788);

        var details = NpTrophy2Exports.PackTrophyDetails(trophy);
        var data = NpTrophy2Exports.PackTrophyData(trophy);

        Assert.Equal(1312, details.Length);
        Assert.Equal(32, data.Length);
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(0x00)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(0x04)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(0x08)));
        Assert.Equal((byte)1, details[0x0C]);
        Assert.Equal("Packed trophy", ReadFixedUtf8(details.AsSpan(0x20, 128)));
        Assert.Equal("Packed description", ReadFixedUtf8(details.AsSpan(0xA0, 1024)));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x00)));
        Assert.Equal((byte)1, data[0x04]);
        Assert.Equal(0x1122_3344_5566_7788UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x18)));
    }

    public void Dispose()
    {
        NpTrophy2Exports.ResetForTests();
        NpTrophy2Exports.TrophyStorageRootOverride = null;
        NpTrophy2Exports.TrophyTitleIdOverride = null;
        Directory.Delete(_storageRoot, recursive: true);
    }

    private (int Context, int Handle) CreateRegisteredContext()
    {
        _ctx[CpuRegister.Rdi] = ContextAddress;
        _ctx[CpuRegister.Rsi] = 1000;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2CreateContext(_ctx));
        Assert.True(_ctx.TryReadInt32(ContextAddress, out var contextId));

        _ctx[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2CreateHandle(_ctx));
        Assert.True(_ctx.TryReadInt32(HandleAddress, out var handleId));

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        _ctx[CpuRegister.Rsi] = (ulong)handleId;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2RegisterContext(_ctx));
        return (contextId, handleId);
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private static string ReadFixedUtf8(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(terminator < 0 ? bytes : bytes[..terminator]);
    }
}
