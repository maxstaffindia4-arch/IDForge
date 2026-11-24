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
using IDForge.Core;
using IDForge.Core.Batch;
using IDForge.Core.Data;
using IDForge.Core.Photo;
using Uid = IDForge.Core.Uid;
using Microsoft.Win32;
using SkiaSharp;

namespace IDForge;

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
    private readonly DispatcherTimer _persistDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _previewRequest;
    private readonly object _photoLock = new();

    public ReviewWindow(MainWindow owner, ObservableCollection<BatchRow> rows, BatchRow? startAt = null)
    {
        InitializeComponent();
        _owner = owner;
        _rows = rows;

        // Window size: remembered bounds, else 90% of work area centred on the
        // owner; below 1280x800 it opens maximised. Minimum 860x620.
        var wa = SystemParameters.WorkArea;
        var saved = AppSettings.ReviewBounds;
        if (saved is { } b && b.Width >= MinWidth && b.Height >= MinHeight
            && wa.Left <= b.Left && b.Left + b.Width <= wa.Right + 1
            && wa.Top <= b.Top && b.Top + b.Height <= wa.Bottom + 1)
        {
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        }
        else if (wa.Width < 1280 || wa.Height < 800)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            Width = wa.Width * 0.9;
            Height = wa.Height * 0.9;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _view = new ListCollectionView(rows);
        _view.Filter = FilterRow;
        LstRows.ItemsSource = _view;
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); _ = UpdatePreviewAsync(); };
        _persistDebounce.Tick += (_, _) =>
        {
            _persistDebounce.Stop();
            if (_current != null) _owner.PersistBatchRow(_current);
        };
        PosPanel.NudgeRequested += PosPanel_Nudge;
        PosPanel.OffsetCommitted += PosPanel_Offset;
        PosPanel.ZoomChanged += PosPanel_Zoom;
        PosPanel.ResetRequested += PosPanel_Reset;
        PosPanel.BgModeChanged += PosPanel_BgMode;

        CmbBlood.SelectedIndex = -1; // set per row
        DpPrintDate.SelectedDate = Validity.TodayInDhaka().ToDateTime(TimeOnly.MinValue);
        // Issue options (confirm/PDF) live in Settings on the main window now.

        var idx = startAt != null ? rows.IndexOf(startAt) : rows.ToList().FindIndex(r => r.Status == "NeedsReview");
        if (idx >= 0) SelectRow(rows[idx]);
        else TxtStatus.Text = rows.Count == 0 ? "No rows." : "No rows need review.";
    }

    internal BatchRow? CurrentForTest => _current;

    private bool FilterRow(object obj)
    {
        if (obj is not BatchRow r) return false;
        return CmbFilter.SelectedIndex switch
        {
            1 => r.Status is "NeedsReview" or "Draft" or "NeedsPhoto",
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
            PosPanel.SetBgMode(row.BgMode);
            SyncPanelFromRow();
            ValidateRowInline();
            TxtStatus.Text = $"{row.Name} ({row.Uid}) — {row.StatusText}";
            Title = string.IsNullOrWhiteSpace(row.Name)
                ? $"Batch Review — {row.Uid}"
                : $"Batch Review — {row.Name} ({row.Uid})";
        }
        finally { _loading = false; }
        _ = EnsureRowPhotoAsync(row);
    }

    /// <summary>Pushes the row's transform into the shared position panel.</summary>
    private void SyncPanelFromRow()
    {
        if (_current == null) return;
        PosPanel.SetState(_current.Zoom, _current.OffsetX, _current.OffsetY);
        PosPanel.IsEnabled = _current.Cutout != null;
    }

    /// <summary>Shared tail for every panel change: write to the row, re-render, persist.</summary>
    private void PhotoAdjusted()
    {
        if (_current?.Cutout == null || _current.AutoTransform == null) return;
        lock (_photoLock) { Recompose(_current); }
        SchedulePreview();
        SchedulePersist();
    }

    private void Row_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is BatchRow r && !ReferenceEquals(r, _current))
            SelectRow(r);
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click only selects the row (Row_Selected already ran). Approving
        // and issuing is exclusively Ctrl+Enter or the Approve & Issue button.
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
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { ApproveIssue_Click(this, e); e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_current != null) _owner.PersistBatchRow(_current);
        if (WindowState == WindowState.Normal)
            AppSettings.ReviewBounds = (Left, Top, Width, Height);
        base.OnClosed(e);
    }

    // ---------------- field <-> row sync ----------------

    private void SchedulePersist()
    {
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); SchedulePersist(); } }
    private void Field_Changed(object sender, TextChangedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); SchedulePersist(); } }
    private void Field_Changed(object sender, RoutedEventArgs e) { if (!_loading) { SyncFormToRow(); ValidateRowInline(); SchedulePreview(); SchedulePersist(); } }

    private void SyncFormToRow()
    {
        if (_current == null) return;
        _current.Name = TxtName.Text.Trim();
        _current.Uid = Core.Uid.Normalize(TxtUid.Text) ?? TxtUid.Text.Trim();
        _current.Blood = CmbBlood.SelectedItem as string ?? "N/A";
        _current.Position = CmbPosition.SelectedItem as string ?? "";
        _current.Contract = CmbContract.SelectedItem as string ?? "Permanent";
        _current.PrintDateText = DpPrintDate.SelectedDate is DateTime dt
            ? DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd")
            : Validity.TodayInDhaka().ToString("yyyy-MM-dd");
    }

    /// <summary>Rewrites pasted "UID20250318"/"ID. 20250318" to bare digits on focus loss.</summary>
    private void Uid_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = Core.Uid.Normalize(TxtUid.Text);
        if (n != null && n != TxtUid.Text.Trim()) TxtUid.Text = n;
    }

    private void ValidateRowInline()
    {
        if (_current == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
            if (!ReferenceEquals(r, _current) && Core.Uid.Normalize(r.Uid) is { } nu) seen.Add(nu);
        var err = BatchValidator.Validate(
            new BatchRowData(_current.Uid, _current.Name, _current.Blood, _current.Position,
                _current.Contract, _current.PrintDateText, _current.PhotoPath),
            seen, uid => _owner.Svc.Store.FindEmployeeByUid(uid) != null,
            checkPhoto: false); // photo presence checked separately below
        ErrName.Text = err is "name required" or "UID required" ? err : "";
        ErrUid.Text = err switch
        {
            "UID must be digits only (4-15)" => "Digits only, 4-15 characters.",
            "UID already exists" or "duplicate UID in batch" => err,
            _ => ""
        };
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
                PosPanel.IsEnabled = true;
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
            PosPanel.IsEnabled = false;
            SchedulePreview();
        }
        else
        {
            PosPanel.IsEnabled = row.Cutout != null;
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

    private void PosPanel_Nudge(int dx, int dy)
    {
        if (_current == null) return;
        _current.OffsetX = Math.Clamp(_current.OffsetX + dx, -320, 320);
        _current.OffsetY = Math.Clamp(_current.OffsetY + dy, -400, 400);
        PosPanel.SetState(_current.Zoom, _current.OffsetX, _current.OffsetY);
        PhotoAdjusted();
    }

    private void PosPanel_Offset(double x, double y)
    {
        if (_current == null) return;
        _current.OffsetX = Math.Clamp(x, -320, 320);
        _current.OffsetY = Math.Clamp(y, -400, 400);
        PosPanel.SetState(_current.Zoom, _current.OffsetX, _current.OffsetY);
        PhotoAdjusted();
    }

    private void PosPanel_Zoom(double z)
    {
        if (_current == null) return;
        _current.Zoom = Math.Clamp(z, 0.4, 3.0);
        PosPanel.SetState(_current.Zoom, _current.OffsetX, _current.OffsetY);
        PhotoAdjusted();
    }

    private void PosPanel_Reset()
    {
        if (_current == null) return;
        _current.Zoom = 1; _current.OffsetX = 0; _current.OffsetY = 0;
        PosPanel.SetState(1, 0, 0);
        PhotoAdjusted();
    }

    private void PosPanel_BgMode(string tag)
    {
        if (_loading || _current == null) return;
        _current.BgMode = tag;   // recorded even without a photo; reprocess needs one
        if (_current.PhotoPath == null) return;
        _ = ProcessRowPhotoAsync(_current, keepNudge: true);
    }

    private async Task ProcessRowPhotoAsync(BatchRow row, bool keepNudge)
    {
        if (row.PhotoPath == null) return;
        float kZ = (float)row.Zoom, kX = (float)row.OffsetX, kY = (float)row.OffsetY;
        var mode = row.BgMode switch { "Model" => BgMode.Model, "Keep" => BgMode.Keep, _ => BgMode.PlainWhite };
        TxtPhotoStatus.Text = "Processing photo...";
        PosPanel.IsEnabled = false;
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
            if (ReferenceEquals(row, _current)) { SyncPanelFromRow(); PosPanel.IsEnabled = job.Cutout != null; }
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

    /// <summary>Shared pre-issue checks for one row. Returns an error message or null.</summary>
    private string? ValidateRowForIssue(BatchRow row)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
            if (!ReferenceEquals(r, row) && Core.Uid.Normalize(r.Uid) is { } nu) seen.Add(nu);
        var err = BatchValidator.Validate(
            new BatchRowData(row.Uid, row.Name, row.Blood, row.Position, row.Contract,
                row.PrintDateText, row.PhotoPath),
            seen, uid => _owner.Svc.Store.FindEmployeeByUid(uid) != null);
        if (err != null) return $"Cannot issue: {err}.";
        if (row.Cutout == null)
            return "Photo not processed yet — wait for processing or use Replace.";
        return null;
    }

    /// <summary>
    /// Issues one row through the owner's single issue path. confirm=false skips
    /// the per-row dialog (batch issue confirms once up front); quiet=true
    /// suppresses per-row result dialogs (batch shows a summary instead).
    /// </summary>
    internal async Task<IDForge.Core.Issuing.IssueOutcome?> IssueRowAsync(BatchRow row, bool confirm, bool quiet)
    {
        if (row.Status == "Issued") return null;   // single-issue rule
        var problem = ValidateRowForIssue(row);
        if (problem != null) { TxtStatus.Text = problem; return null; }

        var validUntil = Validity.CalculateValidUntil(row.PrintDate, row.Contract);
        var uidNorm = Core.Uid.Normalize(row.Uid);
        var isReissue = uidNorm != null && _owner.Svc.Store.FindEmployeeByUid(uidNorm) != null;
        if (confirm && !Dialogs.ConfirmIssue(this, row.Name, row.Uid, validUntil, isReissue))
            return null;

        // Approved is transient: set it only once the issue is really going ahead.
        row.Approved = true;
        row.Status = "Approved";
        _owner.PersistBatchRow(row);

        row.AttemptCount++;
        _owner.PersistBatchRow(row);           // persist attempt so RowId/key are stable
        var key = $"batchrow:{row.RowId}:attempt:{row.AttemptCount}";
        var outcome = await _owner.IssueBatchRowAsync(row, key);
        if (outcome.Success)
        {
            row.Status = "Issued";
            row.Error = "";
            row.CardIssueId = outcome.Issue?.Id;
            _owner.PersistBatchRow(row);
            if (!quiet)
            {
                var goNext = ShowIssueSuccess(row, outcome, outcome.Issue?.ValidUntil ?? validUntil);
                TxtStatus.Text = $"Issued {rowLabel(row)}.";
                if (goNext) Move(1);
            }
            _owner.RefreshRecordsPublic();
        }
        else
        {
            row.Status = "Failed";
            row.Error = outcome.Error ?? "unknown";
            _owner.PersistBatchRow(row);
            if (!quiet)
                Dialogs.ShowIssueFailed(this, outcome.Step ?? "issue", outcome.Error ?? "unknown");
        }
        return outcome;
    }

    /// <summary>"Saved to Records…" dialog with Open folder / Next card buttons.</summary>
    private bool ShowIssueSuccess(BatchRow row, IDForge.Core.Issuing.IssueOutcome outcome, DateOnly validUntil)
    {
        var next = false;
        var dlg = new Window
        {
            Owner = this, Title = "Card issued", Width = 460,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var files = outcome.PdfPath != null ? $"{outcome.PngPath}\n{outcome.PdfPath}" : outcome.PngPath;
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Saved to Records: {row.Uid}, valid until {validUntil:dd/MM/yyyy}.\nFiles:\n{files}",
            Margin = new Thickness(0, 0, 0, 14)
        });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var bOpen = new Button { Content = "Open folder", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        bOpen.Click += (_, _) =>
        {
            try
            {
                if (outcome.PngPath != null)
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outcome.PngPath}\"");
            }
            catch { /* best effort */ }
        };
        var bNext = new Button { Content = "Next card", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        bNext.Click += (_, _) => { next = true; dlg.Close(); };
        btns.Children.Add(bOpen);
        btns.Children.Add(bNext);
        panel.Children.Add(btns);
        dlg.Content = panel;
        dlg.ShowDialog();
        return next;
    }

    /// <summary>Approves and issues the current row in one step (same issue path as Batch).</summary>
    private async void ApproveIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SyncFormToRow();
        ValidateRowInline();
        var row = _current;
        if (row.Status == "Issued") { TxtStatus.Text = $"{rowLabel(row)} already issued."; return; }

        BtnApproveIssue.IsEnabled = false;
        TxtStatus.Text = "Issuing...";
        try { await IssueRowAsync(row, confirm: true, quiet: false); }
        finally { BtnApproveIssue.IsEnabled = true; }
    }

    // Issue-option checkboxes removed in Phase 2 — settings live on the main window.

    private static string rowLabel(BatchRow r) => string.IsNullOrWhiteSpace(r.Name) ? r.Uid : $"{r.Name} ({r.Uid})";
}

