// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
