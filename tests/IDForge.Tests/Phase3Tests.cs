using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IDForge;
using IDForge.Core;
using Xunit;

namespace IDForge.Tests;

/// <summary>Phase 3 — New Card screen redesign tests. WPF tests share the
/// single-AppDomain host, so they run in the "Wpf" collection serially.</summary>
[Collection("Wpf")]
public class Phase3Tests
{
    private static T OnSta<T>(Func<T> work) => WpfSta.Run(work);

    private static MainWindow NewWindow()
    {
        WpfTestHost.EnsureApp();
        MainWindow.SkipResumePrompt = true;
        return new MainWindow();
    }

    private static TextBox Txt(MainWindow w, string name) => (TextBox)w.FindName(name)!;
    private static TextBlock Tb(MainWindow w, string name) => (TextBlock)w.FindName(name)!;
    private static ComboBox Cmb(MainWindow w, string name) => (ComboBox)w.FindName(name)!;
    private static RadioButton Rad(MainWindow w, string name) => (RadioButton)w.FindName(name)!;
    private static DatePicker Dp(MainWindow w, string name) => (DatePicker)w.FindName(name)!;

    [Fact]
    public void Draft_Restored_After_Restart()
    {
        OnSta(() =>
        {
            NewCardDraft.Clear();
            try
            {
                NewCardDraft.Save(new NewCardDraft
                {
                    Name = "Draft Person",
                    Uid = "12345678",
                    Position = "Manager",
                    Blood = "B+",
                    Contract = "Contractual",
                    PrintDate = "2025-08-31",
                    BgMode = "Keep",
                    Zoom = 1.5,
                    OffsetX = 4,
                    OffsetY = -6,
                    SavedAt = "23/11/2026 14:05"
                });

                var main = NewWindow();
                try
                {
                    Assert.Equal("Draft Person", Txt(main, "TxtName").Text);
                    Assert.Equal("12345678", Txt(main, "TxtUid").Text);
                    Assert.Equal("Manager", Cmb(main, "CmbPosition").SelectedItem);
                    Assert.Equal("B+", Cmb(main, "CmbBlood").SelectedItem);
                    Assert.True(Rad(main, "RadContractContract").IsChecked == true);
                    Assert.Equal(new DateTime(2025, 8, 31),
                        Dp(main, "DpPrintDate").SelectedDate!.Value.Date);
                    main.GetNudge(out var zoom, out var x, out var y);
                    Assert.Equal(1.5, zoom, 3);
                    Assert.Equal(4, x, 3);
                    Assert.Equal(-6, y, 3);
                    Assert.Contains("Draft restored", Tb(main, "TxtDraftNote").Text);
                }
                finally { main.Close(); }
            }
            finally { NewCardDraft.Clear(); }
            return true;
        });
    }

    [Fact]
    public void Ctrl0_Resets_Transform_To_Auto()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                main.SetPhotoZoom(2.0);
                main.SetPhotoOffset(10, -20);
                main.GetNudge(out var z1, out var x1, out var y1);
                Assert.Equal(2.0, z1, 3);
                Assert.Equal(10, x1, 3);
                Assert.Equal(-20, y1, 3);

                Assert.True(main.TryHandleShortcut(Key.D0, ModifierKeys.Control));
                main.GetNudge(out var z, out var x, out var y);
                Assert.Equal(1.0, z, 3);
                Assert.Equal(0, x, 3);
                Assert.Equal(0, y, 3);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Arrow_Steps_Are_1px_And_Shift_10px()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                Assert.Equal(1, MainWindow.PhotoNudgeStep(ModifierKeys.None));
                Assert.Equal(10, MainWindow.PhotoNudgeStep(ModifierKeys.Shift));
                Assert.Equal(10, MainWindow.PhotoNudgeStep(ModifierKeys.Control | ModifierKeys.Shift));

                main.GetNudge(out _, out var x0, out _);
                var step = MainWindow.PhotoNudgeStep(ModifierKeys.None);
                main.NudgePhoto(step, 0);
                main.GetNudge(out _, out var x1, out _);
                Assert.Equal(x0 + 1, x1, 3);

                step = MainWindow.PhotoNudgeStep(ModifierKeys.Shift);
                main.NudgePhoto(step, 0);
                main.GetNudge(out _, out var x2, out _);
                Assert.Equal(x0 + 11, x2, 3);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Validity_Line_Matches_Rules_For_Both_Contracts()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                // Month-end print date: 31 August 2025.
                Dp(main, "DpPrintDate").SelectedDate = new DateTime(2025, 8, 31);
                // TxtValidity is a run-based TextBlock (parity plan §5.3) —
                // TextBlock.Text is empty once Inlines are used, so read the runs.
                string Validity() => string.Concat(
                    Tb(main, "TxtValidity").Inlines
                        .Select(i => (i as System.Windows.Documents.Run)?.Text ?? ""));
                var validity = Validity;

                Rad(main, "RadContractPerm").IsChecked = true;
                Assert.Equal("Card will be valid until 31/08/2026 · 12 months from print date",
                    validity(), ignoreCase: false, ignoreLineEndingDifferences: false,
                    ignoreWhiteSpaceDifferences: true);

                Rad(main, "RadContractContract").IsChecked = true;
                // 31 Aug + 6 months clamps to 28 Feb 2026 (2026 is not a leap year).
                Assert.Equal("Card will be valid until 28/02/2026 · 6 months from print date",
                    validity(), ignoreCase: false, ignoreLineEndingDifferences: false,
                    ignoreWhiteSpaceDifferences: true);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Uid_Prefix_Is_Straightened_To_Digits()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var uid = Txt(main, "TxtUid");
                uid.Text = "UID 12345678";
                Assert.Equal("12345678", uid.Text);
                Assert.Equal("8 / 15", Tb(main, "TxtUidCount").Text);

                uid.Text = "ID-9876";
                Assert.Equal("9876", uid.Text);
            }
            finally { main.Close(); }
            return true;
        });
    }

    [Fact]
    public void Position_Is_A_Dropdown_With_7_Values()
    {
        OnSta(() =>
        {
            var main = NewWindow();
            try
            {
                var cmb = Cmb(main, "CmbPosition");
                Assert.Equal(7, cmb.Items.Count);
                Assert.Equal(Config.Positions, cmb.Items.Cast<object>().ToArray());
            }
            finally { main.Close(); }
            return true;
        });
    }
}
