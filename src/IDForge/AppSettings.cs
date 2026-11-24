using System.IO;

namespace IDForge;

/// <summary>Tiny persisted settings (settings.json in the app data dir).</summary>
public static class AppSettings
{
    private static readonly string Path0 = IDForge.Core.AppPaths.SettingsPath;
    private static bool _confirm = true;
    private static bool _includePdf;
    private static string _saveFolder = "";
    private static string _defaultContract = "Permanent";
    private static string _defaultBgMode = "PlainWhite";
    private static string _defaultPrintDate = "today";
    private static DateTime? _lastBackupAt;
    private static double? _winLeft, _winTop, _winWidth, _winHeight;
    private static double? _revLeft, _revTop, _revWidth, _revHeight;
    static AppSettings() => Load();

    /// <summary>Re-issue confirmations can never be disabled (locked on).</summary>
    public static bool AlwaysConfirmReissue => true;

    public static bool ConfirmBeforeIssue
    {
        get => _confirm;
        set { _confirm = value; Save(); }
    }

    /// <summary>Also write a PDF next to the PNG when issuing (default off).</summary>
    public static bool IncludePdf
    {
        get => _includePdf;
        set { _includePdf = value; Save(); }
    }

    /// <summary>Folder new cards are saved to (D4 default: Documents\IDForge Cards).</summary>
    public static string SaveFolder
    {
        get => string.IsNullOrWhiteSpace(_saveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IDForge Cards")
            : _saveFolder;
        set { _saveFolder = value; Save(); }
    }

    /// <summary>Default contract type for new cards.</summary>
    public static string DefaultContract
    {
        get => _defaultContract;
        set { _defaultContract = value; Save(); }
    }

    /// <summary>Default photo background mode: PlainWhite | Model | Keep.</summary>
    public static string DefaultBgMode
    {
        get => _defaultBgMode;
        set { _defaultBgMode = value; Save(); }
    }

    /// <summary>Default print-date behaviour ("today" — contractual offsets are phase 3+).</summary>
    public static string DefaultPrintDate
    {
        get => _defaultPrintDate;
        set { _defaultPrintDate = value; Save(); }
    }

    /// <summary>Date format shown to the operator (display convention).</summary>
    public const string DateFormatDisplay = "dd/MM/yyyy";

    /// <summary>When the last database backup was written, if ever.</summary>
    public static DateTime? LastBackupAt
    {
        get => _lastBackupAt;
        set { _lastBackupAt = value; Save(); }
    }

    /// <summary>Last main-window bounds, or null when never saved / incomplete.</summary>
    public static (double Left, double Top, double Width, double Height)? WindowBounds
    {
        get => _winLeft.HasValue && _winTop.HasValue && _winWidth.HasValue && _winHeight.HasValue
            ? (_winLeft.Value, _winTop.Value, _winWidth.Value, _winHeight.Value)
            : null;
        set
        {
            _winLeft = value?.Left; _winTop = value?.Top;
            _winWidth = value?.Width; _winHeight = value?.Height;
            Save();
        }
    }

    /// <summary>Last Review-window bounds, or null when never saved.</summary>
    public static (double Left, double Top, double Width, double Height)? ReviewBounds
    {
        get => _revLeft.HasValue && _revTop.HasValue && _revWidth.HasValue && _revHeight.HasValue
            ? (_revLeft.Value, _revTop.Value, _revWidth.Value, _revHeight.Value)
            : null;
        set
        {
            _revLeft = value?.Left; _revTop = value?.Top;
            _revWidth = value?.Width; _revHeight = value?.Height;
            Save();
        }
    }

    internal sealed class SettingsFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("confirmBeforeIssue")]
        public bool ConfirmBeforeIssue { get; set; } = true;
        [System.Text.Json.Serialization.JsonPropertyName("includePdf")]
        public bool IncludePdf { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("saveFolder")] public string? SaveFolder { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("defaultContract")] public string? DefaultContract { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("defaultBgMode")] public string? DefaultBgMode { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("defaultPrintDate")] public string? DefaultPrintDate { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("lastBackupAt")] public DateTime? LastBackupAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("winLeft")] public double? WinLeft { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("winTop")] public double? WinTop { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("winWidth")] public double? WinWidth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("winHeight")] public double? WinHeight { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("revLeft")] public double? RevLeft { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("revTop")] public double? RevTop { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("revWidth")] public double? RevWidth { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("revHeight")] public double? RevHeight { get; set; }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(Path0))
            {
                var s = System.Text.Json.JsonSerializer
                    .Deserialize<SettingsFile>(File.ReadAllText(Path0));
                if (s != null)
                {
                    _confirm = s.ConfirmBeforeIssue;
                    _includePdf = s.IncludePdf;
                    if (s.SaveFolder != null) _saveFolder = s.SaveFolder;
                    if (s.DefaultContract != null) _defaultContract = s.DefaultContract;
                    if (s.DefaultBgMode != null) _defaultBgMode = s.DefaultBgMode;
                    if (s.DefaultPrintDate != null) _defaultPrintDate = s.DefaultPrintDate;
                    _lastBackupAt = s.LastBackupAt;
                    _winLeft = s.WinLeft; _winTop = s.WinTop;
                    _winWidth = s.WinWidth; _winHeight = s.WinHeight;
                    _revLeft = s.RevLeft; _revTop = s.RevTop;
                    _revWidth = s.RevWidth; _revHeight = s.RevHeight;
                }
            }
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(Path0, System.Text.Json.JsonSerializer.Serialize(new SettingsFile
            {
                ConfirmBeforeIssue = _confirm,
                IncludePdf = _includePdf,
                SaveFolder = _saveFolder,
                DefaultContract = _defaultContract,
                DefaultBgMode = _defaultBgMode,
                DefaultPrintDate = _defaultPrintDate,
                LastBackupAt = _lastBackupAt,
                WinLeft = _winLeft, WinTop = _winTop, WinWidth = _winWidth, WinHeight = _winHeight,
                RevLeft = _revLeft, RevTop = _revTop, RevWidth = _revWidth, RevHeight = _revHeight
            }));
        }
        catch { }
    }
}
