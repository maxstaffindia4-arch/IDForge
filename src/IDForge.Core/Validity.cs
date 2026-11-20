namespace IDForge.Core;

public static class Validity
{
    /// <summary>Adds months with end-of-month clamping (Aug 31 + 6mo -> Feb 28/29).</summary>
    public static DateOnly AddMonthsClamped(DateOnly date, int months)
    {
        int total = (date.Year * 12 + (date.Month - 1)) + months;
        int year = total / 12;
        int month = total % 12 + 1;
        int day = Math.Min(date.Day, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    /// <summary>Contractual = print date + 6 months; Permanent = + 12 months.</summary>
    public static DateOnly CalculateValidUntil(DateOnly printDate, string contractType)
    {
        int months = contractType == "Permanent"
            ? Config.PermanentMonths
            : Config.ContractualMonths;
        return AddMonthsClamped(printDate, months);
    }

    /// <summary>"Valid Until May 2027" — month name + year only.</summary>
    public static string FormatValidity(DateOnly validUntil)
        => $"Valid Until {validUntil:MMMM yyyy}";

    /// <summary>Today's date in Asia/Dhaka.</summary>
    public static DateOnly TodayInDhaka()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Config.TimeZoneId);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return DateOnly.FromDateTime(now);
    }

    /// <summary>Card ID line: "ID. 20250318" — strips a leading "UID" prefix.</summary>
    public static string FormatIdLine(string uid)
    {
        var s = uid.Trim();
        if (s.StartsWith("UID", StringComparison.OrdinalIgnoreCase) && s.Length > 3)
            s = s[3..];
        return $"ID. {s}";
    }
}
