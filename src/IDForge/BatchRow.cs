using System.ComponentModel;
using System.Runtime.CompilerServices;
using IDForge.Core;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using SkiaSharp;

namespace IDForge;

/// <summary>
/// One editable batch row. Holds UI-editable fields, photo-processing state
/// (cutout + transforms), and the persisted BatchRows.Id (RowId, 0 = unsaved).
/// </summary>
public sealed class BatchRow : INotifyPropertyChanged
{
    public long RowId { get; set; }                    // BatchRows.Id, 0 = not yet saved
    public long SessionId { get; set; }

    private string _uid = "", _name = "", _blood = "N/A", _position = "",
        _contract = "Permanent", _printDateText = Validity.TodayInDhaka().ToString("yyyy-MM-dd"),
        _status = "Draft", _error = "", _message = "", _cardType = "New card",
        _replacesText = "";
    private int _rowNumber;
    private string? _photoPath, _cutoutPath;
    private bool _approved;
    private int _attemptCount;
    private long? _cardIssueId;
    private string _bgMode = "PlainWhite";
    private double _zoom = 1, _offsetX, _offsetY;
    private double _autoScale = 1, _autoOffsetX, _autoOffsetY;

    public string Uid { get => _uid; set { Set(ref _uid, value); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(ReviewLabel)); } }
    public string Name { get => _name; set { Set(ref _name, value); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(ReviewLabel)); } }
    public string Blood { get => _blood; set => Set(ref _blood, value); }
    public string Position { get => _position; set => Set(ref _position, value); }
    public string Contract { get => _contract; set => Set(ref _contract, value); }
    public string PrintDateText { get => _printDateText; set => Set(ref _printDateText, value); }
    public string? PhotoPath { get => _photoPath; set => Set(ref _photoPath, value); }
    public string? CutoutPath { get => _cutoutPath; set => Set(ref _cutoutPath, value); }
    public bool Approved { get => _approved; set => Set(ref _approved, value); }
    public int AttemptCount { get => _attemptCount; set => Set(ref _attemptCount, value); }
    public long? CardIssueId { get => _cardIssueId; set => Set(ref _cardIssueId, value); }
    public string BgMode { get => _bgMode; set => Set(ref _bgMode, value); }
    public double Zoom { get => _zoom; set => Set(ref _zoom, value); }
    public double OffsetX { get => _offsetX; set => Set(ref _offsetX, value); }
    public double OffsetY { get => _offsetY; set => Set(ref _offsetY, value); }
    public double AutoScale { get => _autoScale; set => Set(ref _autoScale, value); }
    public double AutoOffsetX { get => _autoOffsetX; set => Set(ref _autoOffsetX, value); }
    public double AutoOffsetY { get => _autoOffsetY; set => Set(ref _autoOffsetY, value); }

    /// <summary>Draft | NeedsPhoto | Processing | NeedsReview | Approved | Issued | Failed | Skipped</summary>
    public string Status { get => _status; set { Set(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(DisplayText)); } }
    public string Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(DisplayText)); } }

    /// <summary>Status column text: state plus failure reason.</summary>
    public string StatusText => Status == "Failed" && !string.IsNullOrEmpty(Error)
        ? $"Failed — {Error}" : Status;

    public string StatusIcon => Status switch
    {
        "Issued" => "✔",
        "Failed" => "✖",
        "Skipped" => "⤼",
        "NeedsReview" => "👁",
        "Processing" => "…",
        "Approved" => "✓",
        _ => "·"
    };

    /// <summary>1-based position in the batch grid (set by the view).</summary>
    public int RowNumber { get => _rowNumber; set => Set(ref _rowNumber, value); }

    /// <summary>"New card" or "Re-issue" (UID already exists in Records).</summary>
    public string CardType { get => _cardType; set => Set(ref _cardType, value); }

    /// <summary>"Replaces card issued dd/MM/yyyy" for re-issue rows.</summary>
    public string ReplacesText { get => _replacesText; set => Set(ref _replacesText, value); }

    /// <summary>Message column: validation error, re-issue note, duplicate flag.</summary>
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Review-list label: "Name — UID<digits>" (see Core.RowLabels).</summary>
    public string ReviewLabel => Core.RowLabels.For(_name, _uid);

    /// <summary>Row list display: icon + uid + name + status.</summary>
    public string DisplayText => $"{StatusIcon}  {Uid} — {Name}   [{StatusText}]";

    // ---- runtime photo state (not all persisted) ----
    public SKBitmap? Cutout { get; set; }
    public SKBitmap? WindowImage { get; set; }
    public WindowTransform? AutoTransform { get; set; }
    public string? BgMethod { get; set; }
    public string? Warning { get; set; }

    public DateOnly PrintDate =>
        DateOnly.TryParse(PrintDateText, out var d) ? d : Validity.TodayInDhaka();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public BatchRowEntry ToEntry() => new()
    {
        Id = RowId, SessionId = SessionId, Uid = Uid, Name = Name,
        BloodGroup = Blood, Position = Position, ContractType = Contract,
        PrintDate = PrintDateText, PhotoPath = PhotoPath, CutoutPath = CutoutPath,
        BgMode = BgMode, Zoom = Zoom, OffsetX = OffsetX, OffsetY = OffsetY,
        AutoScale = AutoScale, AutoOffsetX = AutoOffsetX, AutoOffsetY = AutoOffsetY,
        Status = Status, Error = Error, AttemptCount = AttemptCount,
        CardIssueId = CardIssueId, Approved = Approved
    };

    public static BatchRow FromEntry(BatchRowEntry e) => new()
    {
        RowId = e.Id, SessionId = e.SessionId, Uid = e.Uid, Name = e.Name,
        Blood = e.BloodGroup, Position = e.Position, Contract = e.ContractType,
        PrintDateText = e.PrintDate, PhotoPath = e.PhotoPath, CutoutPath = e.CutoutPath,
        BgMode = e.BgMode, Zoom = e.Zoom, OffsetX = e.OffsetX, OffsetY = e.OffsetY,
        AutoScale = e.AutoScale, AutoOffsetX = e.AutoOffsetX, AutoOffsetY = e.AutoOffsetY,
        Status = e.Status, Error = e.Error ?? "", AttemptCount = e.AttemptCount,
        CardIssueId = e.CardIssueId, Approved = e.Approved
    };
}
