using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;
using IDForge.Core;
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
        win.WatchBatchRow(row);
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

    // ---------- Part C: grid, thumbnails, pills, selected row (§2.5-2.8) ----------

    [Fact]
    public void Batch_Grid_Columns_Order_Matches_Plan()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                var dg = Find<DataGrid>(win, "DgBatch");
                var headers = dg.Columns.Select(c => c.Header?.ToString()).ToArray();
                Assert.Equal(new[] { "", "#", "Employee", "Employee ID", "Blood group",
                    "Position", "Contract", "Print date", "Card type", "Status", "Message" },
                    headers);
                // no Photo column, no plain-text Status column (§2.6/D3)
                Assert.DoesNotContain("Photo", headers);
                Assert.True(dg.MinWidth >= 1080);
                Assert.Equal(44, dg.RowHeight);
                Assert.Equal(ScrollBarVisibility.Auto,
                    dg.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty));
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Status_Pills_Per_State()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                var expected = new Dictionary<string, (string label, string icon, bool spin)>
                {
                    ["Issued"] = ("Issued", "\uF00C", false),
                    ["NeedsReview"] = ("Ready to review", "\uF06E", false),
                    ["Failed"] = ("Needs attention", "\uF06A", false),
                    ["NeedsPhoto"] = ("Needs photo", "\uF06A", false),
                    ["Processing"] = ("Processing", "\uF110", true),
                    ["Approved"] = ("Issuing…", "\uF110", true),
                    ["Skipped"] = ("Skipped", "\uF056", false),
                    ["Draft"] = ("Draft", "\uF15B", false),
                };
                foreach (var (status, (label, icon, spin)) in expected)
                {
                    var row = new BatchRow { Uid = "88000001", Name = "P", Status = status };
                    Assert.Equal(label, row.StatusPillLabel);
                    Assert.Equal(icon, row.StatusPillIcon);
                    Assert.Equal(spin, row.StatusPillSpinning);
                    Assert.NotNull(row.StatusPillBg);
                    Assert.NotNull(row.StatusPillFg);
                }
                // card-type pills
                var re = new BatchRow { CardType = "Re-issue" };
                Assert.True(re.CardTypeIsReissue);
                Assert.Equal("\uF2F1", re.CardTypePillIcon);
                var fresh = new BatchRow();
                Assert.False(fresh.CardTypeIsReissue);
                Assert.Equal("\uF067", fresh.CardTypePillIcon);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Uid_Invalid_Red_Border_And_Message()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                var row = AddRow(win, "12", "Short ID", "Draft");   // 2 digits -> invalid
                win.UpdateLayout();
                Assert.True(row.UidInvalid);
                var brush = Assert.IsType<SolidColorBrush>(row.UidInvalidBrush);
                Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), brush.Color);
                Assert.True(row.MessageIsError);
                Assert.Contains("4–15 digits", row.Message);

                // valid uid clears it
                row.Uid = "88000002";
                win.UpdateLayout();
                Assert.False(row.UidInvalid);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Photo_Placeholder_And_Thumbnail()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            string? png = null;
            try
            {
                // real tiny PNG for the thumbnail path
                using (var bmp = new SkiaSharp.SKBitmap(40, 48))
                {
                    bmp.Erase(SkiaSharp.SKColors.SteelBlue);
                    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                    png = Path.Combine(Path.GetTempPath(), $"batchthumb_{Guid.NewGuid():N}.png");
                    using (var fs = File.Create(png)) data.SaveTo(fs);
                }

                var needsPhoto = AddRow(win, "89000001", "Needs Photo", "NeedsPhoto");
                var draft = AddRow(win, "89000002", "", "Draft");
                var issued = AddRow(win, "89000003", "Issued Row", "Issued");
                var withPhoto = AddRow(win, "89000004", "Has Photo", "Draft");
                withPhoto.PhotoPath = png;
                win.UpdateBatchSummary();
                win.UpdateLayout();

                Assert.Equal(1, needsPhoto.PhotoPlaceholderKind);
                Assert.True(needsPhoto.ShowPhotoPlaceholderRed);
                Assert.True(needsPhoto.ShowAddPhotoLink);

                Assert.Equal(2, draft.PhotoPlaceholderKind);
                Assert.True(draft.ShowPhotoPlaceholderGrey);

                Assert.Equal(0, issued.PhotoPlaceholderKind);
                Assert.False(issued.ShowPhotoPlaceholderRed || issued.ShowPhotoPlaceholderGrey);

                Assert.True(withPhoto.ShowThumb);
                Assert.NotNull(withPhoto.Thumb);
                Assert.Equal(0, withPhoto.PhotoPlaceholderKind);
            }
            finally
            {
                SettleThenClose(win);
                if (png != null) try { File.Delete(png); } catch { }
            }
        });
    }

    [Fact]
    public void Batch_Selection_Checkbox_Syncs_Grid()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                AddRow(win, "90000001", "One", "Draft");
                AddRow(win, "90000002", "Two", "Draft");
                var dg = Find<DataGrid>(win, "DgBatch");
                win.UpdateLayout();

                dg.SelectedItem = win.BatchRows[0];
                Assert.True(win.BatchRows[0].IsSelected);
                Assert.False(win.BatchRows[1].IsSelected);
                Assert.Equal("1 selected", Find<TextBlock>(win, "TxtBatchShowing").Text);

                // checkbox toggles selection state without moving grid selection
                win.BatchRows[1].IsSelected = true;
                Assert.Equal("2 selected", Find<TextBlock>(win, "TxtBatchShowing").Text);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Reissue_Message_Shows_Previous_And_New_Dates()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownBatch(1280, 800);
            try
            {
                var emp = new Employee
                {
                    Uid = "91000001", Name = "Re Issue Person", BloodGroup = "O+",
                    Position = "Nurse", ContractType = "Permanent"
                };
                var empId = win.Svc.Store.InsertEmployee(emp);
                var issueDate = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
                win.Svc.Store.InsertIssue(new CardIssue
                {
                    EmployeeId = empId, Version = 1,
                    PrintDate = new DateOnly(2026, 3, 10),
                    ValidUntil = new DateOnly(2027, 3, 10), IssuedAt = issueDate
                });

                var row = AddRow(win, "91000001", "Re Issue Person", "NeedsReview");
                row.PrintDateText = "2026-12-01";
                row.Contract = "Contractual";
                win.UpdateBatchSummary();

                Assert.Equal("Re-issue", row.CardType);
                var expected = $"Replaces card issued 10/03/2026. New validity until " +
                    $"{Validity.CalculateValidUntil(row.PrintDate, row.Contract):dd/MM/yyyy}.";
                Assert.Equal(expected, row.ReplacesText);
                Assert.Contains(row.ReplacesText, row.Message);
            }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Batch_Grid_Screenshots()
    {
        WpfSta.Run(() =>
        {
            Directory.CreateDirectory(ShotsDir());
            string? png = null;
            try
            {
                using (var bmp = new SkiaSharp.SKBitmap(60, 72))
                {
                    bmp.Erase(SkiaSharp.SKColors.SeaGreen);
                    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                    png = Path.Combine(Path.GetTempPath(), $"batchgrid_{Guid.NewGuid():N}.png");
                    using (var fs = File.Create(png)) data.SaveTo(fs);
                }

                var win = NewShownBatch(1280, 800);
                try
                {
                    var r1 = AddRow(win, "92000001", "Aisha Rahman", "Issued");
                    var r2 = AddRow(win, "92000002", "Karim Uddin", "NeedsReview");
                    var r3 = AddRow(win, "92000003", "Fatema Khan", "Failed");
                    r3.Error = "bad uid";
                    var r4 = AddRow(win, "92000004", "Hasan Ali", "NeedsPhoto");
                    r4.Error = "photo missing";
                    var r5 = AddRow(win, "92000005", "Nusrat Jahan", "Draft");
                    r5.PhotoPath = png;
                    var r6 = AddRow(win, "92000006", "Tanvir Hasan", "Skipped");
                    win.UpdateBatchSummary();
                    win.UpdateLayout();
                    SaveWindow(win, Path.Combine(ShotsDir(), "batch-grid-1280x800.png"));
                }
                finally { SettleThenClose(win); }

                var win2 = NewShownBatch(1100, 700);
                try
                {
                    for (int i = 0; i < 8; i++)
                        AddRow(win2, $"9300000{i}", $"Person Number {i + 1}",
                            i % 3 == 0 ? "NeedsReview" : i % 3 == 1 ? "Draft" : "Issued");
                    win2.UpdateBatchSummary();
                    win2.UpdateLayout();
                    SaveWindow(win2, Path.Combine(ShotsDir(), "batch-grid-1100x700.png"));
                }
                finally { SettleThenClose(win2); }
            }
            finally
            {
                if (png != null) try { File.Delete(png); } catch { }
            }
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

    [Fact]
    public void Batch_Footer_StatusBar_And_Responsive()
    {
        WpfSta.Run(() =>
        {
            // 860x620: footer inside window, progress card stacks review below counts,
            // status-bar photo progress hidden, footer shot saved.
            var win = NewShownBatch(860, 620);
            try
            {
                AddRow(win, "75000011", "Nusrat Jahan", "NeedsReview");
                AddRow(win, "75000012", "Tanvir Hasan", "Failed");
                win.UpdateBatchSummary();
                win.UpdateLayout();

                foreach (var name in new[] { "TxtBatchShowing", "PnlStatusPhotos" })
                {
                    var el = win.FindName(name) as FrameworkElement;
                    Assert.True(el != null, $"{name} missing");
                }

                // Stacked progress card: review panel sits below the counts block.
                var review = win.FindName("PnlBatchReview") as FrameworkElement;
                var counts = win.FindName("PnlCountsBlock") as FrameworkElement;
                Assert.True(review != null && counts != null, "progress card panels missing");
                var rReview = OnScreenRect(review!, win);
                var rCounts = OnScreenRect(counts!, win);
                Assert.True(rReview.Top >= rCounts.Bottom - 1,
                    $"review should stack below counts at 860: {rReview} vs {rCounts}");

                // Footer: no showing text until a filter/search is active.
                var showing = win.FindName("TxtBatchShowing") as TextBlock;
                Assert.Equal("", showing!.Text);
                var chip = win.FindName("ChkFilterAttention") as RadioButton;
                chip!.IsChecked = true;
                win.UpdateLayout();
                Assert.Contains("Showing 1 of 2", showing.Text);
                Assert.Contains("Needs attention", chip.Content!.ToString());

                (win.FindName("ChkFilterAll") as RadioButton)!.IsChecked = true;
                win.UpdateLayout();
                Assert.Equal("", showing.Text);
                Directory.CreateDirectory(ShotsDir());
                SaveWindow(win, Path.Combine(ShotsDir(), "batch-footer-860x620.png"));
            }
            finally { SettleThenClose(win); }

            // 1280x800: progress card side-by-side (review right of counts).
            var win2 = NewShownBatch(1280, 800);
            try
            {
                AddRow(win2, "75000021", "Aisha Rahman", "NeedsReview");
                win2.UpdateBatchSummary();
                win2.UpdateLayout();
                var review2 = win2.FindName("PnlBatchReview") as FrameworkElement;
                var counts2 = win2.FindName("PnlCountsBlock") as FrameworkElement;
                var rR = OnScreenRect(review2!, win2);
                var rC = OnScreenRect(counts2!, win2);
                Assert.True(rR.Left > rC.Left || rR.Top < rC.Top,
                    "progress card should be side-by-side at 1280");
                Assert.True(rR.Top <= rC.Bottom, "review should overlap counts row at 1280");
            }
            finally { SettleThenClose(win2); }

            // Status-bar photo progress appears only while preparing.
            var win3 = NewShownBatch(1280, 800);
            try
            {
                var pnl = win3.FindName("PnlStatusPhotos") as FrameworkElement;
                var spinner = win3.FindName("TxtStatusSpinner") as FrameworkElement;
                var txt = win3.FindName("TxtStatusPhotos") as TextBlock;
                Assert.True(pnl != null && txt != null && spinner != null, "status photo controls missing");
                Assert.Equal(Visibility.Collapsed, pnl!.Visibility);
            }
            finally { SettleThenClose(win3); }
        });
    }
}
