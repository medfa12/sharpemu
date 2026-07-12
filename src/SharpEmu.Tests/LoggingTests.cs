// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// SharpEmuLog is process-global static state, so these tests run in a single
/// non-parallel collection and restore the previous sink/level afterwards.
/// </summary>
[Collection(nameof(LoggingTests))]
[CollectionDefinition(nameof(LoggingTests), DisableParallelization = true)]
public sealed class LoggingTests : IDisposable
{
    private readonly ISharpEmuLogSink _originalSink = SharpEmuLog.Sink;
    private readonly LogLevel _originalLevel = SharpEmuLog.MinimumLevel;

    public void Dispose()
    {
        SharpEmuLog.MinimumLevel = _originalLevel;
        SharpEmuLog.Sink = _originalSink;
    }

    private sealed class RecordingSink : ISharpEmuLogSink
    {
        public List<LogEntry> Entries { get; } = new();

        public void Write(in LogEntry entry) => Entries.Add(entry);
    }

    private sealed class ThrowingSink : ISharpEmuLogSink
    {
        public int WriteAttempts { get; private set; }

        public void Write(in LogEntry entry)
        {
            WriteAttempts++;
            throw new InvalidOperationException("sink is broken");
        }
    }

    private RecordingSink UseRecordingSink(LogLevel minimumLevel = LogLevel.Trace)
    {
        var sink = new RecordingSink();
        SharpEmuLog.Configure(minimumLevel, sink);
        return sink;
    }

    [Fact]
    public void Logger_WritesEntryWithCategoryLevelAndCallerInfo()
    {
        var sink = UseRecordingSink();

        SharpEmuLog.For("VMEM").Info("mapped region");

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("VMEM", entry.Category);
        Assert.Equal("mapped region", entry.Message);
        Assert.Equal("LoggingTests.cs", entry.SourceFileName); // [CallerFilePath] reduced to file name
        Assert.True(entry.SourceLine > 0);
        Assert.Equal(nameof(Logger_WritesEntryWithCategoryLevelAndCallerInfo), entry.SourceMemberName);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Logger_CapturesExceptionOnErrorAndCritical()
    {
        var sink = UseRecordingSink();
        var failure = new InvalidOperationException("boom");

        SharpEmuLog.For("CPU").Error("dispatch failed", failure);
        SharpEmuLog.For("CPU").Critical("fatal", failure);

        Assert.Equal(2, sink.Entries.Count);
        Assert.Same(failure, sink.Entries[0].Exception);
        Assert.Equal(LogLevel.Error, sink.Entries[0].Level);
        Assert.Same(failure, sink.Entries[1].Exception);
        Assert.Equal(LogLevel.Critical, sink.Entries[1].Level);
    }

    [Fact]
    public void MinimumLevel_FiltersLowerSeverityEntries()
    {
        var sink = UseRecordingSink(LogLevel.Warning);
        var log = SharpEmuLog.For("Filter");

        log.Trace("t");
        log.Debug("d");
        log.Info("i");
        log.Warn("w");
        log.Error("e");

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Error],
            sink.Entries.Select(e => e.Level));
    }

    [Fact]
    public void MinimumLevel_None_SuppressesEverything()
    {
        var sink = UseRecordingSink(LogLevel.None);

        SharpEmuLog.For("Quiet").Critical("should not appear");

        Assert.Empty(sink.Entries);
        Assert.False(SharpEmuLog.For("Quiet").IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void For_ReturnsCachedLoggerPerCategory()
    {
        Assert.Same(SharpEmuLog.For("Same"), SharpEmuLog.For("Same"));
        Assert.NotSame(SharpEmuLog.For("A"), SharpEmuLog.For("B"));
    }

    [Fact]
    public void For_WhitespaceCategory_Throws()
    {
        Assert.Throws<ArgumentException>(() => SharpEmuLog.For("  "));
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("warn", LogLevel.Warning)]     // alias
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("fatal", LogLevel.Critical)]   // alias
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("none", LogLevel.None)]
    public void TryParseLevel_AcceptsNamesAndAliases(string text, LogLevel expected)
    {
        Assert.True(SharpEmuLog.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("verbose")]
    public void TryParseLevel_RejectsUnknownText(string? text)
    {
        Assert.False(SharpEmuLog.TryParseLevel(text, out _));
    }

    [Fact]
    public void CompositeLogSink_FansOutToEveryChild()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        SharpEmuLog.Configure(LogLevel.Trace, new CompositeLogSink(first, second));

        SharpEmuLog.For("Fan").Info("both");

        Assert.Equal("both", Assert.Single(first.Entries).Message);
        Assert.Equal("both", Assert.Single(second.Entries).Message);
    }

    [Fact]
    public void CompositeLogSink_BrokenChildDoesNotSilenceOthers()
    {
        var broken = new ThrowingSink();
        var healthy = new RecordingSink();
        SharpEmuLog.Configure(LogLevel.Trace, new CompositeLogSink(broken, healthy));

        SharpEmuLog.For("Resilient").Warn("still logged");

        Assert.Equal(1, broken.WriteAttempts);
        Assert.Equal("still logged", Assert.Single(healthy.Entries).Message);
    }

    [Fact]
    public void CompositeLogSink_NullChild_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeLogSink(new RecordingSink(), null!));
    }

    [Fact]
    public void FileLogSink_WritesFormattedEntriesAndCreatesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-log-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "nested", "log.txt"); // parent dirs must be created
        try
        {
            using (var sink = new FileLogSink(path, append: false, includeTimestamp: true))
            {
                SharpEmuLog.Configure(LogLevel.Trace, sink);
                SharpEmuLog.For("Kernel").Error("mmap failed", new InvalidOperationException("ENOMEM"));
                SharpEmuLog.Sink = new ConsoleLogSink(useColors: false, includeTimestamp: false);
            }

            var text = File.ReadAllText(path);
            Assert.Contains("[ERROR]", text);
            Assert.Contains("[Kernel]", text);
            Assert.Contains("mmap failed", text);
            Assert.Contains("ENOMEM", text);           // exception rendered
            Assert.Contains("LoggingTests.cs", text);  // caller file
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileLogSink_WriteAfterDispose_IsIgnored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-log-{Guid.NewGuid():N}.txt");
        try
        {
            var sink = new FileLogSink(path, append: false);
            var entry = new LogEntry(
                DateTimeOffset.UnixEpoch, LogLevel.Info, "Cat", "before", "f.cs", 1, "M");
            sink.Write(in entry);
            sink.Dispose();

            var afterDispose = entry with { Message = "after" };
            sink.Write(in afterDispose); // must not throw
            sink.Dispose();              // double dispose must not throw

            var text = File.ReadAllText(path);
            Assert.Contains("before", text);
            Assert.DoesNotContain("after", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileLogSink_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FileLogSink("  "));
    }
}
