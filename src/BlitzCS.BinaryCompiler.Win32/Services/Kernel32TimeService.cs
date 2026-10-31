using System.Text;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>
/// KERNEL32 date, time and time-zone functions (Microsoft Learn, "Time Functions" and "National Language Support Functions").
/// <list type="bullet">
/// <item>SYSTEMTIME (16 bytes) and FILETIME arithmetic is done on the proleptic Gregorian calendar from 1601 to 30827 like Windows, not through System.DateTime. Invalid SYSTEMTIME fields or a negative / too large
/// FILETIME fail with ERROR_INVALID_PARAMETER. wDayOfWeek of an input SYSTEMTIME is ignored, as documented.</item>
/// <item>The guest's zone is <see cref="Win32Options.TimeZone"/> (default: the host's). GetTimeZoneInformation builds the 172-byte TIME_ZONE_INFORMATION from the rule in force now: Bias in minutes west of UTC,
/// names (31 characters at most), the standard / daylight transition dates (floating dates wYear = 0 with wDay = week 1..5, or absolute dates) and the biases; it returns TIME_ZONE_ID_UNKNOWN (0) when the zone has no
/// daylight saving, else STANDARD (1) or DAYLIGHT (2). SystemTimeToTzSpecificLocalTime uses a given TIME_ZONE_INFORMATION (transition dates of the UTC year) or, with NULL, the active zone for that exact instant.</item>
/// <item>GetDateFormatW / GetTimeFormatW support the locales LOCALE_INVARIANT (0x7F), LOCALE_USER_DEFAULT / LOCALE_SYSTEM_DEFAULT (both reported as en-US, 0x400 / 0x800) and en-US (0x0409); any other locale fails with
/// ERROR_INVALID_PARAMETER because no locale database exists here. Pictures: d dd ddd dddd M MM MMM MMMM y yy yyyy and h hh H HH m mm s ss t tt, quoted text in single quotes. Era pictures (g, gg) and
/// alternate calendars fail with ERROR_INVALID_FLAGS / ERROR_INVALID_PARAMETER. Result length counts the terminating NUL; a zero buffer size returns the needed length; a short buffer fails with ERROR_INSUFFICIENT_BUFFER.</item>
/// </list>
/// </summary>
public sealed class Kernel32TimeService : Win32Service
{
    public override string Name => "Kernel32.Time";

    private const long TicksPerMs = 10_000, TicksPerDay = 864_000_000_000;
    private const long MaxFileTime = 0x7FFF35F4F06C58F0; // 30827-12-14 02:48:05.4775807
    private const int TzSize = 172;
    private static readonly string[] Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    internal readonly record struct St(int Year, int Month, int Day, int Hour, int Minute, int Second, int Ms)
    {
        public int DayOfWeek => (int)((DaysFromCivil(Year, Month, Day) + 1) % 7);
    }

    // --- calendar (Howard Hinnant's civil-days algorithms, shifted to the 1601 epoch) ---
    private static long DaysFromCivil(int y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return (long)era * 146097 + doe - 719468 + 134774; // 134774 days from 1601-01-01 to 1970-01-01
    }

    private static (int Y, int M, int D) CivilFromDays(long z)
    {
        z -= 134774; z += 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = (int)(z - era * 146097);
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = doy - (153 * mp + 2) / 5 + 1;
        var m = mp < 10 ? mp + 3 : mp - 9;
        return ((int)(y + (m <= 2 ? 1 : 0)), (int)m, (int)d);
    }

    private static bool Leap(int y) => y % 4 == 0 && (y % 100 != 0 || y % 400 == 0);
    private static int DaysIn(int y, int m) => m == 2 ? (Leap(y) ? 29 : 28) : m is 4 or 6 or 9 or 11 ? 30 : 31;

    internal static bool Valid(St s) =>
        s.Year is >= 1601 and <= 30827 && s.Month is >= 1 and <= 12 && s.Day >= 1 && s.Day <= DaysIn(s.Year, s.Month)
        && s.Hour is >= 0 and < 24 && s.Minute is >= 0 and < 60 && s.Second is >= 0 and < 60 && s.Ms is >= 0 and < 1000;

    internal static long ToFileTime(St s) =>
        DaysFromCivil(s.Year, s.Month, s.Day) * TicksPerDay + ((s.Hour * 60L + s.Minute) * 60 + s.Second) * 10_000_000L + s.Ms * TicksPerMs;

    internal static St FromFileTime(long ft)
    {
        var days = Math.DivRem(ft, TicksPerDay, out var rem);
        var (y, m, d) = CivilFromDays(days);
        var ms = rem / TicksPerMs;
        return new St(y, m, d, (int)(ms / 3_600_000), (int)(ms / 60_000 % 60), (int)(ms / 1000 % 60), (int)(ms % 1000));
    }

