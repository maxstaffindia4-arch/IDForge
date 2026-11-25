using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix J-B — Batch grid: delete recounts, badge hides at zero,
/// zero-state wording.</summary>
[Collection("Wpf")]
public class FixJPartBTests
{
    private static MainWindow NewShownWindow()
    {
        var win = FixPassTests.NewWindow();
        win.Width = 1331; win.Height = 700;
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

    [Fact]
    public void Delete_Only_Row_Hides_Badge_And_Shows_No_Rows_Yet()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownWindow();
            MainWindow.ConfirmRemoveRow = _ => true;
            try
            {
                win.ShowArea("batch");
                win.UpdateBatchSummary();
                var counts = (TextBlock)win.FindName("TxtBatchCounts")!;
                var badge = (Border)win.FindName("NavBatchBadge")!;

                // Empty batch: "No rows yet", badge hidden.
                Assert.Equal("No rows yet", counts.Text);
                Assert.Equal(Visibility.Collapsed, badge.Visibility);

                // Add a content row -> counts and badge appear.
                win.BatchRows.Add(new BatchRow { Name = "A", Uid = "1", Status = "Draft" });
                win.UpdateBatchSummary();
                Assert.NotEqual("No rows yet", counts.Text);
                Assert.Equal(Visibility.Visible, badge.Visibility);

                // Delete it -> counts fall back to "No rows yet", badge hides.
                var dg = (DataGrid)win.FindName("DgBatch")!;
                dg.SelectedIndex = 0;
                win.BatchDelRow_Click(win, null!);
                Assert.Empty(win.BatchRows);
                Assert.Equal("No rows yet", counts.Text);
                Assert.Equal(Visibility.Collapsed, badge.Visibility);

                // Rows that exist but have no content -> "Add details to begin".
                win.BatchRows.Add(new BatchRow { Status = "Draft" });
                win.UpdateBatchSummary();
                Assert.Equal("Add details to begin", counts.Text);

                // Screenshot: one empty row + one row being edited (counter up).
                var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "docs", "redesign", "shots"));
                System.IO.Directory.CreateDirectory(dir);
                var dg2 = (DataGrid)win.FindName("DgBatch")!;
                win.BatchRows.Clear();
                win.BatchRows.Add(new BatchRow { Status = "Draft" });
                win.BatchRows.Add(new BatchRow { Name = "Editing", Uid = "12345678", Status = "Draft" });
                win.UpdateBatchSummary();
                dg2.ScrollIntoView(dg2.Items[0]);
                PumpGrid(dg2); PumpGrid(win);
                var rows = System.Windows.Media.VisualTreeHelper
                    .GetChildrenCount(dg2) > 0
                    ? AllVisuals(dg2).OfType<DataGridRow>().ToList()
                    : new List<DataGridRow>();
                var uidBox = rows.Count > 1
                    ? AllVisuals(rows[1]).OfType<TextBox>()
                        .FirstOrDefault(t => t.Name == "UidInput")
                    : null;
                uidBox?.Focus();
                PumpGrid(dg2); PumpGrid(win);
                var rtb = new RenderTargetBitmap(
                    (int)win.ActualWidth, (int)win.ActualHeight, 96, 96,
                    PixelFormats.Pbgra32);
                rtb.Render(win);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(
                    System.IO.Path.Combine(dir, "batch-editing-1331x700.png"));
                enc.Save(fs);
            }
            finally { win.Close(); MainWindow.ConfirmRemoveRow = null; }
        });
    }

    private static IEnumerable<DependencyObject> AllVisuals(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var d in AllVisuals(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static void PumpGrid(Visual v)
    {
        for (int i = 0; i < 10; i++)
        {
            var rtb = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            if (v is FrameworkElement fe) fe.UpdateLayout();
        }
    }
}
