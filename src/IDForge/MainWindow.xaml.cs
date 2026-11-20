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
    private float _nudgeZoom = 1f, _nudgeX, _nudgeY;
    private bool _photoApproved;
    private Employee? _editingEmployee;

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
        CmbContract.ItemsSource = Config.ContractTypes;
        CmbContract.SelectedIndex = 1; // Permanent
        DpPrintDate.SelectedDate = Validity.TodayInDhaka().ToDateTime(TimeOnly.MinValue);

        // Batch grid combo columns are DataGridTemplateColumns now (ItemsSource
        // bound in XAML); nothing to wire here.
        DgBatch.ItemsSource = _batchRows;
        ChkConfirmIssue.IsChecked = AppSettings.ConfirmBeforeIssue;
        ChkIncludePdf.IsChecked = AppSettings.IncludePdf;

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

    private void MaybeOfferResume()
    {
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

    private void Field_Changed(object sender, SelectionChangedEventArgs e) => SchedulePreview();
    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender == TxtUid) UpdateUidError();
        SchedulePreview();
    }
    private void Field_Changed(object sender, RoutedEventArgs e) => SchedulePreview();

    private (string name, string blood, string position, string uid, string contract, DateOnly printDate) GetForm()
    {
        var name = TxtName.Text.Trim();
        var blood = CmbBlood.SelectedItem as string ?? "N/A";
        var position = CmbPosition.SelectedItem as string ?? "";
        var uid = Core.Uid.Normalize(TxtUid.Text) ?? "";
        var contract = CmbContract.SelectedItem as string ?? "Permanent";
        var printDate = DpPrintDate.SelectedDate is DateTime dt
            ? DateOnly.FromDateTime(dt) : Validity.TodayInDhaka();
        return (name, blood, position, uid, contract, printDate);
    }

    /// <summary>Rewrites the UID field to its normalized digit form on focus loss.</summary>
    private void Uid_LostFocus(object sender, RoutedEventArgs e)
    {
        var n = Core.Uid.Normalize(TxtUid.Text);
        if (n != null && n != TxtUid.Text.Trim()) TxtUid.Text = n;
        UpdateUidError();
    }

    private void UpdateUidError()
    {
        var raw = TxtUid.Text.Trim();
        if (raw.Length == 0) { ErrUid.Text = ""; return; }
        var n = Core.Uid.Normalize(raw);
        ErrUid.Text = Core.Uid.IsValid(n)
            ? $"Saved as: {n}  (barcode: UID{n})"
            : "UID must be digits only, 4-15 characters (e.g. 20250318).";
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
        TxtPhoto.Text = dlg.FileName;
        _photoPath = dlg.FileName;
        ProcessPhotoAsync(dlg.FileName);
    }

    private BgMode CurrentBgMode => (CmbBgMode.SelectedIndex) switch
    {
        1 => BgMode.Model,
        2 => BgMode.Keep,
        _ => BgMode.PlainWhite
    };

    private void BgMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Mode change reprocesses from the original file (cache makes repeat
        // modes cheap) and keeps the current nudge values.
        if (_photoPath != null && IsLoaded)
            ProcessPhotoAsync(_photoPath, keepNudge: true);
    }

    private bool _syncingNudge;

    private async void ProcessPhotoAsync(string path, bool keepNudge = false)
    {
        float keepZoom = _nudgeZoom, keepX = _nudgeX, keepY = _nudgeY;
        TxtPhotoStatus.Text = "Processing photo (background removal)...";
        BtnApprove.IsEnabled = false;
        _photoApproved = false;
        BtnSave.IsEnabled = false;
        GrpNudge.IsEnabled = false;
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
                SldZoom.Value = keepZoom; SldX.Value = keepX; SldY.Value = keepY;
            }
            else
            {
                _nudgeZoom = 1f; _nudgeX = 0; _nudgeY = 0;
                SldZoom.Value = 1; SldX.Value = 0; SldY.Value = 0;
            }
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
            GrpNudge.IsEnabled = job.Cutout != null;
            BtnApprove.IsEnabled = job.Cutout != null;
            SchedulePreview();
        }
        catch (Exception ex)
        {
            TxtPhotoStatus.Text = $"Photo failed: {ex.Message}";
        }
    }

    private void SyncNudgeText()
    {
        // XAML parse fires ValueChanged before the text boxes exist.
        if (_syncingNudge || TxtZoom == null) return;
        _syncingNudge = true;
        TxtZoom.Text = SldZoom.Value.ToString("0.##");
        TxtX.Text = ((int)SldX.Value).ToString();
        TxtY.Text = ((int)SldY.Value).ToString();
        _syncingNudge = false;
    }

    private void Nudge_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SyncNudgeText();
        if (TxtZoom == null || _cutout == null || _autoTransform == null) return;
        _nudgeZoom = (float)SldZoom.Value;
        _nudgeX = (float)SldX.Value;
        _nudgeY = (float)SldY.Value;
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
        _photoApproved = false;
        BtnApprove.IsEnabled = true;
        SchedulePreview();
    }

    private void NudgeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingNudge || _cutout == null) return;
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
        _nudgeZoom = 1; _nudgeX = 0; _nudgeY = 0;
        lock (_photoLock)
        {
            if (_cutout != null)
            {
                _windowImage?.Dispose();
                _windowImage = CardService.ComposeWindow(_cutout, _autoTransform);
            }
        }
        if (_cutout != null)
        {
            _photoApproved = false;
            BtnApprove.IsEnabled = true;
            SchedulePreview();
        }
    }

    private void ApprovePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_windowImage == null) { SetStatus("Process a photo first."); return; }
        _photoApproved = true;
        BtnSave.IsEnabled = true;
        SetStatus("Photo approved.");
    }

    private void ConfirmIssue_Changed(object sender, RoutedEventArgs e)
        => AppSettings.ConfirmBeforeIssue = ChkConfirmIssue.IsChecked == true;

    private void IncludePdf_Changed(object sender, RoutedEventArgs e)
        => AppSettings.IncludePdf = ChkIncludePdf.IsChecked == true;

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
                CmbContract.SelectedItem = existing.ContractType;
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

            // reset form
            TxtName.Clear(); TxtUid.Clear(); TxtPhoto.Clear();
            lock (_photoLock)
            {
                _cutout?.Dispose(); _windowImage?.Dispose();
                _cutout = _windowImage = null;
            }
            _photoPath = null;
            _photoApproved = false; BtnSave.IsEnabled = false; BtnApprove.IsEnabled = false;
            GrpNudge.IsEnabled = false;
            SchedulePreview();
            RefreshRecords();
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
        finally { BtnSave.IsEnabled = true; }
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
    private static List<BatchRow> ParseRows(string text)
        => BatchValidator.Parse(text).Select(d => new BatchRow
        {
            Uid = Core.Uid.Normalize(d.Uid) ?? d.Uid.Trim(), Name = d.Name, Blood = d.Blood, Position = d.Position,
            Contract = d.Contract,
            PrintDateText = string.IsNullOrWhiteSpace(d.PrintDateText)
                ? Validity.TodayInDhaka().ToString("yyyy-MM-dd") : d.PrintDateText,
            PhotoPath = d.PhotoPath
        }).ToList();

    private void BatchPaste_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) { MessageBox.Show("Clipboard is empty."); return; }
        var rows = ParseRows(text);
        foreach (var r in rows) { _batchRows.Add(r); PersistBatchRow(r); }
        TxtBatchStatus.Text = $"Pasted {rows.Count} row(s).";
    }

    private void BatchImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV files|*.csv;*.txt|All files|*.*", Title = "Import batch CSV" };
        if (dlg.ShowDialog() != true) return;
        var rows = ParseRows(File.ReadAllText(dlg.FileName));
        foreach (var r in rows) { _batchRows.Add(r); PersistBatchRow(r); }
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
    }

    // ---- process photos stage (no approval, no issuing) ----

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
