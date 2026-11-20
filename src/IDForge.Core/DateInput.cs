using System.Globalization;

namespace IDForge.Core;

/// <summary>
/// Operator-facing date entry. The UI collects dd/MM/yyyy text (en-GB);
/// ISO yyyy-MM-dd is also accepted for pasted/imported values.
/// </summary>
public static class DateInput
{
    private static readonly CultureInfo Gb = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Parses "d/M/yyyy" (en-GB) or "yyyy-MM-dd". Returns false on empty/garbage.</summary>
    public static bool TryParse(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        string[] formats = { "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy-M-d" };
        return DateOnly.TryParseExact(s, formats, Gb, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// Re-issue guard: the new print date must not be before the employee's
    /// last issued print date. Returns an error message, or null when OK.
    /// </summary>
    public static string? ValidateReissue(DateOnly newPrint, DateOnly? lastPrintDate)
        => lastPrintDate.HasValue && newPrint < lastPrintDate.Value
            ? $"New print date cannot be before the last print date ({lastPrintDate.Value:dd/MM/yyyy})."
            : null;
}
