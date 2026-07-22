// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Tests;

public sealed class SaveDataStorageTests
{
    [Fact]
    public void BuildSaveDirectoryPath_UsesUserAndSanitizedSegments()
    {
        using var temp = new TempDirectory();

        var path = SaveDataExports.BuildSaveDirectoryPath(
            temp.Path,
            42,
            "PPSA/12345",
            "../slot:one");

        Assert.Equal(
            System.IO.Path.Combine(temp.Path, "42", "PPSA_12345", ".._slot_one"),
            path);
        Assert.StartsWith(System.IO.Path.GetFullPath(temp.Path), path, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveDataParam_PackAndUnpack_PreservesSdkFields()
    {
        var expected = new SaveDataExports.SaveDataParamValue(
            "Main title",
            "Chapter 7",
            "Reached the observatory",
            0xDEADBEEF,
            1_752_500_123);

        var packed = SaveDataExports.PackSaveDataParam(expected);
        var actual = SaveDataExports.UnpackSaveDataParam(packed);

        Assert.Equal(0x530, packed.Length);
        Assert.Equal((byte)'C', packed[0x80]);
        Assert.Equal((byte)'R', packed[0x100]);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(0x500)));
        Assert.Equal(1_752_500_123, BinaryPrimitives.ReadInt64LittleEndian(packed.AsSpan(0x508)));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MemoryFile_RoundTrip_PersistsRangeAndZeroFillsNewFile()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "slot", "memory.dat");
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };

        SaveDataExports.EnsureMemoryFile(path, 64);
        SaveDataExports.WriteMemoryFile(path, 17, payload);

        Assert.Equal(payload, SaveDataExports.ReadMemoryFile(path, 17, payload.Length));
        Assert.Equal(new byte[12], SaveDataExports.ReadMemoryFile(path, 0, 12));
        Assert.Equal(64, new FileInfo(path).Length);
    }

    [Fact]
    public void CopySaveDirectory_CopiesNestedFiles()
    {
        using var temp = new TempDirectory();
        var source = System.IO.Path.Combine(temp.Path, "source");
        var destination = System.IO.Path.Combine(temp.Path, "destination");
        var sourceFile = System.IO.Path.Combine(source, "data", "progress.bin");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, [9, 8, 7, 6]);

        SaveDataExports.CopySaveDirectory(source, destination);

        Assert.Equal(
            new byte[] { 9, 8, 7, 6 },
            File.ReadAllBytes(System.IO.Path.Combine(destination, "data", "progress.bin")));
    }

    [Fact]
    public void SaveBackup_RoundTripRestoresSnapshotAndPreservesBackup()
    {
        using var temp = new TempDirectory();
        var savePath = System.IO.Path.Combine(temp.Path, "slot");
        var dataPath = System.IO.Path.Combine(savePath, "data", "progress.bin");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dataPath)!);
        File.WriteAllBytes(dataPath, [1, 2, 3, 4]);

        SaveDataExports.CreateSaveBackup(savePath);
        File.WriteAllBytes(dataPath, [9, 9]);
        File.WriteAllBytes(System.IO.Path.Combine(savePath, "new.bin"), [8]);

        Assert.True(SaveDataExports.RestoreSaveBackup(savePath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dataPath));
        Assert.False(File.Exists(System.IO.Path.Combine(savePath, "new.bin")));
        Assert.True(Directory.Exists(SaveDataExports.GetSaveBackupPath(savePath)));
    }

    [Fact]
    public void SaveDataEvent_PackingUsesSdkFieldOffsets()
    {
        var packed = SaveDataExports.PackSaveDataEvent(2, 37, "PPSA12345", "AUTOSAVE01");

        Assert.Equal(0x68, packed.Length);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(packed));
        Assert.Equal(37, BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0x08)));
        Assert.Equal("PPSA12345", ReadAscii(packed.AsSpan(0x10, 10)));
        Assert.Equal("AUTOSAVE01", ReadAscii(packed.AsSpan(0x1A, 32)));
        Assert.True(packed.AsSpan(0x3A).SequenceEqual(new byte[0x2E]));
    }

    [Fact]
    public void CompatibilitySettings_RoundTripThroughGuestOutputs()
    {
        const ulong OutputAddress = 0x4000;
        var memory = new SparseGuestMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 19;
        SaveDataExports.SaveDataSetSaveDataLibraryUser(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = OutputAddress;
        SaveDataExports.SaveDataGetAppLaunchedUser(ctx);
        Assert.Equal(0, Result(ctx));
        Span<byte> user = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(OutputAddress, user));
        Assert.Equal(19U, BinaryPrimitives.ReadUInt32LittleEndian(user));

        ctx[CpuRegister.Rsi] = 1;
        SaveDataExports.SaveDataSetAutoUploadSetting(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rsi] = OutputAddress;
        SaveDataExports.SaveDataGetAutoUploadSetting(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(memory.TryRead(OutputAddress, user));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(user));
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? value : value[..terminator]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sharpemu-savedata-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
