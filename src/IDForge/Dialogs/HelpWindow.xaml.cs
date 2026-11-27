using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IDForge.Dialogs;

/// <summary>
/// FIX 4 — the Help window opened from the nav rail and F1. Left column
/// lists the three topics that used to live under the (removed) Help menu;
/// the right pane shows the selected topic's text.
/// </summary>
public partial class HelpWindow : Window
{
    private static readonly (string Title, string Body)[] Topics =
    {
        ("Keyboard shortcuts",
            "Ctrl+1  New card\nCtrl+2  Batch\nCtrl+3  Records\nCtrl+,  Settings\n" +
            "Ctrl+B  Back up now\nCtrl+F  Search records\nCtrl+Enter  Issue card\n" +
            "F2  Edit record\nDel  Delete row / record\nF5  Start batch\nF1  Help"),
        ("Quick guide",
            "Issuing a card: fill in the New card form (or pick a photo), " +
            "then click Issue card or press Ctrl+Enter. The card image and " +
            "PDF are saved to the folder chosen in Settings.\n\n" +
            "Batch: add rows on the Batch page, fill in name, ID, position " +
            "and photo for each, then click Approve & Issue to issue them all.\n\n" +
            "Backup and restore: use the backup card in the navigation rail " +
            "or Settings > Back up to file… / Restore from file…."),
        ("Troubleshooting",
            "Missing output folder: issue is blocked until the \"Save cards " +
            "to\" folder in Settings exists and is writable. Choose a valid " +
            "folder there.\n\n" +
            "Dates: enter dates as DD/MM/YYYY or pick them from the calendar.\n\n" +
            "Backup: automatic backups are kept in the backup folder " +
            "(Settings > Open backup folder). Restore replaces all card data " +
            "after backing up the current data first."),
    };

    public HelpWindow()
    {
        InitializeComponent();
        FontFamily = (FontFamily)FindResource("AppFontFamily");
        LstTopics.SelectionChanged += (_, _) => ShowTopic();
        ShowTopic();
    }

    private void ShowTopic()
    {
        var i = LstTopics.SelectedIndex;
        TxtBody.Text = i >= 0 && i < Topics.Length ? Topics[i].Body : "";
    }
}
