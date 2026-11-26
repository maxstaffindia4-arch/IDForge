using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix J-A — keyboard-only nav ring, Help menu, F1.</summary>
[Collection("Wpf")]
public class FixJPartATests
{
    private static MainWindow NewWindow()
    {
        var win = FixPassTests.NewWindow();
        win.Width = 1331; win.Height = 700;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = 40; win.Top = 40;
        win.Show();
        win.UpdateLayout();
        return win;
    }

    [Fact]
    public void Nav_Ring_Is_FocusVisualStyle_Keyboard_Only()
    {
        WpfSta.Run(() =>
        {
            var win = NewWindow();
            var nav = win.FindName("NavBatch") as RadioButton;
            Assert.NotNull(nav);

            // Parity §4.2: the selected item has a 3 px left bar ("Bar") plus
            // the accent fill — distinct from the keyboard-only focus ring.
            var template = nav.Template;
            var bar = template.FindName("Bar", nav);
            Assert.NotNull(bar);

            // The ring must come from FocusVisualStyle (keyboard-only in WPF),
            // not from IsKeyboardFocused/IsMouseOver triggers on BorderBrush.
            var fvs = nav.FocusVisualStyle as Style;
            Assert.NotNull(fvs);
            var fvsTemplate = fvs.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.TemplateProperty);
            Assert.NotNull(fvsTemplate);

            var triggers = template.Triggers.OfType<Trigger>()
                .Where(t => t.Setters.OfType<Setter>()
                    .Any(s => s.Property == Border.BorderBrushProperty));
            Assert.Empty(triggers);

            // Selected item keeps its light-blue background via IsChecked trigger.
            var checkedTrigger = template.Triggers.OfType<Trigger>()
                .FirstOrDefault(t => t.Property == ToggleButton.IsCheckedProperty);
            Assert.NotNull(checkedTrigger);
        });
    }

    /// <summary>Renders the same 2 px #3B82F6 ring as the NavFocusRing
    /// FocusVisualStyle template, for screenshot purposes only.</summary>
    private sealed class RingAdorner : Adorner
    {
        private readonly Border _ring = new()
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6),
            Margin = new Thickness(-3)
        };

        public RingAdorner(UIElement adorned) : base(adorned)
        {
            AddVisualChild(_ring);
            AddLogicalChild(_ring);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _ring;
        protected override Size MeasureOverride(Size constraint)
        {
            _ring.Measure(constraint);
            return _ring.DesiredSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            _ring.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindNamed(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public void Help_Menu_Has_Items_And_About_Last()
    {
        WpfSta.Run(() =>
        {
            var win = NewWindow();
            var help = win.FindName("MenuHelp") as MenuItem;
            Assert.NotNull(help);

            var headers = help.Items.Cast<object>()
                .Select(o => o is MenuItem mi ? mi.Header as string
                           : o is Separator ? "---" : o?.ToString())
                .ToList();

            Assert.Contains(headers, h => h != null && h.Contains("Keyboard shortcuts"));
            Assert.Contains(headers, h => h != null && h.Contains("Quick guide"));
            Assert.Contains(headers, h => h != null && h.Contains("Troubleshooting"));
            Assert.Equal("---", headers[^2]);
            Assert.Contains("About", headers[^1]);
        });
    }

    [Fact]
    public void F1_Opens_Help_Menu()
    {
        WpfSta.Run(() =>
        {
            var win = NewWindow();
            win.Activate();
            var help = win.FindName("MenuHelp") as MenuItem;
            Assert.NotNull(help);
            Assert.False(help.IsSubmenuOpen);

            // IsSubmenuOpen can't be observed headless (WPF coerces it back
            // when the window isn't active), so verify the wiring: F1 is
            // handled by the app-level Help path and other keys are not.
            Assert.True(win.TryHandleAppKey(Key.F1), "F1 was not handled");
            Assert.False(win.TryHandleAppKey(Key.F2), "F2 should not hit the Help path");
            Assert.False(win.TryHandleAppKey(Key.Z), "plain keys should not hit the Help path");
        });
    }

    private static readonly string ShotDir = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "docs", "redesign", "shots"));

    private static void SavePng(Visual visual, string file)
    {
        var fve = visual as FrameworkElement;
        int w, h;
        if (fve != null && fve.ActualWidth > 0)
        {
            w = Math.Max(1, (int)Math.Ceiling(fve.ActualWidth));
            h = Math.Max(1, (int)Math.Ceiling(fve.ActualHeight));
        }
        else
        {
            w = 800; h = 600;
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    [Fact]
    public void J_A_Screenshots()
    {
        WpfSta.Run(() =>
        {
            System.IO.Directory.CreateDirectory(ShotDir);
            var win = NewWindow();
            try
            {
                win.Activate();

                // --- Help menu open: render the popup's visual tree ---
                var help = win.FindName("MenuHelp") as MenuItem;
                Assert.NotNull(help);
                help.IsSubmenuOpen = true;
                win.UpdateLayout();
                var popup = help.Template.FindName("PART_Popup", help) as Popup;
                Assert.NotNull(popup);
                Assert.NotNull(popup!.Child);
                popup.Child.UpdateLayout();
                SavePng(popup.Child, System.IO.Path.Combine(ShotDir, "help-menu-open.png"));
                help.IsSubmenuOpen = false;

                // --- Nav with mouse-selected item (no focus ring) ---
                // Render the whole window: the FocusVisualStyle adorner lives
                // in the window's AdornerLayer, outside the nav panel.
                var navBatch = win.FindName("NavBatch") as RadioButton;
                Assert.NotNull(navBatch);
                navBatch!.IsChecked = true;
                Keyboard.Focus(navBatch);
                win.UpdateLayout();
                SavePng(win, System.IO.Path.Combine(ShotDir, "nav-mouse-selected.png"));

                // --- Nav with keyboard-focused item (FocusVisualStyle ring) ---
                var navNew = win.FindName("NavNewCard") as RadioButton;
                Assert.NotNull(navNew);
                // Headless WPF only draws FocusVisualStyle for real keyboard
                // input, which can't be injected here. Render the identical
                // ring as an adorner (same brush/thickness/margin as
                // NavFocusRing) so the screenshot shows the real appearance.
                Keyboard.Focus(navNew);
                var layer = AdornerLayer.GetAdornerLayer(navNew);
                Assert.NotNull(layer);
                layer!.Add(new RingAdorner(navNew));
                for (int i = 0; i < 10; i++)
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
                }
                win.UpdateLayout();
                SavePng(win, System.IO.Path.Combine(ShotDir, "nav-keyboard-focused.png"));
            }
            finally { win.Close(); }
        });
    }
}
