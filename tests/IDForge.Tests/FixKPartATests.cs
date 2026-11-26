using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using IDForge;
using Xunit;

namespace IDForge.Tests;

/// <summary>Fix K-A — startup XAML/resource validation. WPF's FocusVisualAdorner
/// applies an element's FocusVisualStyle to an internal Control, so a style
/// with a narrower TargetType (e.g. RadioButton) throws
/// "'RadioButton' TargetType does not match type of element 'Control'"
/// the first time keyboard focus lands. This test walks every window and
/// page and fails on that mismatch — it would have caught the launch crash.</summary>
[Collection("Wpf")]
public class FixKPartATests
{
    private static IEnumerable<FrameworkElement> LogicalTree(DependencyObject root)
    {
        if (root is FrameworkElement fe) yield return fe;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
                foreach (var el in LogicalTree(d))
                    yield return el;
    }

    private static List<string> FocusStyleProblems(Window win)
    {
        var problems = new List<string>();
        foreach (var el in LogicalTree(win))
        {
            var fvs = el.FocusVisualStyle;
            if (fvs == null) continue;
            var tt = fvs.TargetType;
            // The adorner hosts the style on an internal Control.
            if (!tt.IsAssignableFrom(typeof(Control)))
            {
                problems.Add($"{win.Title}: {el.GetType().Name} '{el.Name}' " +
                              $"FocusVisualStyle '{fvs.TargetType.Name}' cannot style Control");
                continue;
            }
            try
            {
                var probe = new Control { Style = fvs };   // triggers CheckTargetType
                _ = probe.Template;
            }
            catch (System.Exception ex)
            {
                problems.Add($"{win.Title}: applying FocusVisualStyle of " +
                             $"{el.GetType().Name} '{el.Name}' to Control threw: {ex.Message}");
            }
        }
        return problems;
    }

    [Fact]
    public void Every_Window_And_Page_Loads_With_Valid_Focus_Visual_Styles()
    {
        WpfSta.Run(() =>
        {
            WpfTestHost.EnsureApp();
            MainWindow.SkipResumePrompt = true;
            var problems = new List<string>();
            MainWindow? main = null;

            try
            {
                main = new MainWindow();   // New card page + all resources
                main.Show();               // Owner dialogs need a shown owner
                foreach (var area in new[] { "newcard", "batch", "records", "settings" })
                {
                    main.ShowArea(area);
                    main.UpdateLayout();
                    problems.AddRange(FocusStyleProblems(main));
                }

                var rows = new System.Collections.ObjectModel.ObservableCollection<BatchRow>
                {
                    new() { Uid = "20250101", Name = "Test User", Blood = "A+",
                            Position = "Nurse", Contract = "Permanent", Status = "NeedsReview" }
                };
                var review = new ReviewWindow(main, rows);
                review.Show();
                review.UpdateLayout();
                problems.AddRange(FocusStyleProblems(review));
                review.Close();

                foreach (var dlg in new[]
                {
                    main.BuildHelpDialog("Keyboard shortcuts", "F1 Help"),
                    main.BuildHelpDialog("Quick guide", "steps"),
                    main.BuildHelpDialog("Troubleshooting", "tips"),
                    main.BuildAboutDialog()
                })
                {
                    dlg.UpdateLayout();
                    problems.AddRange(FocusStyleProblems(dlg));
                    dlg.Close();
                }
            }
            finally
            {
                main?.Close();
            }

            Assert.True(problems.Count == 0,
                "FocusVisualStyle TargetType mismatches:\n" + string.Join("\n", problems));
        });
    }
}
