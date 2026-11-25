using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IDForge;

namespace IDForge.Tests;

/// <summary>Fix I-B — Batch Employee / Employee ID inputs: placeholders,
/// single-click editing, digit counter, input height vs dropdowns.</summary>
[Collection("Wpf")]
public class FixIPartBTests
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

    private static TextBox CellTextBox(DataGridRow row, string header)
    {
        var cell = Descendants(row).OfType<DataGridCell>()
            .First(c => (c.Column?.Header as string) == header);
        return Descendants(cell).OfType<TextBox>().First();
    }

    [Fact]
    public void Batch_Name_And_Id_Inputs_Placeholders_Counter_And_Sizing()
    {
        WpfSta.Run(() =>
        {
            var win = NewShownWindow(1331, 700);
            var empty = new BatchRow { Uid = "", Name = "", Status = "Draft" };
            var named = new BatchRow { Uid = "", Name = "", Status = "Draft" };
            try
            {
                win.BatchRows.Add(empty);
                win.BatchRows.Add(named);
                win.UpdateBatchSummary();
                win.ShowArea("batch");
                var dg = (DataGrid)win.FindName("DgBatch")!;
                dg.ScrollIntoView(dg.Items[0]);
                Pump(dg); Pump(win);

                var rows = Descendants(dg).OfType<DataGridRow>().ToList();
                Assert.True(rows.Count >= 2, $"rows realised: {rows.Count}");
                var row0 = rows[0];

                // Placeholders visible on the empty row.
                var nameTb = CellTextBox(row0, "Employee");
                var uidTb = CellTextBox(row0, "Employee ID");
                var placeholders = Descendants(row0).OfType<TextBlock>()
                    .Where(t => t.Text is "Full name" or "Employee ID").ToList();
                Assert.Equal(2, placeholders.Count(p => p.Visibility == Visibility.Visible));

                // Single "click" = focus the TextBox; typing saves to the row.
                nameTb.Focus();
                Assert.Equal(nameTb, Keyboard.FocusedElement);
                nameTb.Text = "Typed Name";
                Assert.Equal("Typed Name", empty.Name);
                uidTb.Focus();
                uidTb.Text = "12345678";
                Assert.Equal("12345678", empty.Uid);
                Assert.Equal("8 / 15", empty.UidCounterText);
                Assert.False(empty.NameEmpty);
                Assert.False(empty.UidEmpty);
                // Placeholders now hidden.
                Assert.DoesNotContain(placeholders, p => p.Visibility == Visibility.Visible);

                // Input height >= the dropdowns in the same row.
                var combo = Descendants(row0).OfType<ComboBox>().First();
                Assert.True(nameTb.ActualHeight >= combo.ActualHeight - 0.5,
                    $"name input {nameTb.ActualHeight:F1} < combo {combo.ActualHeight:F1}");
                Assert.True(uidTb.ActualHeight >= combo.ActualHeight - 0.5,
                    $"uid input {uidTb.ActualHeight:F1} < combo {combo.ActualHeight:F1}");
                // Visible border even when not editing.
                Assert.Equal(new Thickness(1), nameTb.BorderThickness);
                Assert.Equal(new Thickness(1), uidTb.BorderThickness);

                // J-B: counter shows only while the UID cell is being edited.
                var counter = Descendants(row0).OfType<TextBlock>()
                    .FirstOrDefault(t => t.Text == "8 / 15");
                Assert.NotNull(counter);
                nameTb.Focus(); // leave the UID cell -> counter hides
                Pump(dg);
                Assert.Equal(Visibility.Collapsed, counter!.Visibility);
                uidTb.Focus(); // editing again -> counter returns
                Pump(dg);
                Assert.Equal(Visibility.Visible, counter.Visibility);

                // --- screenshots: one empty row + one named row ---
                var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "docs", "redesign", "shots"));
                System.IO.Directory.CreateDirectory(dir);
                SavePng(win, System.IO.Path.Combine(dir, "batch-inputs-empty-1331x700.png"));
                named.Name = "Has Name";
                win.UpdateBatchSummary();
                dg.SelectedIndex = -1;
                Pump(dg); Pump(win);
                SavePng(win, System.IO.Path.Combine(dir, "batch-inputs-1331x700.png"));
            }
            finally { win.Close(); }
        });
    }

    private static void SavePng(Visual visual, string file)
    {
        int w = (int)Math.Ceiling(((FrameworkElement)visual).ActualWidth);
        int h = (int)Math.Ceiling(((FrameworkElement)visual).ActualHeight);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }
}
