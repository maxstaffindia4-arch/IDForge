using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;
using IDForge.Core.Data;

namespace IDForge.Tests;

/// <summary>BATCH_VISUAL_PARITY_PLAN acceptance: header/progress layout at the
/// §2/§3 sizes, saved-batch behaviour, progress-card counts, window title,
/// and the committed header screenshots.</summary>
[Collection("Wpf")]
public class BatchParityTests
{
    private static MainWindow NewShownBatch(int w, int h)
    {
        var win = FixPassTests.NewWindow();
        win.Show();
        win.ShowArea("batch");
        win.MaxWidth = double.PositiveInfinity;
        win.MaxHeight = double.PositiveInfinity;
        win.Width = w; win.Height = h;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = 40; win.Top = 40;
        win.UpdateLayout();
        return win;
    }

    private static string ShotsDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "docs", "redesign", "shots"));

    private static void SettleThenClose(MainWindow win)
    {
        for (int i = 0; i < 20; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        win.Close();
    }

    private static void SaveWindow(MainWindow win, string path)
    {
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(win.ActualWidth), (int)Math.Ceiling(win.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in VisualDescendants(child)) yield return d;
        }
    }

    private static T Find<T>(Window win, string name) where T : FrameworkElement
        => (T)win.FindName(name)!;

    private static Rect OnScreenRect(FrameworkElement el, Window win)
    {
        var r = el.TransformToAncestor(win).TransformBounds(
            new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        return new Rect(r.X + win.Left, r.Y + win.Top, r.Width, r.Height);
    }

    /// <summary>Adds a content row directly (bypasses the store) with a status.</summary>
    private static BatchRow AddRow(MainWindow win, string uid, string name, string status)
    {
        var row = new BatchRow { Uid = uid, Name = name, Status = status,
            Position = "JE", Contract = "Permanent",
            PrintDateText = "2026-12-01" };
        win.BatchRows.Add(row);
        return row;
    }

    [Fact]
    public void BatchHeader_Elements_Inside_Window_At_All_Sizes()
    {
        WpfSta.Run(() =>
        {
            foreach (var (w, h) in new[] { (1280, 800), (1100, 700), (860, 620) })
            {
                var win = NewShownBatch(w, h);
                AddRow(win, "75000001", "Bounds Person", "NeedsReview");
                win.UpdateLayout();
                try
                {
                    foreach (var name in new[]
                    {
                        "TxtBatchName", "BtnRenameBatch", "TxtEmployeeCount",
                        "TxtSaved", "BtnBatches", "BtnNewBatch",
                        "TxtIssuedCount", "TxtRemaining", "GridProgress",
                        "PnlBatchLegend", "BtnReview", "TxtReviewReason"
                    })
                    {
                        var el = win.FindName(name) as FrameworkElement;
                        Assert.True(el != null, $"{name} missing at {w}x{h}");
                        Assert.True(el!.IsVisible, $"{name} not visible at {w}x{h}");
                        var r = OnScreenRect(el, win);
                        Assert.True(r.Width > 0 && r.Height > 0, $"{name} empty rect at {w}x{h}");
                        Assert.True(r.Right <= win.Width + 1 && r.Bottom <= win.Height + 1,
                            $"{name} outside window at {w}x{h}: {r}");
                    }
                }
                finally { SettleThenClose(win); }
            }
        });
    }

    [Fact]
    public void Batch_Progress_Card_Counts_And_Bar()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                // empty batch
                Assert.Equal("No rows yet", Find<TextBlock>(win, "TxtIssuedCount").Text);
                Assert.Equal("", Find<TextBlock>(win, "TxtRemaining").Text);

                AddRow(win, "71000001", "Issued One", "Issued");
                AddRow(win, "71000002", "Review Two", "NeedsReview");
                AddRow(win, "71000003", "Bad Three", "Failed");
                AddRow(win, "71000004", "Skip Four", "Skipped");
                win.BatchRows.Add(new BatchRow { Status = "Draft" });   // truly empty: not counted
                win.UpdateLayout();

                var counts = Find<TextBlock>(win, "TxtIssuedCount").Text;
                Assert.Equal("1 of 4 issued", counts);
                Assert.Equal("— 1 to review", Find<TextBlock>(win, "TxtRemaining").Text);
                Assert.Equal("4 employees", Find<TextBlock>(win, "TxtEmployeeCount").Text);

                // bar: issued=1, review=1, attention=1, processing=0
                var grid = Find<Grid>(win, "GridProgress");
                var cols = grid.ColumnDefinitions;
                Assert.Equal(new GridLength(1, GridUnitType.Star), cols[0].Width);
                Assert.Equal(new GridLength(1, GridUnitType.Star), cols[1].Width);
                Assert.Equal(new GridLength(1, GridUnitType.Star), cols[2].Width);
                Assert.Equal(new GridLength(0, GridUnitType.Star), cols[3].Width);

                // legend has 5 items
                var legend = Find<WrapPanel>(win, "PnlBatchLegend");
                Assert.Equal(5, legend.Children.Count);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Saved_Batches_Rename_New_And_Switch()
    {
        WpfSta.Run(() =>
        {
            MainWindow.BatchNamePrompt = (_, initial) => initial == "June starters" ? null : "June starters";
            var win = NewShownBatch(1280, 800);
            BatchSession? first = null;
            try
            {
                AddRow(win, "72000001", "Person One", "Draft");
                win.PersistBatchRow(win.BatchRows[0]);
                first = win.Svc.Store.LatestOpenSession();
                Assert.NotNull(first);

                // rename
                MainWindow.BatchNamePrompt = (_, _) => "June starters";
                ((Button)win.FindName("BtnRenameBatch")!).RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("June starters", win.Svc.Store.BatchSession(first!.Id)!.Name);
                Assert.Equal("June starters", Find<TextBlock>(win, "TxtBatchName").Text);
                Assert.Contains("Batch: June starters", win.Title);

                // new batch
                MainWindow.BatchNamePrompt = (_, _) => "July starters";
                ((Button)win.FindName("BtnNewBatch")!).RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(0, win.BatchRows.Count);
                Assert.Equal("July starters", Find<TextBlock>(win, "TxtBatchName").Text);
                var second = win.Svc.Store.BatchSessions().First(s => s.Name == "July starters");
                Assert.NotEqual(first.Id, second.Id);

                // switch back via the dropdown path
                win.OpenBatchSession(first.Id);
                Assert.Single(win.BatchRows);
                Assert.Equal("June starters", Find<TextBlock>(win, "TxtBatchName").Text);

                // summaries carry row counts
                var summaries = win.Svc.Store.BatchSessionSummaries();
                Assert.Contains(summaries, s => s.Id == first.Id && s.RowCount == 1);
                Assert.Contains(summaries, s => s.Id == second.Id && s.RowCount == 0);
            }
            finally
            {
                MainWindow.BatchNamePrompt = null;
                try
                {
                    foreach (var s in win.Svc.Store.BatchSessions())
                    {
                        foreach (var e in win.Svc.Store.BatchRows(s.Id))
                            win.Svc.Store.DeleteBatchRow(e.Id);
                        win.Svc.Store.CompleteBatchSession(s.Id);
                    }
                }
                catch { }
                SettleThenClose(win);
            }
        });
    }

    [Fact]
    public void Batch_Saved_Indicator_Updates_On_Persist()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            BatchSession? sess = null;
            try
            {
                var saved = Find<TextBlock>(win, "TxtSaved").Text;
                AddRow(win, "73000001", "Timed Row", "Draft");
                win.PersistBatchRow(win.BatchRows[0]);
                var after = Find<TextBlock>(win, "TxtSaved").Text;
                Assert.Matches(@"^Saved \d{2}:\d{2}$", after);
                sess = win.Svc.Store.LatestOpenSession();
            }
            finally
            {
                try
                {
                    var s = sess ?? win.Svc.Store.LatestOpenSession();
                    if (s != null)
                    {
                        foreach (var e in win.Svc.Store.BatchRows(s.Id))
                            win.Svc.Store.DeleteBatchRow(e.Id);
                        win.Svc.Store.CompleteBatchSession(s.Id);
                    }
                }
                catch { }
                SettleThenClose(win);
            }
        });
    }

    [Fact]
    public void Batch_Window_Title_Shows_Batch_Name()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                Assert.Contains("Batch:", win.Title);
                win.ShowArea("newcard");
                Assert.DoesNotContain("Batch:", win.Title);
                win.ShowArea("batch");
                Assert.Contains("Batch:", win.Title);
            }
            finally { SettleThenClose(win); }
        });
    }

    // ---------- Part B: toolbar + filter chips (§2.3-2.4) ----------

    [Fact]
    public void Batch_Toolbar_And_Chips_Inside_Window_At_All_Sizes()
    {
        WpfSta.Run(() =>
        {
            foreach (var (w, h) in new[] { (1280, 800), (1100, 700), (860, 620) })
            {
                var win = NewShownBatch(w, h);
                AddRow(win, "76000001", "Toolbar Person", "NeedsReview");
                win.UpdateLayout();
                try
                {
                    foreach (var name in new[]
                    {
                        "PnlBatchToolbar", "PnlBatchButtons", "TxtBatchSearch",
                        "PnlBatchChips", "ChkFilterAll", "ChkFilterAttention",
                        "ChkFilterReview", "ChkFilterIssued", "ChkFilterReissue",
                        "BtnAddRow", "BtnDupRow", "BtnPasteRows", "BtnImportCsv",
                        "BtnExportCsv", "BtnPreparePhotos", "BtnRemoveSelected"
                    })
                    {
                        var el = win.FindName(name) as FrameworkElement;
                        Assert.True(el != null, $"{name} missing at {w}x{h}");
                        Assert.True(el!.IsVisible, $"{name} not visible at {w}x{h}");
                        var r = OnScreenRect(el, win);
                        Assert.True(r.Width > 0 && r.Height > 0, $"{name} empty rect at {w}x{h}");
                        Assert.True(r.Right <= win.Width + 1 && r.Bottom <= win.Height + 1,
                            $"{name} outside window at {w}x{h}: {r}");
                    }
                    // no toolbar button text clipped: the inner TextBlock must fit
                    // inside the button's content box (width - padding - border)
                    foreach (var name in new[] { "BtnAddRow", "BtnDupRow", "BtnPasteRows",
                        "BtnImportCsv", "BtnExportCsv", "BtnPreparePhotos", "BtnRemoveSelected" })
                    {
                        var b = (Button)win.FindName(name)!;
                        var tb = VisualDescendants(b).OfType<TextBlock>().FirstOrDefault();
                        Assert.True(tb != null, $"{name} has no text at {w}x{h}");
                        var avail = b.ActualWidth - 26; // ToolButton padding 12+12 + border 1+1
                        Assert.True(tb!.DesiredSize.Width <= avail + 0.5,
                            $"{name} text clipped at {w}x{h}: text={tb.DesiredSize.Width} avail={avail}");
                    }
                }
                finally { SettleThenClose(win); }
            }
        });
    }

    [Fact]
    public void Batch_Chips_Counts_Match_Grid()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                AddRow(win, "77000001", "One", "Issued");
                AddRow(win, "77000002", "Two", "NeedsReview");
                AddRow(win, "77000003", "Three", "Failed");
                AddRow(win, "77000004", "Four", "NeedsPhoto");
                win.BatchRows[3].PhotoPath = null;
                win.UpdateLayout();

                Assert.Equal("All 4", (string)Find<RadioButton>(win, "ChkFilterAll").Content);
                Assert.Equal("Needs attention 2", (string)Find<RadioButton>(win, "ChkFilterAttention").Content);
                Assert.Equal("Ready to review 1", (string)Find<RadioButton>(win, "ChkFilterReview").Content);
                Assert.Equal("Issued 1", (string)Find<RadioButton>(win, "ChkFilterIssued").Content);

                // attention chip shows Failed + NeedsPhoto rows
                var att = Find<RadioButton>(win, "ChkFilterAttention");
                att.IsChecked = true;
                win.UpdateLayout();
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(win.BatchRows);
                Assert.Equal(2, view.Cast<BatchRow>().Count());
                Assert.Equal("Showing 2 of 4", Find<TextBlock>(win, "TxtBatchShowing").Text);

                // issued chip
                Find<RadioButton>(win, "ChkFilterIssued").IsChecked = true;
                win.UpdateLayout();
                Assert.Single(view.Cast<BatchRow>().ToList());
                Assert.Equal("Showing 1 of 4", Find<TextBlock>(win, "TxtBatchShowing").Text);

                // back to All: no "Showing" unless selection exists
                Find<RadioButton>(win, "ChkFilterAll").IsChecked = true;
                win.UpdateLayout();
                Assert.Equal("", Find<TextBlock>(win, "TxtBatchShowing").Text);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Search_Filters_Grid_And_CtrlF()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                AddRow(win, "78000001", "Rahim Uddin", "NeedsReview");
                AddRow(win, "78000002", "Karim Ahmed", "NeedsReview");
                win.UpdateLayout();

                var search = Find<TextBox>(win, "TxtBatchSearch");
                search.Text = "rahim";
                win.UpdateLayout();
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(win.BatchRows);
                Assert.Single(view.Cast<BatchRow>().ToList());
                Assert.Equal("Showing 1 of 2", Find<TextBlock>(win, "TxtBatchShowing").Text);

                // Ctrl+F focuses the search box
                Assert.True(win.TryHandleShortcut(System.Windows.Input.Key.F,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.True(search.IsKeyboardFocusWithin || search.IsFocused
                    || System.Windows.Input.Keyboard.FocusedElement == search);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Toolbar_Shortcuts_Add_Dup_Paste()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                var before = win.BatchRows.Count;
                Assert.True(win.TryHandleShortcut(System.Windows.Input.Key.N,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.Equal(before + 1, win.BatchRows.Count);

                var dg = Find<DataGrid>(win, "DgBatch");
                dg.SelectedItem = win.BatchRows[^1];
                Assert.True(win.TryHandleShortcut(System.Windows.Input.Key.D,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.Equal(before + 2, win.BatchRows.Count);

                // Ctrl+V with empty clipboard shows info, not crash — give it rows
                System.Windows.Clipboard.SetText("Paste Name,Pos,77000099,O+,JE,N/A,Permanent,01/12/2026");
                Assert.True(win.TryHandleShortcut(System.Windows.Input.Key.V,
                    System.Windows.Input.ModifierKeys.Control));
                Assert.Equal(before + 3, win.BatchRows.Count);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Toolbar_And_Filter_Screenshots()
    {
        WpfSta.Run(() =>
        {
            Directory.CreateDirectory(ShotsDir());
            var win = NewShownBatch(1280, 800);
            BatchSession? sess = null;
            try
            {
                AddRow(win, "79000001", "Aisha Rahman", "Issued");
                AddRow(win, "79000002", "Karim Uddin", "NeedsReview");
                AddRow(win, "79000003", "Fatema Khan", "Failed");
                AddRow(win, "79000004", "Hasan Ali", "NeedsPhoto");
                win.BatchRows[3].Error = "photo missing";
                win.UpdateLayout();
                SaveWindow(win, Path.Combine(ShotsDir(), "batch-toolbar-1280x800.png"));
            }
            finally { SettleThenClose(win); }

            var win2 = NewShownBatch(1280, 800);
            try
            {
                AddRow(win2, "79000011", "Nusrat Jahan", "Failed");
                AddRow(win2, "79000012", "Tanvir Hasan", "NeedsPhoto");
                AddRow(win2, "79000013", "Mim Akter", "NeedsReview");
                win2.BatchRows[0].Error = "bad uid";
                win2.BatchRows[1].Error = "photo missing";
                Find<RadioButton>(win2, "ChkFilterAttention").IsChecked = true;
                win2.UpdateLayout();
                SaveWindow(win2, Path.Combine(ShotsDir(), "batch-filter-attention-1280x800.png"));
            }
            finally { SettleThenClose(win2); }
        });
    }

    [Fact]
    public void Batch_Header_Screenshots()
    {
        WpfSta.Run(() =>
        {
            Directory.CreateDirectory(ShotsDir());
            var win = NewShownBatch(1280, 800);
            BatchSession? sess = null;
            try
            {
                AddRow(win, "74000001", "Aisha Rahman", "Issued");
                AddRow(win, "74000002", "Karim Uddin", "NeedsReview");
                AddRow(win, "74000003", "Fatema Khan", "Failed");
                AddRow(win, "74000004", "Hasan Ali", "Processing");
                AddRow(win, "74000005", "", "Draft");
                win.UpdateLayout();
                SaveWindow(win, Path.Combine(ShotsDir(), "batch-header-1280x800.png"));
            }
            finally { SettleThenClose(win); }

            var win2 = NewShownBatch(860, 620);
            try
            {
                win2.BatchRows.Add(new BatchRow { Uid = "74000011", Name = "Nusrat Jahan", Status = "NeedsReview" });
                win2.UpdateLayout();
                SaveWindow(win2, Path.Combine(ShotsDir(), "batch-header-860x620.png"));
            }
            finally { SettleThenClose(win2); }
        });
    }
}
