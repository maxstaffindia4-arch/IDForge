using System.Text.RegularExpressions;

namespace IDForge.Core;

/// <summary>
/// Staff UID handling. The operator enters digits only (e.g. 20250318);
/// "UID"/"ID." prefixes and spaces are stripped on input. Storage keeps the
/// digits; the barcode encodes "UID" + digits; output files are "UID"+digits.
/// </summary>
public static class Uid
{
    public const int MinLength = 4;
    public const int MaxLength = 15;

    private static readonly Regex PrefixRe = new(@"^(?i)(?:uid|id)[\s.:\-]*", RegexOptions.Compiled);
    private static readonly Regex SpaceRe = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes operator input to bare digits: strips a leading UID/ID
    /// prefix and all whitespace. Returns null when nothing usable remains.
    /// Does NOT validate length — call <see cref="IsValid"/> for that.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        var m = PrefixRe.Match(s);
        if (m.Success) s = s[m.Length..];
        s = SpaceRe.Replace(s, "");
        return s.Length == 0 ? null : s;
    }

    /// <summary>Digits only, 4–15 characters.</summary>
    public static bool IsValid(string? normalized) =>
        normalized != null && normalized.Length >= MinLength && normalized.Length <= MaxLength
        && normalized.All(char.IsDigit);

    /// <summary>Barcode payload: "UID" + digits (e.g. UID20250318).</summary>
    public static string BarcodeContent(string normalizedDigits) => $"UID{normalizedDigits}";

    /// <summary>File-name token keeping the historic "UID20250318" shape.</summary>
    public static string FileToken(string normalizedDigits) => $"UID{normalizedDigits}";
}
