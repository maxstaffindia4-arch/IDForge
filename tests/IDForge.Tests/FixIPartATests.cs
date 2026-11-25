using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix I-A — nav selection indicator and New card footer row.</summary>
[Collection("Wpf")]
public class FixIPartATests
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

    [Fact]
    public void Nav_Selection_Has_Tint_No_Bar_And_Focus_Ring_Only_On_Keyboard()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownWindow(1331, 700);
            try
            {
                var nav = (RadioButton)win.FindName("NavBatch")!;
                nav.IsChecked = true;
                win.UpdateLayout();

                // Selected item: light-blue background, NO accent rectangle.
                var tpl = nav.Template;
                var bd = (Border)tpl.FindName("bd", nav)!;
                var bg = (SolidColorBrush)bd.Background;
                var tint = (SolidColorBrush)Application.Current!.FindResource("PrimaryTintBrush");
                Assert.Equal(tint.Color, bg.Color);
                var accent = Descendants(nav).OfType<Rectangle>()
                    .FirstOrDefault(r => r.Name == "accent");
                Assert.True(accent == null || accent.Visibility == Visibility.Collapsed
                    || ((SolidColorBrush)accent.Fill).Color.A == 0,
                    "selected nav item still shows the accent bar");

                // Ring only on keyboard focus: no IsChecked/IsMouseOver trigger
                // may set BorderBrush; only IsKeyboardFocused(Within) may.
                var borderSetters = ((ControlTemplate)nav.Template).Triggers.Cast<Trigger>()
                    .Where(t => t.Setters.OfType<Setter>().Any(s => s.Property == Border.BorderBrushProperty))
                    .Select(t => t.Property?.Name).ToList();
                Assert.All(borderSetters, n => Assert.Contains("KeyboardFocus", n));
            }
            finally { win.Close(); }
        });
    }

    [Fact]
    public void Footer_Items_Share_One_Centre_Line()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownWindow(1331, 700);
            try
            {
                win.ShowArea("newcard");
                win.UpdateLayout();
                var names = new[] { "ChipDetails", "TxtStatus", "TxtOutputText",
                                    "BtnOutputSettings", "BtnSave" };
                var centres = new List<double>();
                foreach (var n in names)
                {
                    var fe = win.FindName(n) as FrameworkElement;
                    if (fe == null || fe.Visibility == Visibility.Collapsed) continue;
                    var tl = fe.TransformToAncestor(win).Transform(new Point(0, 0));
                    centres.Add(tl.Y + fe.ActualHeight / 2);
                }
                Assert.True(centres.Count >= 3, $"footer items found: {centres.Count}");
                Assert.True(centres.Max() - centres.Min() <= 1.0,
                    $"footer centre spread {centres.Max() - centres.Min():F2}px: {string.Join(", ", centres)}");

                // Screenshot: New card footer region.
                var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..", "docs", "redesign", "shots"));
                System.IO.Directory.CreateDirectory(dir);
                var footer = win.FindName("PageNewCard") as FrameworkElement;
                Assert.NotNull(footer);
                // Render just the footer border (Grid.Row=2 of page grid).
                FrameworkElement? footerBar = null;
                foreach (var d in Descendants(footer!).OfType<Border>())
                    if (d.BorderThickness.Top == 1 && d.ActualHeight is > 30 and < 90)
                        footerBar = d;
                var target = (Visual)(footerBar ?? footer!);
                int w = Math.Max(1, (int)Math.Ceiling(((FrameworkElement)target).ActualWidth));
                int h = Math.Max(1, (int)Math.Ceiling(((FrameworkElement)target).ActualHeight));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(target);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "newcard-footer.png"));
                enc.Save(fs);
            }
            finally { win.Close(); }
        });
    }
}
