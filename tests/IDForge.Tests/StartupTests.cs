using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>
/// Loads App resources and constructs both windows on an STA thread.
/// InitializeComponent resolves every StaticResource and seals BasedOn
/// styles — the class of bug that broke launch after Phase 1.
/// Windows are not shown (headless-safe); template application is covered
/// by the owner checklist in docs/redesign/REVIEW.md.
/// </summary>
[Collection("Wpf")]
public class StartupTests
{
    [Fact]
    public void App_Resources_Load_And_Windows_Construct()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();      // shared Application (one per AppDomain)
            MainWindow.SkipResumePrompt = true;  // no modal dialogs headless
            var main = new MainWindow();          // InitializeComponent inside ctor
            var rows = new ObservableCollection<BatchRow>
            {
                new() { Uid = "20250101", Name = "Test User", Blood = "A+",
                        Position = "Nurse", Contract = "Permanent", Status = "NeedsReview" }
            };
            var review = new ReviewWindow(main, rows);
            review.Close();

            // Phase 2: area switching keeps per-page state
            main.ShowArea("batch");
            Assert.Equal("batch", main.CurrentArea);
            Assert.Equal(Visibility.Collapsed, main.FindName("PageNewCard") is Grid g1 ? g1.Visibility : Visibility.Visible);
            main.ShowArea("settings");
            Assert.Equal("settings", main.CurrentArea);
            main.ShowArea("newcard");
            Assert.Equal("newcard", main.CurrentArea);
            main.Close();
        });
    }
}
