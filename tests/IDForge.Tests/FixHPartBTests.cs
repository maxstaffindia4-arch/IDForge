using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix H-B — batch grid behaviour: no double-click handler, dropdown
/// click-through, empty rows not counted.</summary>
[Collection("Wpf")]
public class FixHPartBTests
{
    private static void OnSta(Action work) => WpfSta.Run(work);

    private static MainWindow NewShownWindow(double w, double h)
    {
        var win = FixPassTests.NewWindow();
        win.Width = w; win.Height = h;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = 40; win.Top = 40;
        win.Show();
        for (int i = 0; i < 10; i++)
        {
            if (win.ActualWidth > 0) break;
            var rtb = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            win.UpdateLayout();
        }
        win.UpdateLayout();
        return win;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    [Fact]
    public void Batch_DoubleClick_Has_No_Handler()
    {
        // The old DgBatch_MouseDoubleClick opened Review; it must be gone.
        var m = typeof(MainWindow).GetMethod("DgBatch_MouseDoubleClick",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Null(m);
    }

    /// <summary>Both WPF scenarios run on ONE STA thread: windows only lay out
    /// on the dispatcher that created the Application.</summary>
    [Fact]
    public void Batch_Dropdown_And_Empty_Row_Behaviour()
    {
        OnSta(() =>
        {
            var win = NewShownWindow(1331, 700);
            try
            {
                win.BatchRows.Add(new BatchRow { Uid = "61000001", Name = "Combo One", Status = "NeedsReview" });
                win.BatchRows.Add(new BatchRow { Uid = "61000002", Name = "Combo Two", Status = "NeedsReview" });
                win.UpdateBatchSummary();
                win.ShowArea("batch");
                var dg = (DataGrid)win.FindName("DgBatch")!;
                dg.ScrollIntoView(dg.Items[0]);
                for (int i = 0; i < 10; i++)
                {
                    var rtb = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dg);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                    dg.UpdateLayout();
                }
                var allCombos = Descendants(dg).OfType<ComboBox>().ToList();
                var rows = Descendants(dg).OfType<DataGridRow>().ToList();
                var combos = allCombos
                    .Where(c => c.ItemsSource is ICollection { Count: > 1 })
                    .ToList();
                Assert.True(combos.Count >= 4,
                    $"expected realised cell combos, found {combos.Count} " +
                    $"(all combos={allCombos.Count}, rows={rows.Count}, " +
                    $"items={dg.Items.Count}, dg={dg.ActualWidth:F0}x{dg.ActualHeight:F0}, " +
                    $"sources=[{string.Join("|", allCombos.Select(c => c.ItemsSource?.GetType().Name ?? "null"))}])");
                var cbA = combos[0];
                var cbB = combos[combos.Count - 1];   // a different column/row combo
                Assert.False(ReferenceEquals(cbA, cbB));

                // Simulate: cbA's popup was open, the dismissing click landed on cbB.
                cbA.IsDropDownOpen = true;
                cbA.IsDropDownOpen = false;             // popup already closed by the click
                var tl = cbB.TransformToAncestor(dg).Transform(new Point(0, 0));
                var pt = new Point(tl.X + cbB.ActualWidth / 2, tl.Y + cbB.ActualHeight / 2);
                win.TryOpenComboUnderMouse(cbA, pt);
                Assert.True(cbB.IsDropDownOpen, "combo under the pointer did not open");

                // A dismiss click landing back on the same combo must not reopen it.
                cbB.IsDropDownOpen = false;
                var tl2 = cbB.TransformToAncestor(dg).Transform(new Point(0, 0));
                var pt2 = new Point(tl2.X + cbB.ActualWidth / 2, tl2.Y + cbB.ActualHeight / 2);
                win.TryOpenComboUnderMouse(cbB, pt2);
                Assert.False(cbB.IsDropDownOpen, "closed combo reopened by its own dismiss click");
            }
            finally { /* keep open — see below */ }

            // Empty rows aren't counted (second window, same dispatcher thread).
            var win2 = NewShownWindow(1331, 700);
            try
            {
                var empty = new BatchRow();
                win2.BatchRows.Add(empty);
                win2.PersistBatchRow(empty);
                win2.UpdateBatchSummary();
                var counts = ((TextBlock)win2.FindName("TxtBatchCounts")!).Text;
                Assert.DoesNotContain("to review", counts);
                Assert.Equal("No rows yet", counts);
                Assert.Equal("", empty.Message);

                // Once it has any content it counts again, with the Missing hint.
                empty.Name = "Partial Person";
                win2.UpdateBatchSummary();
                counts = ((TextBlock)win2.FindName("TxtBatchCounts")!).Text;
                Assert.Contains("to review", counts);
                Assert.Contains("Missing:", empty.Message);
            }
            finally { win.Close(); win2.Close(); }
        });
    }
}
