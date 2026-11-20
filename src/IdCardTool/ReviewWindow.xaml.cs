using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdCardTool.Core;
using IdCardTool.Core.Batch;
using IdCardTool.Core.Data;
using IdCardTool.Core.Photo;
using Microsoft.Win32;
using SkiaSharp;

namespace IdCardTool;

/// <summary>
/// Batch Review screen (plan §4): row list + filter, full card preview,
/// editable fields + photo panel, Prev/Save draft/Skip/Approve & Issue/Next.
/// Per-row photo adjustments live on the BatchRow and survive navigation.
/// </summary>
public partial class ReviewWindow : Window
{
    private readonly MainWindow _owner;
    private readonly ObservableCollection<BatchRow> _rows;
    private readonly ListCollectionView _view;
    private BatchRow? _current;
    private bool _loading;

    // preview debounce + stale-render guard + photo lock (same pattern as MainWindow)
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _previewRequest;
    private readonly object _photoLock = new();
    private bool _syncingNudge;

    public ReviewWindow(MainWindow owner, ObservableCollection<BatchRow> rows, BatchRow? startAt = null)
    {
        InitializeComponent();
        _owner = owner;
        _rows = rows;
        _view = new ListCollectionView(rows);
        _view.Filter = FilterRow;
        LstRows.ItemsSource = _view;
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); _ = UpdatePreviewAsync(); };

        CmbBlood.SelectedIndex = -1; // set per row
        DpPrintDate.SelectedDate = Validity.TodayInDhaka().ToDateTime(TimeOnly.MinValue);
        ChkConfirmIssue.IsChecked = AppSettings.ConfirmBeforeIssue;

        var idx = startAt != null ? rows.IndexOf(startAt) : rows.ToList().FindIndex(r => r.Status == "NeedsReview");
        if (idx >= 0) SelectRow(rows[idx]);
        else TxtStatus.Text = rows.Count == 0 ? "No rows." : "No rows need review.";
    }

    private bool FilterRow(object obj)
    {
        if (obj is not BatchRow r) return false;
        return CmbFilter.SelectedIndex switch
        {
            1 => r.Status == "NeedsReview",
            2 => r.Status == "Failed",
            3 => r.Status == "Issued",
            4 => r.Status == "Skipped",
            _ => true
        };
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent (IsSelected) before _view exists.
        if (_view == null) return;
        try
        {
            _view.Refresh();
            if (_current != null && !_view.Contains(_current) && _view.Cast<BatchRow>().Any())
                SelectRow(_view.Cast<BatchRow>().First());
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    // ---------------- row navigation ----------------

    private void SelectRow(BatchRow row)
    {
        try { SelectRowCore(row); }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            TxtStatus.Text = $"Could not load row: {ex.Message}";
        }
    }

    private void SelectRowCore(BatchRow row)
    {
        if (_current != null && !ReferenceEquals(_current, row))
            _owner.PersistBatchRow(_current);   // save outgoing row as-is
        _current = row;
        _loading = true;
        try
        {
            LstRows.SelectedItem = row;
            TxtName.Text = row.Name;
            TxtUid.Text = row.Uid;
            CmbBlood.SelectedItem = row.Blood;
            CmbPosition.SelectedItem = row.Position;
            CmbContract.SelectedItem = row.Contract;
            if (DateOnly.TryParse(row.PrintDateText, out var pd))
                DpPrintDate.SelectedDate = pd.ToDateTime(TimeOnly.MinValue);
            TxtPhoto.Text = row.PhotoPath ?? "";
            CmbBgMode.SelectedIndex = row.BgMode switch
            {
                "Model" => 1, "Keep" => 2, _ => 0
            };
            SyncNudgeFromRow();
            ValidateRowInline();
            TxtStatus.Text = $"Row {row.Uid} — {row.StatusText}";
        }
        finally { _loading = false; }
        _ = EnsureRowPhotoAsync(row);
    }

    private void SyncNudgeFromRow()
    {
        _syncingNudge = true;
        if (_current != null)
        {
            SldZoom.Value = Math.Clamp(_current.Zoom, SldZoom.Minimum, SldZoom.Maximum);
            SldX.Value = Math.Clamp(_current.OffsetX, SldX.Minimum, SldX.Maximum);
            SldY.Value = Math.Clamp(_current.OffsetY, SldY.Minimum, SldY.Maximum);
        }
        _syncingNudge = false;
        SyncNudgeText();
    }

    private void Row_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is BatchRow r && !ReferenceEquals(r, _current))
            SelectRow(r);
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click = quick approve&issue for the selected row.
        Issue_Click(sender, e);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int dir)
    {
        var list = _view.Cast<BatchRow>().ToList();
        if (list.Count == 0 || _current == null) return;
        var i = list.IndexOf(_current);
        var j = i + dir;
        if (j < 0 || j >= list.Count) return;
        SelectRow(list[j]);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.OriginalSource is TextBoxBase or ComboBox or DatePicker or Slider or Button) return;
        if (e.Key == Key.Left) { Move(-1); e.Handled = true; }
        else if (e.Key == Key.Right) { Move(1); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Issue_Click(this, e); e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_current != null) _owner.PersistBatchRow(_current);
        base.OnClosed(e);
    }

    // ---------------- field <-> row sync ----------------

    private void Field_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); } }
    private void Field_Changed(object sender, TextChangedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); } }
    private void Field_Changed(object sender, RoutedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); } }

    private void SyncFormToRow()
    {
        if (_current == null) return;
        _current.Name = TxtName.Text.Trim();
        _current.Uid = TxtUid.Text.Trim();
        _current.Blood = CmbBlood.SelectedItem as string ?? "N/A";
        _current.Position = CmbPosition.SelectedItem as string ?? "";
        _current.Contract = CmbContract.SelectedItem as string ?? "Permanent";
        _current.PrintDateText = DpPrintDate.SelectedDate is DateTime dt
            ? DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd")
            : Validity.TodayInDhaka().ToString("yyyy-MM-dd");
    }

    private void ValidateRowInline()
    {
        if (_current == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows) if (!ReferenceEquals(r, _current)) seen.Add(r.Uid);
        var err = BatchValidator.Validate(
            new BatchRowData(_current.Uid, _current.Name, _current.Blood, _current.Position,
                _current.Contract, _current.PrintDateText, _current.PhotoPath),
            seen, uid => _owner.Svc.Store.FindEmployeeByUid(uid) != null,
            checkPhoto: false); // photo presence checked separately below
        ErrName.Text = err is "name required" or "UID required" or "UID too short" ? err : "";
        ErrUid.Text = err is "UID already exists" or "duplicate UID in batch" ? err : "";
        ErrPosition.Text = err == "bad position" ? "Select one of the listed positions." : "";
        ErrPrintDate.Text = err == "bad print date" ? "Invalid date." : "";
        if (_current.PhotoPath == null || !File.Exists(_current.PhotoPath))
            TxtPhotoStatus.Text = "Photo missing — use Replace.";
    }

    // ---------------- photo ----------------

    private async Task EnsureRowPhotoAsync(BatchRow row)
    {
        try { await EnsureRowPhotoCoreAsync(row); }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            TxtPhotoStatus.Text = $"Photo error: {ex.Message}";
        }
    }

    private async Task EnsureRowPhotoCoreAsync(BatchRow row)
    {
        if (row.Cutout != null) { Recompose(row); SchedulePreview(); return; }
        if (row.CutoutPath != null && File.Exists(row.CutoutPath))
        {
            var bmp = SKBitmap.Decode(row.CutoutPath);
            if (bmp != null)
            {
                lock (_photoLock)
                {
                    row.Cutout?.Dispose();
                    row.Cutout = bmp;
                    row.AutoTransform = new WindowTransform(
                        (float)row.AutoScale, (float)row.AutoOffsetX, (float)row.AutoOffsetY);
                    Recompose(row);
                }
                GrpNudge.IsEnabled = true;
                SchedulePreview();
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(row.PhotoPath) && File.Exists(row.PhotoPath)
            && (row.Status == "NeedsReview" || row.Status == "Failed" || row.Status == "Draft"))
        {
            await ProcessRowPhotoAsync(row, keepNudge: true);
        }
        else if (string.IsNullOrWhiteSpace(row.PhotoPath))
        {
            TxtPhotoStatus.Text = "Row has no photo. Add a photo before review.";
            GrpNudge.IsEnabled = false;
            SchedulePreview();
        }
        else
        {
            GrpNudge.IsEnabled = row.Cutout != null;
            SchedulePreview();
        }
    }

    private void Recompose(BatchRow row)
    {
        if (row.Cutout == null || row.AutoTransform == null) return;
        var t = new WindowTransform(
            row.AutoTransform.Scale * (float)row.Zoom,
            row.AutoTransform.OffsetX + (float)row.OffsetX,
            row.AutoTransform.OffsetY + (float)row.OffsetY);
        row.WindowImage?.Dispose();
        row.WindowImage = CardService.ComposeWindow(row.Cutout, t);
    }

    private async void BrowsePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png", Title = "Select staff photo" };
        if (dlg.ShowDialog() != true) return;
        _current.PhotoPath = dlg.FileName;
        TxtPhoto.Text = dlg.FileName;
        await ProcessRowPhotoAsync(_current, keepNudge: false);
    }

    private async void BgMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _current == null || _current.PhotoPath == null) return;
        _current.BgMode = (CmbBgMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "PlainWhite";
        await ProcessRowPhotoAsync(_current, keepNudge: true);
    }

    private async Task ProcessRowPhotoAsync(BatchRow row, bool keepNudge)
    {
        if (row.PhotoPath == null) return;
        float kZ = (float)row.Zoom, kX = (float)row.OffsetX, kY = (float)row.OffsetY;
        var mode = row.BgMode switch { "Model" => BgMode.Model, "Keep" => BgMode.Keep, _ => BgMode.PlainWhite };
        TxtPhotoStatus.Text = "Processing photo...";
        GrpNudge.IsEnabled = false;
        try
        {
            var job = await Task.Run(() => _owner.Svc.ProcessPhoto(row.PhotoPath, mode));
            lock (_photoLock)
            {
                row.Cutout?.Dispose(); row.WindowImage?.Dispose();
                row.Cutout = job.Cutout;
                row.AutoTransform = job.AutoTransform;
                row.BgMethod = job.BgMethod;
                row.Warning = job.Warning;
                if (keepNudge) { row.Zoom = kZ; row.OffsetX = kX; row.OffsetY = kY; }
                else { row.Zoom = 1; row.OffsetX = 0; row.OffsetY = 0; }
                if (job.AutoTransform != null)
                {
                    row.AutoScale = job.AutoTransform.Scale;
                    row.AutoOffsetX = job.AutoTransform.OffsetX;
                    row.AutoOffsetY = job.AutoTransform.OffsetY;
                }
                Recompose(row);
            }
            // persist cutout for resume
            if (row.Cutout != null)
            {
                var cp = _owner.Svc.CachePhoto(row.Cutout, $"B{row.RowId}");
                row.CutoutPath = cp;
            }
            var bgNote = job.BgMethod switch
            {
                "skipped-transparent" => "Background already transparent — removal skipped.",
                "skipped-manual" => "Background removal off — photo used as-is.",
                "plain-white" => "Plain-white background removed.",
                "fallback" => "Background removal unavailable — photo used as-is.",
                _ => null
            };
            TxtPhotoStatus.Text = string.Join(" ", new[] { bgNote, job.Warning ?? "Photo ready — adjust and approve." }
                .Where(s => s != null));
            if (ReferenceEquals(row, _current)) { SyncNudgeFromRow(); GrpNudge.IsEnabled = job.Cutout != null; }
            if (row.Status is "Draft" or "NeedsPhoto" or "Failed") row.Status = "NeedsReview";
            row.Error = "";
            _owner.PersistBatchRow(row);
            SchedulePreview();
        }
        catch (Exception ex)
        {
            row.Status = "Failed"; row.Error = ex.Message;
            TxtPhotoStatus.Text = $"Photo failed: {ex.Message}";
            _owner.PersistBatchRow(row);
        }
    }

    // ---- nudge (same transform model as New Card tab) ----

    private void SyncNudgeText()
    {
        if (_syncingNudge || TxtZoom == null || _current == null) return;
        _syncingNudge = true;
        TxtZoom.Text = SldZoom.Value.ToString("0.##");
        TxtX.Text = ((int)SldX.Value).ToString();
        TxtY.Text = ((int)SldY.Value).ToString();
        _syncingNudge = false;
    }

    private void Nudge_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SyncNudgeText();
        if (TxtZoom == null || _current?.Cutout == null || _current.AutoTransform == null) return;
        _current.Zoom = SldZoom.Value;
        _current.OffsetX = SldX.Value;
        _current.OffsetY = SldY.Value;
        lock (_photoLock) { Recompose(_current); }
        SchedulePreview();
    }

    private void NudgeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingNudge || _current?.Cutout == null) return;
        if (sender is not TextBox tb || !double.TryParse(tb.Text, out var v)) return;
        _syncingNudge = true;
        switch (tb.Tag as string)
        {
            case "zoom": SldZoom.Value = Math.Clamp(v, SldZoom.Minimum, SldZoom.Maximum); break;
            case "x": SldX.Value = Math.Clamp(v, SldX.Minimum, SldX.Maximum); break;
            case "y": SldY.Value = Math.Clamp(v, SldY.Minimum, SldY.Maximum); break;
        }
        _syncingNudge = false;
        SyncNudgeText();
    }

    private void ResetNudge_Click(object sender, RoutedEventArgs e)
    {
        _syncingNudge = true;
        SldZoom.Value = 1; SldX.Value = 0; SldY.Value = 0;
        TxtZoom.Text = "1"; TxtX.Text = "0"; TxtY.Text = "0";
        _syncingNudge = false;
        if (_current != null)
        {
            _current.Zoom = 1; _current.OffsetX = 0; _current.OffsetY = 0;
            lock (_photoLock) { if (_current.Cutout != null) Recompose(_current); }
            SchedulePreview();
        }
    }

    // ---------------- preview ----------------

    private void SchedulePreview()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        var row = _current;
        if (row == null) { ImgPreview.Source = null; TxtPreviewHint.Visibility = Visibility.Visible; return; }
        var f = (row.Name, row.Blood, row.Position, row.Uid, row.Contract, row.PrintDate);
        if (f.Name.Length < 2 || f.Uid.Length < 3)
        {
            ImgPreview.Source = null;
            TxtPreviewHint.Visibility = Visibility.Visible;
            return;
        }
        var req = ++_previewRequest;
        byte[]? pixels = null; int pw = 0, ph = 0;
        try
        {
            (pixels, pw, ph) = await Task.Run(() =>
            {
                SKBitmap? win;
                lock (_photoLock) { win = row.WindowImage; }
                using var card = CardService.RenderCard(f.Name, f.Blood, f.Position,
                    f.Uid, f.Contract, f.PrintDate, win);
                return (card.GetPixelSpan().ToArray(), card.Width, card.Height);
            });
        }
        catch (Exception ex)
        {
            if (req == _previewRequest) TxtStatus.Text = $"Preview error: {ex.Message}";
            return;
        }
        if (req != _previewRequest || pixels == null) return;
        ImgPreview.Source = ToWriteableBitmap(pixels, pw, ph);
        TxtPreviewHint.Visibility = Visibility.Collapsed;
    }

    private static WriteableBitmap ToWriteableBitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        wb.Freeze();
        return wb;
    }

    // ---------------- row actions ----------------

    private void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SyncFormToRow();
        _owner.PersistBatchRow(_current);
        TxtStatus.Text = $"Draft saved for {rowLabel(_current)}.";
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SyncFormToRow();
        _current.Status = "Skipped";
        _owner.PersistBatchRow(_current);
        TxtStatus.Text = $"Skipped {rowLabel(_current)}.";
        Move(1);
    }

    private async void Issue_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SyncFormToRow();
        ValidateRowInline();
        var row = _current;
        if (row.Status == "Issued") { TxtStatus.Text = $"{rowLabel(row)} already issued."; return; }

        // inline validation (photo presence enforced too)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows) if (!ReferenceEquals(r, row)) seen.Add(r.Uid);
        var err = BatchValidator.Validate(
            new BatchRowData(row.Uid, row.Name, row.Blood, row.Position, row.Contract,
                row.PrintDateText, row.PhotoPath),
            seen, uid => _owner.Svc.Store.FindEmployeeByUid(uid) != null);
        if (err != null) { TxtStatus.Text = $"Cannot issue: {err}."; return; }
        if (row.Cutout == null)
        {
            TxtStatus.Text = "Photo not processed yet — wait for processing or use Replace.";
            return;
        }

        var validUntil = Validity.CalculateValidUntil(row.PrintDate, row.Contract);
        if (!Dialogs.ConfirmIssue(this, row.Name, row.Uid, validUntil, isReissue: false))
            return;

        row.AttemptCount++;
        _owner.PersistBatchRow(row);           // persist attempt so RowId/key are stable
        var key = $"batchrow:{row.RowId}:attempt:{row.AttemptCount}";
        BtnIssue.IsEnabled = false;
        TxtStatus.Text = "Issuing...";
        try
        {
            var outcome = await _owner.IssueBatchRowAsync(row, key);
            if (outcome.Success)
            {
                row.Status = "Issued";
                row.Error = "";
                row.CardIssueId = outcome.Issue?.Id;
                _owner.PersistBatchRow(row);
                Dialogs.ShowIssued(this, row.Uid, outcome.Issue?.ValidUntil ?? validUntil,
                    outcome.PngPath, outcome.PdfPath);
                _owner.RefreshRecordsPublic();
                TxtStatus.Text = $"Issued {rowLabel(row)}.";
            }
            else
            {
                row.Status = "Failed";
                row.Error = outcome.Error ?? "unknown";
                _owner.PersistBatchRow(row);
                Dialogs.ShowIssueFailed(this, outcome.Step ?? "issue", outcome.Error ?? "unknown");
                TxtStatus.Text = $"Issue failed ({outcome.Step}): {outcome.Error}";
            }
        }
        finally { BtnIssue.IsEnabled = true; }
    }

    private void ConfirmIssue_Changed(object sender, RoutedEventArgs e)
        => AppSettings.ConfirmBeforeIssue = ChkConfirmIssue.IsChecked == true;

    private static string rowLabel(BatchRow r) => string.IsNullOrWhiteSpace(r.Name) ? r.Uid : $"{r.Name} ({r.Uid})";
}

