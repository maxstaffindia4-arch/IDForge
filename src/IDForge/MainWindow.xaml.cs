using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge.Core;
using IDForge.Core.Batch;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using Uid = IDForge.Core.Uid;
using Microsoft.Win32;
using SkiaSharp;

namespace IDForge;

public partial class MainWindow : Window
{
    private readonly CardService _svc;

    // New-card state
    private SKBitmap? _cutout;
    private SKBitmap? _windowImage;
    private WindowTransform _autoTransform = WindowTransform.Identity;
    private string? _photoPath;
    private DateTime? _photoAddedAt;
    private float _nudgeZoom = 1f, _nudgeX, _nudgeY;
    private bool _photoApproved;
    private Employee? _editingEmployee;
    private bool _restoringDraft;
    private int _previewZoomMode; // 0=Fit, 1=100%, 2=200%
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    // Records state
    private Employee? _selectedEmployee;
    private CardIssue? _selectedIssue;

    // Batch state — rows are persisted to BatchSessions/BatchRows (plan §7).
    private readonly System.Collections.ObjectModel.ObservableCollection<BatchRow> _batchRows = new();
    private long _batchSessionId;
    private CancellationTokenSource? _batchCts;
    private readonly List<string> _batchLog = new();
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xA1, 0x2A, 0x26));

    // Preview debounce + stale-render guard
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _previewRequest;

    // Guards _cutout/_windowImage/_autoTransform: the preview renders them on a
    // background thread while the UI thread disposes/replaces them on slider
    // ticks — an unguarded dispose mid-render is a native crash (no managed
    // exception, process just exits).
    private readonly object _photoLock = new();

    public MainWindow()
    {
        InitializeComponent();
        FitWindowToWorkArea();
        Closed += (_, _) => AppSettings.WindowBounds = (Left, Top, Width, Height);
        Title = $"IDForge v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        _svc = new CardService();
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); _ = UpdatePreviewAsync(); };

        CmbBlood.ItemsSource = Config.BloodGroups;
        CmbBlood.SelectedItem = "N/A";
        CmbPosition.ItemsSource = Config.Positions;
        CmbPosition.SelectedIndex = 0;
        DpPrintDate.SelectedDate = Validity.TodayInDhaka().ToDateTime(TimeOnly.MinValue);
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); SaveDraftNow(); };

        // Batch grid combo columns are DataGridTemplateColumns now (ItemsSource
        // bound in XAML); nothing to wire here.
        DgBatch.ItemsSource = _batchRows;
        InitShell();

        RefreshRecords();
        MaybeOfferResume();
        SchedulePreview();
    }

    // ---------------- batch session persistence (plan §7) ----------------

    internal CardService Svc => _svc;
    internal string OutputDir => AppPaths.OutputDir;
    private string? _batchOutputDir;

    private void EnsureBatchSession()
    {
        if (_batchSessionId == 0)
            _batchSessionId = _svc.Store.CreateBatchSession($"Batch {DateTime.Now:yyyy-MM-dd HH:mm}");
    }

    /// <summary>Persists a row's current state to BatchRows (called on every change).</summary>
    internal void PersistBatchRow(BatchRow row)
    {
        try
        {
            EnsureBatchSession();
            row.SessionId = _batchSessionId;
            row.RowId = _svc.Store.SaveBatchRow(row.ToEntry());
            UpdateBatchSummary();
        }
        catch (Exception ex)
        {
            TxtBatchStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>Test hook: skip the modal "resume batch?" prompt (headless tests).</summary>
    public static bool SkipResumePrompt;

    private void MaybeOfferResume()
    {
        if (SkipResumePrompt) return;
        var sess = _svc.Store.LatestOpenSession();
        if (sess == null) return;
        var entries = _svc.Store.BatchRows(sess.Id);
        if (entries.Count == 0) { _svc.Store.CompleteBatchSession(sess.Id); return; }
        int needReview = entries.Count(e => e.Status == "NeedsReview");
        var r = MessageBox.Show(this,
            $"Resume batch ({entries.Count} rows, {needReview} need review)?",
            "Unfinished batch", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _batchSessionId = sess.Id;
        foreach (var e in entries)
        {
            var row = BatchRow.FromEntry(e);
            HookRow(row);
            _batchRows.Add(row);
        }
        TxtBatchStatus.Text = $"Resumed batch: {_batchRows.Count} rows.";
        UpdateBatchSummary();
    }

    internal void RefreshRecordsPublic() => RefreshRecords();

    /// <summary>
    /// The single issue call used by batch review (plan §5). Returns the outcome;
    /// the caller updates row state. RequireNewUid blocks UIDs already in Records.
    /// </summary>
    internal async Task<IDForge.Core.Issuing.IssueOutcome> IssueBatchRowAsync(BatchRow row, string issueKey)
    {
        var auto = row.AutoTransform;
        WindowTransform? t = null;
        if (auto != null)
            t = new WindowTransform(
                auto.Scale * (float)row.Zoom,
                auto.OffsetX + (float)row.OffsetX,
                auto.OffsetY + (float)row.OffsetY);
        var req = new IDForge.Core.Issuing.IssueRequest(
            row.Uid, row.Name, row.Blood, row.Position, row.Contract,
            row.PrintDate, row.Cutout, t, issueKey,
            Detail: $"Batch row {row.RowId}", RequireNewUid: true,
            IncludePdf: AppSettings.IncludePdf);
        return await Task.Run(() => new IDForge.Core.Issuing.CardIssuer(_svc.Store, _batchOutputDir ?? OutputDir).Issue(req));
    }

    // ---------------- helpers ----------------

    private static WriteableBitmap ToWriteableBitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        wb.Freeze();
        return wb;
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    private void SchedulePreview()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e) { MarkDirty(); NewCardUiChanged(); }
    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender == TxtUid) Uid_TextChanged(sender, e);
        else { MarkDirty(); NewCardUiChanged(); }
    }
    private void Field_Changed(object sender, RoutedEventArgs e) { MarkDirty(); NewCardUiChanged(); }

    /// <summary>Side-effects of any New Card field change: preview, validity, chips, autosave.</summary>
    private void NewCardUiChanged()
    {
        if (!_shellReady) return;   // Checked/SelectionChanged fire during InitializeComponent
        SchedulePreview();
        UpdateValidityLine();
        UpdateFooterChips();
        ScheduleDraftSave();
    }

    private (string name, string blood, string position, string uid, string contract, DateOnly printDate) GetForm()
    {
        var name = TxtName.Text.Trim();
        var blood = CmbBlood.SelectedItem as string ?? "N/A";
        var position = CmbPosition.SelectedItem as string ?? "";
        var uid = Core.Uid.Normalize(TxtUid.Text) ?? "";
        var contract = RadContractPerm.IsChecked == true ? "Permanent" : "Contractual";
        var printDate = DpPrintDate.SelectedDate is DateTime dt
            ? DateOnly.FromDateTime(dt) : Validity.TodayInDhaka();
        return (name, blood, position, uid, contract, printDate);
    }

    /// <summary>Digits only — letters and symbols never reach the field.</summary>
    private void Uid_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(c => !char.IsDigit(c));

    /// <summary>
    /// Keeps the field digits-only (covers paste, which bypasses
    /// PreviewTextInput) and strips a pasted "UID" prefix — the prefix is
    /// added to the card for the operator.
    /// </summary>
    private void Uid_TextChanged(object sender, TextChangedEventArgs e)
    {
        var digits = new string(TxtUid.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > Core.Uid.MaxLength) digits = digits[..Core.Uid.MaxLength];
        if (digits != TxtUid.Text)
        {
            var caret = TxtUid.CaretIndex;
            TxtUid.Text = digits;
            TxtUid.CaretIndex = Math.Min(caret, digits.Length);
        }
        TxtUidCount.Text = $"{digits.Length} / {Core.Uid.MaxLength}";
        UpdateUidStatus();
        MarkDirty();
        NewCardUiChanged();
    }

    /// <summary>Rewrites the UID field to its normalized digit form on focus loss.</summary>
    private void Uid_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = Core.Uid.Normalize(TxtUid.Text);
        if (n != null && n != TxtUid.Text.Trim()) TxtUid.Text = n;
        UpdateUidStatus();
    }

    /// <summary>Catalogue wording: length error, existing-card warning, or "new employee".</summary>
    private void UpdateUidStatus()
    {
        var digits = new string(TxtUid.Text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) { ErrUid.Text = ""; ErrUid.Foreground = Brushes.Gray; return; }
        if (!Core.Uid.IsValid(digits))
        {
            ErrUid.Text = $"Employee ID must be {Core.Uid.MinLength} to {Core.Uid.MaxLength} digits. " +
                          $"You've entered {digits.Length}.";
            ErrUid.Foreground = FindResource("ErrorBrush") as Brush ?? Brushes.Red;
            return;
        }
        var existing = _svc?.Store.FindEmployeeByUid(digits);
        if (existing != null)
        {
            var issue = _svc.Store.LatestIssue(existing.Id);
            var when = issue?.IssuedAt.LocalDateTime.ToString("dd/MM/yyyy")
                       ?? existing.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy");
            ErrUid.Text = $"UID already exists — issued to {existing.Name} on {when}.";
            ErrUid.Foreground = FindResource("WarningBrush") as Brush ?? Brushes.DarkOrange;
        }
        else
        {
            ErrUid.Text = "New employee — no existing card with this ID";
            ErrUid.Foreground = FindResource("SuccessBrush") as Brush ?? Brushes.Green;
        }
    }

    private bool FormValid(out string error)
    {
        var f = GetForm();
        error = "";
        if (f.name.Length < 2) { error = "Name is required (min 2 characters)."; return false; }
        if (!Core.Uid.IsValid(f.uid)) { error = $"UID must be digits only ({Core.Uid.MinLength}-{Core.Uid.MaxLength} characters)."; return false; }
        if (!Config.Positions.Contains(f.position)) { error = "Select a position."; return false; }
        if (!Config.BloodGroups.Contains(f.blood)) { error = "Select a blood group."; return false; }
        // name fit check happens in render (throws)
        return true;
    }

    private async Task UpdatePreviewAsync()
    {
        var f = GetForm();
        if (f.name.Length < 2 || !Core.Uid.IsValid(f.uid))
        {
            ImgPreview.Source = null;
            TxtPreviewHint.Visibility = Visibility.Visible;
            return;
        }

        var req = ++_previewRequest;
        byte[]? pixels = null;
        int pw = 0, ph = 0;
        try
        {
            // Render + pixel copy off the UI thread; no PNG round-trip.
            // The lock keeps _windowImage alive for the whole render — the UI
            // thread blocks briefly in ComposeWindow/Dispose instead.
            (pixels, pw, ph) = await Task.Run(() =>
            {
                lock (_photoLock)
                {
                    using var card = CardService.RenderCard(f.name, f.blood, f.position,
                        f.uid, f.contract, f.printDate, _windowImage);
                    return (card.GetPixelSpan().ToArray(), card.Width, card.Height);
                }
            });
        }
        catch (ArgumentException ex)
        {
            if (req == _previewRequest) SetStatus(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(AppPaths.CrashLogPath, $"{DateTime.Now:O} preview: {ex}\n\n"); } catch { }
            if (req == _previewRequest) SetStatus($"Preview error: {ex.Message}");
            return;
        }

        if (req != _previewRequest || pixels == null) return; // stale render
        ImgPreview.Source = ToWriteableBitmap(pixels, pw, ph);
        TxtPreviewHint.Visibility = Visibility.Collapsed;
        ApplyPreviewZoom();
    }

    // ---------------- photo ----------------

    private void BrowsePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.jpg;*.jpeg;*.png",
            Title = "Select staff photo"
        };
        if (dlg.ShowDialog() != true) return;
        _photoPath = dlg.FileName;
        _photoAddedAt = DateTime.Now;
        UpdatePhotoMeta();
        ProcessPhotoAsync(dlg.FileName);
        ScheduleDraftSave();
    }

    private void UpdatePhotoMeta()
    {
        if (_photoPath == null) { TxtPhotoName.Text = "No photo chosen"; TxtPhotoMeta.Text = ""; return; }
        TxtPhotoName.Text = Path.GetFileName(_photoPath);
        try
        {
            var size = new FileInfo(_photoPath).Length;
            var sizeText = size > 1024 * 1024 ? $"{size / 1048576.0:0.0} MB" : $"{size / 1024.0:0.0} KB";
            TxtPhotoMeta.Text = _photoAddedAt is { } t ? $"{sizeText} · added {t:HH:mm}" : sizeText;
        }
        catch { TxtPhotoMeta.Text = ""; }
    }

    private void UpdatePhotoThumb()
    {
        SKBitmap? bmp;
        lock (_photoLock) bmp = _windowImage;
        if (bmp == null) { ImgPhotoThumb.Source = null; return; }
        try
        {
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 80);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            ImgPhotoThumb.Source = bi;
        }
        catch { ImgPhotoThumb.Source = null; }
    }

    private BgMode CurrentBgMode
    {
        get
        {
            if (RadBgModel.IsChecked == true) return BgMode.Model;
            if (RadBgKeep.IsChecked == true) return BgMode.Keep;
            return BgMode.PlainWhite;
        }
    }

    private string CurrentBgModeTag => CurrentBgMode switch
    {
        BgMode.Model => "Model", BgMode.Keep => "Keep", _ => "PlainWhite"
    };

    private void BgRadio_Changed(object sender, RoutedEventArgs e)
    {
        // Mode change reprocesses from the original file (cache makes repeat
        // modes cheap) and keeps the current nudge values. Checked also fires
        // for programmatic IsChecked sets — IsLoaded + _photoPath guard those.
        if (_restoringDraft || _photoPath == null || !IsLoaded) return;
        ProcessPhotoAsync(_photoPath, keepNudge: true);
    }

    private void SetBgModeFromTag(string tag)
    {
        RadBgPlain.IsChecked = tag == "PlainWhite";
        RadBgModel.IsChecked = tag == "Model";
        RadBgKeep.IsChecked = tag == "Keep";
    }

    private async void ProcessPhotoAsync(string path, bool keepNudge = false)
    {
        float keepZoom = _nudgeZoom, keepX = _nudgeX, keepY = _nudgeY;
        TxtPhotoStatus.Text = "Processing photo (background removal)...";
        BtnApprove.IsEnabled = false;
        _photoApproved = false;
        UpdateApproveButton();
        BtnSave.IsEnabled = false;
        PosPanel.IsEnabled = false;
        try
        {
            var mode = CurrentBgMode;
            var job = await Task.Run(() => _svc.ProcessPhoto(path, mode));
            lock (_photoLock)
            {
                _cutout?.Dispose(); _windowImage?.Dispose();
                _cutout = job.Cutout;
                _windowImage = job.WindowImage;
                _autoTransform = job.AutoTransform ?? WindowTransform.Identity;
            }
            if (keepNudge)
            {
                _nudgeZoom = keepZoom; _nudgeX = keepX; _nudgeY = keepY;
            }
            else
            {
                _nudgeZoom = 1f; _nudgeX = 0; _nudgeY = 0;
            }
            PosPanel.SetState(_nudgeZoom, _nudgeX, _nudgeY);
            var bgNote = job.BgMethod switch
            {
                "skipped-transparent" => "Background already transparent — removal skipped.",
                "skipped-manual" => "Background removal off — photo used as-is.",
                "plain-white" => "Plain-white background removed.",
                "fallback" => "Background removal unavailable — photo used as-is.",
                _ => null
            };
            TxtPhotoStatus.Text = string.Join(" ", new[] { bgNote, job.Warning ?? "Photo ready — check preview, then Approve." }
                .Where(s => s != null));
            PosPanel.IsEnabled = job.Cutout != null;
            BtnApprove.IsEnabled = job.Cutout != null;
            UpdatePhotoThumb();
            UpdateFooterChips();
            SchedulePreview();
            ScheduleDraftSave();
        }
        catch (Exception ex)
        {
            TxtPhotoStatus.Text = $"Photo failed: {ex.Message}";
        }
    }

    // ---------------- photo position (PhotoPositionPanel + preview keys) ----------------

    /// <summary>Rebuilds the window image from auto-transform + nudge values.</summary>
    private void RebuildPhotoWindow()
    {
        if (_cutout == null || _autoTransform == null) return;
        lock (_photoLock)
        {
            if (_cutout == null) return;
            var t = new WindowTransform(
                _autoTransform.Scale * _nudgeZoom,
                _autoTransform.OffsetX + _nudgeX,
                _autoTransform.OffsetY + _nudgeY);
            _windowImage?.Dispose();
            _windowImage = CardService.ComposeWindow(_cutout, t);
        }
    }

    /// <summary>Shared tail for every nudge/zoom/offset change.</summary>
    private void PhotoAdjusted()
    {
        PosPanel.SetState(_nudgeZoom, _nudgeX, _nudgeY);
        _photoApproved = false;
        UpdateApproveButton();
        BtnApprove.IsEnabled = _cutout != null;
        UpdateFooterChips();
        SchedulePreview();
        ScheduleDraftSave();
    }

    /// <summary>Nudge the photo by (dx, dy) px — arrows call with 1, Shift+arrows with 10.</summary>
    public void NudgePhoto(int dx, int dy)
    {
        _nudgeX = Math.Clamp(_nudgeX + dx, -320, 320);
        _nudgeY = Math.Clamp(_nudgeY + dy, -400, 400);
        RebuildPhotoWindow();
        PhotoAdjusted();
    }

    /// <summary>Absolute offset commit from the numeric boxes (px).</summary>
    public void SetPhotoOffset(double x, double y)
    {
        _nudgeX = Math.Clamp((float)x, -320, 320);
        _nudgeY = Math.Clamp((float)y, -400, 400);
        RebuildPhotoWindow();
        PhotoAdjusted();
    }

    /// <summary>Zoom factor commit (existing slider range 0.4–3.0).</summary>
    public void SetPhotoZoom(double factor)
    {
        _nudgeZoom = Math.Clamp((float)factor, 0.4f, 3.0f);
        RebuildPhotoWindow();
        PhotoAdjusted();
    }

    /// <summary>Current nudge transform (zoom factor, x px, y px).</summary>
    public void GetNudge(out double zoom, out double x, out double y)
    {
        zoom = _nudgeZoom; x = _nudgeX; y = _nudgeY;
    }

    /// <summary>Arrow-key step: 1 px, or 10 px with Shift.</summary>
    public static int PhotoNudgeStep(ModifierKeys m)
        => (m & ModifierKeys.Shift) != 0 ? 10 : 1;

    /// <summary>Ctrl+0 / Reset: back to the auto-fit transform.</summary>
    public void ResetPhotoPosition()
    {
        _nudgeZoom = 1f; _nudgeX = 0; _nudgeY = 0;
        lock (_photoLock)
        {
            if (_cutout != null)
            {
                _windowImage?.Dispose();
                _windowImage = CardService.ComposeWindow(_cutout, _autoTransform);
            }
        }
        PhotoAdjusted();
    }

    private void WirePhotoPanel()
    {
        PosPanel.NudgeRequested += (dx, dy) => NudgePhoto(dx, dy);
        PosPanel.OffsetCommitted += SetPhotoOffset;
        PosPanel.ZoomChanged += SetPhotoZoom;
        PosPanel.ResetRequested += ResetPhotoPosition;
        PosPanel.SetState(1, 0, 0);
    }

    private void UpdateApproveButton()
    {
        if (_photoApproved)
        {
            BtnApprove.Content = "✓ Photo approved";
            BtnApprove.Style = FindResource("SuccessButton") as Style;
        }
        else
        {
            BtnApprove.Content = "Approve photo";
            BtnApprove.Style = null;
        }
        ChkPhoto.Visibility = _photoApproved ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApprovePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_windowImage == null) { SetStatus("Process a photo first."); return; }
        _photoApproved = true;
        BtnSave.IsEnabled = true;
        UpdateApproveButton();
        UpdateFooterChips();
        SetStatus("Photo approved.");
    }

    // ---------------- New Card: validity, chips, output line ----------------

    /// <summary>"Card will be valid until dd/MM/yyyy · N months from print date".</summary>
    private void UpdateValidityLine()
    {
        if (DpPrintDate.SelectedDate is not DateTime dt)
        {
            TxtValidity.Text = "";
            return;
        }
        var printDate = DateOnly.FromDateTime(dt);
        var contract = RadContractPerm.IsChecked == true ? "Permanent" : "Contractual";
        var months = contract == "Permanent" ? Config.PermanentMonths : Config.ContractualMonths;
        var until = Validity.CalculateValidUntil(printDate, contract);
        TxtValidity.Text = $"Card will be valid until {until:dd/MM/yyyy} · {months} months from print date";
    }

    private void SetChip(Border chip, TextBlock text, bool on)
    {
        if (on)
        {
            chip.Background = FindResource("SuccessTintBrush") as Brush;
            chip.BorderBrush = FindResource("SuccessBrush") as Brush;
            text.Foreground = FindResource("SuccessBrush") as Brush;
        }
        else
        {
            chip.ClearValue(Border.BackgroundProperty);
            chip.BorderBrush = FindResource("BorderBrush") as Brush;
            text.Foreground = FindResource("MutedBrush") as Brush;
        }
    }

    private void UpdateFooterChips()
    {
        var f = GetForm();
        bool detailsDone = f.name.Length >= 2 && Core.Uid.IsValid(f.uid)
            && Config.Positions.Contains(f.position) && Config.BloodGroups.Contains(f.blood);
        bool uidNew = Core.Uid.IsValid(f.uid) && _svc.Store.FindEmployeeByUid(f.uid) == null;
        SetChip(ChipDetails, TxtChipDetails, detailsDone);
        SetChip(ChipUidNew, TxtChipUidNew, uidNew);
        SetChip(ChipPhoto, TxtChipPhoto, _photoApproved);
    }

    /// <summary>"Saves image + PDF" when PDF is on in Settings, else "Saves image · change in Settings".</summary>
    private void UpdateOutputLine()
    {
        TxtOutputPdf.Visibility = AppSettings.IncludePdf ? Visibility.Visible : Visibility.Collapsed;
        BtnOutputSettings.Visibility = AppSettings.IncludePdf ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- New Card: draft ----------------

    private void SaveDraftNow()
    {
        if (!_shellReady) return;
        var f = GetForm();
        NewCardDraft.Save(new NewCardDraft
        {
            Name = TxtName.Text.Trim(),
            Blood = f.blood,
            Position = f.position,
            Uid = new string(TxtUid.Text.Where(char.IsDigit).ToArray()),
            Contract = f.contract,
            PrintDate = DpPrintDate.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
            BgMode = CurrentBgModeTag,
            PhotoPath = _photoPath,
            Zoom = _nudgeZoom,
            OffsetX = _nudgeX,
            OffsetY = _nudgeY,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        });
        TxtDraftNote.Text = $"Draft saved automatically {DateTime.Now:HH:mm}";
    }

    private void SaveDraft_Click(object sender, RoutedEventArgs e) => SaveDraftNow();

    /// <summary>Restores the autosaved draft after restart (fields + photo + nudges).</summary>
    private void RestoreDraft()
    {
        var d = NewCardDraft.Load();
        if (d == null) return;
        _restoringDraft = true;
        try
        {
            TxtName.Text = d.Name;
            if (Config.BloodGroups.Contains(d.Blood)) CmbBlood.SelectedItem = d.Blood;
            if (Config.Positions.Contains(d.Position)) CmbPosition.SelectedItem = d.Position;
            TxtUid.Text = d.Uid;
            RadContractPerm.IsChecked = d.Contract != "Contractual";
            RadContractContract.IsChecked = d.Contract == "Contractual";
            if (DateOnly.TryParse(d.PrintDate, out var pd))
                DpPrintDate.SelectedDate = pd.ToDateTime(TimeOnly.MinValue);
            SetBgModeFromTag(d.BgMode);
            _nudgeZoom = (float)d.Zoom; _nudgeX = (float)d.OffsetX; _nudgeY = (float)d.OffsetY;
            PosPanel.SetState(_nudgeZoom, _nudgeX, _nudgeY);

            if (!string.IsNullOrWhiteSpace(d.PhotoPath) && File.Exists(d.PhotoPath))
            {
                _photoPath = d.PhotoPath;
                _photoAddedAt = null;
                UpdatePhotoMeta();
                ProcessPhotoAsync(d.PhotoPath, keepNudge: true);
            }
            else if (!string.IsNullOrWhiteSpace(d.PhotoPath))
            {
                TxtPhotoStatus.Text = "Draft photo file is missing — choose the photo again.";
            }
            TxtDraftNote.Text = $"Draft restored — saved {d.SavedAt}";
        }
        finally { _restoringDraft = false; }
    }

    /// <summary>Clear form: back to defaults and remove the saved draft.</summary>
    private void ClearForm_Click(object sender, RoutedEventArgs e)
    {
        _restoringDraft = true;
        try
        {
            TxtName.Clear(); TxtUid.Clear();
            CmbBlood.SelectedItem = "N/A";
            CmbPosition.SelectedIndex = 0;
            RadContractPerm.IsChecked = AppSettings.DefaultContract != "Contractual";
            RadContractContract.IsChecked = AppSettings.DefaultContract == "Contractual";
            DpPrintDate.SelectedDate = Validity.TodayInDhaka().ToDateTime(TimeOnly.MinValue);
            SetBgModeFromTag(AppSettings.DefaultBgMode);
            _photoPath = null; _photoAddedAt = null;
            TxtPhotoName.Text = "No photo chosen"; TxtPhotoMeta.Text = "";
            ImgPhotoThumb.Source = null;
            lock (_photoLock)
            {
                _cutout?.Dispose(); _windowImage?.Dispose();
                _cutout = _windowImage = null;
            }
            _photoApproved = false;
            UpdateApproveButton();
            BtnApprove.IsEnabled = false;
            PosPanel.IsEnabled = false;
            PosPanel.SetState(1, 0, 0);
            _nudgeZoom = 1f; _nudgeX = _nudgeY = 0;
            TxtPhotoStatus.Text = "";
            TxtStatus.Text = "";
            NewCardDraft.Clear();
            TxtDraftNote.Text = "";
            _dirty = false;
            UpdateTitle();
        }
        finally { _restoringDraft = false; }
        UpdateUidStatus();
        UpdateValidityLine();
        UpdateFooterChips();
        SchedulePreview();
    }

    // ---------------- New Card: preview zoom, arrows, drag-drop ----------------

    private void ZoomMode_Changed(object sender, RoutedEventArgs e)
    {
        _previewZoomMode = RadZoomFit.IsChecked == true ? 0 : RadZoom100.IsChecked == true ? 1 : 2;
        ApplyPreviewZoom();
    }

    private void ApplyPreviewZoom()
    {
        if (ImgPreview?.Source == null) return;   // ImgPreview still null during InitializeComponent
        switch (_previewZoomMode)
        {
            case 1: ImgPreview.Width = Config.CardWidth; ImgPreview.Height = Config.CardHeight; break;
            case 2: ImgPreview.Width = Config.CardWidth * 2; ImgPreview.Height = Config.CardHeight * 2; break;
            default:
                var w = PreviewArea.ActualWidth - 24;
                var h = PreviewArea.ActualHeight - 24;
                if (w <= 0 || h <= 0) { ImgPreview.Width = double.NaN; ImgPreview.Height = double.NaN; return; }
                var s = Math.Min(w / Config.CardWidth, h / Config.CardHeight);
                ImgPreview.Width = Config.CardWidth * s;
                ImgPreview.Height = Config.CardHeight * s;
                break;
        }
    }

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyPreviewZoom();

    /// <summary>Ctrl+wheel cycles Fit → 100% → 200% → Fit.</summary>
    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        _previewZoomMode = (_previewZoomMode + (e.Delta > 0 ? 1 : 2)) % 3;
        RadZoomFit.IsChecked = _previewZoomMode == 0;
        RadZoom100.IsChecked = _previewZoomMode == 1;
        RadZoom200.IsChecked = _previewZoomMode == 2;
        e.Handled = true;
    }

    /// <summary>Arrow keys move the photo 1 px; Shift+arrows 10 px.</summary>
    private void Preview_KeyDown(object sender, KeyEventArgs e)
    {
        var step = PhotoNudgeStep(Keyboard.Modifiers);
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0),
            Key.Up => (0, -step), Key.Down => (0, step),
            _ => (0, 0)
        };
        if (dx == 0 && dy == 0) return;
        NudgePhoto(dx, dy);
        e.Handled = true;
    }

    private void Preview_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Preview_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _photoPath = files[0];
            _photoAddedAt = DateTime.Now;
            UpdatePhotoMeta();
            ProcessPhotoAsync(files[0]);
            ScheduleDraftSave();
        }
    }

    // ---------------- shell: nav, menu, status bar, settings (Phase 2) ----------------

    private string _area = "newcard";
    private bool _dirty;
    private bool _navCollapsed;
    private bool _shellReady;

    /// <summary>Area the operator is currently looking at.</summary>
    public string CurrentArea => _area;

    private void InitShell()
    {
        // Settings page initial values
        SetConfirm.IsChecked = AppSettings.ConfirmBeforeIssue;
        SetPdf.IsChecked = AppSettings.IncludePdf;
        TxtSaveFolder.Text = AppSettings.SaveFolder;
        SetContract.ItemsSource = Config.ContractTypes;
        SetContract.SelectedItem = AppSettings.DefaultContract;
        SetPrintDate.SelectedIndex = 0;
        SetBg.SelectedIndex = AppSettings.DefaultBgMode switch
        {
            "Model" => 1, "Keep" => 2, _ => 0
        };
        // New Card: contract + background defaults from Settings
        RadContractPerm.IsChecked = AppSettings.DefaultContract != "Contractual";
        RadContractContract.IsChecked = AppSettings.DefaultContract == "Contractual";
        SetBgModeFromTag(AppSettings.DefaultBgMode);
        WirePhotoPanel();
        UpdateOutputLine();
        UpdateStatusBar();
        UpdateTitle();
        ShowArea("newcard");
        _shellReady = true;
        RestoreDraft();
        UpdateValidityLine();
        UpdateFooterChips();
        UpdateUidStatus();
    }

    /// <summary>Switches the visible page + nav highlight. State is kept per page.</summary>
    public void ShowArea(string area)
    {
        _area = area;
        PageNewCard.Visibility = area == "newcard" ? Visibility.Visible : Visibility.Collapsed;
        PageBatch.Visibility = area == "batch" ? Visibility.Visible : Visibility.Collapsed;
        PageRecords.Visibility = area == "records" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = area == "settings" ? Visibility.Visible : Visibility.Collapsed;
        NavNewCard.IsChecked = area == "newcard";
        NavBatch.IsChecked = area == "batch";
        NavRecords.IsChecked = area == "records";
        NavSettings.IsChecked = area == "settings";
        UpdateTitle();
        if (area == "records") RefreshRecords();
    }

    private void UpdateTitle()
        => Title = $"IDForge — {AreaLabel(_area)}" +
           (_area == "newcard" && _dirty ? " • unsaved changes" : "");

    private static string AreaLabel(string a) => a switch
    {
        "batch" => "Batch", "records" => "Records", "settings" => "Settings", _ => "New card"
    };

    private void MarkDirty()
    {
        if (!_shellReady) return;   // ctor/InitializeComponent field sets are not edits
        if (!_dirty) { _dirty = true; UpdateTitle(); }
    }

    private void ScheduleDraftSave()
    {
        if (!_shellReady || _restoringDraft) return;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private void UpdateStatusBar()
    {
        var count = _svc?.Store.AllEmployees().Count ?? 0;
        TxtStatusCount.Text = $"{count} record{(count == 1 ? "" : "s")}";
        TxtStatusBackup.Text = AppSettings.LastBackupAt is { } t
            ? $"Last backup {t:dd/MM/yyyy}" : "No backup yet";
        TxtLastBackup.Text = AppSettings.LastBackupAt is { } t2
            ? $"{t2:dd/MM/yyyy} at {t2:HH:mm}" : "Never";
        TxtStatusVersion.Text = $"IDForge v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · About (F1)";
        UpdateBatchBadge();
        var backupTip = AppSettings.LastBackupAt is { } bt
            ? $"Last backup {bt:dd/MM/yyyy} at {bt:HH:mm}\nClick to back up now (Ctrl+B)"
            : "Never backed up\nClick to back up now (Ctrl+B)";
        PnlBackupFull.ToolTip = backupTip;
        PnlBackupCompact.ToolTip = backupTip;
    }

    private void UpdateBatchBadge()
    {
        var n = _batchRows.Count;
        TxtNavBatchCount.Text = n.ToString();
        NavBatchBadge.Visibility = n > 0 && !_navCollapsed ? Visibility.Visible : Visibility.Collapsed;
        NavBatch.ToolTip = $"Batch — {n} row{(n == 1 ? "" : "s")} (Ctrl+2)";
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var collapsed = ActualWidth < 1100;
        if (collapsed == _navCollapsed) return;
        _navCollapsed = collapsed;
        NavCol.Width = new GridLength(collapsed ? 56 : 208);
        foreach (var name in new[] { "NavLblNewCard", "NavKeyNewCard", "NavLblBatch", "NavKeyBatch",
                     "NavLblRecords", "NavKeyRecords", "NavLblSettings", "NavKeySettings",
                     "LblIssueCards", "LblManage" })
        {
            if (FindName(name) is FrameworkElement fe) fe.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        // Collapsed: icon-only items, centered, with tighter padding.
        foreach (var (rb, pnl) in new (RadioButton, FrameworkElement)[]
                 { (NavNewCard, NavPnlNewCard), (NavBatch, NavPnlBatch),
                   (NavRecords, NavPnlRecords), (NavSettings, NavPnlSettings) })
        {
            pnl.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            rb.Padding = collapsed ? new Thickness(4, 8, 4, 8) : new Thickness(10, 7, 10, 7);
        }
        NavBatchBadge.Visibility = _batchRows.Count > 0 && !collapsed ? Visibility.Visible : Visibility.Collapsed;
        PnlBackupFull.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        PnlBackupCompact.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Area shortcuts + New Card editing shortcuts. Returns true when handled.</summary>
    public bool TryHandleShortcut(Key key, ModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl || modifiers.HasFlag(ModifierKeys.Alt) || modifiers.HasFlag(ModifierKeys.Windows))
            return false;
        var inNewCard = _area == "newcard";
        switch (key)
        {
            case Key.D1: ShowArea("newcard"); break;
            case Key.D2: ShowArea("batch"); break;
            case Key.D3: ShowArea("records"); break;
            case Key.OemComma: ShowArea("settings"); break;
            case Key.B when !shift: NavBackup_Click(this, null!); break;
            case Key.O when inNewCard && !shift: BrowsePhoto_Click(this, null!); break;
            case Key.D0 when inNewCard && !shift:
            case Key.NumPad0 when inNewCard && !shift: ResetPhotoPosition(); break;
            case Key.S when inNewCard && !shift: SaveDraftNow(); break;
            case Key.Enter when inNewCard && !shift: Save_Click(this, null!); break;
            case Key.A when inNewCard && shift: ApprovePhoto_Click(this, null!); break;
            default: return false;
        }
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void NavNewCard_Click(object sender, RoutedEventArgs e) => ShowArea("newcard");
    private void NavBatch_Click(object sender, RoutedEventArgs e) => ShowArea("batch");
    private void NavRecords_Click(object sender, RoutedEventArgs e) => ShowArea("records");
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowArea("settings");
    private void GoSettings_Click(object sender, RoutedEventArgs e) => ShowArea("settings");
    private void MenuToggleNav_Click(object sender, RoutedEventArgs e)
        => NavCol.Width = new GridLength(NavCol.Width.Value > 100 ? 56 : 208);
    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Single backup path used by the nav card, Tools menu, and Ctrl+B:
    /// silent backup + transient status-bar message.
    /// </summary>
    public string BackupNow()
    {
        var path = _svc.BackupDatabase();
        AppSettings.LastBackupAt = DateTime.Now;
        UpdateStatusBar();
        var count = _svc.Store.AllEmployees().Count;
        var msg = $"Backup saved — {count} record{(count == 1 ? "" : "s")}, {DateTime.Now:dd/MM/yyyy HH:mm}";
        ShowStatusMessage(msg);
        return msg;
    }

    private string BackupNowSafe()
    {
        try { return BackupNow(); }
        catch (Exception ex)
        {
            ShowStatusMessage($"Backup failed: {ex.Message}");
            return $"Backup failed: {ex.Message}";
        }
    }

    private DispatcherTimer? _statusMsgTimer;

    private void ShowStatusMessage(string msg)
    {
        TxtStatusMsg.Text = msg;
        PnlStatusMsg.Visibility = Visibility.Visible;
        _statusMsgTimer?.Stop();
        _statusMsgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusMsgTimer.Tick += (_, _) => { PnlStatusMsg.Visibility = Visibility.Collapsed; _statusMsgTimer!.Stop(); };
        _statusMsgTimer.Start();
    }

    public void NavBackup_Click(object sender, RoutedEventArgs e) => BackupNowSafe();

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_shellReady) return;   // events can fire during InitializeComponent
        if (SetConfirm.IsChecked != null) AppSettings.ConfirmBeforeIssue = SetConfirm.IsChecked == true;
        if (SetPdf.IsChecked != null) AppSettings.IncludePdf = SetPdf.IsChecked == true;
        if (SetContract.SelectedItem is string c) AppSettings.DefaultContract = c;
        if (SetBg.SelectedItem is ComboBoxItem bg && bg.Tag is string tag) AppSettings.DefaultBgMode = tag;
        UpdateOutputLine();
        TxtSettingsStatus.Text = "Setting saved";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { TxtSettingsStatus.Text = ""; t.Stop(); };
        t.Start();
    }

    private void BrowseSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where new cards are saved",
            FolderName = AppSettings.SaveFolder
        };
        if (dlg.ShowDialog(this) == true)
        {
            AppSettings.SaveFolder = dlg.FolderName;
            TxtSaveFolder.Text = dlg.FolderName;
        }
    }

    // ---------------- save / issue ----------------

    /// <summary>Default save folder: Documents\IDForge Cards (created on demand).</summary>
    internal static string DefaultCardDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(docs, "IDForge Cards");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Prompts for the PNG destination; null = user cancelled.</summary>
    private string? PickCardImagePath(string defaultName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save card image",
            Filter = "PNG image (*.png)|*.png",
            FileName = defaultName,
            InitialDirectory = DefaultCardDir()
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_photoApproved) { SetStatus("Approve the photo first."); return; }
        if (!FormValid(out var err)) { SetStatus(err); return; }
        var f = GetForm();

        var existing = _svc.Store.FindEmployeeByUid(f.uid);
        if (existing != null)
        {
            var r = MessageBox.Show(
                $"UID {f.uid} already exists ({existing.Name}).\n\nOpen existing record?",
                "Duplicate UID", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
            {
                _editingEmployee = existing;
                // Load into form
                TxtName.Text = existing.Name;
                CmbBlood.SelectedItem = existing.BloodGroup;
                CmbPosition.SelectedItem = existing.Position;
                RadContractPerm.IsChecked = existing.ContractType == "Permanent";
                RadContractContract.IsChecked = existing.ContractType != "Permanent";
                RefreshRecords();
                SetStatus("Existing record loaded. Adjust and Save to issue a new version.");
                return;
            }
            // No -> continue as re-issue on same employee below
        }

        var validUntil = Validity.CalculateValidUntil(f.printDate, f.contract);
        var isReissue = existing != null;
        if (!Dialogs.ConfirmIssue(this, f.name, f.uid, validUntil, isReissue)) return;

        var savePath = PickCardImagePath($"{Core.Uid.FileToken(f.uid)}.png");
        if (savePath == null) return;

        SKBitmap? cutout;
        WindowTransform? transform;
        lock (_photoLock)
        {
            cutout = _cutout;
            transform = _cutout != null && _autoTransform != null
                ? new WindowTransform(
                    _autoTransform.Scale * _nudgeZoom,
                    _autoTransform.OffsetX + _nudgeX,
                    _autoTransform.OffsetY + _nudgeY)
                : null;
        }
        var issueKey = $"single:{f.uid}:{DateTime.UtcNow.Ticks}";
        var req = new IDForge.Core.Issuing.IssueRequest(
            f.uid, f.name, f.blood, f.position, f.contract, f.printDate,
            cutout, transform, issueKey, RequireNewUid: false,
            IncludePdf: AppSettings.IncludePdf, OutputFile: savePath);

        BtnSave.IsEnabled = false;
        SetStatus("Issuing...");
        try
        {
            var outcome = await Task.Run(() =>
                new IDForge.Core.Issuing.CardIssuer(_svc.Store, OutputDir).Issue(req));
            if (!outcome.Success)
            {
                SetStatus($"Issue failed ({outcome.Step}): {outcome.Error}");
                Dialogs.ShowIssueFailed(this, outcome.Step ?? "issue", outcome.Error ?? "unknown");
                return;
            }
            var issue = outcome.Issue!;
            var savedFiles = outcome.PdfPath != null
                ? $"{outcome.PngPath}\n{outcome.PdfPath}" : outcome.PngPath;
            SetStatus($"Issued v{issue.Version} — valid until {issue.ValidUntil:MMMM yyyy}.\nSaved: {savedFiles}");
            Dialogs.ShowIssued(this, f.uid, issue.ValidUntil, outcome.PngPath, outcome.PdfPath);

            // reset form (same fields as before: name, UID, photo)
            TxtName.Clear(); TxtUid.Clear();
            _photoPath = null; _photoAddedAt = null;
            TxtPhotoName.Text = "No photo chosen"; TxtPhotoMeta.Text = "";
            ImgPhotoThumb.Source = null;
            lock (_photoLock)
            {
                _cutout?.Dispose(); _windowImage?.Dispose();
                _cutout = _windowImage = null;
            }
            _photoApproved = false;
            UpdateApproveButton();
            BtnApprove.IsEnabled = false;
            PosPanel.IsEnabled = false;
            PosPanel.SetState(1, 0, 0);
            _nudgeZoom = 1f; _nudgeX = _nudgeY = 0;
            _dirty = false;
            NewCardDraft.Clear();
            TxtDraftNote.Text = "";
            UpdateUidStatus();
            UpdateFooterChips();
            SchedulePreview();
            RefreshRecords();
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
        finally { BtnSave.IsEnabled = _photoApproved; }
    }

    // ---------------- records ----------------

    private sealed record RecordRow(Employee Emp, CardIssue? Issue)
    {
        public string Uid => Emp.Uid;
        public string Name => Emp.Name;
        public string Position => Emp.Position;
        public string ContractType => Emp.ContractType;
        public string ValidUntil => Issue?.ValidUntil.ToString("yyyy-MM-dd") ?? "-";
        public string LastIssued => Issue?.IssuedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private void RefreshRecords()
    {
        var rows = _svc.Store.AllEmployees()
            .Select(e => new RecordRow(e, _svc.Store.LatestIssue(e.Id)))
            .ToList();
        LstRecords.ItemsSource = rows;
    }

    private void Search_Click(object sender, RoutedEventArgs e) => ApplySearch();
    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplySearch();
    }

    private void ApplySearch()
    {
        var term = TxtSearch.Text.Trim();
        var emps = string.IsNullOrEmpty(term)
            ? _svc.Store.AllEmployees()
            : _svc.Store.SearchEmployees(term);
        LstRecords.ItemsSource = emps
            .Select(e => new RecordRow(e, _svc.Store.LatestIssue(e.Id)))
            .ToList();
    }

    private void RefreshRecords_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Clear();
        RefreshRecords();
    }

    private void Record_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LstRecords.SelectedItem is not RecordRow row) { BtnReprint.IsEnabled = false; BtnReissue.IsEnabled = false; BtnExportRec.IsEnabled = false; BtnEditRec.IsEnabled = false; BtnDeleteRec.IsEnabled = false; return; }
        _selectedEmployee = row.Emp;
        _selectedIssue = row.Issue;
        BtnReprint.IsEnabled = row.Issue != null;
        BtnReissue.IsEnabled = true;
        BtnExportRec.IsEnabled = row.Issue != null;
        BtnEditRec.IsEnabled = true;
        BtnDeleteRec.IsEnabled = true;
        TxtRecordInfo.Text = $"{row.Emp.Name} — {row.Emp.Position}, {row.Emp.ContractType}";
    }

    private void Reprint_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEmployee == null || _selectedIssue == null) return;
        var path = PickCardImagePath(
            $"{Core.Uid.FileToken(_selectedEmployee.Uid)}_v{_selectedIssue.Version}.png");
        if (path == null) return;
        try
        {
            var (pdf, png) = _svc.ExportCard(_selectedEmployee, _selectedIssue, pngPath: path);
            MessageBox.Show($"Reprinted v{_selectedIssue.Version}:\n{pdf}\n{png}", "Reprint");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "?";
        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = "IDForge", FontSize = 20, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {version}", Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Created by {AppInfo.CreatorName}", Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new Button
        {
            Content = "Close", IsCancel = true, Width = 90,
            Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
        });
        var dlg = new Window
        {
            Title = "About IDForge",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    /// <summary>
    /// Sizes the window to the primary work area (taskbar excluded) and restores
    /// the saved bounds only when they still land on a connected screen.
    /// </summary>
    private void FitWindowToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        MaxWidth = wa.Width;
        MaxHeight = wa.Height;
        double maxW = wa.Width * 0.95, maxH = wa.Height * 0.95;
        Width = Math.Max(MinWidth, Math.Min(1080, maxW));
        Height = Math.Max(MinHeight, Math.Min(750, maxH));

        if (AppSettings.WindowBounds is { } bb)
        {
            double w = Math.Min(Math.Max(bb.Width, MinWidth), maxW);
            double h = Math.Min(Math.Max(bb.Height, MinHeight), maxH);
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var inter = Rect.Intersect(new Rect(bb.Left, bb.Top, w, h), vs);
            // Restore only when a usable chunk (>=100x40 px, enough for the title
            // bar) is still on a connected screen; otherwise fall back to center.
            if (!inter.IsEmpty && inter.Width >= 100 && inter.Height >= 40)
            {
                Left = bb.Left; Top = bb.Top; Width = w; Height = h;
                WindowStartupLocation = WindowStartupLocation.Manual;
                return;
            }
        }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private async void Reissue_Click(object sender, RoutedEventArgs e)
    {
        var emp = _selectedEmployee;
        if (emp == null) return;
        var today = Validity.TodayInDhaka();
        var lastIssue = _svc.Store.LatestIssue(emp.Id);

        // Date is a plain TextBox so typed text is always what we parse —
        // a DatePicker can hold uncommitted text and report SelectedDate=null.
        var txtDate = new TextBox
        {
            Text = today.ToString("d/M/yyyy"),
            Width = 140,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Format: dd/MM/yyyy"
        };
        var errText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0x2A, 0x26)),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var btnReissue = new Button { Content = "Re-issue", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"New print date for {emp.Name} (UID {emp.Uid}):",
            Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap
        });
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal };
        dateRow.Children.Add(txtDate);
        dateRow.Children.Add(new TextBlock
        {
            Text = "  dd/MM/yyyy", VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray, FontSize = 11
        });
        panel.Children.Add(dateRow);
        panel.Children.Add(errText);
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true });
        btnRow.Children.Add(btnReissue);
        panel.Children.Add(btnRow);
        var dlg = new Window
        {
            Title = "Re-issue card",
            Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        dlg.Owner = this;

        btnReissue.Click += (_, _) =>
        {
            if (!Core.DateInput.TryParse(txtDate.Text, out var newPrint))
            {
                errText.Text = "Enter a valid date as dd/MM/yyyy (e.g. 1/7/2027).";
                return;   // keep dialog open
            }
            var guard = Core.DateInput.ValidateReissue(newPrint, lastIssue?.PrintDate);
            if (guard != null) { errText.Text = guard; return; }
            dlg.DialogResult = true;
        };
        txtDate.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { btnReissue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); ke.Handled = true; }
        };

        if (dlg.ShowDialog() != true) return;
        Core.DateInput.TryParse(txtDate.Text, out var printDate);   // validated above
        var validUntil = Validity.CalculateValidUntil(printDate, emp.ContractType);

        // Re-issue always confirms — name, UID, new print date, new valid-until.
        var ok = MessageBox.Show(this,
            $"Re-issue card for {emp.Name}, UID {emp.Uid}?\n" +
            $"New print date: {printDate:dd/MM/yyyy}\nValid until: {validUntil:MMMM yyyy}",
            "Confirm re-issue", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        var nextVer = _svc.Store.NextIssueVersion(emp.Id);
        var path = PickCardImagePath(
            $"{Core.Uid.FileToken(emp.Uid)}_v{nextVer}.png");
        if (path == null) return;

        // Same issue transaction as Approve & Issue: render -> verify -> one DB
        // transaction (employee upsert + new CardIssue version + audit).
        var cutout = CardService.LoadCachedPhoto(emp.PhotoPath);
        if (cutout == null)
        {
            MessageBox.Show("No cached photo found for this record — cannot re-issue.",
                "Re-issue", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var req = new IDForge.Core.Issuing.IssueRequest(
            emp.Uid, emp.Name, emp.BloodGroup, emp.Position, emp.ContractType,
            printDate, cutout, null,
            IssueKey: $"reissue:{emp.Id}:{DateTime.UtcNow.Ticks}",
            Detail: $"Re-issue of employee {emp.Id}",
            RequireNewUid: false,
            IncludePdf: AppSettings.IncludePdf,
            OutputFile: path);
        try
        {
            var outcome = await Task.Run(() =>
                new IDForge.Core.Issuing.CardIssuer(_svc.Store, OutputDir).Issue(req));
            if (!outcome.Success)
            {
                App.LogCrash(new InvalidOperationException($"Re-issue failed ({outcome.Step}): {outcome.Error}"), notify: false);
                MessageBox.Show($"Re-issue failed ({outcome.Step}): {outcome.Error}\n\nNothing was saved.",
                    "Re-issue", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var issue = outcome.Issue!;
            var files = outcome.PdfPath != null
                ? $"{outcome.PngPath}\n{outcome.PdfPath}" : outcome.PngPath;
            MessageBox.Show($"Re-issued v{issue.Version}, valid until {issue.ValidUntil:MMMM yyyy}.\n{files}",
                "Re-issue", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshRecords();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex, notify: false);
            MessageBox.Show($"Re-issue failed: {ex.Message}", "Re-issue",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { cutout.Dispose(); }
    }

    private void ExportRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEmployee == null || _selectedIssue == null) return;
        var path = PickCardImagePath(
            $"{Core.Uid.FileToken(_selectedEmployee.Uid)}_v{_selectedIssue.Version}.png");
        if (path == null) return;
        try
        {
            var (pdf, png) = _svc.ExportCard(_selectedEmployee, _selectedIssue, pngPath: path);
            MessageBox.Show($"Exported:\n{pdf}\n{png}", "Export");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error"); }
    }

    // ---------------- records management ----------------

    private void EditRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEmployee == null) return;
        var emp = _selectedEmployee;
        var issue = _selectedIssue ?? _svc.Store.LatestIssue(emp.Id);
        var dlg = BuildRecordEditDialog(emp, issue);
        if (dlg.ShowDialog() != true) return;
        var p = (RecordEditDialogState)dlg.Tag!;

        var newUid = Core.Uid.Normalize(p.Uid) ?? p.Uid.Trim();
        if (!Core.Uid.IsValid(newUid))
        { MessageBox.Show($"UID must be digits only ({Core.Uid.MinLength}-{Core.Uid.MaxLength} characters).", "Invalid UID"); return; }
        bool uidChanged = !string.Equals(newUid, emp.Uid, StringComparison.Ordinal);
        if (uidChanged)
        {
            if (MessageBox.Show($"Change UID from {emp.Uid} to {newUid}?", "Change UID",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var dup = _svc.Store.FindEmployeeByUid(newUid);
            if (dup != null && dup.Id != emp.Id)
            { MessageBox.Show($"UID {newUid} already exists ({dup.Name}).", "Duplicate UID"); return; }
            emp.Uid = newUid;
        }
        try
        {
            var newIssue = _svc.EditRecord(emp, p.Name.Trim(), p.Blood, p.Position,
                p.Contract, p.PrintDate, uidChanged);
            SetStatus(newIssue != null
                ? $"Saved. New card version v{newIssue.Version}, valid until {newIssue.ValidUntil:MMMM yyyy}."
                : "Saved.");
            RefreshRecords();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed"); }
    }

    private sealed class RecordEditDialogState
    {
        public string Uid = ""; public string Name = ""; public string Blood = "N/A";
        public string Position = ""; public string Contract = "Permanent"; public DateOnly PrintDate;
    }

    private Window BuildRecordEditDialog(Employee emp, CardIssue? issue)
    {
        var state = new RecordEditDialogState
        {
            Uid = emp.Uid, Name = emp.Name, Blood = emp.BloodGroup,
            Position = emp.Position, Contract = emp.ContractType,
            PrintDate = issue?.PrintDate ?? Validity.TodayInDhaka()
        };
        var txtUid = new TextBox { Text = state.Uid, Width = 200 };
        var txtName = new TextBox { Text = state.Name, Width = 200 };
        var cmbBlood = new ComboBox { Width = 200, ItemsSource = Config.BloodGroups, SelectedItem = state.Blood };
        var cmbPos = new ComboBox { Width = 200, ItemsSource = Config.Positions, SelectedItem = state.Position };
        var cmbCt = new ComboBox { Width = 200, ItemsSource = Config.ContractTypes, SelectedItem = state.Contract };
        var dp = new DatePicker { SelectedDate = state.PrintDate.ToDateTime(TimeOnly.MinValue), Width = 200 };
        var validLabel = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Foreground = AccentBrush };

        void RefreshValid()
        {
            var ct = cmbCt.SelectedItem as string ?? "Permanent";
            var pd = dp.SelectedDate is DateTime d ? DateOnly.FromDateTime(d) : state.PrintDate;
            var vu = Validity.CalculateValidUntil(pd, ct);
            validLabel.Text = $"Valid until: {vu:MMMM yyyy}";
        }
        cmbCt.SelectionChanged += (_, _) => RefreshValid();
        dp.SelectedDateChanged += (_, _) => RefreshValid();
        RefreshValid();

        var dlg = new Window
        {
            Title = "Edit record", Width = 380, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = "UID" }, txtUid,
                    new TextBlock { Text = "Full name", Margin = new Thickness(0,8,0,0) }, txtName,
                    new TextBlock { Text = "Blood group", Margin = new Thickness(0,8,0,0) }, cmbBlood,
                    new TextBlock { Text = "Position", Margin = new Thickness(0,8,0,0) }, cmbPos,
                    new TextBlock { Text = "Contract type", Margin = new Thickness(0,8,0,0) }, cmbCt,
                    new TextBlock { Text = "Print date", Margin = new Thickness(0,8,0,0) }, dp,
                    validLabel,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0),
                        Children =
                        {
                            new Button { Content = "Cancel", Margin = new Thickness(0,0,8,0), IsCancel = true },
                            new Button { Content = "Save", IsDefault = true }
                        } }
                }
            }
        };
        var panel = (StackPanel)dlg.Content;
        var saveBtn = (Button)((StackPanel)panel.Children[13]).Children[1];
        saveBtn.Click += (_, _) =>
        {
            state.Uid = txtUid.Text; state.Name = txtName.Text;
            state.Blood = cmbBlood.SelectedItem as string ?? "N/A";
            state.Position = cmbPos.SelectedItem as string ?? "";
            state.Contract = cmbCt.SelectedItem as string ?? "Permanent";
            state.PrintDate = dp.SelectedDate is DateTime d ? DateOnly.FromDateTime(d) : state.PrintDate;
            dlg.DialogResult = true;
        };
        dlg.Tag = state;
        return dlg;
    }

    private void DeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEmployee == null) return;
        var emp = _selectedEmployee;
        var r = MessageBox.Show($"Delete record for {emp.Name} ({emp.Uid})?\nIt will be hidden but kept for audit and can be restored from 'Deleted...'.",
            "Delete record", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _svc.Store.SoftDeleteEmployee(emp.Id);
        _svc.Store.LogAudit(emp.Id, "delete", $"Soft-deleted {emp.Name} ({emp.Uid})");
        SetStatus($"Deleted {emp.Name}. Restore via Records → Deleted... .");
        _selectedEmployee = null;
        RefreshRecords();
    }

    private void DeletedRecords_Click(object sender, RoutedEventArgs e)
    {
        var deleted = _svc.Store.DeletedEmployees();
        if (deleted.Count == 0) { MessageBox.Show("No deleted records."); return; }
        var list = new StackPanel { Margin = new Thickness(10) };
        foreach (var emp in deleted)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = $"{emp.Name} ({emp.Uid})", Width = 280, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Content = "Restore" };
            var id = emp.Id;
            btn.Click += (_, _) =>
            {
                _svc.Store.RestoreEmployee(id);
                _svc.Store.LogAudit(id, "restore", $"Restored {emp.Name} ({emp.Uid})");
                MessageBox.Show($"Restored {emp.Name}.");
            };
            row.Children.Add(btn);
            list.Children.Add(row);
        }
        var dlg = new Window
        {
            Title = "Deleted records", Width = 420, Height = Math.Min(120 + deleted.Count * 36, 500),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = list }
        };
        dlg.ShowDialog();
        RefreshRecords();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => ExportRecords(false);
    private void ExportJson_Click(object sender, RoutedEventArgs e) => ExportRecords(true);

    private void ExportRecords(bool json)
    {
        var dlg = new SaveFileDialog
        {
            Filter = json ? "JSON|*.json" : "CSV|*.csv",
            FileName = $"idcards_{DateTime.Now:yyyyMMdd_HHmm}.{(json ? "json" : "csv")}"
        };
        if (dlg.ShowDialog() != true) return;
        var path = _svc.ExportRecords(dlg.FileName, json);
        SetStatus($"Exported {path}");
    }

    private void BackupDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _svc.BackupDatabase();
            AppSettings.LastBackupAt = DateTime.Now;
            UpdateStatusBar();
            MessageBox.Show($"Database backed up to:\n{path}", "Backup");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Backup failed"); }
    }

    private void RestoreDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SQLite DB|*.db|All files|*.*", Title = "Choose backup to restore" };
        if (dlg.ShowDialog() != true) return;
        var r = MessageBox.Show(
            $"Restore database from:\n{dlg.FileName}\n\nThe CURRENT database will be backed up first, then replaced. Continue?",
            "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            var safety = _svc.RestoreDatabase(dlg.FileName);
            MessageBox.Show($"Restored.\nSafety backup of previous DB: {safety}", "Restore");
            RefreshRecords();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Restore failed"); }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var count = _svc.Store.AllEmployees().Count + _svc.Store.DeletedEmployees().Count;
        var dlg = new Window
        {
            Title = "Clear ALL records", Width = 420, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = $"This permanently deletes ALL {count} employee record(s), card issues, and batch data. A backup will be created first. This cannot be undone.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Type DELETE ALL to confirm:", Margin = new Thickness(0,10,0,4) },
                    new TextBox { Width = 200 },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0),
                        Children =
                        {
                            new Button { Content = "Cancel", Margin = new Thickness(0,0,8,0), IsCancel = true },
                            new Button { Content = "Delete Everything", IsDefault = true, IsEnabled = false, Foreground = AccentBrush }
                        } }
                }
            }
        };
        var txt = (TextBox)((StackPanel)dlg.Content).Children[2];
        var delBtn = (Button)((StackPanel)((StackPanel)dlg.Content).Children[3]).Children[1];
        txt.TextChanged += (_, _) => delBtn.IsEnabled = txt.Text == "DELETE ALL";
        delBtn.Click += (_, _) => dlg.DialogResult = true;
        if (dlg.ShowDialog() != true) return;
        try
        {
            var backup = _svc.BackupDatabase();
            _svc.Store.ClearAll();
            _svc.Store.LogAudit(null, "clear-all", $"Deleted all records by {Environment.UserName}; backup: {backup}");
            MessageBox.Show($"All records deleted.\nBackup saved to:\n{backup}", "Done");
            _selectedEmployee = null; _selectedIssue = null;
            RefreshRecords();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Clear failed"); }
    }

    // ---------------- batch ----------------
    // Stages (plan §2): import -> process photos -> review -> issue (per row).
    // Nothing is approved or issued without an operator action in ReviewWindow.

    // ---- grid row ops ----

    private void BatchAddRow_Click(object sender, RoutedEventArgs e)
    {
        var row = new BatchRow();
        HookRow(row);
        _batchRows.Add(row);
        PersistBatchRow(row);
    }

    private void BatchDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (DgBatch.SelectedItem is not BatchRow row) return;
        if (row.RowId != 0)
        {
            try { _svc.Store.DeleteBatchRow(row.RowId); } catch { }
        }
        row.Cutout?.Dispose(); row.WindowImage?.Dispose();
        _batchRows.Remove(row);
    }

    private void BatchDupRow_Click(object sender, RoutedEventArgs e)
    {
        if (DgBatch.SelectedItem is not BatchRow row) return;
        var idx = _batchRows.IndexOf(row);
        var copy = new BatchRow
        {
            Uid = row.Uid, Name = row.Name, Blood = row.Blood, Position = row.Position,
            Contract = row.Contract, PrintDateText = row.PrintDateText, PhotoPath = row.PhotoPath
        };
        _batchRows.Insert(idx + 1, copy);
        PersistBatchRow(copy);
    }

    private void BatchBrowse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BatchRow row) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png", Title = "Select staff photo" };
        if (dlg.ShowDialog() == true)
        {
            row.PhotoPath = dlg.FileName;
            row.Status = "Draft"; row.Error = "";
            PersistBatchRow(row);
        }
    }

    /// <summary>Parses pasted/imported text into rows. TSV or CSV, optional header.</summary>
    private static List<BatchRow> ParseRows(string text, string? baseDir = null)
    {
        var data = BatchValidator.Parse(text);
        if (baseDir != null) BatchValidator.ResolvePaths(data, baseDir);
        return data.Select(d => new BatchRow
        {
            Uid = Core.Uid.Normalize(d.Uid) ?? d.Uid.Trim(), Name = d.Name, Blood = d.Blood, Position = d.Position,
            Contract = d.Contract,
            PrintDateText = string.IsNullOrWhiteSpace(d.PrintDateText)
                ? Validity.TodayInDhaka().ToString("yyyy-MM-dd") : d.PrintDateText,
            PhotoPath = d.PhotoPath
        }).ToList();
    }

    private void BatchPaste_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) { MessageBox.Show("Clipboard is empty."); return; }
        var rows = ParseRows(text);
        foreach (var r in rows) { HookRow(r); _batchRows.Add(r); PersistBatchRow(r); }
        TxtBatchStatus.Text = $"Pasted {rows.Count} row(s).";
    }

    private void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV files|*.csv;*.txt|All files|*.*", Title = "Import batch CSV" };
        if (dlg.ShowDialog() != true) return;
        var rows = ParseRows(File.ReadAllText(dlg.FileName), Path.GetDirectoryName(dlg.FileName));
        foreach (var r in rows) { HookRow(r); _batchRows.Add(r); PersistBatchRow(r); }
        TxtBatchStatus.Text = $"Imported {rows.Count} row(s) from {Path.GetFileName(dlg.FileName)}.";
    }

    private void BatchExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "batch_rows.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("UID,Name,BloodGroup,Position,ContractType,PrintDate,PhotoPath,Status\n");
        foreach (var r in _batchRows)
            sb.AppendLine(string.Join(",", new[] { r.Uid, r.Name, r.Blood, r.Position, r.Contract, r.PrintDateText, r.PhotoPath ?? "", r.Status }
                .Select(s => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s)));
        File.WriteAllText(dlg.FileName, sb.ToString());
        TxtBatchStatus.Text = $"Exported {_batchRows.Count} row(s) to {dlg.FileName}.";
    }

    private void DgBatch_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        try
        {
            // Normalize the UID column to digits as soon as the cell loses focus.
            if (e.Column.Header as string != "UID" || e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row.Item is not BatchRow row) return;
            var tb = e.EditingElement as TextBox;
            var n = Core.Uid.Normalize(tb?.Text ?? row.Uid);
            if (n != null) row.Uid = n;
            // Refresh must run after the cell's edit transaction commits —
            // calling it here throws "Refresh is not allowed during an
            // AddNew or EditItem transaction" (this fired on Review clicks too,
            // since moving focus commits the edit first).
            Dispatcher.BeginInvoke(() =>
            {
                try { DgBatch.Items.Refresh(); }
                catch (Exception ex) { App.LogCrash(ex, notify: false); }
            });
        }
        catch (Exception ex) { App.LogCrash(ex, notify: false); }
    }

    // ---- review ----

    private void BatchReview_Click(object sender, RoutedEventArgs e)
        => OpenReview(DgBatch.SelectedItem as BatchRow);

    private void DgBatch_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenReview(DgBatch.SelectedItem as BatchRow);

    private void OpenReview(BatchRow? startAt)
    {
        try
        {
            if (_batchRows.Count == 0) { MessageBox.Show("Add rows first."); return; }
            if (_batchOutputDir == null)
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "Choose folder for issued card files",
                    InitialDirectory = DefaultCardDir()
                };
                if (dlg.ShowDialog(this) != true) return;
                _batchOutputDir = dlg.FolderName;
            }
            var win = new ReviewWindow(this, _batchRows, startAt);
            win.Owner = this;
            win.ShowDialog();
            DgBatch.Items.Refresh();
            UpdateBatchSummary();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex, notify: false);
            MessageBox.Show($"Could not open review: {ex.Message}", "Review",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateBatchSummary()
    {
        int issued = _batchRows.Count(r => r.Status == "Issued");
        int review = _batchRows.Count(r => r.Status is "NeedsReview" or "Draft" or "NeedsPhoto");
        int failed = _batchRows.Count(r => r.Status == "Failed");
        int skipped = _batchRows.Count(r => r.Status == "Skipped");
        TxtBatchStatus.Text = $"Ready to review: {review}, Issued: {issued}, Failed: {failed}" +
            (skipped > 0 ? $", Skipped: {skipped}" : "");
        // Auto-complete the session when every row reached a terminal state.
        if (_batchSessionId != 0 && _batchRows.Count > 0 &&
            _batchRows.All(r => r.Status is "Issued" or "Skipped"))
        {
            try { _svc.Store.CompleteBatchSession(_batchSessionId); _batchSessionId = 0; } catch { }
        }
        UpdateBatchBadge();
    }

    // ---- process photos stage (no approval, no issuing) ----

    /// <summary>
    /// Persists grid edits (dropdowns, text cells) immediately and re-validates
    /// the row so the Status column reflects the change. ComboBoxes in the grid
    /// update the row in-memory via TwoWay binding; this hook saves them.
    /// </summary>
    private void HookRow(BatchRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not ("Uid" or "Name" or "Blood" or "Position"
                or "Contract" or "PrintDateText" or "PhotoPath")) return;
            if (row.RowId == 0) return;   // not yet persisted — PersistBatchRow assigns RowId
            if (row.Status is "Draft" or "Failed" or "NeedsPhoto")
            {
                var err = ValidateBatchRow(row, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                row.Status = err == null ? "Draft" : "Failed";
                row.Error = err ?? "";
            }
            PersistBatchRow(row);
        };
    }

    private string? ValidateBatchRow(BatchRow row, HashSet<string> seenUids)
        => BatchValidator.Validate(
            new BatchRowData(row.Uid, row.Name, row.Blood, row.Position, row.Contract,
                row.PrintDateText, row.PhotoPath),
            seenUids, uid => _svc.Store.FindEmployeeByUid(uid) != null);

    private async void StartBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_batchRows.Count == 0) { MessageBox.Show("Add rows first."); return; }
        // validate everything up front; invalid rows are marked Failed w/ reason
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toProcess = new List<BatchRow>();
        foreach (var row in _batchRows)
        {
            if (row.Status is "Issued" or "Skipped" or "NeedsReview" or "Processing") continue;
            var err = ValidateBatchRow(row, seen);
            if (err == null) { row.Status = "Draft"; toProcess.Add(row); }
            else { row.Status = "Failed"; row.Error = err; }
            PersistBatchRow(row);
        }
        DgBatch.Items.Refresh();
        var withPhoto = toProcess.Where(r => !string.IsNullOrWhiteSpace(r.PhotoPath)).ToList();
        if (withPhoto.Count == 0) { TxtBatchStatus.Text = "No rows with photos to process."; UpdateBatchSummary(); return; }

        _batchLog.Clear();
        _batchLog.Add($"Photo processing started {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {withPhoto.Count} rows");
        _batchCts = new CancellationTokenSource();
        BtnStartBatch.IsEnabled = false;
        BtnBatchCancel.IsEnabled = true;
        BatchProgress.Visibility = Visibility.Visible;
        TxtBatchStatus.Text = $"Processing photos for {withPhoto.Count} row(s)...";
        try
        {
            await ProcessPhotosAsync(withPhoto, _batchCts.Token);
            _batchLog.Add($"Processing finished {DateTime.Now:HH:mm:ss}");
            TxtBatchStatus.Text = "Photos processed — open Review to check each card.";
        }
        catch (OperationCanceledException)
        {
            _batchLog.Add($"Processing cancelled {DateTime.Now:HH:mm:ss}");
            TxtBatchStatus.Text = "Processing cancelled.";
        }
        finally
        {
            BtnStartBatch.IsEnabled = true;
            BtnBatchCancel.IsEnabled = false;
            BatchProgress.Visibility = Visibility.Collapsed;
            WriteBatchLog();
            UpdateBatchSummary();
        }
    }

    private async Task ProcessPhotosAsync(List<BatchRow> rows, CancellationToken ct)
    {
        int done = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            row.Status = "Processing";
            DgBatch.Items.Refresh();
            try
            {
                var mode = row.BgMode switch { "Model" => BgMode.Model, "Keep" => BgMode.Keep, _ => BgMode.PlainWhite };
                var job = await Task.Run(() => _svc.ProcessPhoto(row.PhotoPath!, mode), ct);
                if (job.Cutout == null)
                {
                    row.Status = "Failed";
                    row.Error = job.Warning ?? "photo error";
                }
                else
                {
                    row.Cutout?.Dispose(); row.WindowImage?.Dispose();
                    row.Cutout = job.Cutout;
                    row.AutoTransform = job.AutoTransform;
                    row.BgMethod = job.BgMethod;
                    row.Warning = job.Warning;
                    if (job.AutoTransform != null)
                    {
                        row.AutoScale = job.AutoTransform.Scale;
                        row.AutoOffsetX = job.AutoTransform.OffsetX;
                        row.AutoOffsetY = job.AutoTransform.OffsetY;
                    }
                    row.Zoom = 1; row.OffsetX = 0; row.OffsetY = 0;
                    row.CutoutPath = _svc.CachePhoto(row.Cutout, $"B{row.RowId}");
                    row.WindowImage = job.WindowImage;
                    row.Status = "NeedsReview";
                    row.Error = "";
                }
                _batchLog.Add($"{DateTime.Now:HH:mm:ss} {row.Uid}: {row.Status} {row.Error}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                row.Status = "Failed"; row.Error = ex.Message;
                _batchLog.Add($"{DateTime.Now:HH:mm:ss} {row.Uid}: Error {ex.Message}");
            }
            PersistBatchRow(row);
            done++;
            BatchProgress.Value = done * 100.0 / rows.Count;
            DgBatch.Items.Refresh();
        }
    }

    private void BatchCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

    private void BatchOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = OutputDir;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = dir, UseShellExecute = true });
    }

    private void WriteBatchLog()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir,
                $"batch_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllLines(path, _batchLog);
            TxtBatchLog.Text = $"Log: {path}";
        }
        catch { /* logging is best-effort */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var r in _batchRows) { r.Cutout?.Dispose(); r.WindowImage?.Dispose(); }
        _cutout?.Dispose(); _windowImage?.Dispose();
        _svc.Dispose();
        base.OnClosed(e);
    }
}
