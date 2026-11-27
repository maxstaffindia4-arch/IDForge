using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using IDForge;
using IDForge.Core.Data;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 2/3 fix-pass tests. WPF tests share the single-AppDomain
/// host, so they run in the "Wpf" collection serially.</summary>
[Collection("Wpf")]
public class FixPassTests
{
    internal static T OnSta<T>(Func<T> work) => WpfSta.Run(work);

    internal static MainWindow NewWindow()
    {
        WpfTestHost.EnsureApp();
        MainWindow.SkipResumePrompt = true;
        return new MainWindow();
    }

    internal static int CountSeparators(DependencyObject root)
    {
        var n = 0;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d)
            {
                if (child is TextBlock tb && tb.Text.Trim() == "|") n++;
                n += CountSeparators(d);
            }
        }
        return n;
    }

    [Fact]
    public void Status_Bar_Separates_Every_Item_Including_Version()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var bar = ((FrameworkElement)main.FindName("PnlStatusLeft")!).Parent!;
                // offline | count | backup = 3 separators; the version is plain
                // right-aligned text — the About link moved to the nav rail (FIX 4).
                Assert.True(CountSeparators(bar) >= 3,
                    $"expected >=3 separators, found {CountSeparators(bar)}");
                var version = (TextBlock)main.FindName("TxtStatusVersion")!;
                Assert.StartsWith("IDForge ", version.Text);
                Assert.DoesNotContain("About", version.Text);
            }
            finally { main.Close(); }
            return true;
        });
    }

    /// <summary>A digits-only UID not present even among soft-deleted rows
    /// (Employees.Uid is UNIQUE, so deleted rows still occupy the value).</summary>
    private static string FreshUid(IDForge.Core.Data.CardStore store)
    {
        for (var i = 0; i < 200; i++)
        {
            var u = (90000000000L + Random.Shared.NextInt64(0, 9999999999L)).ToString();
            if (store.FindEmployeeByUidIncludingDeleted(u) == null) return u;
        }
        throw new InvalidOperationException("no free test UID");
    }

    [Fact]
    public void Duplicate_Uid_Shows_Red_Message_Link_And_Disables_Issue()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var uid = FreshUid(main.Svc.Store);
                var store = main.Svc.Store;
                var emp = new Employee
                {
                    Uid = uid, Name = "Dup Test Person", BloodGroup = "N/A",
                    Position = "Intern", ContractType = "Permanent"
                };
                emp.Id = store.InsertEmployee(emp);
                try
                {
                    // A duplicate UID blocks Issue even with a photo present:
                    // set the cutout (photo ready), then enter the duplicate UID.
                    var uidBox = (TextBox)main.FindName("TxtUid")!;
                    uidBox.Text = uid;

                    var err = (TextBlock)main.FindName("ErrUid")!;
                    var errorColor = ((SolidColorBrush)main.FindResource("ErrorBrush")!).Color;
                    Assert.Equal(errorColor, ((SolidColorBrush)err.Foreground).Color);
                    Assert.Contains("already exists", err.Text);
                    Assert.Equal(Visibility.Visible,
                        ((TextBlock)main.FindName("LnkOpenRecord")!).Visibility);

                    var issue = (Button)main.FindName("BtnSave")!;
                    Assert.False(issue.IsEnabled);
                    Assert.Equal(Visibility.Visible,
                        ((TextBlock)main.FindName("TxtIssueBlocked")!).Visibility);

                    // "Open record" switches to Records with the row selected.
                    main.OpenRecord_Click(null!, null!);
                    Assert.Equal("records", main.CurrentArea);
                    Assert.Contains("Dup Test Person",
                        ((TextBlock)main.FindName("TxtRecordInfo")!).Text);
                }
                finally { store.SoftDeleteEmployee(emp.Id); }
            }
            finally { main.Close(); }
            return true;
        });
    }

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock tb) yield return tb;
            if (child is DependencyObject d)
                foreach (var t in FindTextBlocks(d)) yield return t;
        }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button b) yield return b;
            if (child is DependencyObject d)
                foreach (var x in FindButtons(d)) yield return x;
        }
    }

    private static Color ChipColor(MainWindow w, string name)
        => ((SolidColorBrush)((TextBlock)w.FindName(name)!).Foreground).Color;

    private static Color ResColor(MainWindow w, string key)
        => ((SolidColorBrush)w.FindResource(key)!).Color;

    [Fact]
    public void Completion_Chips_Reflect_Details_Uid_And_Photo()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var store = main.Svc.Store;
                // A UID that is guaranteed not to exist (not even soft-deleted).
                var newUid = FreshUid(store);

                // A duplicate employee for the amber/red states.
                var dupUid = FreshUid(store);
                var dupEmp = new Employee
                {
                    Uid = dupUid, Name = "Chip Dup", BloodGroup = "N/A",
                    Position = "Intern", ContractType = "Permanent"
                };
                dupEmp.Id = store.InsertEmployee(dupEmp);
                try
                {
                    var name = (TextBox)main.FindName("TxtName")!;
                    var uid = (TextBox)main.FindName("TxtUid")!;
                    var pos = (ComboBox)main.FindName("CmbPosition")!;
                    var blood = (ComboBox)main.FindName("CmbBlood")!;

                    // 1 · Valid new card, photo NOT approved.
                    name.Text = "Chip Test";
                    pos.SelectedItem = "Manager";
                    blood.SelectedItem = blood.Items.Cast<object>().Skip(1).First();
                    uid.Text = newUid;

                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipDetails"));
                    Assert.Equal("Employee ID is new",
                        ((TextBlock)main.FindName("TxtChipUidNew")!).Text);
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipUidNew"));
                    Assert.Equal(ResColor(main, "MutedBrush"), ChipColor(main, "TxtChipPhoto"));

                    // 2 · Photo present → "Photo ready" chip goes green.
                    main._cutout = new SkiaSharp.SKBitmap(1, 1);
                    name.Text = "Chip Test ";   // retrigger chip refresh
                    Assert.Equal("Photo ready",
                        ((TextBlock)main.FindName("TxtChipPhoto")!).Text);
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipPhoto"));

                    // 3 · Duplicate ID → details amber, ID chip red with reason.
                    uid.Text = dupUid;
                    Assert.Equal(ResColor(main, "WarningBrush"), ChipColor(main, "TxtChipDetails"));
                    Assert.Equal("Employee ID already exists",
                        ((TextBlock)main.FindName("TxtChipUidNew")!).Text);
                    Assert.Equal(ResColor(main, "ErrorBrush"), ChipColor(main, "TxtChipUidNew"));
                    Assert.Equal(ResColor(main, "SuccessBrush"), ChipColor(main, "TxtChipPhoto"));
                }
                finally
                {
                    store.SoftDeleteEmployee(dupEmp.Id);
                    NewCardDraft.Clear();
                }
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void ComboBox_And_DatePicker_Use_Theme_Template()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var borderColor = ((SolidColorBrush)main.FindResource("BorderBrush")!).Color;

                // ComboBox: themed border inside the toggle template.
                var cmb = (ComboBox)main.FindName("CmbPosition")!;
                cmb.ApplyTemplate();
                var toggle = cmb.Template!.FindName("ToggleButton", cmb) as ToggleButton;
                Assert.NotNull(toggle);
                toggle!.ApplyTemplate();
                var bd = toggle.Template!.FindName("bd", toggle) as Border;
                Assert.NotNull(bd);
                Assert.Equal(new CornerRadius(6), bd!.CornerRadius);
                Assert.Equal(borderColor, ((SolidColorBrush)bd.BorderBrush!).Color);
                // Dropdown part preserved.
                Assert.NotNull(cmb.Template.FindName("Popup", cmb));
                Assert.Equal(7, cmb.Items.Count);   // V1: still a dropdown

                // DatePicker: themed outer border + calendar button + popup part.
                var dp = (DatePicker)main.FindName("DpPrintDate")!;
                dp.ApplyTemplate();
                var outer = dp.Template!.FindName("bd", dp) as Border;
                Assert.NotNull(outer);
                Assert.Equal(new CornerRadius(6), outer!.CornerRadius);
                Assert.Equal(borderColor, ((SolidColorBrush)outer.BorderBrush!).Color);
                Assert.NotNull(dp.Template.FindName("PART_TextBox", dp));
                Assert.NotNull(dp.Template.FindName("PART_Button", dp));
                Assert.NotNull(dp.Template.FindName("PART_Popup", dp));
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Zoom_Box_Shows_Percent_And_Matches_Transform()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var panel = (IDForge.Controls.PhotoPositionPanel)main.FindName("PosPanel")!;
                var pct = (TextBox)panel.FindName("TxtZoomPct")!;
                var slider = (Slider)panel.FindName("SldZoom")!;
                var xBox = (TextBox)panel.FindName("TxtX")!;
                var yBox = (TextBox)panel.FindName("TxtY")!;

                // "%" suffix shown next to the box.
                var hasPctSign = FindTextBlocks(panel).Any(t => t.Text == "%");
                Assert.True(hasPctSign, "expected a '%' label beside the zoom box");

                // Scale labels exist under the slider.
                var scale = panel.FindName("ZoomScale") as Canvas;
                Assert.NotNull(scale);
                var labels = System.Windows.LogicalTreeHelper.GetChildren(scale!)
                    .OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("40 %", labels);
                Assert.Contains("100 %", labels);
                Assert.Contains("300 %", labels);

                // SetState pushes transform values into every control.
                double? committed = null;
                panel.ZoomChanged += z => committed = z;
                panel.SetState(1.75, 12, -8);
                Assert.Equal("175", pct.Text);
                Assert.Equal(1.75, slider.Value, 3);
                Assert.Equal("12", xBox.Text);
                Assert.Equal("-8", yBox.Text);

                // Typing a new % commits the matching zoom factor.
                committed = null;
                pct.Text = "150";
                Assert.Equal(1.5, committed!.Value, 3);
                Assert.Equal(1.5, slider.Value, 3);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Issue_Card_Enabled_With_Photo_No_Separate_Approve()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            var cutout = new SkiaSharp.SKBitmap(4, 4);
            var window = new SkiaSharp.SKBitmap(4, 4);
            try
            {
                // No separate approve step: valid details + photo → Issue enabled.
                ((TextBox)main.FindName("TxtName")!).Text = "Issue Enable Test";
                ((TextBox)main.FindName("TxtUid")!).Text = FreshUid(main.Svc.Store);
                ((ComboBox)main.FindName("CmbPosition")!).SelectedItem = "Manager";
                var blood = (ComboBox)main.FindName("CmbBlood")!;
                blood.SelectedItem = blood.Items.Cast<object>().Skip(1).First();
                var issue = (Button)main.FindName("BtnSave")!;
                Assert.Null(main.FindName("BtnApprove"));   // approve button removed (F-A)

                main._cutout = cutout;
                main._windowImage = window;
                main.UpdateApproveButton();
                Assert.True(issue.IsEnabled);
            }
            finally
            {
                cutout.Dispose(); window.Dispose();
                NewCardDraft.Clear();
                main.Close();
            }
            return true;
        });
    }

    [Fact]
    public void Zoom_Chips_Sit_On_Their_Own_Row_Under_Preview_Title()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var title = FindTextBlocks(main).First(t => t.Text == "Live preview");
                var subtitle = FindTextBlocks(main).First(t => t.Text == "Updates as you type");
                var fit = (Button)main.FindName("BtnZoomFit")!;

                // Parity §5.4: title and chip share the header's left group;
                // the zoom group sits in the header's right group.
                Assert.Equal(title.Parent, (subtitle.Parent as FrameworkElement)?.Parent);
                Assert.NotEqual(title.Parent, fit.Parent);

                // Parity plan §5.1: draft note sits in the header button group.
                var note = (TextBlock)main.FindName("TxtDraftNote")!;
                var notePanel = (StackPanel)main.FindName("PnlDraftNote")!;
                Assert.Same(notePanel, note.Parent);
                var clearBtn = FindButtons(main).First(b => b.Content as string == "Clear form");
                Assert.Same(clearBtn.Parent, notePanel.Parent);
            }
            finally { main.Close(); }
            return true;
        });
    }
}
