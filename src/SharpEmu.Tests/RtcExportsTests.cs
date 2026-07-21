// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Rtc;
using Xunit;

namespace SharpEmu.Tests;

public sealed class RtcExportsTests
{
    private const ulong TimeAddress = 0x1000;
    private const ulong OutputAddress = 0x2000;
    private const ulong TickAddress = 0x3000;
    private const ulong SecondTickAddress = 0x4000;

    private const ulong UnixEpochTick = 62_135_596_800_000_000;
    private const ulong Y2KTick = 63_082_281_600_000_000;

    private const int RtcErrorDateTimeUninitialized = 0x7FFEF9FE;
    private const int RtcErrorInvalidPointer = unchecked((int)0x80B50002);
    private const int RtcErrorInvalidValue = unchecked((int)0x80B50003);
    private const int RtcErrorInvalidYear = unchecked((int)0x80B50008);
    private const int RtcErrorInvalidMonth = unchecked((int)0x80B50009);
    private const int RtcErrorInvalidDay = unchecked((int)0x80B5000A);

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(1, 1, 1, 0, 0, 0, 0, 0UL)]
    [InlineData(1970, 1, 1, 0, 0, 0, 0, UnixEpochTick)]
    [InlineData(2000, 1, 1, 0, 0, 0, 0, Y2KTick)]
    [InlineData(2020, 2, 29, 13, 45, 30, 123456, 63_718_580_730_123_456UL)]
    public void GetTick_MatchesKnownOrbisEpochValues(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond,
        ulong expected)
    {
        var ctx = NewContext(out var memory);
        WriteRtc(memory, TimeAddress, year, month, day, hour, minute, second, microsecond);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = TickAddress;

        Assert.Equal(0, RtcExports.RtcGetTick(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(TickAddress, out var tick));
        Assert.Equal(expected, tick);
    }

    [Theory]
    [InlineData(0UL, 1, 1, 1, 0, 0, 0, 0)]
    [InlineData(UnixEpochTick, 1970, 1, 1, 0, 0, 0, 0)]
    [InlineData(63_718_580_730_123_456UL, 2020, 2, 29, 13, 45, 30, 123456)]
    [InlineData(315_537_897_599_999_999UL, 9999, 12, 31, 23, 59, 59, 999999)]
    public void SetTick_MatchesKnownDatesAndPreservesMicroseconds(
        ulong tick,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond)
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(TickAddress, tick);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = TickAddress;

        Assert.Equal(0, RtcExports.RtcSetTick(ctx));
        AssertRtc(memory, TimeAddress, year, month, day, hour, minute, second, microsecond);
    }

    [Fact]
    public void SetTick_RejectsTickBeyondSupportedRtcCalendar()
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(TickAddress, 315_537_897_600_000_000);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = TickAddress;

        Assert.Equal(RtcErrorInvalidValue, RtcExports.RtcSetTick(ctx));
        Assert.Equal(unchecked((ulong)RtcErrorInvalidValue), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void TimeT_RoundTripsUnixSecondsAndClampsDatesBeforeEpoch()
    {
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = 946_684_800;
        Assert.Equal(0, RtcExports.RtcSetTimeT(ctx));
        AssertRtc(memory, TimeAddress, 2000, 1, 1, 0, 0, 0, 0);

        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, RtcExports.RtcGetTimeT(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var seconds));
        Assert.Equal(946_684_800UL, seconds);

        WriteRtc(memory, TimeAddress, 1960, 1, 1, 0, 0, 0, 0);
        Assert.Equal(0, RtcExports.RtcGetTimeT(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out seconds));
        Assert.Equal(0UL, seconds);
    }

    [Fact]
    public void SetTimeT_RejectsNegativeAndOutOfCalendarValues()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = ulong.MaxValue;
        Assert.Equal(RtcErrorInvalidValue, RtcExports.RtcSetTimeT(ctx));

        ctx[CpuRegister.Rsi] = long.MaxValue;
        Assert.Equal(RtcErrorInvalidValue, RtcExports.RtcSetTimeT(ctx));
    }

    [Fact]
    public void Win32FileTime_Uses1601EpochAndRoundTripsMicroseconds()
    {
        var ctx = NewContext(out var memory);
        WriteRtc(memory, TimeAddress, 1970, 1, 1, 0, 0, 0, 123456);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, RtcExports.RtcGetWin32FileTime(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var fileTime));
        Assert.Equal(116_444_736_001_234_560UL, fileTime);

        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = fileTime;
        Assert.Equal(0, RtcExports.RtcSetWin32FileTime(ctx));
        AssertRtc(memory, TimeAddress, 1970, 1, 1, 0, 0, 0, 123456);
    }

    [Fact]
    public void Win32FileTime_ClampsDatesBefore1601AndRejectsOverflow()
    {
        var ctx = NewContext(out var memory);
        WriteRtc(memory, TimeAddress, 1500, 1, 1, 0, 0, 0, 0);
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, RtcExports.RtcGetWin32FileTime(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var fileTime));
        Assert.Equal(0UL, fileTime);

        ctx[CpuRegister.Rsi] = ulong.MaxValue;
        Assert.Equal(RtcErrorInvalidValue, RtcExports.RtcSetWin32FileTime(ctx));
    }

    [Fact]
    public void CompareTick_ReturnsOneForLessOrEqualAndZeroForGreater()
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(TickAddress, 100);
        memory.WriteUInt64(SecondTickAddress, 200);
        ctx[CpuRegister.Rdi] = TickAddress;
        ctx[CpuRegister.Rsi] = SecondTickAddress;

        Assert.Equal(1, RtcExports.RtcCompareTick(ctx));
        memory.WriteUInt64(TickAddress, 200);
        Assert.Equal(1, RtcExports.RtcCompareTick(ctx));
        memory.WriteUInt64(TickAddress, 201);
        Assert.Equal(0, RtcExports.RtcCompareTick(ctx));
    }

    [Theory]
    [InlineData("ticks", 1L, 1L)]
    [InlineData("microseconds", -250L, 1L)]
    [InlineData("seconds", 2L, 1_000_000L)]
    [InlineData("minutes", -3L, 60_000_000L)]
    [InlineData("hours", 4L, 3_600_000_000L)]
    [InlineData("days", -5L, 86_400_000_000L)]
    [InlineData("weeks", 6L, 604_800_000_000L)]
    public void FixedTickAdds_ApplySdkUnitAndSignedDelta(string operation, long delta, long unit)
    {
        var ctx = NewContext(out var memory);
        memory.WriteUInt64(TickAddress, Y2KTick);
        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = TickAddress;
        ctx[CpuRegister.Rdx] = unchecked((ulong)delta);

        var result = operation switch
        {
            "ticks" => RtcExports.RtcTickAddTicks(ctx),
            "microseconds" => RtcExports.RtcTickAddMicroseconds(ctx),
            "seconds" => RtcExports.RtcTickAddSeconds(ctx),
            "minutes" => RtcExports.RtcTickAddMinutes(ctx),
            "hours" => RtcExports.RtcTickAddHours(ctx),
            "days" => RtcExports.RtcTickAddDays(ctx),
            "weeks" => RtcExports.RtcTickAddWeeks(ctx),
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(0, result);
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var tick));
        Assert.Equal(unchecked(Y2KTick + (ulong)(delta * unit)), tick);
    }

    [Fact]
    public void TickAddMonths_ClampsDayAtTargetMonthEnd()
    {
        var ctx = NewContext(out var memory);
        var sourceTick = GetTick(ctx, memory, 2020, 1, 31, 9, 8, 7, 654321);
        memory.WriteUInt64(TickAddress, sourceTick);
        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = TickAddress;
        ctx[CpuRegister.Rdx] = 1;

        Assert.Equal(0, RtcExports.RtcTickAddMonths(ctx));
        SetTick(ctx, memory, OutputAddress, SecondTickAddress);
        AssertRtc(memory, SecondTickAddress, 2020, 2, 29, 9, 8, 7, 654321);
    }

    [Fact]
    public void TickAddYears_PreservesFieldsAndReportsInvalidLeapDay()
    {
        var ctx = NewContext(out var memory);
        var sourceTick = GetTick(ctx, memory, 2020, 2, 29, 9, 8, 7, 654321);
        memory.WriteUInt64(TickAddress, sourceTick);
        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = TickAddress;
        ctx[CpuRegister.Rdx] = 1;

        Assert.Equal(RtcErrorInvalidDay, RtcExports.RtcTickAddYears(ctx));

        ctx[CpuRegister.Rdx] = 4;
        Assert.Equal(0, RtcExports.RtcTickAddYears(ctx));
        SetTick(ctx, memory, OutputAddress, SecondTickAddress);
        AssertRtc(memory, SecondTickAddress, 2024, 2, 29, 9, 8, 7, 654321);
    }

    [Theory]
    [InlineData(2000, 1)]
    [InlineData(1900, 0)]
    [InlineData(2024, 1)]
    [InlineData(2023, 0)]
    public void LeapYear_UsesGregorianRule(int year, int expected)
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = (ulong)year;
        Assert.Equal(expected, RtcExports.RtcIsLeapYear(ctx));
    }

    [Fact]
    public void CalendarQueries_ReturnKnownValuesAndSpecificErrors()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 2024;
        ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(29, RtcExports.RtcGetDaysInMonth(ctx));

        ctx[CpuRegister.Rdx] = 29;
        Assert.Equal(4, RtcExports.RtcGetDayOfWeek(ctx));

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(RtcErrorInvalidYear, RtcExports.RtcGetDayOfWeek(ctx));
        ctx[CpuRegister.Rdi] = 2024;
        ctx[CpuRegister.Rsi] = 13;
        Assert.Equal(RtcErrorInvalidMonth, RtcExports.RtcGetDayOfWeek(ctx));
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 30;
        Assert.Equal(RtcErrorInvalidDay, RtcExports.RtcGetDayOfWeek(ctx));
    }

    [Theory]
    [InlineData(0, 1, 1, 0, 0, 0, 0, 0x80B50008L)]
    [InlineData(2024, 13, 1, 0, 0, 0, 0, 0x80B50009L)]
    [InlineData(2023, 2, 29, 0, 0, 0, 0, 0x80B5000AL)]
    [InlineData(2024, 1, 1, 24, 0, 0, 0, 0x80B5000BL)]
    [InlineData(2024, 1, 1, 0, 60, 0, 0, 0x80B5000CL)]
    [InlineData(2024, 1, 1, 0, 0, 60, 0, 0x80B5000DL)]
    [InlineData(2024, 1, 1, 0, 0, 0, 1000000, 0x80B5000EL)]
    public void CheckValid_ReturnsFieldSpecificRtcError(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond,
        long expected)
    {
        var ctx = NewContext(out var memory);
        WriteRtc(memory, TimeAddress, year, month, day, hour, minute, second, microsecond);
        ctx[CpuRegister.Rdi] = TimeAddress;

        Assert.Equal(unchecked((int)expected), RtcExports.RtcCheckValid(ctx));
        Assert.Equal(unchecked((ulong)(int)expected), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void UtcAndLocalConversions_RoundTripOrdinaryWallClockTime()
    {
        var ctx = NewContext(out var memory);
        var utcTick = GetTick(ctx, memory, 2024, 1, 15, 12, 0, 0, 123456);
        memory.WriteUInt64(TickAddress, utcTick);
        ctx[CpuRegister.Rdi] = TickAddress;
        ctx[CpuRegister.Rsi] = SecondTickAddress;
        Assert.Equal(0, RtcExports.RtcConvertUtcToLocalTime(ctx));

        ctx[CpuRegister.Rdi] = SecondTickAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, RtcExports.RtcConvertLocalTimeToUtc(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var roundTrip));
        Assert.Equal(utcTick, roundTrip);
    }

    [Fact]
    public void CurrentTickAndNetworkTick_TrackHostUtcClock()
    {
        var ctx = NewContext(out _);
        var before = (ulong)(DateTime.UtcNow.Ticks / 10);
        ctx[CpuRegister.Rdi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcGetCurrentTick(ctx));
        var after = (ulong)(DateTime.UtcNow.Ticks / 10);
        Assert.True(ctx.TryReadUInt64(TickAddress, out var tick));
        Assert.InRange(tick, before, after);

        before = (ulong)(DateTime.UtcNow.Ticks / 10);
        ctx[CpuRegister.Rdi] = SecondTickAddress;
        Assert.Equal(0, RtcExports.RtcGetCurrentNetworkTick(ctx));
        after = (ulong)(DateTime.UtcNow.Ticks / 10);
        Assert.True(ctx.TryReadUInt64(SecondTickAddress, out tick));
        Assert.InRange(tick, before, after);
    }

    [Fact]
    public void CurrentClock_AppliesMinuteOffsetToHostUtcClock()
    {
        var ctx = NewContext(out var memory);
        const int offsetMinutes = 90;
        var before = (ulong)(DateTime.UtcNow.Ticks / 10) + 90UL * 60_000_000;
        ctx[CpuRegister.Rdi] = TimeAddress;
        ctx[CpuRegister.Rsi] = offsetMinutes;

        Assert.Equal(0, RtcExports.RtcGetCurrentClock(ctx));
        var after = (ulong)(DateTime.UtcNow.Ticks / 10) + 90UL * 60_000_000;
        var result = GetTickFromMemory(ctx, TimeAddress, TickAddress);
        Assert.InRange(result, before, after);
        AssertRtcValid(memory, TimeAddress);
    }

    [Fact]
    public void CurrentExports_ReturnSdkSpecificNullErrors()
    {
        var ctx = NewContext(out _);
        Assert.Equal(RtcErrorDateTimeUninitialized, RtcExports.RtcGetCurrentTick(ctx));
        Assert.Equal(RtcErrorDateTimeUninitialized, RtcExports.RtcGetCurrentClock(ctx));
        Assert.Equal(RtcErrorDateTimeUninitialized, RtcExports.RtcGetCurrentClockLocalTime(ctx));
        Assert.Equal(RtcErrorInvalidPointer, RtcExports.RtcGetCurrentNetworkTick(ctx));
    }

    [Fact]
    public void TickResolution_IsOneMillionTicksPerSecondAndSetsRax()
    {
        var ctx = NewContext(out _);
        Assert.Equal(1_000_000, RtcExports.RtcGetTickResolution(ctx));
        Assert.Equal(1_000_000UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void PointerExports_RejectNullAndUnreadableGuestMemory()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(RtcErrorInvalidPointer, RtcExports.RtcGetTick(ctx));

        ctx[CpuRegister.Rdi] = TimeAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, RtcExports.RtcGetTick(ctx));
    }

    private static ulong GetTick(
        CpuContext ctx,
        SparseGuestMemory memory,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond)
    {
        WriteRtc(memory, TimeAddress, year, month, day, hour, minute, second, microsecond);
        return GetTickFromMemory(ctx, TimeAddress, TickAddress);
    }

    private static ulong GetTickFromMemory(CpuContext ctx, ulong timeAddress, ulong tickAddress)
    {
        ctx[CpuRegister.Rdi] = timeAddress;
        ctx[CpuRegister.Rsi] = tickAddress;
        Assert.Equal(0, RtcExports.RtcGetTick(ctx));
        Assert.True(ctx.TryReadUInt64(tickAddress, out var tick));
        return tick;
    }

    private static void SetTick(
        CpuContext ctx,
        SparseGuestMemory memory,
        ulong tickAddress,
        ulong timeAddress)
    {
        ctx[CpuRegister.Rdi] = timeAddress;
        ctx[CpuRegister.Rsi] = tickAddress;
        Assert.Equal(0, RtcExports.RtcSetTick(ctx));
        AssertRtcValid(memory, timeAddress);
    }

    private static void WriteRtc(
        SparseGuestMemory memory,
        ulong address,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0..2], (ushort)year);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..4], (ushort)month);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..6], (ushort)day);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..8], (ushort)hour);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..10], (ushort)minute);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..12], (ushort)second);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..16], microsecond);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void AssertRtc(
        SparseGuestMemory memory,
        ulong address,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        uint microsecond)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(memory.TryRead(address, bytes));
        Assert.Equal(year, BinaryPrimitives.ReadUInt16LittleEndian(bytes[0..2]));
        Assert.Equal(month, BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..4]));
        Assert.Equal(day, BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]));
        Assert.Equal(hour, BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]));
        Assert.Equal(minute, BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
        Assert.Equal(second, BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]));
        Assert.Equal(microsecond, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]));
    }

    private static void AssertRtcValid(SparseGuestMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(memory.TryRead(address, bytes));
        Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(bytes[0..2]), (ushort)1, (ushort)9999);
        Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..4]), (ushort)1, (ushort)12);
    }
}
