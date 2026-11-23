# IDForge — Phase Review Steps (owner)

## Run the app for review

```powershell
.\tools\review.ps1
```

The script publishes a self-contained Release build into `publish\idforge\`,
prints the branch name and commit hash, then launches `IDForge.exe`.
If the publish fails it stops with a clear message and does not launch.

## Import the sample batch

1. Open the **Batch** tab.
2. Click **Import CSV...** and choose `tests\fixtures\batch_sample.csv`
   (or paste its contents with **Paste Rows**).
3. Six rows appear. Photo paths are absolute and point at
   `tests\fixtures\photos\` on this machine.

### Sample rows and intended states

**Every row imports as Draft.** The CSV format has no status column — rows
only reach other states after **Process photos** and review:

| # | UID | Name | State after processing/review | Note |
|---|-----|------|-------------------------------|------|
| 1 | 20250318 | Mehdi Hasan Sakib | re-issue / approved path | UID must already exist in the local database (`%LOCALAPPDATA%\IDForge\idcard.db`) for the duplicate/re-issue behaviour; on a fresh DB it imports as a normal row |
| 2 | 20260101 | Ayesha Rahman | Needs review → ready to approve | complete row with photo |
| 3 | 20260102 | Karim Uddin | Needs attention (missing photo) | photo path empty |
| 4 | ID-XX-99 | Salma Khatun | Needs attention (invalid ID) | letters/symbols — not digits |
| 5 | 20260104 | Tanvir Ahmed | Processing → Needs review | complete row; shows Processing during Prepare photos |
| 6 | 20260105 | New Hire | Draft | sparse row, most fields empty |

Photo paths in the CSV are relative to the CSV file (`photos\portrait_x.png`);
the importer resolves them against the CSV's folder, so the file works from
any working directory.

## Back up before reviewing

The app uses the real data folder `%LOCALAPPDATA%\IDForge`.
Before each phase review: **Records → Back up now** (or Restore... to roll back).

## Phase 0 owner checklist

- [ ] `tools\review.ps1` builds and the app opens with the current look.
- [ ] Import `batch_sample.csv` → all 6 rows appear in the Batch grid.
- [ ] Row 4 shows an invalid/missing UID; row 3 has no photo; row 6 is sparse.
- [ ] `dotnet test tests\IDForge.Tests` passes (golden card test included).