    private static St ReadSt(Win32Call c, uint p) => new(c.Mem.ReadUInt16(p), c.Mem.ReadUInt16(p + 2), c.Mem.ReadUInt16(p + 6), c.Mem.ReadUInt16(p + 8), c.Mem.ReadUInt16(p + 10), c.Mem.ReadUInt16(p + 12), c.Mem.ReadUInt16(p + 14));

    private static void WriteSt(Win32Call c, uint p, St s)
    {
        c.Mem.WriteUInt16(p, (ushort)s.Year); c.Mem.WriteUInt16(p + 2, (ushort)s.Month); c.Mem.WriteUInt16(p + 4, (ushort)s.DayOfWeek); c.Mem.WriteUInt16(p + 6, (ushort)s.Day);
        c.Mem.WriteUInt16(p + 8, (ushort)s.Hour); c.Mem.WriteUInt16(p + 10, (ushort)s.Minute); c.Mem.WriteUInt16(p + 12, (ushort)s.Second); c.Mem.WriteUInt16(p + 14, (ushort)s.Ms);
    }

    private static long NowFileTime() => DateTime.UtcNow.ToFileTimeUtc();

    // --- time zones ---
    private static TimeZoneInfo Zone(Win32Call c) => c.Ctx.Options.TimeZone ?? TimeZoneInfo.Local;

    private readonly record struct Raw(int Year, int Month, int DayOfWeek, int Day, int Hour, int Minute, int Second, int Ms);

    private static Raw ReadRaw(Win32Call c, uint p) =>
        new(c.Mem.ReadUInt16(p), c.Mem.ReadUInt16(p + 2), c.Mem.ReadUInt16(p + 4), c.Mem.ReadUInt16(p + 6), c.Mem.ReadUInt16(p + 8), c.Mem.ReadUInt16(p + 10), c.Mem.ReadUInt16(p + 12), c.Mem.ReadUInt16(p + 14));

    private static void WriteRaw(Win32Call c, uint p, Raw r)
    {
        c.Mem.WriteUInt16(p, (ushort)r.Year); c.Mem.WriteUInt16(p + 2, (ushort)r.Month); c.Mem.WriteUInt16(p + 4, (ushort)r.DayOfWeek); c.Mem.WriteUInt16(p + 6, (ushort)r.Day);
        c.Mem.WriteUInt16(p + 8, (ushort)r.Hour); c.Mem.WriteUInt16(p + 10, (ushort)r.Minute); c.Mem.WriteUInt16(p + 12, (ushort)r.Second); c.Mem.WriteUInt16(p + 14, (ushort)r.Ms);
    }

    private static Raw ToRaw(TimeZoneInfo.TransitionTime t)
    {
        var tod = t.TimeOfDay;
        return t.IsFixedDateRule
            ? new Raw(0, t.Month, 0, t.Day, tod.Hour, tod.Minute, tod.Second, tod.Millisecond)   // a fixed day of every year: not expressible as wDay = week, so it is the absolute-date form below
            : new Raw(0, t.Month, (int)t.DayOfWeek, t.Week, tod.Hour, tod.Minute, tod.Second, tod.Millisecond);
    }

    private static void WriteName(Win32Call c, uint p, string name)
    {
        var buf = new byte[64];
        Encoding.Unicode.GetBytes(name.Length > 31 ? name[..31] : name).CopyTo(buf, 0);
        c.Mem.Write(p, buf);
    }

