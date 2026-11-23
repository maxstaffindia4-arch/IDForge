using System.IO;
using System.Text.Json;
using IDForge.Core;

namespace IDForge;

/// <summary>
/// Autosaved New Card draft (newcard_draft.json in the data dir).
/// Holds every field plus the photo path and nudge transform so a
/// half-finished card survives an app restart. Cleared by Clear form
/// and after a successful issue.
/// </summary>
public sealed class NewCardDraft
{
    public string Name { get; set; } = "";
    public string Blood { get; set; } = "N/A";
    public string Position { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Contract { get; set; } = "Permanent";
    public string PrintDate { get; set; } = "";        // yyyy-MM-dd
    public string BgMode { get; set; } = "PlainWhite"; // PlainWhite | Model | Keep
    public string? PhotoPath { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public string SavedAt { get; set; } = "";

    public static string DraftPath => Path.Combine(AppPaths.DataDir, "newcard_draft.json");

    public static void Save(NewCardDraft d)
    {
        try { File.WriteAllText(DraftPath, JsonSerializer.Serialize(d)); }
        catch { }
    }

    public static NewCardDraft? Load()
    {
        try
        {
            if (!File.Exists(DraftPath)) return null;
            var d = JsonSerializer.Deserialize<NewCardDraft>(File.ReadAllText(DraftPath));
            return d == null || string.IsNullOrWhiteSpace(d.Name) && string.IsNullOrWhiteSpace(d.Uid)
                && string.IsNullOrWhiteSpace(d.PhotoPath) ? null : d;
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); } catch { }
    }
}
