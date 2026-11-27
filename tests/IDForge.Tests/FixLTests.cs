using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>Fix L — batch row count after delete. Drives the real UI path:
/// "Add row" / "Remove selected (Del)" button clicks and a real KeyDown
/// Delete on the window. Asserts badge, header/counts line, grid, and the
/// persisted batch rows after every delete.</summary>
[Collection("Wpf")]
public class FixLTests
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static Button? FindButton(Window win, string content)
        => Descendants(win).OfType<Button>()
            .FirstOrDefault(b => b.Content as string == content
                || b.Name == content);

    private static void Click(Window win, string content)
    {
        var btn = FindButton(win, content) ?? win.FindName(content) as Button;
        Assert.NotNull(btn);
        btn!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>Raises a real KeyDown(Delete) on the window — the same routed
    /// event WPF delivers for the Del key, handled by Window_KeyDown.</summary>
    private static void PressDelete(Window win)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(win), 0, Key.Delete);
        var prop = typeof(RoutedEventArgs).GetProperty("RoutedEvent",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        prop.GetSetMethod(true)!.Invoke(args, new object[] { Keyboard.KeyDownEvent });
        win.RaiseEvent(args);
    }

    private static TextBlock Txt(Window win, string name)
        => (TextBlock)win.FindName(name)!;

    private static int SavedBatchRowCount(MainWindow win)
    {
        var sess = win.Svc.Store.LatestOpenSession();
        return sess == null ? 0 : win.Svc.Store.BatchRows(sess.Id).Count;
    }

    [Fact]
    public void Delete_Via_Button_Then_Del_Updates_All_Counts()
    {
        WpfSta.Run(() =>
        {
            var win = FixPassTests.NewWindow();
            IDForge.Core.Data.BatchSession? session = null;
            try
            {
                win.Width = 1331; win.Height = 700;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Show();
                win.ShowArea("batch");
                win.UpdateLayout();

                var dg = (DataGrid)win.FindName("DgBatch")!;
                var badge = (FrameworkElement)win.FindName("NavBatchBadge")!;

                // --- add two rows through the real button ---
                Click(win, "BtnAddRow");
                Click(win, "BtnAddRow");
                Assert.Equal(2, dg.Items.Count);
                Assert.Equal("2", Txt(win, "TxtNavBatchCount").Text);
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal(2, SavedBatchRowCount(win));

                // --- delete row 1 with "Remove selected (Del)" ---
                dg.SelectedItem = dg.Items[0];
                Click(win, "BtnRemoveSelected");
                Assert.Equal(1, dg.Items.Count);
                Assert.Equal("1", Txt(win, "TxtNavBatchCount").Text);
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("Add details to begin", Txt(win, "TxtBatchCounts").Text);
                Assert.Equal(1, SavedBatchRowCount(win));

                // --- delete the last row with the Del key ---
                dg.SelectedItem = dg.Items[0];
                PressDelete(win);
                Assert.Equal(0, dg.Items.Count);
                Assert.Equal("0", Txt(win, "TxtNavBatchCount").Text);
                Assert.Equal(Visibility.Collapsed, badge.Visibility);
                Assert.Equal("No rows yet", Txt(win, "TxtBatchCounts").Text);
                Assert.Equal(0, SavedBatchRowCount(win));
                // Status/counts line reads zero across the board.
                Assert.StartsWith("Ready to review: 0, Issued: 0, Failed: 0",
                    Txt(win, "TxtBatchStatus").Text);

                // Review button disabled with a reason beside it.
                var review = (Button)win.FindName("BtnReview")!;
                Assert.False(review.IsEnabled);
                var reason = Txt(win, "TxtReviewReason");
                Assert.False(string.IsNullOrWhiteSpace(reason.Text));

                // Screenshot: Batch screen after deleting all rows.
                var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..", "docs", "redesign", "shots"));
                Directory.CreateDirectory(dir);
                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(win.ActualWidth),
                    (int)Math.Ceiling(win.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(win);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, "batch-after-delete-all.png")))
                    enc.Save(fs);

                session = win.Svc.Store.LatestOpenSession();
            }
            finally
            {
                // Clean up the persisted batch session this test created.
                try
                {
                    var sess = session ?? win.Svc.Store.LatestOpenSession();
                    if (sess != null)
                    {
                        foreach (var e in win.Svc.Store.BatchRows(sess.Id))
                            win.Svc.Store.DeleteBatchRow(e.Id);
                        win.Svc.Store.CompleteBatchSession(sess.Id);
                    }
                }
                catch { }
                win.Close();
            }
        });
    }
}