/// <summary>Shared issue confirmations + result dialogs (plan §6).</summary>
public static class Dialogs
{
    /// <summary>Pre-issue confirm. Honors the 'confirm before issue' setting;
    /// always shows for re-issue / UID change.</summary>
    public static bool ConfirmIssue(Window owner, string name, string uid, DateOnly validUntil, bool isReissue)
    {
        if (!isReissue && !AppSettings.ConfirmBeforeIssue) return true;
        var r = MessageBox.Show(owner,
            $"Issue card for {name}, UID {uid}, valid until {validUntil:MMMM yyyy}?",
            "Confirm issue", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public static void ShowIssued(Window owner, string uid, DateOnly validUntil, string? png, string? pdf)
    {
        var msg = $"Saved to Records: {uid}, valid until {validUntil:yyyy-MM-dd}.\n" +
                  $"Files:\n{png}\n{pdf}";
        MessageBox.Show(owner, msg, "Card issued", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void ShowIssueFailed(Window owner, string step, string reason)
    {
        MessageBox.Show(owner, $"Issue failed at step '{step}':\n{reason}\n\nNothing was saved to Records.",
            "Issue failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

/// <summary>Tiny persisted settings (app dir settings.json).</summary>
public static class AppSettings
{
    private static readonly string Path0 = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static bool _confirm = true;
    static AppSettings() => Load();

    public static bool ConfirmBeforeIssue
    {
        get => _confirm;
        set { _confirm = value; Save(); }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(Path0))
                _confirm = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, bool>>(File.ReadAllText(Path0))
                    ?.GetValueOrDefault("confirmBeforeIssue", true) ?? true;
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(Path0,
                System.Text.Json.JsonSerializer.Serialize(
                    new Dictionary<string, bool> { ["confirmBeforeIssue"] = _confirm }));
        }
        catch { }
    }
}