/// <summary>Shared issue confirmations + result dialogs (plan §6).</summary>
public static class Dialogs
{
    /// <summary>Pre-issue confirm. Honors the 'confirm before issue' setting;
    /// always shows for re-issue / UID change.</summary>
    /// <summary>Test hook: replaces the modal confirm. Args: owner,name,uid,validUntil,isReissue.</summary>
    internal static Func<Window, string, string, DateOnly, bool, bool>? ConfirmOverride;

    public static bool ConfirmIssue(Window owner, string name, string uid, DateOnly validUntil, bool isReissue)
    {
        if (!isReissue && !AppSettings.ConfirmBeforeIssue) return true;
        if (ConfirmOverride != null) return ConfirmOverride(owner, name, uid, validUntil, isReissue);
        var r = MessageBox.Show(owner,
            $"Issue card for {name}, UID {uid}, valid until {validUntil:MMMM yyyy}?",
            "Confirm issue", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public static void ShowIssued(Window owner, string uid, DateOnly validUntil, string? png, string? pdf)
    {
        var files = pdf != null ? $"{png}\n{pdf}" : png;
        var msg = $"Saved to Records: {uid}, valid until {validUntil:yyyy-MM-dd}.\nFiles:\n{files}";
        MessageBox.Show(owner, msg, "Card issued", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void ShowIssueFailed(Window owner, string step, string reason)
    {
        MessageBox.Show(owner, $"Issue failed at step '{step}':\n{reason}\n\nNothing was saved to Records.",
            "Issue failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

// AppSettings moved to src/IDForge/AppSettings.cs (extended for Phase 2).
