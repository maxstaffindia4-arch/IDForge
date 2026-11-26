using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix H — rendered-clipping checks + screenshots for the
/// collapsed rail and the Batch grid.</summary>
[Collection("Wpf")]
public class FixHTests
{
    private static void OnSta(Action work) => WpfSta.Run(work);

    private static MainWindow NewShownWindow(double w, double h)
    {
        var win = FixPassTests.NewWindow();
        win.Width = w; win.Height = h;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = 40; win.Top = 40;   // keep on-screen so WPF performs layout
        win.Show();
        Pump(win);
        return win;
    }

    /// <summary>Forces the deferred first layout/render pass.</summary>
    private static void Pump(Visual v)
    {
        for (int i = 0; i < 10; i++)
        {
            if (v is FrameworkElement fe && fe.ActualWidth > 0) break;
            var rtb = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (v is FrameworkElement f2) f2.UpdateLayout();
        }
        if (v is FrameworkElement f3) f3.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    /// <summary>DesiredSize includes the element's Margin in this layout pass,
    /// so the clip test compares desired against actual + margin.</summary>
    private static bool IsSizeClipped(FrameworkElement fe)
        => fe.ActualWidth + fe.Margin.Left + fe.Margin.Right + 0.51 < fe.DesiredSize.Width
        || fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom + 0.51 < fe.DesiredSize.Height;

    private static bool InsideRect(FrameworkElement fe, FrameworkElement parent, out string detail)
    {
        var tl = fe.TransformToAncestor(parent).Transform(new Point(0, 0));
        var r = new Rect(tl, new Size(fe.ActualWidth, fe.ActualHeight));
        var pr = new Rect(0, 0, parent.ActualWidth, parent.ActualHeight);
        detail = $"{fe.GetType().Name} rect=({r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1}) " +
                 $"parent=({pr.Width:F1}x{pr.Height:F1})";
        return r.Left >= -0.5 && r.Top >= -0.5 &&
               r.Right <= pr.Right + 0.5 && r.Bottom <= pr.Bottom + 0.5;
    }

    /// <summary>Rendered check: every named rail control is at least its
    /// desired size and lies fully inside the sidebar rail.</summary>
    private static void AssertRailClean(MainWindow w)
    {
        // The sidebar rail is the Border column hosting the nav stack —
        // find the narrowest Border ancestor of NavNewCard.
        FrameworkElement? rail = null;
        var chain = new List<string>();
        for (var cur = (DependencyObject?)w.FindName("NavNewCard"); cur != null && chain.Count < 14;
             cur = VisualTreeHelper.GetParent(cur))
        {
            chain.Add(cur is FrameworkElement fe
                ? $"{fe.GetType().Name}#{fe.Name} {fe.ActualWidth:F0}x{fe.ActualHeight:F0}"
                : cur.GetType().Name);
            if (cur is Border b && b.ActualWidth is > 40 and < 300 && b.ActualHeight > 300) { rail = b; break; }
        }
        Assert.True(rail != null, "rail not found; ancestor chain: " + string.Join(" <- ", chain));

        var names = new List<string> { "NavNewCard", "NavBatch", "NavRecords", "NavSettings", "PnlBackupCompact" };
        var badge = w.FindName("NavBatchBadge") as FrameworkElement;
        if (badge is { Visibility: Visibility.Visible }) names.Add("NavBatchBadge");

        foreach (var name in names)
        {
            var fe = w.FindName(name) as FrameworkElement
                ?? throw new Exception($"{name} not found");
            Assert.False(IsSizeClipped(fe),
                $"{name} clipped: actual={fe.ActualWidth:F1}x{fe.ActualHeight:F1} " +
                $"desired={fe.DesiredSize.Width:F1}x{fe.DesiredSize.Height:F1} margin={fe.Margin}");
            Assert.True(InsideRect(fe, rail!, out var d), $"{name} outside rail: {d}");
        }
        Assert.True(rail!.ActualWidth >= 56, $"rail too narrow: {rail.ActualWidth}");
    }

    /// <summary>Rendered check: every element inside each Batch row lies fully
    /// inside its parent (ultimately its cell/row) and is not smaller than its
    /// desired size.</summary>
    private static void AssertBatchRowsClean(DataGrid dg)
    {
        var rows = dg.Items.Cast<object>()
            .Select((_, i) => dg.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow)
            .Where(r => r != null).Cast<DataGridRow>().ToList();
        if (rows.Count == 0)
            rows = Descendants(dg).OfType<DataGridRow>().ToList();
        var pageVis = dg.Parent is FrameworkElement pp ? pp.Visibility.ToString() : "?";
        Assert.True(rows.Count > 0,
            $"no DataGridRow realized; dg.Items={dg.Items.Count} " +
            $"vis={dg.Visibility} actual={dg.ActualWidth:F0}x{dg.ActualHeight:F0} pageVis={pageVis}");
        foreach (var row in rows)
        {
            foreach (var d in Descendants(row))
            {
                if (d is not FrameworkElement fe || fe.Visibility == Visibility.Collapsed) continue;
                if (fe is DataGridRow or DataGridCellsPresenter or ScrollViewer or Border { Name: "bd" }) continue;
                // skip elements whose parent is a ScrollViewer viewport (by design)
                if (IsSizeClipped(fe))
                {
                    // ignore purely horizontal star-column trims? No — report all.
                    Assert.Fail($"row {row.GetIndex()}: {fe.GetType().Name} " +
                        $"\"{(fe is TextBlock t ? t.Text : fe is Button b ? b.Content?.ToString() : "")}\" " +
                        $"actual={fe.ActualWidth:F1}x{fe.ActualHeight:F1} " +
                        $"desired={fe.DesiredSize.Width:F1}x{fe.DesiredSize.Height:F1} margin={fe.Margin}");
                }
                if (fe.Parent is DependencyObject pd && pd is Visual pv && pv != row)
                {
                    try
                    {
                        Assert.True(InsideRect(fe, (FrameworkElement)pv, out var det),
                            $"row {row.GetIndex()}: outside parent — {det}");
                    }
                    catch (InvalidCastException) { /* parent is a non-FE visual (e.g. Viewbox) */ }
                }
            }
        }
    }

    /// <summary>All rendered checks run on ONE STA thread: WPF windows only
    /// lay out on the dispatcher that created the Application, so splitting
    /// these across facts would leave later windows unrendered.</summary>
    [Fact]
    public void Collapsed_Rail_And_Batch_Rows_Have_No_Rendered_Clipping()
    {
        OnSta(() =>
        {
            // Keep every window open until the end: closing one disposes its
            // CardStore/SQLite connection, which can poison the pool for the
            // next window's store on the same database file.
            var w = NewShownWindow(860, 620);
            {
                w.BatchRows.Add(new BatchRow { Uid = "70000888", Name = "Rail Badge", Status = "NeedsReview" });
                w.UpdateBatchSummary();
                w.Window_SizeChanged(w, null!);
                Pump(w);
                Assert.True(w.TestNavCollapsed);
                AssertRailClean(w);
                // H-A: collapsed rail hides the section headings, shows thin
                // dividers instead, and draws only the backup icon tile.
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("LblIssueCards")!).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("LblManage")!).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)w.FindName("DvdIssueCards")!).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)w.FindName("DvdManage")!).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("PnlBackupFull")!).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)w.FindName("PnlBackupCompact")!).Visibility);
                var badge = (FrameworkElement)w.FindName("NavBatchBadge")!;
                Assert.Equal(Visibility.Visible, badge.Visibility);
                w.BatchRows.Clear();
                w.UpdateBatchSummary();
                Assert.Equal(Visibility.Collapsed, badge.Visibility);
            }

            var w2 = NewShownWindow(1331, 700);
            {
                w2.BatchRows.Add(new BatchRow
                {
                    Uid = "70000999", Name = "Diag Person", Position = "Supervisor",
                    Contract = "Permanent", Blood = "O+", PrintDateDisplay = "01/01/2027",
                    Status = "NeedsReview"
                });
                w2.UpdateBatchSummary();
                w2.ShowArea("batch");
                w2.UpdateLayout();
                var dg = (DataGrid)w2.FindName("DgBatch")!;
                dg.ScrollIntoView(dg.Items[0]);
                Pump(dg);
                Pump(w2);
                AssertBatchRowsClean(dg);
                Assert.True(dg.DesiredSize.Width <= dg.ActualWidth + 0.5,
                    $"grid wants {dg.DesiredSize.Width:F1} but has {dg.ActualWidth:F1}");

                // H-A: selected row text must contrast with the row background.
                dg.SelectedIndex = 0;
                Pump(dg); Pump(w2);
                AssertSelectedRowTextVisible(dg);
            }

            // --- 3. screenshots ---
            var dir = ShotDir();
            var w3 = NewShownWindow(860, 620);
            {
                w3.BatchRows.Add(new BatchRow { Uid = "70000888", Name = "Rail Badge", Status = "NeedsReview" });
                w3.UpdateBatchSummary();
                w3.Window_SizeChanged(w3, null!);
                Pump(w3);
                SavePng(w3, Path.Combine(dir, "collapsed-rail-860x620.png"));
            }

            var w4 = NewShownWindow(1331, 700);
            {
                w4.ShowArea("newcard");
                Pump(w4);
                SavePng(w4, Path.Combine(dir, "expanded-1331x700.png"));

                w4.BatchRows.Add(new BatchRow
                {
                    Uid = "70000999", Name = "Diag Person", Position = "Supervisor",
                    Contract = "Permanent", Blood = "O+", PrintDateDisplay = "01/01/2027",
                    Status = "NeedsReview"
                });
                w4.UpdateBatchSummary();
                w4.ShowArea("batch");
                var dg4 = (DataGrid)w4.FindName("DgBatch")!;
                dg4.ScrollIntoView(dg4.Items[0]);
                Pump(dg4);
                Pump(w4);
                SavePng(w4, Path.Combine(dir, "batch-1331x700.png"));
                // H-A screenshot: Batch with a selected row
                dg4.SelectedIndex = 0;
                Pump(dg4); Pump(w4);
                SavePng(w4, Path.Combine(dir, "batch-selected-row-1331x700.png"));
            }

            w.Close(); w2.Close(); w3.Close(); w4.Close();
        });
    }

    /// <summary>H-A: text inside the selected Batch row must have a foreground
    /// that contrasts with the effective background (cells, row, grid).</summary>
    private static void AssertSelectedRowTextVisible(DataGrid dg)
    {
        var row = Descendants(dg).OfType<DataGridRow>()
            .FirstOrDefault(r => r.IsSelected);
        Assert.True(row != null, "no selected DataGridRow realised");
        var texts = Descendants(row!).OfType<TextBlock>().Where(t => t.Text.Length > 0).ToList();
        Assert.True(texts.Count >= 3, $"expected cell text in selected row, found {texts.Count}");
        foreach (var fe in texts.Cast<FrameworkElement>())
        {
            var fg = EffectiveBrush(fe, f => (f as TextBlock)?.Foreground, VisualTreeHelper.GetParent);
            var bg = EffectiveBrush(row, f => (f as Panel)?.Background ?? (f as Border)?.Background, VisualTreeHelper.GetParent);
            Assert.True(Contrast(fg!, bg!) >= 2.0,
                $"selected-row text \"{((TextBlock)fe).Text}\" fg={fg} vs bg={bg} — contrast {Contrast(fg!, bg!):F2}");
        }
    }

    private static Brush? EffectiveBrush(DependencyObject start,
        Func<DependencyObject, Brush?> pick, Func<DependencyObject, DependencyObject?> parent)
    {
        for (var cur = start; cur != null; cur = parent(cur))
        {
            var b = pick(cur);
            if (b != null && b != Brushes.Transparent) return b;
        }
        return Brushes.White;
    }

    private static double Contrast(Brush fg, Brush bg)
    {
        var f = ((SolidColorBrush)fg).Color; var b = ((SolidColorBrush)bg).Color;
        double L1 = Lum(f), L2 = Lum(b);
        if (L1 < L2) (L1, L2) = (L2, L1);
        return (L1 + 0.05) / (L2 + 0.05);
    }

    private static double Lum(Color c)
    {
        double R = Lin(c.R), G = Lin(c.G), B = Lin(c.B);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    }

    private static string ShotDir()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "docs", "redesign", "shots"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(Visual visual, string file)
    {
        int w = (int)Math.Ceiling(visual is FrameworkElement fe ? fe.ActualWidth : 0);
        int h = (int)Math.Ceiling(visual is FrameworkElement fe2 ? fe2.ActualHeight : 0);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

}
