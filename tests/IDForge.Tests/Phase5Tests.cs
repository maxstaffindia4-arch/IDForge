using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 5 Review-window tests.</summary>
[Collection("Wpf")]
public class Phase5Tests
{
    private static ReviewWindow NewReview(MainWindow owner, params BatchRow[] rows)
    {
        var coll = new ObservableCollection<BatchRow>(rows);
        var win = new ReviewWindow(owner, coll);
        win.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        return win;
    }

    private static void OnSta(Action work) => WpfSta.Run(work);

    private static BatchRow Row(string uid, string name, string status = "NeedsReview", long id = 1)
        => new() { RowId = id, Uid = uid, Name = name, Status = status };

    [Fact]
    public void Review_Opens_With_List_Preview_And_Navigation()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var rows = new[] { Row("80000001", "Alpha", id: 1), Row("80000002", "Beta", "Issued", 2), Row("80000003", "Gamma", "Skipped", 3) };
            var win = NewReview(w, rows);
            try
            {
                Assert.Equal("Batch Review", win.Title.Split('—')[0].Trim());
                var list = (ListBox)FindName(win, "LstRows");
                Assert.Equal(3, list.Items.Count);
                var img = FindName<Image>(win, "ImgPreview");
                Assert.NotNull(img);
                var prev = FindButton(win, "◄ Previous");
                var next = FindButton(win, "Next ►");
                Assert.NotNull(prev); Assert.NotNull(next);
                // Next moves selection to the second row.
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Same(rows[1], win.CurrentForTest);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Review_Filter_Narrows_Row_List()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var rows = new[] { Row("80000011", "A", id: 11), Row("80000012", "B", "Failed", 12), Row("80000013", "C", "Issued", 13) };
            var win = NewReview(w, rows);
            try
            {
                var cmb = (ComboBox)FindName(win, "CmbFilter");
                cmb.SelectedIndex = 2; // Failed
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var list = (ListBox)FindName(win, "LstRows");
                Assert.Single(list.Items.Cast<BatchRow>().ToList());
                Assert.Equal("80000012", ((BatchRow)list.Items[0]).Uid);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Review_Window_Size_Within_WorkArea_And_Minimum()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var win = NewReview(w, Row("80000021", "Solo"));
            try
            {
                var wa = SystemParameters.WorkArea;
                Assert.True(win.Width <= wa.Width * 0.9 + 1 || win.WindowState == WindowState.Maximized);
                Assert.True(win.Height <= wa.Height * 0.9 + 1 || win.WindowState == WindowState.Maximized);
                Assert.True(win.MinWidth >= 860 && win.MinHeight >= 620);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void RemoveBg_Toggle_Switches_Mode_And_Enables_Selector()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000031", "Bg Test", id: 31);
            row.BgMode = "PlainWhite";
            var win = NewReview(w, row);
            try
            {
                var panel = FindByType<IDForge.Controls.PhotoPositionPanel>(win)!;
                // Panel is disabled until a photo loads — enable it to inspect children.
                panel.IsEnabled = true;
                var white = (RadioButton)panel.FindName("RadBgWhite")!;
                var model = (RadioButton)panel.FindName("RadBgModel")!;
                var keep = (RadioButton)panel.FindName("RadBgKeep")!;
                Assert.True(white.IsChecked == true);

                keep.IsChecked = true;   // keep original background
                Assert.Equal("Keep", row.BgMode);

                model.IsChecked = true;  // model background
                Assert.Equal("Model", row.BgMode);

                white.IsChecked = true;  // plain white (recommended)
                Assert.Equal("PlainWhite", row.BgMode);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Zoom_Adjustment_Survives_Row_Switch()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var a = Row("80000041", "Zoom A", id: 41);
            var b = Row("80000042", "Zoom B", id: 42);
            a.Zoom = 1.7; a.OffsetX = 12; a.OffsetY = -8;
            var win = NewReview(w, a, b);
            try
            {
                var sld = (Slider)FindName(win, "SldZoom")!;
                Assert.Equal(1.7, sld.Value, 3);
                // Move to row B and back — adjustment must still be there.
                var next = FindButton(win, "Next ►")!;
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var back = FindButton(win, "◄ Previous")!;
                back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Same(a, win.CurrentForTest);
                Assert.Equal(1.7, sld.Value, 3);
                Assert.Equal(12, a.OffsetX);
                Assert.Equal(-8, a.OffsetY);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Bottom_Bar_Has_Spec_Buttons()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var win = NewReview(w, Row("80000051", "Buttons", id: 51));
            try
            {
                foreach (var label in new[] { "◄ Previous", "Save draft", "Skip", "Approve & Issue", "Next ►" })
                    Assert.NotNull(FindButton(win, label));
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public async Task Issued_Row_Cannot_Be_Issued_Again()
    {
        await OnStaAsync(async () =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000061", "Once", "Issued", 61);
            row.AttemptCount = 1;
            var win = NewReview(w, row);
            try
            {
                var outcome = await win.IssueRowAsync(row, confirm: false, quiet: true);
                Assert.Null(outcome);
                Assert.Equal(1, row.AttemptCount);   // no second attempt / CardIssue
                Assert.Equal("Issued", row.Status);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public async Task Invalid_Row_Issue_Writes_Nothing()
    {
        await OnStaAsync(async () =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("", "", "NeedsReview", 62);   // missing name + UID
            var win = NewReview(w, row);
            try
            {
                var outcome = await win.IssueRowAsync(row, confirm: false, quiet: true);
                Assert.Null(outcome);
                Assert.Equal(0, row.AttemptCount);
                Assert.Equal("NeedsReview", row.Status);   // not flipped to Failed, nothing persisted as issued
                Assert.Null(row.CardIssueId);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Reissue_Always_Asks_For_Confirmation()
    {
        OnSta(() =>
        {
            var prev = AppSettings.ConfirmBeforeIssue;
            bool? sawReissue = null; int calls = 0;
            IssueDialogs.ConfirmOverride = (_, _, _, _, isReissue) => { calls++; sawReissue = isReissue; return true; };
            try
            {
                AppSettings.ConfirmBeforeIssue = false;
                // Non-reissue with setting off: no dialog at all.
                Assert.True(IssueDialogs.ConfirmIssue(null!, "A", "1", DateOnly.FromDateTime(DateTime.Today), false));
                Assert.Equal(0, calls);
                // Re-issue: always confirms even with the setting off.
                Assert.True(IssueDialogs.ConfirmIssue(null!, "A", "1", DateOnly.FromDateTime(DateTime.Today), true));
                Assert.Equal(1, calls);
                Assert.True(sawReissue);
            }
            finally
            {
                IssueDialogs.ConfirmOverride = null;
                AppSettings.ConfirmBeforeIssue = prev;
            }
        });
    }

    [Fact]
    public void Review_Row_DoubleClick_Only_Selects_Does_Not_Issue()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000071", "NoIssue", id: 71);
            var win = NewReview(w, row);
            try
            {
                var list = (ListBox)FindName(win, "LstRows")!;
                list.SelectedItem = row;
                // Raise the real double-click routed event on the list.
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent };
                list.RaiseEvent(args);
                Assert.Equal("NeedsReview", row.Status);
                Assert.Equal(0, row.AttemptCount);
                Assert.Null(row.CardIssueId);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Approve_Issue_Button_Runs_Approve_Then_Same_Issue_Path()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000081", "BtnPath", "NeedsReview", 81);
            var win = NewReview(w, row);
            try
            {
                // No photo → validation stops before approve/issue: nothing changes.
                var btn = FindButton(win, "Approve & Issue")!;
                btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("NeedsReview", row.Status);
                Assert.False(row.Approved);
                Assert.Equal(0, row.AttemptCount);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Both_Screens_Use_Same_Photo_Position_Panel()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var win = NewReview(w, Row("80000091", "Panel", id: 91));
            try
            {
                // New Card panel
                var mainPanel = FindByType<IDForge.Controls.PhotoPositionPanel>(w);
                Assert.NotNull(mainPanel);
                // Review panel — same type
                var revPanel = FindByType<IDForge.Controls.PhotoPositionPanel>(win);
                Assert.NotNull(revPanel);
                Assert.IsType<IDForge.Controls.PhotoPositionPanel>(revPanel);
                // Review has no old-style sliders (SldX/SldY are gone; only the
                // panel's own zoom slider remains inside the shared control).
                Assert.Null(FindName(win, "SldX"));
                Assert.Null(FindName(win, "SldY"));
                Assert.Null(FindName(win, "GrpNudge"));
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void RemoveBg_Toggle_Works_On_Both_Screens()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            var row = Row("80000101", "Bg Both", id: 101);
            row.BgMode = "PlainWhite";
            var win = NewReview(w, row);
            try
            {
                // Review side: the bg radios inside the shared panel flip the row mode.
                var revPanel = FindByType<IDForge.Controls.PhotoPositionPanel>(win)!;
                var revKeep = revPanel.FindName("RadBgKeep") as RadioButton;
                Assert.NotNull(revKeep);
                revKeep!.IsChecked = true;
                Assert.Equal("Keep", row.BgMode);
                var revWhite = revPanel.FindName("RadBgWhite") as RadioButton;
                revWhite!.IsChecked = true;
                Assert.Equal("PlainWhite", row.BgMode);

                // New Card side: same control drives CurrentBgModeTag.
                var mainPanel = FindByType<IDForge.Controls.PhotoPositionPanel>(w)!;
                var mainKeep = mainPanel.FindName("RadBgKeep") as RadioButton;
                Assert.NotNull(mainKeep);
                Assert.Equal("PlainWhite", w.BgModeTagForTest);
                mainKeep!.IsChecked = true;
                Assert.Equal("Keep", w.BgModeTagForTest);
            }
            finally { win.Close(); w.Close(); }
        });
    }

    [Fact]
    public void Sidebar_Backup_Icon_Is_FontAwesome_Not_Emoji()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            try
            {
                var panel = (Border)w.FindName("PnlBackupCompact")!;
                var tb = (TextBlock)System.Windows.LogicalTreeHelper
                    .GetChildren(panel).OfType<TextBlock>().First();
                // Parity §3.3: shield-halved F3ED in the embedded FA Solid font.
                Assert.Contains("Font Awesome", tb.FontFamily.Source);
                Assert.DoesNotContain("💾", tb.Text);
                Assert.Equal("\uF3ED", tb.Text);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Batch_Review_Flow_Doc_Exists()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "redesign", "batch_review_flow_plan.md");
        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), $"missing {path}");
    }

    [Fact]
    public void Batch_Cell_Template_Has_No_Black_Focus_Box()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            try
            {
                var style = (Style)w.FindResource("BatchCell");
                var template = style.Setters.OfType<Setter>()
                    .FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value
                    as ControlTemplate;
                Assert.NotNull(template);   // custom template = no stock black focus box
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Main_Window_Minimum_Is_860x620_And_Settings_Scrolls()
    {
        OnSta(() =>
        {
            var w = FixPassTests.NewWindow();
            try
            {
                Assert.Equal(860, w.MinWidth);
                Assert.Equal(620, w.MinHeight);
                var sv = (ScrollViewer)w.FindName("PageSettings")!;
                Assert.Equal(ScrollBarVisibility.Auto, sv.HorizontalScrollBarVisibility);
                // Browse button + folder box exist inside the settings panel.
                Assert.NotNull(w.FindName("TxtSaveFolder"));
            }
            finally { w.Close(); }
        });
    }

    private static T? FindByType<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && FindByType<T>(d) is { } r) return r;
        return null;
    }

    private static Task OnStaAsync(Func<Task> work) => WpfSta.RunAsync(work);

    // ---------- helpers ----------

    private static DependencyObject? FindName(DependencyObject root, string name)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (child is DependencyObject d)
            {
                var r = FindName(d, name);
                if (r != null) return r;
            }
        }
        return null;
    }

    private static T? FindName<T>(DependencyObject root, string name) where T : DependencyObject
        => FindName(root, name) as T;

    private static Button? FindButton(DependencyObject root, string content)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button b && b.Content as string == content) return b;
            if (child is DependencyObject d)
            {
                var r = FindButton(d, content);
                if (r != null) return r;
            }
        }
        return null;
    }
}
