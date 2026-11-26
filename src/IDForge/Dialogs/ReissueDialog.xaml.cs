using System.Windows;
using System.Windows.Controls;

namespace IDForge.Dialogs;

/// <summary>
/// Phase 7 — re-issue dialog as a real XAML window on the DialogWindow style.
/// Exposes the validated print date; "Adjust photo…" closes with
/// <see cref="AdjustRequested"/> so the caller can route to the New card page.
/// </summary>
public partial class ReissueDialog : Window
{
    /// <summary>Validated print date when DialogResult == true.</summary>
    public DateOnly? PrintDate { get; private set; }

    /// <summary>True when the operator chose "Adjust photo…".</summary>
    public bool AdjustRequested { get; private set; }

    private readonly Func<DateOnly, string?> _guard;

    public ReissueDialog(string heading, string body, DateOnly defaultDate,
        Func<DateOnly, string?> guard)
    {
        InitializeComponent();
        FontFamily = (System.Windows.Media.FontFamily)FindResource("AppFontFamily");
        TxtHeading.Text = heading;
        TxtBody.Text = body;
        TxtDate.Text = defaultDate.ToString("d/M/yyyy");
        _guard = guard;
    }

    private void Reissue_Click(object sender, RoutedEventArgs e)
    {
        if (TryConfirm())
            try { DialogResult = true; } catch (InvalidOperationException) { }
    }

    /// <summary>Validates the typed date; on success sets <see cref="PrintDate"/>.
    /// Split from the click handler so tests can drive it without ShowDialog.</summary>
    internal bool TryConfirm()
    {
        if (!Core.DateInput.TryParse(TxtDate.Text, out var d))
        {
            TxtError.Text = "Enter a valid date as dd/MM/yyyy (e.g. 1/7/2027).";
            return false;
        }
        var problem = _guard(d);
        if (problem != null) { TxtError.Text = problem; return false; }
        PrintDate = d;
        return true;
    }

    private void Adjust_Click(object sender, RoutedEventArgs e)
    {
        AdjustRequested = true;
        try { DialogResult = false; } catch (InvalidOperationException) { }
    }
}
