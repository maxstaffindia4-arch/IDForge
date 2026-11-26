namespace IDForge;

/// <summary>
/// Phase 7 — single message catalogue. Every user-facing string shown in a
/// dialog, banner or status message lives here so wording stays consistent
/// with the spec (uxpilot_spec_text.md §message catalogue).
/// </summary>
public static class Messages
{
    /// <summary>Phrase the operator must type to confirm destructive actions.</summary>
    public const string DeleteAllPhrase = "DELETE ALL";

    // ---- confirm dialogs -------------------------------------------------

    public static string ConfirmIssueHeading(string name) => $"Issue a card for {name}?";
    public static string ConfirmIssueBody(string uid, DateOnly validUntil, bool isReissue) =>
        $"Employee ID {uid}\nValid until {validUntil:dd/MM/yyyy}" +
        (isReissue ? "\n\nThis replaces the existing card — the old card should be collected." : "");

    public static string ConfirmBatchHeading(int count, string? batchName) =>
        $"Issue {count} card{(count == 1 ? "" : "s")}" +
        (string.IsNullOrWhiteSpace(batchName) ? "?" : $" from \"{batchName}\"?");

    public static string ReissueHeading(string name) => $"Re-issue {name}'s card";
    public static string ReissueBody(DateTime issuedOn) =>
        $"A new card replaces the one issued on {issuedOn:dd/MM/yyyy}. The old card should be collected.";

    public static string DeleteRecordHeading(string name) => $"Delete {name}'s record?";
    public static string DeleteRecordBody(DateTime deletedOn, DateTime purgeOn) =>
        $"Moves to Deleted records for 30 days.\nYou can restore it any time before {purgeOn:dd/MM/yyyy}.\n\n" +
        "Card files in the output folder are not deleted.";

    public static string RemoveRowHeading => "Remove this row?";
    public static string RemoveRowBody => "The row's details are removed from the batch. You can add them again.";

    public static string ClearAllHeading => "Remove every card on this PC?";
    public static string ClearAllBody(int records, int batches) =>
        $"This removes {records:N0} record{(records == 1 ? "" : "s")} and {batches} batch{(batches == 1 ? "" : "es")}. " +
        "A backup is saved first, so you can restore it later.";
    public const string ClearAllTypePrompt = "Type DELETE ALL to confirm";

    public static string RestoreBackupHeading => "Choose a backup to restore";
    public static string RestoreBackupBody(
        string path, DateTime backupDate, int backupRecords, int currentRecords) =>
        $"Today's data ({currentRecords:N0} cards) will be replaced.\n" +
        (backupRecords > currentRecords
            ? $"{backupRecords - currentRecords:N0} cards in this backup are not in today's data.\n"
            : "") +
        $"Backup from {backupDate:dd/MM/yyyy} at {backupDate:HH:mm} — {backupRecords:N0} records.\n" +
        "We'll back up today's data first.";

    public static string ResumeBatchHeading => "Resume unfinished batch?";
    public static string ResumeBatchBody(int rows) =>
        $"You left {rows} row{(rows == 1 ? "" : "s")} in Batch. Continue where you left off?";

    public static string OutputFolderMissingHeading => "Output folder not found";
    public static string OutputFolderMissingBody(string dir) =>
        $"{dir}\n\nCards can't be saved until this folder exists or is changed in Settings.";

    public static string DuplicateUidHeading => "Employee ID already exists";
    public static string DuplicateUidBody(string uid, string name) =>
        $"UID {uid} already exists — issued to {name}.";

    // ---- info / result ----------------------------------------------------

    public static string IssuedBanner(string name) => $"Card issued for {name}.";
    public static string IssueResultHeading(string name) => $"Card issued — {name}";
    public static string IssueResultBody(string uid, DateOnly validUntil, string? png, string? pdf) =>
        $"Saved to Records: {uid}, valid until {validUntil:dd/MM/yyyy}.\n" +
        $"Files:\n{(pdf != null ? $"{png}\n{pdf}" : png)}";

    public static string BatchIssuedHeading(int issued, int failed) =>
        $"{issued} card{(issued == 1 ? "" : "s")} issued{(failed > 0 ? $", {failed} not issued" : "")}";

    public static string IssueFailedHeading => "Card not issued";
    public static string IssueFailedBody(string step, string reason) =>
        $"Issue failed at step '{step}':\n{reason}\n\nNothing was saved to Records.";

    public static string BackupSavedStatus(int records, DateTime at) =>
        $"Backup saved — {records:N0} cards, {at:dd/MM/yyyy} at {at:HH:mm}.";

    public static string RestoreCompleteHeading => "Restore complete";
    public static string RestoreCompleteBody => "Records have been reloaded.";

    public static string DeletedRecordsHeading => "Deleted records";
    public const string DeletedRecordsIntro =
        "Records are kept for 30 days after deletion, then removed for good.";

    public static string PurgeRecordHeading(string name) => $"Remove {name} for good?";
    public static string PurgeRecordBody(string uid) =>
        $"Permanently deletes {uid} and all their card issues. This cannot be undone.";

    public static string BackupAgeWarning(int days) =>
        days <= 0 ? "No backup yet. Back up now?"
                  : $"Your last backup was {days} day{(days == 1 ? "" : "s")} ago. Back up now?";

    public const int BackupAgeWarningDays = 7;

    public static string OutputFolderMissingConfirm => "Open Settings";
    public const string CancelLabel = "Cancel";
    public const string KeepLabel = "Keep";
}
