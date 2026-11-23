namespace IDForge.Core.Batch;

/// <summary>Parsed batch row before validation.</summary>
public sealed record BatchRowData(
    string Uid, string Name, string Blood, string Position,
    string Contract, string PrintDateText, string? PhotoPath);

/// <summary>CSV/TSV paste parsing and per-row validation for batch mode.</summary>
public static class BatchValidator
{
    /// <summary>Parses pasted/imported text. TSV or CSV, optional header row.</summary>
    public static List<BatchRowData> Parse(string text)
    {
        var rows = new List<BatchRowData>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sep = line.Contains('\t') ? '\t' : ',';
            var p = line.Split(sep).Select(s => s.Trim().Trim('"')).ToArray();
            if (p.Length == 0) continue;
            if (p[0].Equals("uid", StringComparison.OrdinalIgnoreCase)) continue; // header
            rows.Add(new BatchRowData(
                Uid.Normalize(p.ElementAtOrDefault(0) ?? "") ?? "",
                p.ElementAtOrDefault(1) ?? "",
                p.ElementAtOrDefault(2) is { Length: > 0 } b ? b : "N/A",
                p.ElementAtOrDefault(3) ?? "",
                p.ElementAtOrDefault(4) is { Length: > 0 } c ? c : "Permanent",
                p.ElementAtOrDefault(5) is { Length: > 0 } pd ? pd : "",
                p.ElementAtOrDefault(6) is { Length: > 0 } ph ? ph : null));
        }
        return rows;
    }

    /// <summary>
    /// Resolves relative photo paths against the folder containing the CSV file,
    /// so sample/import files work from any working directory. Rooted paths and
    /// empty paths are left unchanged.
    /// </summary>
    public static void ResolvePaths(List<BatchRowData> rows, string baseDir)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i].PhotoPath;
            if (string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p)) continue;
            rows[i] = rows[i] with { PhotoPath = Path.GetFullPath(Path.Combine(baseDir, p)) };
        }
    }

    /// <summary>
    /// Validates one row. seenUids accumulates UIDs for intra-batch duplicate
    /// detection; uidExists checks the database. Returns null when valid.
    /// </summary>
    public static string? Validate(BatchRowData r, ISet<string> seenUids,
        Func<string, bool> uidExists, bool checkPhoto = true)
    {
        var uid = Uid.Normalize(r.Uid);
        if (uid == null) return "UID required";
        if (!Uid.IsValid(uid)) return "UID must be digits only (4-15)";
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length < 2) return "name required";
        if (!Config.BloodGroups.Contains(r.Blood)) return "bad blood group";
        if (!Config.Positions.Contains(r.Position)) return "bad position";
        if (!Config.ContractTypes.Contains(r.Contract)) return "bad contract type";
        var dateText = string.IsNullOrWhiteSpace(r.PrintDateText)
            ? Validity.TodayInDhaka().ToString("yyyy-MM-dd") : r.PrintDateText;
        if (!DateOnly.TryParse(dateText, out _)) return "bad print date";
        if (!seenUids.Add(uid)) return "duplicate UID in batch";
        if (uidExists(uid)) return "UID already exists";
        if (checkPhoto && (string.IsNullOrWhiteSpace(r.PhotoPath) || !File.Exists(r.PhotoPath)))
            return "photo missing";
        return null;
    }
}
