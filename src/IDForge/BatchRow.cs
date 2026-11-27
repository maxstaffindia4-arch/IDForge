using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    public string Uid { get => _uid; set { Set(ref _uid, value); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(ReviewLabel)); OnPropertyChanged(nameof(UidEmpty)); OnPropertyChanged(nameof(UidCounterText)); OnPropertyChanged(nameof(UidInvalid)); OnPropertyChanged(nameof(UidInvalidBrush)); OnPropertyChanged(nameof(MessageIsError)); OnPropertyChanged(nameof(MessageBrush)); } }
    public string Name { get => _name; set { Set(ref _name, value); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(ReviewLabel)); OnPropertyChanged(nameof(NameEmpty)); } }
    /// <summary>Placeholder visibility for the grid inputs (I-B).</summary>
    public bool NameEmpty => string.IsNullOrWhiteSpace(_name);
    public bool UidEmpty => string.IsNullOrWhiteSpace(_uid);
    /// <summary>"8 / 15" digit counter under the Employee ID input (I-B).</summary>
    public string UidCounterText => $"{_uid.Length} / {Core.Uid.MaxLength}";
    public string Blood { get => _blood; set => Set(ref _blood, value); }
    public string Position { get => _position; set { Set(ref _position, value); OnPropertyChanged(nameof(PositionEmpty)); } }
    /// <summary>True when no position is chosen — drives the "Choose position" placeholder.</summary>
    public bool PositionEmpty => string.IsNullOrWhiteSpace(_position);
    public string Contract { get => _contract; set => Set(ref _contract, value); }
    public string PrintDateText { get => _printDateText; set { Set(ref _printDateText, value); OnPropertyChanged(nameof(PrintDateDisplay)); } }
    /// <summary>Print date as shown in the grid (dd/MM/yyyy); the stored value stays ISO.</summary>
    public string PrintDateDisplay
    {
        get => DateOnly.TryParse(PrintDateText, out var d) ? d.ToString("dd/MM/yyyy") : PrintDateText;
        set
        {
            var v = (value ?? "").Trim();
            if (DateOnly.TryParseExact(v, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dmy))
                PrintDateText = dmy.ToString("yyyy-MM-dd");
            else if (DateOnly.TryParse(v, out var d))
                PrintDateText = d.ToString("yyyy-MM-dd");
            else
                PrintDateText = v;
        }
    }
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
    public string Status { get => _status; set { Set(ref _status, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(ThumbOpacity)); OnPropertyChanged(nameof(PhotoPlaceholderKind)); OnPropertyChanged(nameof(StatusPillLabel)); OnPropertyChanged(nameof(StatusPillIcon)); OnPropertyChanged(nameof(StatusPillSpinning)); OnPropertyChanged(nameof(StatusPillBg)); OnPropertyChanged(nameof(StatusPillFg)); OnPropertyChanged(nameof(StatusPillBorder)); OnPropertyChanged(nameof(MessageIsError)); OnPropertyChanged(nameof(MessageBrush)); } }
    public string Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(DisplayText)); OnPropertyChanged(nameof(MessageIsError)); OnPropertyChanged(nameof(MessageBrush)); } }

    /// <summary>Status column text: state plus failure reason.</summary>
    public string StatusText => Status switch
    {
        "Approved" => "Issuing…",   // transient state inside Approve & Issue — never a resting row state
        "Failed" when !string.IsNullOrEmpty(Error) => $"Failed — {Error}",
        _ => Status
    };

    public string StatusIcon => Status switch
    {
        "Issued" => "✔",
        "Failed" => "✖",
        "Skipped" => "⤼",
        "NeedsReview" => "◉",
        "Processing" => "…",
        "Approved" => "…",
        _ => "·"
    };

    /// <summary>1-based position in the batch grid (set by the view).</summary>
    public int RowNumber { get => _rowNumber; set => Set(ref _rowNumber, value); }

    /// <summary>"New card" or "Re-issue" (UID already exists in Records).</summary>
    public string CardType { get => _cardType; set { Set(ref _cardType, value); OnPropertyChanged(nameof(CardTypePillIcon)); OnPropertyChanged(nameof(CardTypePillBg)); OnPropertyChanged(nameof(CardTypePillFg)); OnPropertyChanged(nameof(CardTypeIsReissue)); } }

    /// <summary>"Replaces card issued dd/MM/yyyy" for re-issue rows.</summary>
    public string ReplacesText { get => _replacesText; set => Set(ref _replacesText, value); }

    /// <summary>Message column: validation error, re-issue note, duplicate flag.</summary>
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Review-list label: "Name — UID<digits>" (see Core.RowLabels).</summary>
    public string ReviewLabel => Core.RowLabels.For(_name, _uid);

    /// <summary>Row list display: icon + uid + name + status.</summary>
    public string DisplayText => $"{StatusIcon}  {Uid} — {Name}   [{StatusText}]";

    // ---- grid cell presentation (§2.5-2.8) ----

    /// <summary>Select-column checkbox; synced with single grid selection.</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    private bool _isSelected;

    /// <summary>Employee-cell thumbnail (cutout/window image preferred, else photo).</summary>
    public ImageSource? Thumb { get; private set; }
    /// <summary>Thumbnail dims: 28x32 at 2x for DPI.</summary>
    public void SetThumb(ImageSource? src)
    {
        if (ReferenceEquals(Thumb, src)) return;
        Thumb = src;
        OnPropertyChanged(nameof(Thumb));
        OnPropertyChanged(nameof(ShowThumb));
        OnPropertyChanged(nameof(PhotoPlaceholderKind));
    }
    public bool ShowThumb => Thumb != null;

    /// <summary>0 = none, 1 = red "needs photo" placeholder, 2 = grey draft placeholder.
    /// Set by RefreshBatchDerived; the grid picks the border style from it.</summary>
    public int PhotoPlaceholderKind { get; private set; }
    public bool ShowPhotoPlaceholderRed => PhotoPlaceholderKind == 1;
    public bool ShowPhotoPlaceholderGrey => PhotoPlaceholderKind == 2;
    public void SetPhotoPlaceholderKind(int kind)
    {
        if (PhotoPlaceholderKind == kind) return;
        PhotoPlaceholderKind = kind;
        OnPropertyChanged(nameof(PhotoPlaceholderKind));
        OnPropertyChanged(nameof(ShowPhotoPlaceholderRed));
        OnPropertyChanged(nameof(ShowPhotoPlaceholderGrey));
    }

    /// <summary>Thumbnail dims at 60% while the row is being processed (§2.6).</summary>
    public double ThumbOpacity => Status is "Processing" or "Approved" ? 0.6 : 1.0;

    /// <summary>Employee ID fails 4-15 digits -> red cell border + red message.</summary>
    public bool UidInvalid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_uid)) return false;
            return !IDForge.Core.Uid.IsValid(IDForge.Core.Uid.Normalize(_uid));
        }
    }
    /// <summary>Border brush for the UID cell wrapper: red when invalid.</summary>
    public Brush UidInvalidBrush => UidInvalid ? PillError : PillBorder;

    /// <summary>Status pill (87.7): label + FA icon + tint bg + solid fg.</summary>
    public string StatusPillLabel => Status switch
    {
        "Issued" => "Issued",
        "NeedsReview" => "Ready to review",
        "Failed" => "Needs attention",
        "NeedsPhoto" => "Needs photo",
        "Processing" => "Processing",
        "Approved" => "Issuing…",
        "Skipped" => "Skipped",
        _ => "Draft"
    };
    public string StatusPillIcon => Status switch
    {
        "Issued" => "\uF00C",
        "NeedsReview" => "\uF06E",
        "Failed" or "NeedsPhoto" => "\uF06A",
        "Processing" or "Approved" => "\uF110",
        "Skipped" => "\uF056",
        _ => "\uF15B"
    };
    public bool StatusPillSpinning => Status is "Processing" or "Approved";
    public Brush StatusPillBg => Status switch
    {
        "Issued" => PillSuccessTint,
        "NeedsReview" => PillPrimaryTint,
        "Failed" or "NeedsPhoto" => PillErrorTint,
        "Processing" or "Approved" => PillWarningTint,
        _ => PillSecondary
    };
    public Brush StatusPillFg => Status switch
    {
        "Issued" => PillSuccess,
        "NeedsReview" => PillPrimaryHover,
        "Failed" or "NeedsPhoto" => PillError,
        "Processing" or "Approved" => PillWarning,
        "Skipped" => PillMuted,
        _ => PillSecondaryText
    };
    public Brush StatusPillBorder => Status is "Draft" or "Skipped" ? PillBorder : Brushes.Transparent;

    /// <summary>Card-type pill (§2.7): "New card" accent, "Re-issue" warning + rotate icon.</summary>
    public string CardTypePillIcon => CardType == "Re-issue" ? "\uF2F1" : "\uF067";
    public Brush CardTypePillBg => CardType == "Re-issue" ? PillWarningTint : PillAccent;
    public Brush CardTypePillFg => CardType == "Re-issue" ? PillWarning : PillAccentText;
    public bool CardTypeIsReissue => CardType == "Re-issue";

    /// <summary>Message column: red for errors/invalid ID, secondary otherwise.</summary>
    public bool MessageIsError => Status == "Failed" || UidInvalid || !string.IsNullOrEmpty(Error);
    /// <summary>Foreground brush for the Message cell.</summary>
    public Brush MessageBrush => MessageIsError ? PillError : PillSecondaryText;

    /// <summary>"Add photo…" link under the message when the row lacks a photo.</summary>
    public bool ShowAddPhotoLink { get; private set; }
    public void SetShowAddPhotoLink(bool show)
    {
        if (ShowAddPhotoLink == show) return;
        ShowAddPhotoLink = show;
        OnPropertyChanged(nameof(ShowAddPhotoLink));
    }

    // Frozen pill brushes — values mirror Themes/Colors.xaml (no dark theme).
    private static readonly SolidColorBrush PillSuccessTint = Frozen(0x1A, 0x16, 0xA3, 0x4A);
    private static readonly SolidColorBrush PillPrimaryTint = Frozen(0x1A, 0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush PillErrorTint = Frozen(0x1A, 0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush PillWarningTint = Frozen(0x1A, 0xD9, 0x77, 0x06);
    private static readonly SolidColorBrush PillSuccess = Frozen(0xFF, 0x16, 0xA3, 0x4A);
    private static readonly SolidColorBrush PillPrimaryHover = Frozen(0xFF, 0x25, 0x63, 0xEB);
    private static readonly SolidColorBrush PillError = Frozen(0xFF, 0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush PillWarning = Frozen(0xFF, 0xD9, 0x77, 0x06);
    private static readonly SolidColorBrush PillSecondary = Frozen(0xFF, 0xF3, 0xF4, 0xF6);
    private static readonly SolidColorBrush PillSecondaryText = Frozen(0xFF, 0x4B, 0x55, 0x63);
    private static readonly SolidColorBrush PillMuted = Frozen(0xFF, 0x6B, 0x72, 0x80);
    private static readonly SolidColorBrush PillBorder = Frozen(0xFF, 0xE5, 0xE7, 0xEB);
    private static readonly SolidColorBrush PillAccent = Frozen(0xFF, 0xE0, 0xF2, 0xFE);
    private static readonly SolidColorBrush PillAccentText = Frozen(0xFF, 0x1E, 0x3A, 0x8A);
    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---- runtime photo state (not all persisted) ----
    /// <summary>Inputs the current <see cref="Thumb"/> was built from (MainWindow caches on this).</summary>
    internal (string? PhotoPath, string? CutoutPath, SKBitmap? WindowImage, SKBitmap? Cutout)? ThumbKey;
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
