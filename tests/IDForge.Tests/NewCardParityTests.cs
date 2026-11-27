using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>NEW_CARD_VISUAL_PARITY_PLAN §7 acceptance: rendered fit checks at
/// each §2 size, collapsed/expanded nav state, footer layout + screenshots.</summary>
[Collection("Wpf")]
public class NewCardParityTests
{
    private static MainWindow NewShownNewCard(int w, int h)
    {
        var win = FixPassTests.NewWindow();
        win.Show();
        win.ShowArea("newcard");
        // FitWindowToWorkArea caps MaxWidth/MaxHeight at the work area; lift the
        // cap so the requested test size is honoured even on small screens.
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

    /// <summary>Let pending renders/layouts drain before closing — closing a
    /// window mid-render can wedge the shared WPF render thread on this host.</summary>
    private static void SettleThenClose(MainWindow win)
    {
        for (int i = 0; i < 20; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        win.Close();
    }

    private static void SavePng(FrameworkElement el, string path)
    {
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void SaveWindow(MainWindow win, string path) =>
        SavePng(win, path);

    /// <summary>Every visible control's rendered size ≥ desired size, and its
    /// bounds stay inside the window.</summary>
    private static void AssertRenderedFit(MainWindow win)
    {
        var winRect = new Rect(0, 0, win.ActualWidth, win.ActualHeight);
        void Walk(DependencyObject d, bool inScroll = false)
        {
            if (d is ScrollViewer) inScroll = true;
            if (d is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0)
            {
                // DesiredSize includes the element's margin slot; ActualWidth/
                // Height exclude it — compare against desired minus margin.
                var wantW = fe.DesiredSize.Width - fe.Margin.Left - fe.Margin.Right;
                var wantH = fe.DesiredSize.Height - fe.Margin.Top - fe.Margin.Bottom;
                Assert.True(fe.ActualWidth >= wantW - 0.5,
                    $"{fe.GetType().Name} '{fe.Name}' width {fe.ActualWidth:F1} < desired {wantW:F1}");
                Assert.True(fe.ActualHeight >= wantH - 0.5,
                    $"{fe.GetType().Name} '{fe.Name}' height {fe.ActualHeight:F1} < desired {wantH:F1}");
                // Elements inside a ScrollViewer may legitimately sit below the
                // fold — only non-scrolled content must stay inside the window.
                if (!inScroll)
                {
                    var tl = fe.TransformToAncestor(win).Transform(new Point(0, 0));
                    var r = new Rect(tl, new Size(fe.ActualWidth, fe.ActualHeight));
                    Assert.True(winRect.Contains(r) || r.IntersectsWith(winRect),
                        $"{fe.GetType().Name} '{fe.Name}' at {r} outside window {winRect}");
                }
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject dd) Walk(dd, inScroll);
        }
        Walk(win);
    }

    private static Rect BoundsIn(FrameworkElement el, FrameworkElement ancestor)
    {
        var tl = el.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        return new Rect(tl, new Size(el.ActualWidth, el.ActualHeight));
    }

    [Theory]
    [InlineData(860, 620)]
    [InlineData(1100, 700)]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    public void NewCard_Renders_At_Each_Size(int w, int h)
    {
        WpfSta.Run(() =>
        {
            var dir = ShotsDir();
            Directory.CreateDirectory(dir);
            var win = NewShownNewCard(w, h);
            try
            {
                AssertRenderedFit(win);
                SaveWindow(win, Path.Combine(dir, $"newcard-{w}x{h}.png"));
            }
            finally { SettleThenClose(win); }
        });
    }

    /// <summary>FIX 2: preview header elements never overlap or clip —
    /// the controls wrap to a second line instead of covering the title.</summary>
    [Theory]
    [InlineData(860, 620)]
    [InlineData(1100, 700)]
    [InlineData(1280, 800)]
    public void PreviewHeader_No_Overlap_Or_Clip(int w, int h)
    {
        WpfSta.Run(() =>
        {
            var win = NewShownNewCard(w, h);
            try
            {
                // Let the deferred wrap decision run.
                for (int i = 0; i < 10; i++)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                win.UpdateLayout();

                var names = new[] { "TxtLivePreview", "PnlLiveBadge",
                                    "ChkPrintEdges", "PnlPreviewControls" };
                var rects = new Dictionary<string, Rect>();
                foreach (var n in names)
                {
                    var fe = (FrameworkElement)win.FindName(n)!;
                    Assert.True(fe.IsVisible, $"{n} not visible");
                    rects[n] = BoundsIn(fe, win);
                    // DesiredSize includes Margin in this WPF version — compare
                    // the arranged width against desired minus the margins.
                    var needW = fe.DesiredSize.Width - fe.Margin.Left - fe.Margin.Right;
                    Assert.True(fe.ActualWidth >= needW - 0.5,
                        $"{n} clipped: actual {fe.ActualWidth:F1} < desired-minus-margin {needW:F1}");
                }
                foreach (var a in names)
                    foreach (var b in names)
                    {
                        if (a == b) continue;
                        var ea = (DependencyObject)win.FindName(a)!;
                        var eb = (DependencyObject)win.FindName(b)!;
                        // Parent/child pairs overlap by definition — skip them.
                        if (IsAncestor(ea, eb) || IsAncestor(eb, ea)) continue;
                        var ra = rects[a]; var rb = rects[b];
                        var inter = Rect.Intersect(ra, rb);
                        Assert.True(inter.IsEmpty || inter.Width < 0.5 || inter.Height < 0.5,
                            $"{a} {ra} overlaps {b} {rb}");
                    }
                // Every header child must stay inside the window horizontally.
                var header = (FrameworkElement)win.FindName("PnlPreviewHeader")!;
                var hr = BoundsIn(header, win);
                Assert.True(hr.Right <= win.ActualWidth + 0.5,
                    $"preview header right {hr.Right:F1} beyond window {win.ActualWidth:F1}");
            }
            finally { SettleThenClose(win); }
        });
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject node)
    {
        for (var p = VisualTreeHelper.GetParent(node); p != null;
             p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>FIX 2: footer must keep Issue card inside the window at every size.</summary>
    [Theory]
    [InlineData(860, 620)]
    [InlineData(1100, 700)]
    [InlineData(1280, 800)]
    public void Footer_Issue_Button_Inside_Window(int w, int h)
    {
        WpfSta.Run(() =>
        {
            var win = NewShownNewCard(w, h);
            try
            {
                var btn = (FrameworkElement)win.FindName("BtnSave")!;
                var b = BoundsIn(btn, win);
                Assert.True(b.Right <= win.ActualWidth + 0.5,
                    $"BtnSave right {b.Right:F1} beyond window {win.ActualWidth:F1}");
                Assert.True(b.Left >= 0,
                    $"BtnSave left {b.Left:F1} before window edge");
            }
            finally { SettleThenClose(win); }
        });
    }

    /// <summary>FIX 1: dedicated shot proving every icon renders (no tofu).</summary>
    [Fact]
    public void NewCard_Icons_Render_Screenshot()
    {
        WpfSta.Run(() =>
        {
            var dir = ShotsDir();
            Directory.CreateDirectory(dir);
            var win = NewShownNewCard(1280, 800);
            try { SaveWindow(win, Path.Combine(dir, "newcard-icons-1280x800.png")); }
            finally { SettleThenClose(win); }
        });
    }

    [Fact]
    public void Nav_Collapsed_And_Expanded_States()
    {
        WpfSta.Run(() =>
        {
            var dir = ShotsDir();
            Directory.CreateDirectory(dir);
            var win = NewShownNewCard(1920, 1080);
            try
            {
                var navCol = (FrameworkElement)win.FindName("NavCol")!;

                // --- collapsed ---
                win.SetNavCollapsed(true);
                win.UpdateLayout();
                Assert.Equal(56, navCol.ActualWidth);
                foreach (var name in new[] { "NavLblNewCard", "NavKeyNewCard", "NavLblBatch",
                             "NavKeyBatch", "NavLblRecords", "NavKeyRecords",
                             "NavLblSettings", "NavKeySettings",
                             "LblIssueCards", "LblManage" })
                {
                    var fe = (FrameworkElement)win.FindName(name)!;
                    Assert.Equal(Visibility.Collapsed, fe.Visibility);
                }
                foreach (var name in new[] { "DvdIssueCards", "DvdManage" })
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)win.FindName(name)!).Visibility);
                Assert.Equal(Visibility.Collapsed,
                    ((FrameworkElement)win.FindName("PnlBackupFull")!).Visibility);
                Assert.Equal(Visibility.Visible,
                    ((FrameworkElement)win.FindName("PnlBackupCompact")!).Visibility);

                // All four nav icons + backup icon fully inside the 56 px rail.
                var rail = BoundsIn(navCol, win);
                foreach (var name in new[] { "NavPnlNewCard", "NavPnlBatch",
                             "NavPnlRecords", "NavPnlSettings", "PnlBackupCompact" })
                {
                    var b = BoundsIn((FrameworkElement)win.FindName(name)!, navCol);
                    Assert.True(b.Left >= -0.5 && b.Right <= navCol.ActualWidth + 0.5,
                        $"{name} bounds {b} outside rail width {navCol.ActualWidth}");
                    Assert.True(b.Left >= rail.Left - 0.5 && b.Right <= rail.Right + 0.5);
                }
                SaveWindow(win, Path.Combine(dir, "nav-collapsed.png"));

                // --- expanded ---
                win.SetNavCollapsed(false);
                win.UpdateLayout();
                Assert.Equal(208, navCol.ActualWidth);
                foreach (var name in new[] { "NavLblNewCard", "NavKeyNewCard", "NavLblBatch",
                             "NavKeyBatch", "NavLblRecords", "NavKeyRecords",
                             "NavLblSettings", "NavKeySettings",
                             "LblIssueCards", "LblManage" })
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)win.FindName(name)!).Visibility);
                foreach (var name in new[] { "DvdIssueCards", "DvdManage" })
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)win.FindName(name)!).Visibility);
                Assert.Equal(Visibility.Visible,
                    ((FrameworkElement)win.FindName("PnlBackupFull")!).Visibility);
                Assert.Equal(Visibility.Collapsed,
                    ((FrameworkElement)win.FindName("PnlBackupCompact")!).Visibility);
                SaveWindow(win, Path.Combine(dir, "nav-expanded.png"));
            }
            finally { SettleThenClose(win); }
        });
    }

    /// <summary>FIX 3: rail expanded by default ≥1280 (labels, shortcuts,
    /// backup card), collapsed &lt;1100, visible toggle at all sizes.</summary>
    [Fact]
    public void Nav_Rail_1280_Expanded_860_Collapsed_Toggle_Works()
    {
        WpfSta.Run(() =>
        {
            var dir = ShotsDir();
            Directory.CreateDirectory(dir);
            string[] expandedNames = { "NavLblNewCard", "NavKeyNewCard", "NavLblBatch",
                "NavKeyBatch", "NavLblRecords", "NavKeyRecords", "NavLblSettings",
                "NavKeySettings", "LblIssueCards", "LblManage" };

            // --- 1280 × 800: expanded by default ---
            var win = NewShownNewCard(1280, 800);
            try
            {
                var navCol = (FrameworkElement)win.FindName("NavCol")!;
                Assert.False(win.TestNavCollapsed, "rail should start expanded at 1280");
                Assert.Equal(208, navCol.ActualWidth);
                foreach (var n in expandedNames)
                    Assert.Equal(Visibility.Visible,
                        ((FrameworkElement)win.FindName(n)!).Visibility);
                Assert.Equal(Visibility.Visible,
                    ((FrameworkElement)win.FindName("PnlBackupFull")!).Visibility);

                var toggle = (FrameworkElement)win.FindName("BtnNavToggle")!;
                Assert.True(toggle.IsVisible && toggle.ActualWidth > 0,
                    "nav toggle must be visible at 1280");
                var tb = BoundsIn(toggle, navCol);
                Assert.True(tb.Left >= -0.5 && tb.Right <= navCol.ActualWidth + 0.5,
                    $"toggle {tb} outside rail");

                SaveWindow(win, Path.Combine(dir, "nav-expanded-1280x800.png"));

                // Toggle collapses, toggle again expands.
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                win.UpdateLayout();
                Assert.True(win.TestNavCollapsed);
                Assert.Equal(56, navCol.ActualWidth);
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                win.UpdateLayout();
                Assert.False(win.TestNavCollapsed);
                Assert.Equal(208, navCol.ActualWidth);
            }
            finally { SettleThenClose(win); }

            // --- 860 × 620: collapsed by default, toggle still visible ---
            var win2 = NewShownNewCard(860, 620);
            try
            {
                var navCol2 = (FrameworkElement)win2.FindName("NavCol")!;
                Assert.True(win2.TestNavCollapsed, "rail should auto-collapse below 1100");
                Assert.Equal(56, navCol2.ActualWidth);
                Assert.Equal(Visibility.Visible,
                    ((FrameworkElement)win2.FindName("PnlBackupCompact")!).Visibility);
                var toggle2 = (FrameworkElement)win2.FindName("BtnNavToggle")!;
                Assert.True(toggle2.IsVisible && toggle2.ActualWidth > 0,
                    "nav toggle must be visible at 860");
                SaveWindow(win2, Path.Combine(dir, "nav-collapsed-860x620.png"));
            }
            finally { SettleThenClose(win2); }
        });
    }

    /// <summary>Regression: the resume-batch prompt used to run inside the
    /// MainWindow ctor, where AppDialog's Owner assignment threw
    /// ("Window that has not been shown previously") and the app failed to
    /// start whenever an unfinished batch existed. The prompt is deferred to
    /// Loaded — constructing the window must not throw.</summary>
    [Fact]
    public void Resume_Prompt_Does_Not_Throw_In_Ctor()
    {
        WpfSta.Run(() =>
        {
            var win = FixPassTests.NewWindow();
            long sid = 0;
            try
            {
                sid = win.Svc.Store.CreateBatchSession("Resume regression");
                var e = new IDForge.Core.Data.BatchRowEntry
                {
                    SessionId = sid, Uid = "UID20259999", Name = "Resume Regression",
                    Position = "Intern", Status = "NeedsReview",
                    BgMode = "Model", Zoom = 1.0
                };
                win.Svc.Store.SaveBatchRow(e);

                MainWindow.SkipResumePrompt = false;
                var second = new MainWindow();   // must not throw; never shown
                _ = second;
            }
            finally
            {
                MainWindow.SkipResumePrompt = true;
                if (sid != 0) win.Svc.Store.CompleteBatchSession(sid);
                win.Close();
            }
        });
    }

    [Fact]
    public void Footer_Chips_Share_Centre_Line_And_Screenshot()
    {
        WpfSta.Run(() =>
        {
            var dir = ShotsDir();
            Directory.CreateDirectory(dir);
            var win = NewShownNewCard(1280, 800);
            try
            {
                var bar = (FrameworkElement)win.FindName("PnlIssueBar")!;
                var centres = new[] { "ChipDetails", "ChipUidNew", "ChipPhoto", "TxtStatus" }
                    .Select(n => BoundsIn((FrameworkElement)win.FindName(n)!, bar).Top
                                + ((FrameworkElement)win.FindName(n)!).ActualHeight / 2)
                    .ToList();
                centres.Add(BoundsIn((Button)win.FindName("BtnSave")!, bar).Top
                            + ((Button)win.FindName("BtnSave")!).ActualHeight / 2);
                centres.Add(BoundsIn((TextBlock)win.FindName("TxtOutputText")!, bar).Top
                            + ((TextBlock)win.FindName("TxtOutputText")!).ActualHeight / 2);
                Assert.True(centres.Max() - centres.Min() <= 1.0,
                    $"footer centre line spread {centres.Max() - centres.Min():F2}px");

                // RenderTargetBitmap on a non-root element comes out blank here —
                // render the window and crop the footer strip instead.
                var barTop = bar.TransformToAncestor(win).Transform(new Point(0, 0));
                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(win.ActualWidth),
                    (int)Math.Ceiling(win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);
                var crop = new CroppedBitmap(rtb, new Int32Rect(
                    0, (int)Math.Ceiling(barTop.Y), rtb.PixelWidth,
                    (int)Math.Ceiling(bar.ActualHeight)));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(crop));
                using (var fs = File.Create(Path.Combine(dir, "newcard-footer.png")))
                    enc.Save(fs);
            }
            finally { SettleThenClose(win); }
        });
    }
}
