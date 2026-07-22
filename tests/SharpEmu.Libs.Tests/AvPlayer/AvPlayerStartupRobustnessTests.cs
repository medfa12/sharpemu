// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[CollectionDefinition("AvPlayerStartupState", DisableParallelization = true)]
public sealed class AvPlayerStartupStateCollection;

[Collection("AvPlayerStartupState")]
public sealed class AvPlayerStartupRobustnessTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong InfoAddress = BaseAddress + 0x100;
    private const ulong Handle = 0xA0_0000_0001;
    private const ulong DurationMilliseconds = 0x0102_0304_0506_0708;
    private const byte Sentinel = 0xAB;

    private readonly string? _originalApp0;
    private readonly string _tempRoot;
    private readonly string _app0Root;

    public AvPlayerStartupRobustnessTests()
    {
        _originalApp0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-avplayer-{Guid.NewGuid():N}");
        _app0Root = Path.Combine(_tempRoot, "app0");
        Directory.CreateDirectory(_app0Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Root);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    [InlineData(false, 1u)]
    [InlineData(true, 1u)]
    public void GetStreamInfoFunctionsWriteExactly32Bytes(
        bool useExtendedFunction,
        uint streamIndex)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        AvPlayerExports.RegisterPlayerForTest(Handle, 1280, 720, DurationMilliseconds);

        try
        {
            Span<byte> window = stackalloc byte[40];
            window.Fill(Sentinel);
            Assert.True(memory.TryWrite(InfoAddress, window));

            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = streamIndex;
            context[CpuRegister.Rdx] = InfoAddress;

            var resultCode = useExtendedFunction
                ? AvPlayerExports.AvPlayerGetStreamInfoEx(context)
                : AvPlayerExports.AvPlayerGetStreamInfo(context);
            Assert.Equal(0, resultCode);

            Span<byte> result = stackalloc byte[40];
            Assert.True(memory.TryRead(InfoAddress, result));
            Assert.Equal(streamIndex, BinaryPrimitives.ReadUInt32LittleEndian(result));
            if (streamIndex == 0)
            {
                Assert.Equal(1280u, BinaryPrimitives.ReadUInt32LittleEndian(result[8..]));
                Assert.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            else
            {
                Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(result[8..]));
                Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            Assert.Equal(
                DurationMilliseconds,
                BinaryPrimitives.ReadUInt64LittleEndian(result[24..]));
            Assert.All(result[32..].ToArray(), value => Assert.Equal(Sentinel, value));
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Fact]
    public void StreamInfoExExportUsesRuntimeReflectionRegistration()
    {
        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(AvPlayerExports).Assembly, Generation.Gen5);

        Assert.True(manager.TryGetExport("ctTAcF5DiKQ", out var export));
        Assert.Equal("sceAvPlayerGetStreamInfoEx", export.Name);
        Assert.Equal("libSceAvPlayer", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void RawUnrealRelativePathsAnchorAtApp0WithoutEscaping()
    {
        var mediaPath = CreateFile("SampleProject/Content/Movies/Startup.bk2");
        var currentDirectoryPath = CreateFile("Movies/Intro.bk2");
        CreateFile("outside.bk2");

        var resolved = AvPlayerExports.ResolveGuestPath(
            "../../../SampleProject/Content/Movies/Startup.bk2");

        Assert.Equal(Path.GetFullPath(mediaPath), resolved);
        Assert.Equal(
            Path.GetFullPath(currentDirectoryPath),
            AvPlayerExports.ResolveGuestPath("./Movies/Intro.bk2"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("../../../outside.bk2"));
    }

    [Theory]
    [InlineData(false, "ffmpeg", "ffprobe")]
    [InlineData(true, "ffmpeg.exe", "ffprobe.exe")]
    public void MediaToolLookupUsesQuotedPathAndPlatformNames(
        bool isWindows,
        string ffmpegName,
        string ffprobeName)
    {
        var toolDirectory = Path.Combine(_tempRoot, "Media Tools");
        Directory.CreateDirectory(toolDirectory);
        var ffmpeg = Path.Combine(toolDirectory, ffmpegName);
        var ffprobe = Path.Combine(toolDirectory, ffprobeName);
        File.WriteAllBytes(ffmpeg, []);
        File.WriteAllBytes(ffprobe, []);

        Assert.Equal(
            ffmpeg,
            AvPlayerExports.FindFfmpegTool(
                "ffmpeg",
                configured: null,
                ffmpegConfigured: null,
                searchPath: $"\"{toolDirectory}\"",
                isWindows));
        Assert.Equal(
            ffprobe,
            AvPlayerExports.FindFfmpegTool(
                "ffprobe",
                configured: null,
                ffmpegConfigured: ffmpeg,
                searchPath: null,
                isWindows));
    }

    public void Dispose()
    {
        AvPlayerExports.RemovePlayerForTest(Handle);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _originalApp0);
        Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_app0Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
        return path;
    }
}
