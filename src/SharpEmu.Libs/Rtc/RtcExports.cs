// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Rtc;

public static class RtcExports
{
    private const ulong DateTimeTicksPerMicrosecond = 10;
    private const ulong MicrosecondsPerSecond = 1_000_000;
    private const ulong MicrosecondsPerMinute = 60_000_000;
    private const ulong MicrosecondsPerHour = 3_600_000_000;
    private const ulong MicrosecondsPerDay = 86_400_000_000;
    private const ulong MicrosecondsPerWeek = 604_800_000_000;
    private const ulong UnixEpochTicks = 62_135_596_800_000_000;
    private const ulong Win32FileTimeEpochTicks = 50_491_123_200_000_000;
    private const ulong MaximumRtcTick = 315_537_897_599_999_999;

    private const int RtcErrorDateTimeUninitialized = 0x7FFEF9FE;
    private const int RtcErrorInvalidPointer = unchecked((int)0x80B50002);
    private const int RtcErrorInvalidValue = unchecked((int)0x80B50003);
    private const int RtcErrorInvalidArgument = unchecked((int)0x80B50004);
    private const int RtcErrorInvalidYear = unchecked((int)0x80B50008);
    private const int RtcErrorInvalidMonth = unchecked((int)0x80B50009);
    private const int RtcErrorInvalidDay = unchecked((int)0x80B5000A);
    private const int RtcErrorInvalidHour = unchecked((int)0x80B5000B);
    private const int RtcErrorInvalidMinute = unchecked((int)0x80B5000C);
    private const int RtcErrorInvalidSecond = unchecked((int)0x80B5000D);
    private const int RtcErrorInvalidMicrosecond = unchecked((int)0x80B5000E);

