using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix K-B — Records layout: the grid must end above the bottom
/// button bar, and the bar has its own opaque background so no grid content
/// or scrollbar shows through the gaps between buttons.</summary>
[Collection("Wpf")]
public class FixKPartBTests
{
    private static MainWindow NewShownRecordsWindow(int w, int h)
    {
        var win = FixPassTests.NewWindow();
        win.Width = w; win.Height = h;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = 40; win.Top = 40;
        win.Show();
        win.ShowArea("records");
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

    private static Rect BoundsIn(FrameworkElement el, FrameworkElement ancestor)
    {
        var tl = el.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        return new Rect(tl, new Size(el.ActualWidth, el.ActualHeight));
    }

    private static void AssertOpaqueBar(Panel bar)
    {
        var bg = bar.Background as SolidColorBrush;
        Assert.NotNull(bg);
        Assert.Equal(255, bg!.Color.A);
    }

    [Theory]
    [InlineData(860, 620)]
    [InlineData(1331, 700)]
    public void Records_Grid_Ends_Above_Opaque_Button_Bar(int w, int h)
    {
        WpfSta.Run(() =>
        {
            var win = NewShownRecordsWindow(w, h);
            try
            {
                var page = (FrameworkElement)win.FindName("PageRecords")!;
                var grid = (FrameworkElement)win.FindName("LstRecords")!;
                var actions = (StackPanel)win.FindName("PnlRecActions")!;
                var tools = (StackPanel)win.FindName("PnlRecTools")!;

                var gridBounds = BoundsIn(grid, page);
                var actionsBounds = BoundsIn(actions, page);
                var toolsBounds = BoundsIn(tools, page);

                Assert.True(gridBounds.Bottom <= actionsBounds.Top + 0.5,
                    $"grid bottom {gridBounds.Bottom:F1} overlaps bar top {actionsBounds.Top:F1}");
                Assert.True(actionsBounds.Bottom <= toolsBounds.Top + 0.5,
                    "action bar overlaps tools bar");
                Assert.True(toolsBounds.Bottom <= page.ActualHeight + 0.5,
                    "tools bar extends past page bottom");

                AssertOpaqueBar(actions);
                AssertOpaqueBar(tools);
            }
            finally { win.Close(); }
        });
    }

    [Fact]
    public void Records_Screen_Screenshots()
    {
        WpfSta.Run(() =>
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "docs", "redesign", "shots"));
            Directory.CreateDirectory(dir);

            foreach (var (w, h, name) in new[]
            {
                (860, 620, "records-860x620.png"),
                (1331, 700, "records-1331x700.png")
            })
            {
                var win = NewShownRecordsWindow(w, h);
                try
                {
                    var rtb = new RenderTargetBitmap(
                        (int)Math.Ceiling(win.ActualWidth),
                        (int)Math.Ceiling(win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(win);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, name));
                    enc.Save(fs);
                }
                finally { win.Close(); }
            }
        });
    }
}
