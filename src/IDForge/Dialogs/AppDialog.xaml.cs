using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IDForge.Dialogs;

/// <summary>
/// Phase 7 — the single dialog template used by every in-app dialog.
/// Anatomy: window title, plain-language question (heading), body (text or
/// custom content), right-aligned footer buttons. Enter activates the
/// default button; Esc activates Cancel. Destructive dialogs default to
/// Cancel — Enter cancels rather than confirms.
/// </summary>
public partial class AppDialog : Window
{
    /// <summary>Value returned when the dialog closes (set by CloseWith).</summary>
    public object? Result { get; private set; }

    /// <summary>Typed-confirm input box (set by BuildTyped; tests drive it).</summary>
    internal TextBox? ConfirmInput;

    public AppDialog()
    {
        InitializeComponent();
        FontFamily = (FontFamily)FindResource("AppFontFamily");
        // Esc always cancels — dialogs without an IsCancel button still close.
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    /// <summary>Plain-language question shown as the heading.</summary>
    public string Heading
    {
        get => TxtHeading.Text;
        set => TxtHeading.Text = value;
    }

    /// <summary>Plain text body in the standard dialog style.</summary>
    public void SetBodyText(string text, bool wrap = true)
    {
        BodyHost.Content = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("DialogBody"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
    }

    /// <summary>Custom body content (forms, lists, typed-confirm boxes).</summary>
    public void SetBody(UIElement content) => BodyHost.Content = content;

    /// <summary>Adds a footer button. At most one button should be default
    /// and one cancel; helpers below enforce that.</summary>
    public Button AddButton(string text, Action? onClick = null,
        bool isDefault = false, bool isCancel = false, object? tag = null,
        Style? style = null, bool enabled = true)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            IsEnabled = enabled,
            Tag = tag
        };
        if (style != null) b.Style = style;
        if (onClick != null) b.Click += (_, _) => onClick();
        ButtonsHost.Children.Add(b);
        return b;
    }

    /// <summary>Closes the dialog and records the result payload.
    /// DialogResult throws when the window wasn't shown via ShowDialog
    /// (headless tests construct dialogs without showing them).</summary>
    public void CloseWith(object? result = null)
    {
        Result = result;
        try { DialogResult = true; } catch (InvalidOperationException) { }
        Close();
    }

    // ---------- static helpers (confirm / info / error) ----------

    /// <summary>Yes-style confirm. destructive=true makes Enter hit Cancel.</summary>
    public static bool Confirm(Window? owner, string windowTitle, string heading,
        string body, string confirmText, bool destructive = false,
        string cancelText = Messages.CancelLabel, Style? confirmStyle = null)
    {
        var dlg = BuildConfirm(owner, windowTitle, heading, body, confirmText,
            destructive, cancelText, confirmStyle);
        dlg.ShowDialog();
        return dlg.Result is true;
    }

    /// <summary>Builds the confirm dialog without showing it (tests inspect
    /// the button flags: Enter=default, Esc=cancel, destructive→Cancel).</summary>
    internal static AppDialog BuildConfirm(Window? owner, string windowTitle, string heading,
        string body, string confirmText, bool destructive = false,
        string cancelText = Messages.CancelLabel, Style? confirmStyle = null)
    {
        var dlg = new AppDialog { Owner = owner, Title = windowTitle, Heading = heading };
        dlg.SetBodyText(body);
        // Destructive: Enter must cancel — the cancel button is the default.
        dlg.AddButton(cancelText, isCancel: true, isDefault: destructive);
        dlg.AddButton(confirmText, () => dlg.CloseWith(true),
            isDefault: !destructive, style: confirmStyle);
        return dlg;
    }

    /// <summary>Informational dialog with a single Close button (Enter = Close).
    /// IsCancel makes the click/Esc close the window — no explicit Close call
    /// (setting DialogResult after close would throw).</summary>
    public static void Info(Window? owner, string windowTitle, string heading, string body)
    {
        var dlg = new AppDialog { Owner = owner, Title = windowTitle, Heading = heading };
        dlg.SetBodyText(body);
        dlg.AddButton("Close", isDefault: true, isCancel: true);
        dlg.ShowDialog();
    }

    /// <summary>Error dialog — single OK, Enter/Esc both dismiss.</summary>
    public static void Error(Window? owner, string windowTitle, string heading, string body)
        => Info(owner, windowTitle, heading, body);

    /// <summary>Confirm dialog whose confirm button enables only when the
    /// typed phrase matches (e.g. DELETE ALL). Returns true on confirm.</summary>
    public static bool ConfirmTyped(Window? owner, string windowTitle, string heading,
        string body, string prompt, string requiredPhrase,
        string confirmText, Style? confirmStyle = null)
    {
        var dlg = BuildTyped(owner, windowTitle, heading, body, prompt, requiredPhrase,
            confirmText, confirmStyle);
        dlg.ShowDialog();
        return dlg.Result is true;
    }

    /// <summary>Builds the typed-confirm dialog without showing it (tests drive
    /// the TextBox and inspect the confirm button's IsEnabled).</summary>
    internal static AppDialog BuildTyped(Window? owner, string windowTitle, string heading,
        string body, string prompt, string requiredPhrase,
        string confirmText, Style? confirmStyle = null)
    {
        var dlg = new AppDialog { Owner = owner, Title = windowTitle, Heading = heading };
        dlg.SetBodyText(body);
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 4) });
        var txt = new TextBox { Width = 220 };
        panel.Children.Add(txt);
        dlg.ConfirmInput = txt;
        dlg.SetBody(panel);
        // Destructive: Enter cancels until the exact phrase is typed.
        dlg.AddButton(Messages.CancelLabel, isCancel: true, isDefault: true);
        var ok = dlg.AddButton(confirmText, () => dlg.CloseWith(true),
            enabled: false, style: confirmStyle);
        txt.TextChanged += (_, _) => ok.IsEnabled = txt.Text == requiredPhrase;
        return dlg;
    }
}