    private static uint GetTimeZone(Win32Call c, uint p)
    {
        var tz = Zone(c);
        var now = DateTime.UtcNow;
        var rule = tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart.Date <= now.Date && now.Date <= r.DateEnd.Date);
        var bias = -(tz.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero));
        c.Mem.Write(p, new byte[TzSize]);
        c.Mem.WriteUInt32(p, (uint)(int)bias.TotalMinutes);
        WriteName(c, p + 4, tz.StandardName);
        WriteName(c, p + 88, tz.SupportsDaylightSavingTime ? tz.DaylightName : tz.StandardName);
        if (!tz.SupportsDaylightSavingTime || rule is null || rule.DaylightDelta == TimeSpan.Zero) return 0;
        // A fixed-date rule recurs on the same day every year, which Windows spells with wYear = 0 only for floating dates; an exact day of month is given as week 1 of that day's weekday is not
        // equivalent, so fixed rules are written as absolute dates of the current year (documented SYSTEMTIME form).
        Raw Date(TimeZoneInfo.TransitionTime t)
        {
            var r = ToRaw(t);
            return t.IsFixedDateRule ? r with { Year = now.Year, DayOfWeek = new St(now.Year, t.Month, t.Day, 0, 0, 0, 0).DayOfWeek } : r;
        }
        WriteRaw(c, p + 68, Date(rule.DaylightTransitionEnd));
        WriteRaw(c, p + 152, Date(rule.DaylightTransitionStart));
        c.Mem.WriteUInt32(p + 168, (uint)(int)-rule.DaylightDelta.TotalMinutes);
        return tz.IsDaylightSavingTime(now) ? 2u : 1u;
    }

    /// <summary>Local wall-clock instant (as a FILETIME-scale value) of a transition in the given local year, or null for "no transition".</summary>
    private static long? TransitionLocal(Raw r, int year)
    {
        if (r.Month == 0) return null;
        int day;
        if (r.Year != 0) { year = r.Year; day = r.Day; }
        else
        {
            var firstDow = new St(year, r.Month, 1, 0, 0, 0, 0).DayOfWeek;
            day = 1 + (r.DayOfWeek - firstDow + 7) % 7 + (Math.Clamp(r.Day, 1, 5) - 1) * 7;
            while (day > DaysIn(year, r.Month)) day -= 7;
        }
        if (day < 1 || day > DaysIn(year, r.Month)) return null;
        return ToFileTime(new St(year, r.Month, day, r.Hour, r.Minute, r.Second, r.Ms));
    }

    private const long TicksPerMinute = 600_000_000;

    private static bool IsDaylight(long utc, int bias, int standardBias, int daylightBias, Raw standard, Raw daylight)
    {
        if (standard.Month == 0 || daylight.Month == 0) return false;
        var year = FromFileTime(Math.Max(0, utc - bias * TicksPerMinute)).Year;
        if (TransitionLocal(daylight, year) is not { } start || TransitionLocal(standard, year) is not { } end) return false;
        var utcStart = start + (bias + standardBias) * TicksPerMinute;   // daylight time starts at a standard-time wall clock reading
        var utcEnd = end + (bias + daylightBias) * TicksPerMinute;       // and ends at a daylight-time reading
        return utcStart < utcEnd ? utc >= utcStart && utc < utcEnd : !(utc >= utcEnd && utc < utcStart);
    }

    private static uint ToLocal(Win32Call c, uint tzi, uint utcPtr, uint localPtr)
    {
        var st = ReadSt(c, utcPtr);
        if (!Valid(st)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        var utc = ToFileTime(st);
        long offset;
        if (tzi == 0)
        {
            var tz = Zone(c);
            try { offset = tz.GetUtcOffset(DateTime.FromFileTimeUtc(utc)).Ticks; }
            catch (ArgumentOutOfRangeException) { offset = tz.BaseUtcOffset.Ticks; }
        }
        else
        {
            var bias = (int)c.Mem.ReadUInt32(tzi);
            var standardBias = (int)c.Mem.ReadUInt32(tzi + 84);
            var daylightBias = (int)c.Mem.ReadUInt32(tzi + 168);
            var dst = IsDaylight(utc, bias, standardBias, daylightBias, ReadRaw(c, tzi + 68), ReadRaw(c, tzi + 152));
            offset = -(bias + (dst ? daylightBias : standardBias)) * TicksPerMinute;
        }
        var local = utc + offset;
        if (local < 0 || local > MaxFileTime) return c.Ctx.Fail(Win32Error.InvalidParameter);
        WriteSt(c, localPtr, FromFileTime(local));
        return 1;
    }

    // --- locale formatting ---
    private const uint LocaleInvariant = 0x7F, LocaleUserDefault = 0x400, LocaleSystemDefault = 0x800, LocaleEnUs = 0x409, LocaleModifierBits = 0xC0000000;

    private static bool Locale(uint locale, out bool invariant)
    {
        invariant = locale == LocaleInvariant;
        return invariant || locale is LocaleUserDefault or LocaleSystemDefault or LocaleEnUs;
    }

    /// <summary>Applies a date/time picture. Returns null when the picture contains an unquoted letter this formatter does not know.</summary>
    private static string? Picture(string picture, St t, bool invariant)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < picture.Length;)
        {
            var ch = picture[i];
            if (ch == '\'')
            {
                for (i++; i < picture.Length;)
                {
                    if (picture[i] == '\'') { if (i + 1 < picture.Length && picture[i + 1] == '\'') { sb.Append('\''); i += 2; continue; } i++; break; }
                    sb.Append(picture[i++]);
                }
                continue;
            }
            if (!char.IsAsciiLetter(ch)) { sb.Append(ch); i++; continue; }
            var n = 1;
            while (i + n < picture.Length && picture[i + n] == ch) n++;
            i += n;
            var hour12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
            switch (ch)
            {
                case 'd': sb.Append(n switch { 1 => t.Day.ToString(), 2 => t.Day.ToString("00"), 3 => Days[t.DayOfWeek][..3], _ => Days[t.DayOfWeek] }); break;
                case 'M': sb.Append(n switch { 1 => t.Month.ToString(), 2 => t.Month.ToString("00"), 3 => Months[t.Month - 1][..3], _ => Months[t.Month - 1] }); break;
                case 'y': sb.Append(n switch { 1 => (t.Year % 100).ToString(), 2 => (t.Year % 100).ToString("00"), _ => t.Year.ToString("0000") }); break;
                case 'h': sb.Append(n == 1 ? hour12.ToString() : hour12.ToString("00")); break;
                case 'H': sb.Append(n == 1 ? t.Hour.ToString() : t.Hour.ToString("00")); break;
                case 'm': sb.Append(n == 1 ? t.Minute.ToString() : t.Minute.ToString("00")); break;
                case 's': sb.Append(n == 1 ? t.Second.ToString() : t.Second.ToString("00")); break;
                case 't': { var marker = t.Hour < 12 ? "AM" : "PM"; sb.Append(n == 1 ? marker[..1] : marker); break; }
                default: return null;
            }
        }
        return sb.ToString();
    }

    private static uint Format(Win32Call c, bool date)
    {
        uint locale = c[0], flags = c[1], timePtr = c[2], formatPtr = c[3], buffer = c[4];
        var cch = (int)c[5];
        if (!Locale(locale, out var invariant) || cch < 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        flags &= ~LocaleModifierBits & ~0x40000000u;
        St t;
        if (timePtr == 0) t = FromFileTime(NowFileTime() + Zone(c).GetUtcOffset(DateTime.UtcNow).Ticks);
        else
        {
            t = ReadSt(c, timePtr);
            if (!Valid(t)) return c.Ctx.Fail(Win32Error.InvalidParameter);
        }
        string picture;
        if (formatPtr != 0)
        {
            if (flags != 0) return c.Ctx.Fail(Win32Error.InvalidFlags);
            picture = c.Mem.ReadWideString(formatPtr, 1024);
        }
        else if (date)
        {
            if (flags is not (0 or 1 or 2)) return c.Ctx.Fail(Win32Error.InvalidFlags);
            picture = flags == 2 ? (invariant ? "dddd, dd MMMM yyyy" : "dddd, MMMM d, yyyy") : (invariant ? "MM/dd/yyyy" : "M/d/yyyy");
        }
        else
        {
            if ((flags & ~0xFu) != 0) return c.Ctx.Fail(Win32Error.InvalidFlags);
            var h24 = invariant || (flags & 8) != 0;
            picture = (h24 ? "HH" : "h") + ((flags & 1) != 0 ? "" : ":mm" + ((flags & 3) != 0 ? "" : ":ss")) + (h24 || (flags & 4) != 0 ? "" : " tt");
        }
        var text = Picture(picture, t, invariant);
        if (text is null) return c.Ctx.Fail(Win32Error.InvalidFlags);
        var needed = text.Length + 1;
        if (cch == 0) return (uint)needed;
        if (buffer == 0) return c.Ctx.Fail(Win32Error.InvalidParameter);
        if (cch < needed) return c.Ctx.Fail(Win32Error.InsufficientBuffer);
        c.Mem.Write(buffer, Encoding.Unicode.GetBytes(text + "\0"));
        return (uint)needed;
    }

    protected override void Define()
    {
        const string K = "KERNEL32.dll";
        Std(K, "GetSystemTime", 1, c => { WriteSt(c, c[0], FromFileTime(NowFileTime())); return 0; });
        Std(K, "GetLocalTime", 1, c => { WriteSt(c, c[0], FromFileTime(NowFileTime() + Zone(c).GetUtcOffset(DateTime.UtcNow).Ticks)); return 0; });
        Std(K, "SystemTimeToFileTime", 2, c =>
        {
            var st = ReadSt(c, c[0]);
            if (!Valid(st)) return c.Ctx.Fail(Win32Error.InvalidParameter);
            c.Mem.WriteUInt64(c[1], (ulong)ToFileTime(st));
            return 1;
        });
        Std(K, "FileTimeToSystemTime", 2, c =>
        {
            Span<byte> b = stackalloc byte[8];
            c.Mem.Read(c[0], b);
            var ft = BitConverter.ToInt64(b);
            if (ft < 0 || ft > MaxFileTime) return c.Ctx.Fail(Win32Error.InvalidParameter);
            WriteSt(c, c[1], FromFileTime(ft));
            return 1;
        });
        Std(K, "GetTimeZoneInformation", 1, c => GetTimeZone(c, c[0]));
        Std(K, "SystemTimeToTzSpecificLocalTime", 3, c => ToLocal(c, c[0], c[1], c[2]));
        Std(K, "GetDateFormatW", 6, c => Format(c, true));
        Std(K, "GetTimeFormatW", 6, c => Format(c, false));
    }
}