    [SysAbiExport(
        Nid = "lPEBYdVX0XQ",
        ExportName = "sceRtcCheckValid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcCheckValid(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(ValidateRtcDateTime(time));
    }

    [SysAbiExport(
        Nid = "fNaZ4DbzHAE",
        ExportName = "sceRtcCompareTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcCompareTick(CpuContext ctx)
    {
        var firstAddress = ctx[CpuRegister.Rdi];
        var secondAddress = ctx[CpuRegister.Rsi];
        if (firstAddress == 0 || secondAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!ctx.TryReadUInt64(firstAddress, out var first) ||
            !ctx.TryReadUInt64(secondAddress, out var second))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(first <= second ? 1 : 0);
    }

    [SysAbiExport(
        Nid = "8Yr143yEnRo",
        ExportName = "sceRtcConvertLocalTimeToUtc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcConvertLocalTimeToUtc(CpuContext ctx)
    {
        var localAddress = ctx[CpuRegister.Rdi];
        var utcAddress = ctx[CpuRegister.Rsi];
        if (localAddress == 0 || utcAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!ctx.TryReadUInt64(localAddress, out var localTick))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryConvertTickToDateTime(localTick, DateTimeKind.Unspecified, out var localTime))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        DateTime utcTime;
        try
        {
            utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return ctx.SetReturn(RtcErrorInvalidArgument);
        }

        return ctx.TryWriteUInt64(utcAddress, ToTick(utcTime))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "M1TvFst-jrM",
        ExportName = "sceRtcConvertUtcToLocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcConvertUtcToLocalTime(CpuContext ctx)
    {
        var utcAddress = ctx[CpuRegister.Rdi];
        var localAddress = ctx[CpuRegister.Rsi];
        if (utcAddress == 0 || localAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!ctx.TryReadUInt64(utcAddress, out var utcTick))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryConvertTickToDateTime(utcTick, DateTimeKind.Utc, out var utcTime))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        DateTime localTime;
        try
        {
            localTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return ctx.SetReturn(RtcErrorInvalidArgument);
        }

        return ctx.TryWriteUInt64(localAddress, ToTick(localTime))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8SljQx6pDP8",
        ExportName = "sceRtcEnd",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcEnd(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "LN3Zcb72Q0c",
        ExportName = "sceRtcGetCurrentAdNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentAdNetworkTick(CpuContext ctx) => GetCurrentNetworkTick(ctx);

    [SysAbiExport(
        Nid = "8lfvnRMqwEM",
        ExportName = "sceRtcGetCurrentClock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentClock(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorDateTimeUninitialized);
        }

        var timeZoneMinutes = unchecked((int)ctx[CpuRegister.Rsi]);
        var utcTick = ToTick(DateTime.UtcNow);
        var adjustedTick = unchecked(utcTick + (ulong)(unchecked((long)timeZoneMinutes * (long)MicrosecondsPerMinute)));
        if (!TryConvertTickToRtcDateTime(adjustedTick, out var time))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        return TryWriteRtcDateTime(ctx, timeAddress, time)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "ZPD1YOKI+Kw",
        ExportName = "sceRtcGetCurrentClockLocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentClockLocalTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorDateTimeUninitialized);
        }

        return TryWriteRtcDateTime(ctx, timeAddress, ToRtcDateTime(DateTime.Now))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ot1DE3gif84",
        ExportName = "sceRtcGetCurrentDebugNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentDebugNetworkTick(CpuContext ctx) => GetCurrentNetworkTick(ctx);

    [SysAbiExport(
        Nid = "zO9UL3qIINQ",
        ExportName = "sceRtcGetCurrentNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentNetworkTick(CpuContext ctx) => GetCurrentNetworkTick(ctx);

    [SysAbiExport(
        Nid = "HWxHOdbM-Pg",
        ExportName = "sceRtcGetCurrentRawNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentRawNetworkTick(CpuContext ctx) => GetCurrentNetworkTick(ctx);

    [SysAbiExport(
        Nid = "18B2NS1y9UU",
        ExportName = "sceRtcGetCurrentTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentTick(CpuContext ctx)
    {
        var tickAddress = ctx[CpuRegister.Rdi];
        if (tickAddress == 0)
        {
            return ctx.SetReturn(RtcErrorDateTimeUninitialized);
        }

        return ctx.TryWriteUInt64(tickAddress, ToTick(DateTime.UtcNow))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "CyIK-i4XdgQ",
        ExportName = "sceRtcGetDayOfWeek",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDayOfWeek(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        var month = unchecked((int)ctx[CpuRegister.Rsi]);
        var day = unchecked((int)ctx[CpuRegister.Rdx]);

        if (year < 1 || year > 9999)
        {
            return ctx.SetReturn(RtcErrorInvalidYear);
        }

        if (month < 1 || month > 12)
        {
            return ctx.SetReturn(RtcErrorInvalidMonth);
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return ctx.SetReturn(RtcErrorInvalidDay);
        }

        return ctx.SetReturn((int)new DateTime(year, month, day).DayOfWeek);
    }

    [SysAbiExport(
        Nid = "3O7Ln8AqJ1o",
        ExportName = "sceRtcGetDaysInMonth",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDaysInMonth(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        var month = unchecked((int)ctx[CpuRegister.Rsi]);
        if (year < 1 || year > 9999)
        {
            return ctx.SetReturn(RtcErrorInvalidYear);
        }

        if (month < 1 || month > 12)
        {
            return ctx.SetReturn(RtcErrorInvalidMonth);
        }

        return ctx.SetReturn(DateTime.DaysInMonth(year, month));
    }

    [SysAbiExport(
        Nid = "E7AR4o7Ny7E",
        ExportName = "sceRtcGetDosTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDosTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var dosTimeAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || dosTimeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        uint dosTime = 0;
        dosTime |= (uint)((time.Second / 2) & 0x1F);
        dosTime |= (uint)(time.Minute & 0x3F) << 5;
        dosTime |= (uint)(time.Hour & 0x1F) << 11;
        dosTime |= (uint)(time.Day & 0x1F) << 16;
        dosTime |= (uint)(time.Month & 0x0F) << 21;
        dosTime |= (uint)((time.Year - 1980) & 0x7F) << 25;

        return ctx.TryWriteUInt32(dosTimeAddress, dosTime)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8w-H19ip48I",
        ExportName = "sceRtcGetTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTick(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var tickAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || tickAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        var tick = ConvertRtcDateTimeToTick(time);
        return ctx.TryWriteUInt64(tickAddress, tick)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "jMNwqYr4R-k",
        ExportName = "sceRtcGetTickResolution",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTickResolution(CpuContext ctx) => ctx.SetReturn((int)MicrosecondsPerSecond);

    [SysAbiExport(
        Nid = "BtqmpTRXHgk",
        ExportName = "sceRtcGetTime_t",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTimeT(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var secondsAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || secondsAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        var tick = ConvertRtcDateTimeToTick(time);
        var seconds = tick < UnixEpochTicks ? 0 : (tick - UnixEpochTicks) / MicrosecondsPerSecond;
        return ctx.TryWriteUInt64(secondsAddress, seconds)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "jfRO0uTjtzA",
        ExportName = "sceRtcGetWin32FileTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetWin32FileTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var fileTimeAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || fileTimeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        var tick = ConvertRtcDateTimeToTick(time);
        var fileTime = tick < Win32FileTimeEpochTicks ? 0 : (tick - Win32FileTimeEpochTicks) * 10;
        return ctx.TryWriteUInt64(fileTimeAddress, fileTime)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "LlodCMDbk3o",
        ExportName = "sceRtcInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "Ug8pCwQvh0c",
        ExportName = "sceRtcIsLeapYear",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcIsLeapYear(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        if (year < 1 || year > 9999)
        {
            return ctx.SetReturn(RtcErrorInvalidYear);
        }

        return ctx.SetReturn(DateTime.IsLeapYear(year) ? 1 : 0);
    }

    [SysAbiExport(
        Nid = "NR1J0N7L2xY",
        ExportName = "sceRtcTickAddDays",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddDays(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerDay, true);

    [SysAbiExport(
        Nid = "MDc5cd8HfCA",
        ExportName = "sceRtcTickAddHours",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddHours(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerHour, true);

    [SysAbiExport(
        Nid = "XPIiw58C+GM",
        ExportName = "sceRtcTickAddMicroseconds",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMicroseconds(CpuContext ctx) => AddTickDelta(ctx, 1, false);

    [SysAbiExport(
        Nid = "mn-tf4QiFzk",
        ExportName = "sceRtcTickAddMinutes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMinutes(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerMinute, false);

    [SysAbiExport(
        Nid = "CL6y9q-XbuQ",
        ExportName = "sceRtcTickAddMonths",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMonths(CpuContext ctx) => AddCalendarMonths(ctx);

    [SysAbiExport(
        Nid = "07O525HgICs",
        ExportName = "sceRtcTickAddSeconds",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddSeconds(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerSecond, false);

    [SysAbiExport(
        Nid = "AqVMssr52Rc",
        ExportName = "sceRtcTickAddTicks",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddTicks(CpuContext ctx) => AddTickDelta(ctx, 1, false);

    [SysAbiExport(
        Nid = "gI4t194c2W8",
        ExportName = "sceRtcTickAddWeeks",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddWeeks(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerWeek, true);

    [SysAbiExport(
        Nid = "-5y2uJ62qS8",
        ExportName = "sceRtcTickAddYears",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddYears(CpuContext ctx) => AddCalendarYears(ctx);

    [SysAbiExport(
        Nid = "ueega6v3GUw",
        ExportName = "sceRtcSetTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetTick(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var tickAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || tickAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!ctx.TryReadUInt64(tickAddress, out var tick))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryConvertTickToRtcDateTime(tick, out var time))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        return TryWriteRtcDateTime(ctx, timeAddress, time)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "aYPCd1cChyg",
        ExportName = "sceRtcSetDosTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetDosTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        var dosTime = unchecked((uint)ctx[CpuRegister.Rsi]);
        var time = new RtcDateTime(
            (ushort)(1980 + ((dosTime >> 25) & 0x7F)),
            (ushort)((dosTime >> 21) & 0x0F),
            (ushort)((dosTime >> 16) & 0x1F),
            (ushort)((dosTime >> 11) & 0x1F),
            (ushort)((dosTime >> 5) & 0x3F),
            (ushort)((dosTime << 1) & 0x3E),
            0);

        return TryWriteRtcDateTime(ctx, timeAddress, time)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "bDEVVP4bTjQ",
        ExportName = "sceRtcSetTime_t",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetTimeT(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        var seconds = unchecked((long)ctx[CpuRegister.Rsi]);
        if (seconds < 0 || (ulong)seconds > (MaximumRtcTick - UnixEpochTicks) / MicrosecondsPerSecond)
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        var tick = UnixEpochTicks + (ulong)seconds * MicrosecondsPerSecond;
        if (!TryConvertTickToRtcDateTime(tick, out var time))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        return TryWriteRtcDateTime(ctx, timeAddress, time)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "n5JiAJXsbcs",
        ExportName = "sceRtcSetWin32FileTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetWin32FileTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        var fileTime = ctx[CpuRegister.Rsi];
        if (fileTime / 10 > MaximumRtcTick - Win32FileTimeEpochTicks)
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        var tick = Win32FileTimeEpochTicks + fileTime / 10;
        if (!TryConvertTickToRtcDateTime(tick, out var time))
        {
            return ctx.SetReturn(RtcErrorInvalidValue);
        }

        return TryWriteRtcDateTime(ctx, timeAddress, time)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int GetCurrentNetworkTick(CpuContext ctx)
    {
        var tickAddress = ctx[CpuRegister.Rdi];
        if (tickAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        return ctx.TryWriteUInt64(tickAddress, ToTick(DateTime.UtcNow))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int AddTickDelta(CpuContext ctx, ulong microsecondsPerUnit, bool isInt32)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (destinationAddress == 0 || sourceAddress == 0)
        {
            return ctx.SetReturn(RtcErrorInvalidPointer);
        }

        if (!ctx.TryReadUInt64(sourceAddress, out var sourceTick))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var delta = isInt32
            ? unchecked((int)ctx[CpuRegister.Rdx])
            : unchecked((long)ctx[CpuRegister.Rdx]);
        var offset = unchecked(delta * (long)microsecondsPerUnit);
        var resultTick = unchecked(sourceTick + (ulong)offset);
        return ctx.TryWriteUInt64(destinationAddress, resultTick)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int AddCalendarMonths(CpuContext ctx)
    {
        if (!TryReadCalendarOperands(ctx, out var destinationAddress, out var sourceTime, out var error))
        {
            return ctx.SetReturn(error);
        }

        var delta = unchecked((int)ctx[CpuRegister.Rdx]);
        var monthIndex = ((long)sourceTime.Year - 1) * 12 + sourceTime.Month - 1 + delta;
        if (monthIndex < 0 || monthIndex >= 9999L * 12)
        {
            return ctx.SetReturn(RtcErrorInvalidYear);
        }

        var year = (int)(monthIndex / 12) + 1;
        var month = (int)(monthIndex % 12) + 1;
        var day = Math.Min(sourceTime.Day, DateTime.DaysInMonth(year, month));
        var result = sourceTime with { Year = (ushort)year, Month = (ushort)month, Day = (ushort)day };
        return WriteCalendarResult(ctx, destinationAddress, result);
    }

    private static int AddCalendarYears(CpuContext ctx)
    {
        if (!TryReadCalendarOperands(ctx, out var destinationAddress, out var sourceTime, out var error))
        {
            return ctx.SetReturn(error);
        }

        var year = (long)sourceTime.Year + unchecked((int)ctx[CpuRegister.Rdx]);
        if (year < 1 || year > 9999)
        {
            return ctx.SetReturn(RtcErrorInvalidYear);
        }

        var result = sourceTime with { Year = (ushort)year };
        var validationResult = ValidateRtcDateTime(result);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        return WriteCalendarResult(ctx, destinationAddress, result);
    }

    private static bool TryReadCalendarOperands(
        CpuContext ctx,
        out ulong destinationAddress,
        out RtcDateTime sourceTime,
        out int error)
    {
        destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (destinationAddress == 0 || sourceAddress == 0)
        {
            sourceTime = default;
            error = RtcErrorInvalidPointer;
            return false;
        }

        if (!ctx.TryReadUInt64(sourceAddress, out var sourceTick))
        {
            sourceTime = default;
            error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        if (!TryConvertTickToRtcDateTime(sourceTick, out sourceTime))
        {
            error = RtcErrorInvalidValue;
            return false;
        }

        error = 0;
        return true;
    }

    private static int WriteCalendarResult(CpuContext ctx, ulong destinationAddress, RtcDateTime time)
    {
        var tick = ConvertRtcDateTimeToTick(time);
        return ctx.TryWriteUInt64(destinationAddress, tick)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ValidateRtcDateTime(RtcDateTime time)
    {
        if (time.Year < 1 || time.Year > 9999)
        {
            return RtcErrorInvalidYear;
        }

        if (time.Month < 1 || time.Month > 12)
        {
            return RtcErrorInvalidMonth;
        }

        if (time.Day < 1 || time.Day > DateTime.DaysInMonth(time.Year, time.Month))
        {
            return RtcErrorInvalidDay;
        }

        if (time.Hour > 23)
        {
            return RtcErrorInvalidHour;
        }

        if (time.Minute > 59)
        {
            return RtcErrorInvalidMinute;
        }

        if (time.Second > 59)
        {
            return RtcErrorInvalidSecond;
        }

        if (time.Microsecond > 999_999)
        {
            return RtcErrorInvalidMicrosecond;
        }

        return 0;
    }

    private static bool TryReadRtcDateTime(CpuContext ctx, ulong address, out RtcDateTime time)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            time = default;
            return false;
        }

        time = new RtcDateTime(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..10]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]));
        return true;
    }

    private static bool TryWriteRtcDateTime(CpuContext ctx, ulong address, RtcDateTime time)
    {
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], time.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], time.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], time.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], time.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..10], time.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], time.Second);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], time.Microsecond);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static ulong ToTick(DateTime time) => (ulong)time.Ticks / DateTimeTicksPerMicrosecond;

    private static RtcDateTime ToRtcDateTime(DateTime time)
    {
        return new RtcDateTime(
            (ushort)time.Year,
            (ushort)time.Month,
            (ushort)time.Day,
            (ushort)time.Hour,
            (ushort)time.Minute,
            (ushort)time.Second,
            (uint)((ulong)time.Ticks % TimeSpan.TicksPerSecond / DateTimeTicksPerMicrosecond));
    }

    private static bool TryConvertTickToDateTime(ulong tick, DateTimeKind kind, out DateTime time)
    {
        if (tick > MaximumRtcTick)
        {
            time = default;
            return false;
        }

        time = new DateTime((long)(tick * DateTimeTicksPerMicrosecond), kind);
        return true;
    }

    private static ulong ConvertRtcDateTimeToTick(RtcDateTime time)
    {
        ulong year = time.Year;
        ulong month = time.Month;
        if (month > 2)
        {
            month -= 3;
        }
        else
        {
            month += 9;
            year--;
        }

        var century = year / 100;
        var yearOfCentury = year - 100 * century;
        var days = ((146097 * century) >> 2)
            + ((1461 * yearOfCentury) >> 2)
            + (153 * month + 2) / 5
            + time.Day
            - 307;

        return days * MicrosecondsPerDay
            + (ulong)time.Hour * MicrosecondsPerHour
            + (ulong)time.Minute * MicrosecondsPerMinute
            + (ulong)time.Second * MicrosecondsPerSecond
            + time.Microsecond;
    }

    private static bool TryConvertTickToRtcDateTime(ulong tick, out RtcDateTime time)
    {
        var days = tick / MicrosecondsPerDay;
        var microseconds = tick % MicrosecondsPerDay;
        days += 307;

        var intermediate = (days << 2) - 1;
        var year = intermediate / 146097;
        intermediate -= 146097 * year;
        var day = intermediate >> 2;
        intermediate = ((day << 2) + 3) / 1461;
        day = (((day << 2) + 7) - 1461 * intermediate) >> 2;
        var month = (5 * day - 3) / 153;
        day = (5 * day + 2 - 153 * month) / 5;
        year = 100 * year + intermediate;

        if (month < 10)
        {
            month += 3;
        }
        else
        {
            month -= 9;
            year++;
        }

        if (year is < 1 or > 9999)
        {
            time = default;
            return false;
        }

        var hour = microseconds / MicrosecondsPerHour;
        microseconds %= MicrosecondsPerHour;
        var minute = microseconds / MicrosecondsPerMinute;
        microseconds %= MicrosecondsPerMinute;
        var second = microseconds / MicrosecondsPerSecond;
        var microsecond = microseconds % MicrosecondsPerSecond;

        time = new RtcDateTime(
            (ushort)year,
            (ushort)month,
            (ushort)day,
            (ushort)hour,
            (ushort)minute,
            (ushort)second,
            (uint)microsecond);
        return ValidateRtcDateTime(time) == 0;
    }

    private readonly record struct RtcDateTime(
        ushort Year,
        ushort Month,
        ushort Day,
        ushort Hour,
        ushort Minute,
        ushort Second,
        uint Microsecond);
}
