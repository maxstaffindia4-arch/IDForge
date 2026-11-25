using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix I-C — Position placeholder clears the dropdown arrow;
/// header says "Add details to begin" when rows exist but are empty.</summary>
[Collection("Wpf")]
public class FixIPartCTests
{
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

    private static void Pump(Visual v)
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

    [Fact]
    public void Position_Placeholder_Clears_Arrow_And_Header_Wording()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownWindow(1331, 700);
            try
            {
                // Header: rows exist but empty -> "Add details to begin".
                win.BatchRows.Add(new BatchRow());
                win.UpdateBatchSummary();
                var counts = ((TextBlock)win.FindName("TxtBatchCounts")!).Text;
                Assert.Equal("Add details to begin", counts);
                Assert.DoesNotContain("No rows yet", counts);

                win.ShowArea("batch");
                var dg = (DataGrid)win.FindName("DgBatch")!;
                dg.ScrollIntoView(dg.Items[0]);
                Pump(dg); Pump(win);
                var row = Descendants(dg).OfType<DataGridRow>().First();

                // Placeholder bounds must end before the combo's arrow starts.
                var cell = Descendants(row).OfType<DataGridCell>()
                    .First(c => (c.Column?.Header as string) == "Position");
                var ph = Descendants(cell).OfType<TextBlock>()
                    .First(t => t.Text == "Choose position");
                var combo = Descendants(cell).OfType<ComboBox>().First();
                var phRect = ph.TransformToAncestor(cell).TransformBounds(
                    new Rect(0, 0, ph.ActualWidth, ph.ActualHeight));
                var comboRect = combo.TransformToAncestor(cell).TransformBounds(
                    new Rect(0, 0, combo.ActualWidth, combo.ActualHeight));
                // The drop-down arrow occupies the right ~24 px of the combo.
                var arrowLeft = comboRect.Right - 24;
                Assert.True(phRect.Right <= arrowLeft + 0.5,
                    $"placeholder right {phRect.Right:F1} overlaps arrow zone starting {arrowLeft:F1}");
            }
            finally { win.Close(); }
        });
    }
}
