using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    internal SKBitmap? _cutout;      // internal for tests
    internal SKBitmap? _windowImage; // internal for tests
    private WindowTransform _autoTransform = WindowTransform.Identity;
    private string? _photoPath;
    private DateTime? _photoAddedAt;
    private float _nudgeZoom = 1f, _nudgeX, _nudgeY;
    
    private Employee? _editingEmployee;
    private bool _restoringDraft;
    private int _previewZoomMode; // 0=Fit, 1=100%, 2=200%
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    // Records state
    private Employee? _selectedEmployee;
    private CardIssue? _selectedIssue;

    // Batch state — rows are persisted to BatchSessions/BatchRows (plan §7).
    private readonly System.Collections.ObjectModel.ObservableCollection<BatchRow> _batchRows = new();
    private string _batchFilter = "all";
    private string _batchSessionName = "";
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
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _undoEmployeeId = 0;
            PnlUndo.Visibility = Visibility.Collapsed;
        };

        // Batch grid combo columns are DataGridTemplateColumns now (ItemsSource
        // bound in XAML); nothing to wire here.
        DgBatch.ItemsSource = _batchRows;
        CollectionViewSource.GetDefaultView(_batchRows).Filter = BatchRowFilter;
        TxtName.TextChanged += (_, _) => UpdateNameHint();
        InitShell();

        RefreshRecords();
        MaybeOfferResume();
        SchedulePreview();
    }

    // ---------------- batch session persistence (plan §7) ----------------

    internal CardService Svc => _svc;
    internal string OutputDir => AppPaths.OutputDir;
    internal System.Collections.ObjectModel.ObservableCollection<BatchRow> BatchRows => _batchRows;
    private void EnsureBatchSession()
    {
        if (_batchSessionId == 0)
        {
            _batchSessionName = $"Batch {DateTime.Now:yyyy-MM-dd HH:mm}";
            _batchSessionId = _svc.Store.CreateBatchSession(_batchSessionName);
        }
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

    /// <summary>Test hook: skip re-processing the draft's photo (native pipeline, headless tests).</summary>
    public static bool SkipDraftPhotoRestore;

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
        _batchSessionName = sess.Name;
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
        return await Task.Run(() => new IDForge.Core.Issuing.CardIssuer(_svc.Store, AppSettings.SaveFolder).Issue(req));
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

    /// <summary>Photo status line: info messages are neutral with an icon; red is for errors only.</summary>
    internal void SetPhotoStatus(string msg, bool isError = false)
    {
        TxtPhotoStatus.Text = !isError && msg.Length > 0 ? "ℹ " + msg : msg;
        TxtPhotoStatus.Foreground = isError
            ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("MutedBrush");
    }

    /// <summary>True when a word has an uppercase letter right after a lowercase one ("mEHDI").</summary>
    internal static bool HasMixedCapitals(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            for (var i = 1; i < word.Length; i++)
                if (char.IsUpper(word[i]) && char.IsLower(word[i - 1])) return true;
        return false;
    }

    private void UpdateNameHint()
    {
        if (TxtNameHint == null) return;
        TxtNameHint.Visibility = HasMixedCapitals(TxtName?.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SchedulePreview()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e) => NewCardUiChanged();
    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender == TxtUid) Uid_TextChanged(sender, e);
        else NewCardUiChanged();
    }
    private void Field_Changed(object sender, RoutedEventArgs e) => NewCardUiChanged();

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
        NewCardUiChanged();
    }

    /// <summary>Rewrites the UID field to its normalized digit form on focus loss.</summary>
    private void Uid_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = Core.Uid.Normalize(TxtUid.Text);
        if (n != null && n != TxtUid.Text.Trim()) TxtUid.Text = n;
        UpdateUidStatus();
    }

    /// <summary>Catalogue wording: length error, existing-card error, or "new employee".</summary>
    private void UpdateUidStatus()
    {
        var digits = new string(TxtUid.Text.Where(char.IsDigit).ToArray());
        string msg = "";
        Brush? color = null;
        Employee? dup = null;
        if (digits.Length > 0)
        {
            if (!Core.Uid.IsValid(digits))
            {
                msg = $"Employee ID must be {Core.Uid.MinLength} to {Core.Uid.MaxLength} digits. " +
                      $"You've entered {digits.Length}.";
                color = FindResource("ErrorBrush") as Brush ?? Brushes.Red;
            }
            else
            {
                var existing = _svc?.Store.FindEmployeeByUid(digits);
                if (existing != null)
                {
                    dup = existing;
                    var issue = _svc.Store.LatestIssue(existing.Id);
                    var when = issue?.IssuedAt.LocalDateTime.ToString("dd/MM/yyyy")
                               ?? existing.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy");
                    msg = $"UID already exists — issued to {existing.Name} on {when}.";
                    color = FindResource("ErrorBrush") as Brush ?? Brushes.Red;
                }
                else
                {
                    msg = "New employee — no existing card with this ID";
                    color = FindResource("SuccessBrush") as Brush ?? Brushes.Green;
                }
            }
        }
        _uidDuplicate = dup;
        ErrUid.Text = msg;
        ErrUid.Foreground = color ?? Brushes.Gray;
        if (LnkOpenRecord != null)
            LnkOpenRecord.Visibility = dup != null ? Visibility.Visible : Visibility.Collapsed;
        if (_shellReady) UpdateApproveButton();   // refresh Issue enabled + blocked note
    }

    /// <summary>Opens Records with the duplicate employee's row selected.</summary>
    internal void OpenRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_uidDuplicate == null) return;
        ShowArea("records");
        RefreshRecords();
        var row = LstRecords.Items.OfType<RecordRow>()
            .FirstOrDefault(r => r.Emp.Id == _uidDuplicate.Id);
        if (row != null)
        {
            LstRecords.SelectedItem = row;
            LstRecords.ScrollIntoView(row);
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

    private bool _closed;

    private async Task UpdatePreviewAsync()
    {
        if (_closed) return;   // shared test dispatcher keeps timers alive after Close
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

    private BgMode CurrentBgMode => PosPanel.BgMode switch
    {
        "Model" => BgMode.Model, "Keep" => BgMode.Keep, _ => BgMode.PlainWhite
    };

    private string CurrentBgModeTag => PosPanel.BgMode;
    internal string BgModeTagForTest => CurrentBgModeTag;
    internal bool TestNavCollapsed => _navCollapsed;

    private void BgMode_Changed_Reprocess(string tag)
    {
        // Mode change reprocesses from the original file (cache makes repeat
        // modes cheap) and keeps the current nudge values.
        if (_restoringDraft || _photoPath == null || !IsLoaded) return;
        ProcessPhotoAsync(_photoPath, keepNudge: true);
    }

    private void SetBgModeFromTag(string tag) => PosPanel.SetBgMode(tag);

    private static int _photoBusy;

    /// <summary>Serializes photo jobs — the native pipeline is not re-entrant.</summary>
    private async void ProcessPhotoAsync(string path, bool keepNudge = false)
    {
        if (System.Threading.Interlocked.Exchange(ref _photoBusy, 1) == 1) return;
        try { await ProcessPhotoCoreAsync(path, keepNudge); }
        finally { _photoBusy = 0; }
    }

    private async Task ProcessPhotoCoreAsync(string path, bool keepNudge = false)
    {
        float keepZoom = _nudgeZoom, keepX = _nudgeX, keepY = _nudgeY;
        SetPhotoStatus("Processing photo (background removal)...");
        UpdateApproveButton();
        BtnSave.IsEnabled = false;
        PosPanel.IsEnabled = false;
        try
        {
            var mode = CurrentBgMode;
            var job = await Task.Run(() => _svc.ProcessPhoto(path, mode));
            // Continuations can land off the UI thread (tests, shutdown) — run all
            // UI work inside InvokeAsync so it always executes on the dispatcher.
            if (Dispatcher.HasShutdownStarted) { job.Cutout?.Dispose(); job.WindowImage?.Dispose(); return; }
            await Dispatcher.InvokeAsync(() =>
            {
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
                SetPhotoStatus(string.Join(" ", new[] { bgNote, job.Warning ?? "Photo ready — check the preview, then Issue card." }
                    .Where(s => s != null)));
                PosPanel.IsEnabled = job.Cutout != null;
                UpdateApproveButton();
                UpdatePhotoThumb();
                UpdateFooterChips();
                SchedulePreview();
                ScheduleDraftSave();
            });
        }
        catch (Exception ex)
        {
            try
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.Invoke(() => SetPhotoStatus($"Photo failed: {ex.Message}", isError: true));
            }
            catch { /* dispatcher already gone */ }
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
        UpdateApproveButton();
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
        PosPanel.BgModeChanged += BgMode_Changed_Reprocess;
        PosPanel.SetState(1, 0, 0);
    }

    private Employee? _uidDuplicate;

    /// <summary>Issue card is enabled when details are valid and a photo is present.
    /// Photo approval is gone — issuing is the only approval step (Fix F-A).</summary>
    internal void UpdateApproveButton()
    {
        ChkPhoto.Visibility = _cutout != null ? Visibility.Visible : Visibility.Collapsed;
        BtnSave.IsEnabled = _cutout != null && _uidDuplicate == null;
        if (TxtIssueBlocked != null)
            TxtIssueBlocked.Visibility = _uidDuplicate != null ? Visibility.Visible : Visibility.Collapsed;
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

    private enum ChipState { Idle, Ok, Warn, Err }

    private void SetChip(Border chip, TextBlock text, string label, ChipState state)
    {
        text.Text = label;
        var (bg, br, fg) = state switch
        {
            ChipState.Ok => ((Brush?)FindResource("SuccessTintBrush"),
                             (Brush?)FindResource("SuccessBrush"),
                             (Brush?)FindResource("SuccessBrush")),
            ChipState.Warn => ((Brush?)FindResource("WarningTintBrush"),
                               (Brush?)FindResource("WarningBrush"),
                               (Brush?)FindResource("WarningBrush")),
            ChipState.Err => ((Brush?)FindResource("ErrorTintBrush"),
                              (Brush?)FindResource("ErrorBrush"),
                              (Brush?)FindResource("ErrorBrush")),
            _ => (null,
                  (Brush?)FindResource("BorderBrush"),
                  (Brush?)FindResource("MutedBrush"))
        };
        if (bg == null) chip.ClearValue(Border.BackgroundProperty); else chip.Background = bg;
        chip.BorderBrush = br;
        text.Foreground = fg;
    }

    private void UpdateFooterChips()
    {
        var f = GetForm();
        bool fieldsOk = f.name.Length >= 2 && Core.Uid.IsValid(f.uid)
            && Config.Positions.Contains(f.position) && Config.BloodGroups.Contains(f.blood);
        bool uidValid = Core.Uid.IsValid(f.uid);
        var dup = uidValid ? _uidDuplicate : null;
        bool uidNew = uidValid && dup == null;

        // Green only when every required field is valid AND the ID is new;
        // amber when the fields are fine but the ID already exists.
        SetChip(ChipDetails, TxtChipDetails, "Details complete",
            fieldsOk && uidNew ? ChipState.Ok : fieldsOk && dup != null ? ChipState.Warn : ChipState.Idle);
        // One truthful chip: green "is new", red "already exists", grey otherwise.
        SetChip(ChipUidNew, TxtChipUidNew,
            dup != null ? "Employee ID already exists" : "Employee ID is new",
            uidNew ? ChipState.Ok : dup != null ? ChipState.Err : ChipState.Idle);
        SetChip(ChipPhoto, TxtChipPhoto, "Photo ready",
            _cutout != null ? ChipState.Ok : ChipState.Idle);
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
        if (_draftPending) { _draftPending = false; UpdateTitle(); }
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

            if (!SkipDraftPhotoRestore && !string.IsNullOrWhiteSpace(d.PhotoPath) && File.Exists(d.PhotoPath))
            {
                _photoPath = d.PhotoPath;
                _photoAddedAt = null;
                UpdatePhotoMeta();
                ProcessPhotoAsync(d.PhotoPath, keepNudge: true);
            }
            else if (!string.IsNullOrWhiteSpace(d.PhotoPath))
            {
                SetPhotoStatus("Draft photo file is missing — choose the photo again.");
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
            UpdateApproveButton();
            PosPanel.IsEnabled = false;
            PosPanel.SetState(1, 0, 0);
            _nudgeZoom = 1f; _nudgeX = _nudgeY = 0;
            SetPhotoStatus("");
            TxtStatus.Text = "";
            _draftPending = false;
            _draftTimer.Stop();
            NewCardDraft.Clear();
            TxtDraftNote.Text = "";
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
    private bool _draftPending;   // edits not yet written to the autosaved draft
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
        UpdateApproveButton();
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
           (_area == "newcard" && _draftPending ? " • saving…" : "");

    private static string AreaLabel(string a) => a switch
    {
        "batch" => "Batch", "records" => "Records", "settings" => "Settings", _ => "New card"
    };

    private void ScheduleDraftSave()
    {
        if (!_shellReady || _restoringDraft) return;
        if (!_draftPending) { _draftPending = true; UpdateTitle(); }
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
        // Collapsed rail: icon only, single-line tooltip per spec.
        PnlBackupCompact.ToolTip = AppSettings.LastBackupAt is { } cb
            ? $"Last backup {cb:dd/MM/yyyy HH:mm}. Back up now"
            : "Never backed up. Back up now";
    }

    private void UpdateBatchBadge()
    {
        var n = _batchRows.Count;
        TxtNavBatchCount.Text = n.ToString();
        NavBatchBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        var review = _batchRows.Count(r => BatchRowHasContent(r) && r.Status is "NeedsReview" or "Draft" or "NeedsPhoto");
        NavBatch.ToolTip = _navCollapsed
            ? $"Batch — {(review > 0 ? $"{review} to review" : "nothing to review")} (Ctrl+2)"
            : $"Batch (Ctrl+2){(n > 0 ? $" — {n} row{(n == 1 ? "" : "s")}" : "")}";
    }

    internal void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var collapsed = ActualWidth < 1100;
        if (collapsed == _navCollapsed) return;
        SetNavCollapsed(collapsed);
    }

    /// <summary>Single collapse path used by auto-collapse AND the nav toggle —
    /// the menu toggle used to set NavCol.Width directly, leaving labels and the
    /// full backup panel visible (and clipped) at rail width.</summary>
    internal void SetNavCollapsed(bool collapsed)
    {
        _navCollapsed = collapsed;
        NavCol.Width = new GridLength(collapsed ? 64 : 208);
        foreach (var name in new[] { "NavLblNewCard", "NavKeyNewCard", "NavLblBatch", "NavKeyBatch",
                     "NavLblRecords", "NavKeyRecords", "NavLblSettings", "NavKeySettings",
                     "LblIssueCards", "LblManage" })
        {
            if (FindName(name) is FrameworkElement fe) fe.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        // Collapsed: fixed-size icon buttons centred in the 64 px rail —
        // 44 px button + 2 px focus ring + padding on every side, nothing clipped.
        foreach (var (rb, pnl) in new (RadioButton, FrameworkElement)[]
                 { (NavNewCard, NavPnlNewCard), (NavBatch, NavPnlBatch),
                   (NavRecords, NavPnlRecords), (NavSettings, NavPnlSettings) })
        {
            if (collapsed)
            {
                rb.Width = 44; rb.Height = 44;
                rb.HorizontalAlignment = HorizontalAlignment.Center;
                rb.Padding = new Thickness(0);
                pnl.HorizontalAlignment = HorizontalAlignment.Center;
                pnl.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                rb.ClearValue(FrameworkElement.WidthProperty);
                rb.ClearValue(FrameworkElement.HeightProperty);
                rb.HorizontalAlignment = HorizontalAlignment.Stretch;
                // 18 left = old 10 padding + 8 ContentPresenter margin.
                rb.Padding = new Thickness(18, 7, 10, 7);
                pnl.HorizontalAlignment = HorizontalAlignment.Left;
                pnl.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }
        // Badge overlays the Batch icon in both modes; hidden only at zero rows.
        NavBatchBadge.Margin = collapsed ? new Thickness(0, 0, 2, 26) : new Thickness(0, 0, 10, 0);
        NavBatchBadge.VerticalAlignment = collapsed ? VerticalAlignment.Top : VerticalAlignment.Center;
        NavBatchBadge.HorizontalAlignment = HorizontalAlignment.Right;
        // Section headings hide; thin dividers mark the groups instead.
        foreach (var name in new[] { "DvdIssueCards", "DvdManage" })
            if (FindName(name) is FrameworkElement dv) dv.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
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
            case Key.F when _area == "records" && !shift:
                TxtSearch.Focus(); TxtSearch.SelectAll(); break;
            case Key.P when _area == "records" && !shift:
                if (BtnReprint.IsEnabled) Reprint_Click(this, null!); break;
            case Key.R when _area == "records" && shift:
                if (BtnReissue.IsEnabled) Reissue_Click(this, null!); break;
            case Key.O when inNewCard && !shift: BrowsePhoto_Click(this, null!); break;
            case Key.D0 when inNewCard && !shift:
            case Key.NumPad0 when inNewCard && !shift: ResetPhotoPosition(); break;
            case Key.S when inNewCard && !shift: SaveDraftNow(); break;
            case Key.Enter when inNewCard && !shift: Save_Click(this, null!); break;
            default: return false;
        }
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_area == "batch" && Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            if (e.Key == Key.F5) { StartBatch_Click(this, null!); e.Handled = true; return; }
            if (e.Key == Key.Delete && DgBatch.SelectedItem is BatchRow)
            {
                BatchDelRow_Click(this, null!); e.Handled = true; return;
            }
        }
        if (_area == "records" && Keyboard.FocusedElement is not TextBoxBase and not ComboBox)
        {
            if (e.Key == Key.F2 && BtnEditRec.IsEnabled)
            { EditRecord_Click(this, null!); e.Handled = true; return; }
            if (e.Key == Key.Delete && BtnDeleteRec.IsEnabled)
            { DeleteRecord_Click(this, null!); e.Handled = true; return; }
        }
        if (TryHandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void NavNewCard_Click(object sender, RoutedEventArgs e) => ShowArea("newcard");
    private void NavBatch_Click(object sender, RoutedEventArgs e) => ShowArea("batch");
    private void NavRecords_Click(object sender, RoutedEventArgs e) => ShowArea("records");
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowArea("settings");
    private void GoSettings_Click(object sender, RoutedEventArgs e) => ShowArea("settings");
    private void MenuToggleNav_Click(object sender, RoutedEventArgs e)
        => SetNavCollapsed(NavCol.Width.Value > 100);
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
            try { Directory.CreateDirectory(dlg.FolderName); } catch { }
        }
    }

    /// <summary>Test hook: replaces the "output folder missing" prompt.
    /// Args: owner, path — returns true when the user chose to open Settings.</summary>
    internal static Func<Window, string, bool>? OutputFolderPrompt;

    /// <summary>
    /// H-C: issuing never asks for a folder — cards go to Settings "Save cards
    /// to". If that folder is missing or not writable, block BEFORE anything is
    /// written (no files, no Records entry) and offer to open Settings.
    /// </summary>
    internal bool EnsureIssueOutputFolder(Window owner)
    {
        var dir = AppSettings.SaveFolder;
        if (IsWritableDir(dir)) return true;
        var open = OutputFolderPrompt?.Invoke(owner, dir) ?? ShowOutputFolderMissing(owner, dir);
        if (open) ShowArea("settings");
        return false;
    }

    private static bool ShowOutputFolderMissing(Window owner, string dir)
    {
        var r = MessageBox.Show(owner,
            $"Output folder not found: {dir}.\n\nChange it in Settings.",
            "Output folder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private static bool IsWritableDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".idforge-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
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
        if (_cutout == null) { SetStatus("Add a photo first."); return; }
        if (_uidDuplicate != null) { SetStatus("Employee ID already exists — open the record to re-issue."); return; }
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
        if (!EnsureIssueOutputFolder(this)) return;
        if (!Dialogs.ConfirmIssue(this, f.name, f.uid, validUntil, isReissue)) return;

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
            IncludePdf: AppSettings.IncludePdf);

        BtnSave.IsEnabled = false;
        SetStatus("Issuing...");
        try
        {
            var outcome = await Task.Run(() =>
                new IDForge.Core.Issuing.CardIssuer(_svc.Store, AppSettings.SaveFolder).Issue(req));
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
            UpdateApproveButton();
            PosPanel.IsEnabled = false;
            PosPanel.SetState(1, 0, 0);
            _nudgeZoom = 1f; _nudgeX = _nudgeY = 0;
            _draftPending = false;
            _draftTimer.Stop();
            NewCardDraft.Clear();
            TxtDraftNote.Text = "";
            UpdateTitle();
            UpdateUidStatus();
            UpdateFooterChips();
            SchedulePreview();
            RefreshRecords();
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
        finally { UpdateApproveButton(); }
    }

    // ---------------- records ----------------

    private sealed record RecordRow(Employee Emp, CardIssue? Issue)
    {
        public string Uid => Emp.Uid;
        public string Name => Emp.Name;
        public string Position => Emp.Position;
        public string ContractType => Emp.ContractType;
        public string ValidUntil => Issue?.ValidUntil.ToString("dd/MM/yyyy") ?? "-";
        public string IssuedOn => Issue?.IssuedAt.LocalDateTime.ToString("dd/MM/yyyy") ?? "-";
        public string LastIssued => Issue?.IssuedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") ?? "-";
        public string ValidityKind { get; init; } = "None";
        public string BadgeText => ValidityKind == "None" ? "-" : ValidityKind;
        public SolidColorBrush BadgeBrush { get; init; } = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    }

    /// <summary>Active / Expires soon (≤30 days) / Expired / None.</summary>
    internal static string ValidityKindFor(DateOnly? validUntil, DateOnly today)
    {
        if (validUntil == null) return "None";
        if (validUntil < today) return "Expired";
        if (validUntil <= today.AddDays(30)) return "Expires soon";
        return "Active";
    }

    private static SolidColorBrush BadgeBrushFor(string kind) => kind switch
    {
        "Active" => new(Color.FromRgb(0x16, 0xA3, 0x4A)),
        "Expires soon" => new(Color.FromRgb(0xB4, 0x53, 0x09)),
        "Expired" => new(Color.FromRgb(0xDC, 0x26, 0x26)),
        _ => new(Color.FromRgb(0x9C, 0xA3, 0xAF))
    };

    private long _undoEmployeeId;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private void RecordFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_svc == null) return;   // combo init fires before the ctor finishes
        ApplyRecordFilters();
    }

    private void ApplyRecordFilters()
    {
        // 30-day retention for deleted records.
        try { _svc.Store.PurgeDeletedOlderThan(30); } catch { }

        var term = TxtSearch.Text.Trim();
        var contract = CmbContractFilter.SelectedItem as ComboBoxItem;
        var validity = CmbValidityFilter.SelectedItem as ComboBoxItem;
        var issued = CmbIssuedFilter.SelectedItem as ComboBoxItem;
        var contractF = contract?.Content?.ToString() ?? "Any contract";
        var validityF = validity?.Content?.ToString() ?? "Any validity";
        var issuedF = issued?.Content?.ToString() ?? "Any time";
        var today = Validity.TodayInDhaka();
        var rows = new List<RecordRow>();
        foreach (var emp in _svc.Store.AllEmployees())
        {
            if (term.Length > 0 &&
                emp.Uid.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                emp.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (contractF is "Permanent" or "Contractual" && emp.ContractType != contractF)
                continue;
            var issue = _svc.Store.LatestIssue(emp.Id);
            var kind = ValidityKindFor(issue?.ValidUntil, today);
            if (validityF is "Active" or "Expires soon" or "Expired" && kind != validityF)
                continue;
            if (issuedF is "Today" or "Last 7 days" or "Last 30 days")
            {
                if (issue == null) continue;
                var days = issuedF == "Today" ? 0 : issuedF == "Last 7 days" ? 7 : 30;
                if (issue.IssuedAt.LocalDateTime.Date < today.ToDateTime(TimeOnly.MinValue).Date.AddDays(-days))
                    continue;
            }
            rows.Add(new RecordRow(emp, issue)
            {
                ValidityKind = kind,
                BadgeBrush = BadgeBrushFor(kind)
            });
        }
        LstRecords.ItemsSource = rows;
        TxtRecordCount.Text = $"{rows.Count} record{(rows.Count == 1 ? "" : "s")}";
        if (LstRecords.SelectedItem == null) UpdateRecordButtons(null);
    }

    private void RefreshRecords() => ApplyRecordFilters();

    private void Search_Click(object sender, RoutedEventArgs e) => ApplyRecordFilters();
    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyRecordFilters();
    }

    private void RefreshRecords_Click(object sender, RoutedEventArgs e)
    {
        TxtSearch.Clear();
        ApplyRecordFilters();
    }

    private void Record_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (LstRecords.SelectedItem is not RecordRow row) { UpdateRecordButtons(null); return; }
        _selectedEmployee = row.Emp;
        _selectedIssue = row.Issue;
        UpdateRecordButtons(row);
        TxtRecordInfo.Text = $"{row.Emp.Name} — {row.Emp.Position}, {row.Emp.ContractType}";
        ShowRecordDetails(row);
    }

    private void UpdateRecordButtons(RecordRow? row)
    {
        BtnReprint.IsEnabled = row?.Issue != null;
        BtnReissue.IsEnabled = row != null;
        BtnExportRec.IsEnabled = row?.Issue != null;
        BtnEditRec.IsEnabled = row != null;
        BtnDeleteRec.IsEnabled = row != null;
        BtnShowFile.IsEnabled = row?.Issue?.OutputPngPath != null && File.Exists(row.Issue.OutputPngPath);
        if (row == null)
        {
            ImgRecPreview.Source = null;
            TxtRecPreviewEmpty.Visibility = Visibility.Visible;
            DtlDetails.Children.Clear();
            DtlFiles.Children.Clear();
            LstHistory.ItemsSource = null;
        }
    }

    private void ShowRecordDetails(RecordRow row)
    {
        var emp = row.Emp;
        var issue = row.Issue;
        // Card preview: the saved PNG when it exists.
        if (issue?.OutputPngPath != null && File.Exists(issue.OutputPngPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(issue.OutputPngPath);
                bmp.EndInit();
                ImgRecPreview.Source = bmp;
                TxtRecPreviewEmpty.Visibility = Visibility.Collapsed;
            }
            catch { ImgRecPreview.Source = null; TxtRecPreviewEmpty.Visibility = Visibility.Visible; }
        }
        else { ImgRecPreview.Source = null; TxtRecPreviewEmpty.Visibility = Visibility.Visible; }

        void AddPair(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            sp.Children.Add(new TextBlock { Text = label + ":", Width = 90,
                Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12 });
            sp.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(value) ? "-" : value,
                FontSize = 12, TextWrapping = TextWrapping.Wrap });
            DtlDetails.Children.Add(sp);
        }
        DtlDetails.Children.Clear();
        AddPair("Name", emp.Name);
        AddPair("Employee ID", emp.Uid);
        AddPair("Position", emp.Position);
        AddPair("Blood group", emp.BloodGroup);
        AddPair("Contract", emp.ContractType);
        AddPair("Issued on", row.IssuedOn);
        AddPair("Valid until", row.ValidUntil);
        AddPair("Card version", issue != null ? $"v{issue.Version}" : "-");
        AddPair("Added", emp.CreatedAt.LocalDateTime.ToString("dd/MM/yyyy"));

        DtlFiles.Children.Clear();
        void AddFile(string label, string? path)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            sp.Children.Add(new TextBlock { Text = path ?? "-", FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap });
            DtlFiles.Children.Add(sp);
        }
        AddFile("Card PNG", issue?.OutputPngPath);
        AddFile("Card PDF", issue?.OutputPdfPath);
        AddFile("Photo", emp.PhotoPath);

        try
        {
            LstHistory.ItemsSource = _svc.Store.AuditForEmployee(emp.Id)
                .Select(a => $"{a.At.LocalDateTime:dd/MM/yyyy HH:mm}  {a.Action}  {a.Detail}")
                .ToList();
        }
        catch { LstHistory.ItemsSource = null; }
    }

    private void ShowCardFile_Click(object sender, RoutedEventArgs e)
    {
        var path = _selectedIssue?.OutputPngPath;
        if (path == null || !File.Exists(path))
        { SetStatus("No card file for this record."); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Show file"); }
    }

    private void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_undoEmployeeId == 0) return;
        var id = _undoEmployeeId;
        _undoEmployeeId = 0;
        _undoTimer.Stop();
        PnlUndo.Visibility = Visibility.Collapsed;
        try
        {
            var emp = _svc.Store.FindEmployeeById(id);
            _svc.Store.RestoreEmployee(id);
            _svc.Store.LogAudit(id, "restore", $"Undid delete of {emp?.Name} ({emp?.Uid})");
            SetStatus($"Restored {emp?.Name}.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Undo failed"); }
        ApplyRecordFilters();
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

        if (!EnsureIssueOutputFolder(this)) return;

        // Re-issue always confirms — name, UID, new print date, new valid-until.
        var ok = MessageBox.Show(this,
            $"Re-issue card for {emp.Name}, UID {emp.Uid}?\n" +
            $"New print date: {printDate:dd/MM/yyyy}\nValid until: {validUntil:MMMM yyyy}",
            "Confirm re-issue", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

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
            IncludePdf: AppSettings.IncludePdf);
        try
        {
            var outcome = await Task.Run(() =>
                new IDForge.Core.Issuing.CardIssuer(_svc.Store, AppSettings.SaveFolder).Issue(req));
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

    /// <summary>Injectable confirm for tests; null shows the Yes/No prompt.</summary>
    internal static Func<string, bool>? ConfirmDeleteRecord;

    private void DeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEmployee == null) return;
        var emp = _selectedEmployee;
        var prompt = $"Delete record for {emp.Name} ({emp.Uid})?\nIt moves to Deleted records for 30 days — you can undo for 10 seconds.";
        var r = ConfirmDeleteRecord?.Invoke(prompt) == true
            ? MessageBoxResult.Yes
            : ConfirmDeleteRecord != null ? MessageBoxResult.No
            : MessageBox.Show(this, prompt, "Delete record", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _svc.Store.SoftDeleteEmployee(emp.Id);
        _svc.Store.LogAudit(emp.Id, "delete", $"Soft-deleted {emp.Name} ({emp.Uid})");
        // Undo banner for 10 seconds.
        _undoEmployeeId = emp.Id;
        TxtUndo.Text = $"Deleted {emp.Name} ({emp.Uid})";
        PnlUndo.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
        SetStatus($"Deleted {emp.Name}. Restore via Records → Deleted records, or Undo.");
        _selectedEmployee = null;
        _selectedIssue = null;
        UpdateRecordButtons(null);
        ApplyRecordFilters();
    }

    private void DeletedRecords_Click(object sender, RoutedEventArgs e)
    {
        var deleted = _svc.Store.DeletedEmployees();
        if (deleted.Count == 0) { MessageBox.Show("No deleted records."); return; }
        var list = new StackPanel { Margin = new Thickness(10) };
        Window? dlg = null;
        foreach (var emp in deleted)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = $"{emp.Name} ({emp.Uid})", Width = 280, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Content = "Restore" };
            var btnPurge = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0) };
            var id = emp.Id;
            btn.Click += (_, _) =>
            {
                _svc.Store.RestoreEmployee(id);
                _svc.Store.LogAudit(id, "restore", $"Restored {emp.Name} ({emp.Uid})");
                MessageBox.Show($"Restored {emp.Name}.");
                dlg?.Close();
                ApplyRecordFilters();
            };
            btnPurge.Click += (_, _) =>
            {
                var ok = MessageBox.Show($"Permanently delete {emp.Name} ({emp.Uid}) and all their card issues?\nThis cannot be undone.",
                    "Remove for good", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.Yes) return;
                _svc.Store.PurgeEmployee(id);
                _svc.Store.LogAudit(id, "purge", $"Permanently removed {emp.Name} ({emp.Uid})");
                MessageBox.Show($"{emp.Name} permanently removed.");
                dlg?.Close();
                ApplyRecordFilters();
            };
            row.Children.Add(btn);
            row.Children.Add(btnPurge);
            list.Children.Add(row);
        }
        dlg = new Window
        {
            Title = "Deleted records", Width = 460, Height = Math.Min(120 + deleted.Count * 36, 500),
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

    internal void BatchDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (DgBatch.SelectedItem is not BatchRow row) return;
        if (BatchRowHasContent(row) && !ConfirmRowRemoval(row)) return;
        if (row.RowId != 0)
        {
            try { _svc.Store.DeleteBatchRow(row.RowId); } catch { }
        }
        row.Cutout?.Dispose(); row.WindowImage?.Dispose();
        _batchRows.Remove(row);
    }

    /// <summary>Content that makes a row worth confirming before removal.</summary>
    internal static bool BatchRowHasContent(BatchRow row) =>
        !string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.Uid)
        || !string.IsNullOrWhiteSpace(row.Position) || !string.IsNullOrWhiteSpace(row.PhotoPath);

    /// <summary>Injectable confirm for tests; default shows the Remove prompt.</summary>
    internal static Func<string, bool>? ConfirmRemoveRow;

    private bool ConfirmRowRemoval(BatchRow row)
    {
        var n = _batchRows.IndexOf(row) + 1;
        var label = string.IsNullOrWhiteSpace(row.Name) ? row.Uid : row.Name;
        var prompt = $"Remove row {n} ({label})?";
        if (ConfirmRemoveRow != null) return ConfirmRemoveRow(prompt);
        return MessageBox.Show(this, prompt, "Remove row",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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

    /// <summary>I-B: the Employee/Employee ID cells are always-editable text
    /// boxes. Arrows/Enter still move between cells — Left/Right only leave the
    /// field at the caret boundary so normal caret movement is preserved.</summary>
    private void BatchText_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var (dRow, dCol) = e.Key switch
        {
            Key.Up => (-1, 0),
            Key.Down => (1, 0),
            Key.Enter => (1, 0),
            Key.Left when tb.SelectionStart == 0 && tb.SelectionLength == 0 => (0, -1),
            Key.Right when tb.SelectionStart == tb.Text.Length && tb.SelectionLength == 0 => (0, 1),
            _ => (0, 0)
        };
        if (dRow == 0 && dCol == 0) return;
        e.Handled = true;
        tb.MoveFocus(new TraversalRequest(dCol < 0 ? FocusNavigationDirection.Left
            : dCol > 0 ? FocusNavigationDirection.Right
            : dRow < 0 ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
    }

    /// <summary>I-B: normalize the Employee ID to digits when the cell loses focus.</summary>
    private void BatchUid_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is BatchRow row)
        {
            var n = Core.Uid.Normalize(tb.Text);
            if (n != null && n != row.Uid) row.Uid = n;
        }
    }

    // ---- review ----

    private void BatchReview_Click(object sender, RoutedEventArgs e)
    {
        // Open on the first row that needs review (same rule that enables the button).
        var first = _batchRows.FirstOrDefault(r =>
            r.Status is "NeedsReview" or "Draft" or "NeedsPhoto");
        OpenReview(first);
    }

    /// <summary>H-B: the click that dismisses an open dropdown popup is
    /// swallowed, so clicking a second combo used to need a double-click.
    /// When a batch combo's popup closes, reopen whichever combo is under the
    /// pointer — one click now opens it.</summary>
    private void BatchCellCombo_Closed(object sender, EventArgs e)
    {
        if (sender is not ComboBox closed) return;
        var pos = Mouse.GetPosition(DgBatch);
        Dispatcher.BeginInvoke(() => TryOpenComboUnderMouse(closed, pos),
            DispatcherPriority.Input);
    }

    /// <summary>Opens the combo under <paramref name="posInGrid"/> unless it is
    /// the one that just closed. Internal for tests.</summary>
    internal void TryOpenComboUnderMouse(ComboBox closed, Point posInGrid)
    {
        if (DgBatch.InputHitTest(posInGrid) is not DependencyObject hit) return;
        for (var cur = hit; cur != null && cur != DgBatch; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is ComboBox cb)
            {
                if (!ReferenceEquals(cb, closed) && !cb.IsDropDownOpen)
                    cb.IsDropDownOpen = true;
                return;
            }
        }
    }

    private void OpenReview(BatchRow? startAt)
    {
        try
        {
            if (_batchRows.Count == 0) { MessageBox.Show("Add rows first."); return; }
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

    internal void UpdateBatchSummary()
    {
        // H-B: empty rows (no content) don't count toward any tally.
        var active = _batchRows.Where(BatchRowHasContent).ToList();
        int issued = active.Count(r => r.Status == "Issued");
        int review = active.Count(r => r.Status is "NeedsReview" or "Draft" or "NeedsPhoto");
        int failed = active.Count(r => r.Status == "Failed");
        int skipped = active.Count(r => r.Status == "Skipped");
        TxtBatchStatus.Text = $"Ready to review: {review}, Issued: {issued}, Failed: {failed}" +
            (skipped > 0 ? $", Skipped: {skipped}" : "");
        TxtBatchName.Text = FormatBatchName(_batchSessionName);
        TxtBatchCounts.Text = BatchCountsText();
        RefreshBatchDerived();
        UpdateReviewButton();
        // Auto-complete the session when every content row reached a terminal state.
        if (_batchSessionId != 0 && active.Count > 0 &&
            active.All(r => r.Status is "Issued" or "Skipped"))
        {
            try { _svc.Store.CompleteBatchSession(_batchSessionId); _batchSessionId = 0; } catch { }
        }
        UpdateBatchBadge();
    }

    /// <summary>
    /// Fills the read-only grid columns that need store lookups: row numbers,
    /// New card/Re-issue card type with the replaced-issue date, the Message
    /// column (error + re-issue note + within-batch duplicate flag).
    /// </summary>
    private void RefreshBatchDerived()
    {
        var empCache = new Dictionary<string, IDForge.Core.Data.Employee?>(StringComparer.OrdinalIgnoreCase);
        IDForge.Core.Data.Employee? EmpFor(string uid)
        {
            var n = Core.Uid.Normalize(uid);
            if (n == null) return null;
            if (!empCache.TryGetValue(n, out var emp))
            {
                emp = _svc.Store.FindEmployeeByUid(n);
                empCache[n] = emp;
            }
            return emp;
        }

        // first occurrence row number per normalized uid, for duplicate flags
        var firstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dupOf = new Dictionary<BatchRow, int>();
        for (int i = 0; i < _batchRows.Count; i++)
        {
            var r = _batchRows[i];
            r.RowNumber = i + 1;
            var n = Core.Uid.Normalize(r.Uid);
            if (n == null) continue;
            if (firstRow.TryGetValue(n, out var first)) dupOf[r] = first;
            else firstRow[n] = i + 1;
        }

        foreach (var r in _batchRows)
        {
            var emp = EmpFor(r.Uid);
            r.CardType = emp != null ? "Re-issue" : "New card";
            var issue = emp != null ? _svc.Store.LatestIssue(emp.Id) : null;
            r.ReplacesText = issue != null
                ? $"Replaces card issued {issue.IssuedAt.ToString("dd/MM/yyyy")}" : "";
            // Draft/NeedsPhoto rows list what is still missing instead of an error.
            // H-B: a row with no content at all shows no Message.
            var lead = r.Error;
            if (BatchRowHasContent(r) && r.Status is "Draft" or "NeedsPhoto")
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(r.Name)) missing.Add("name");
                if (string.IsNullOrWhiteSpace(r.Uid)) missing.Add("ID");
                if (string.IsNullOrWhiteSpace(r.Position)) missing.Add("position");
                if (string.IsNullOrWhiteSpace(r.PhotoPath)) missing.Add("photo");
                if (missing.Count > 0) lead = $"Missing: {string.Join(", ", missing)}";
            }
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(lead)) parts.Add(lead);
            if (HasMixedCapitals(r.Name)) parts.Add("Mixed capitals, check spelling");
            if (r.ReplacesText.Length > 0) parts.Add(r.ReplacesText);
            if (dupOf.TryGetValue(r, out var first)) parts.Add($"Duplicate of row {first}");
            r.Message = string.Join(" · ", parts);
        }
    }

    /// <summary>Session names store "Batch yyyy-MM-dd HH:mm" — show dd/MM/yyyy.</summary>
    private static string FormatBatchName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Batch";
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d{4})-(\d{2})-(\d{2})");
        return m.Success
            ? name.Substring(0, m.Index) + m.Groups[3].Value + "/" + m.Groups[2].Value
                + "/" + m.Groups[1].Value + name.Substring(m.Index + m.Length)
            : name;
    }

    private bool BatchRowFilter(object o)
    {
        if (o is not BatchRow r) return false;
        return _batchFilter switch
        {
            "attention" => r.Status is "Failed" or "NeedsPhoto",
            "review" => r.Status == "NeedsReview",
            "reissue" => r.CardType == "Re-issue",
            _ => true,
        };
    }

    private void BatchFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _batchFilter = tag;
            CollectionViewSource.GetDefaultView(_batchRows).Refresh();
        }
    }

    /// <summary>Counts line: "3 issued, 5 to review, 1 failed" — "All rows done" at the end.</summary>
    private string BatchCountsText()
    {
        // I-B: rows with no content don't count and show no Message.
        var rows = _batchRows.Where(BatchRowHasContent).ToList();
        // I-C: rows exist but all are empty -> prompt for details, not "No rows yet".
        if (rows.Count == 0) return _batchRows.Count == 0 ? "No rows yet" : "Add details to begin";
        if (rows.All(r => r.Status is "Issued" or "Skipped")) return "All rows done";
        var parts = new List<string>();
        var issued = rows.Count(r => r.Status == "Issued");
        var review = rows.Count(r => r.Status is "NeedsReview" or "Draft" or "NeedsPhoto");
        var failed = rows.Count(r => r.Status == "Failed");
        var skipped = rows.Count(r => r.Status == "Skipped");
        if (issued > 0) parts.Add($"{issued} issued");
        if (review > 0) parts.Add($"{review} to review");
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        return string.Join(", ", parts);
    }

    /// <summary>Review opens on the first row that needs review; disabled with a reason otherwise.</summary>
    private void UpdateReviewButton()
    {
        var first = _batchRows.FirstOrDefault(r => BatchRowHasContent(r) && NeedsReview(r));
        BtnReview.IsEnabled = first != null;
        TxtReviewReason.Text = first != null ? "" : _batchRows.Count == 0 ? "Add rows to begin" : "No rows to review";

        static bool NeedsReview(BatchRow r) => r.Status is "NeedsReview" or "Draft" or "NeedsPhoto";
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
            if (!BatchRowHasContent(row)) { row.Status = "Draft"; row.Error = ""; }
            else if (row.Status is "Draft" or "Failed" or "NeedsPhoto")
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
            if (!BatchRowHasContent(row)) continue;   // H-B: empty rows aren't work
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
        TxtBatchStatus.Text = $"Preparing photos for {withPhoto.Count} row(s)...";
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            row.Status = "Processing";
            if (done > 0)
            {
                var avg = sw.Elapsed.TotalSeconds / done;
                var eta = TimeSpan.FromSeconds(avg * (rows.Count - done));
                TxtBatchStatus.Text = $"Preparing photos: {done} of {rows.Count}, about {eta.TotalSeconds:F0} seconds left";
            }
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
        var dir = AppSettings.SaveFolder;   // H-C: issues write to the Settings folder
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
        _closed = true;
        _previewDebounce.Stop();
        _draftTimer.Stop();
        foreach (var r in _batchRows) { r.Cutout?.Dispose(); r.WindowImage?.Dispose(); }
        // Dispose under _photoLock AND null the fields: a preview/issue render
        // may be reading these bitmaps on a thread-pool thread — disposing
        // unlocked (or leaving disposed refs) crashes SkiaSharp.
        lock (_photoLock) { _cutout?.Dispose(); _windowImage?.Dispose(); _cutout = _windowImage = null; }
        _svc.Dispose();
        base.OnClosed(e);
    }
}
