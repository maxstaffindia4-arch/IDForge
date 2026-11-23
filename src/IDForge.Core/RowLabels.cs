namespace IDForge.Core;

/// <summary>Display label for a batch/review row: "Name — UID<digits>".</summary>
public static class RowLabels
{
    /// <summary>Name — UID-prefixed digits; "(no name)" / "(no UID)" fallbacks.</summary>
    public static string For(string? name, string? uid)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Trim();
        var digits = Uid.Normalize(uid);
        var u = digits != null && Uid.IsValid(digits) ? Uid.BarcodeContent(digits) : "(no UID)";
        return $"{n} — {u}";
    }
}
