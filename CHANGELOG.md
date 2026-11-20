# Changelog

## v1.1.0 — 2026-11-20

### Fixed
- **App wouldn't launch after install** — all writable data (database, photo
  cache, exports, backups, settings, logs) moved from the Program Files install
  dir to `%LOCALAPPDATA%\IDForge` (`AppPaths`). One-time migration copies an
  existing `idcard.db` next to the exe if present.
- **Card files saved to a hidden folder** — issue, reprint, re-issue, and
  export now show a Save dialog (default: `Documents\IDForge Cards`); batch
  review asks for an output folder once per session.
- **Crash clicking Review / batch rows** — `DgBatch_CellEditEnding` called
  `Items.Refresh()` inside the grid's edit transaction
  ("Refresh is not allowed during an AddNew or EditItem transaction").
  Refresh is now deferred past the commit; review/row-load paths log full
  exceptions to `crash.log` and show friendly messages instead of crashing.
- **Office contact number** — card background regenerated from the corrected
  PSD: `Contact: 01712-109-683, 01703-790-950` (replaces broken glyph-swap
  patch that dropped digits).
- **UID handling** — operator enters digits only (4–15); `UID`/`ID.` prefixes
  and spaces are stripped on input across single card, batch grid, CSV import,
  and paste. Cards store digits, show `ID. <digits>`, barcode encodes
  `UID<digits>`, files stay `UID<digits>_v<N>.png`. One-time DB migration
  converts stored prefixed UIDs (backs up first, stops on conflicts,
  idempotent).
- **Records integrity** — verified photo processing writes only `BatchRows`;
  Employees/CardIssues are created solely by issue actions. Batch count line
  (`Ready to review / Issued / Failed`) now refreshes on every row change.
- **Output format** — PNG at 300 DPI is the default; "Also create PDF"
  checkbox (off by default, persisted) on New Card and Review panels.
  Success messages list only the files actually written.

### Added
- App icon (exe, windows, installer wizard) and Inno Setup installer script.
- ONNX models auto-download on first build when missing (`EnsureOnnxModels`
  MSBuild target + `tools\fetch-models.ps1`); models are no longer in git.
- Version info stamped into the assembly and shown in the window title.

### Tests
- 75 tests, all passing — including UID normalization/migration, PNG-vs-PDF
  output, batch-records isolation, and a golden-image check of the corrected
  contact line.
