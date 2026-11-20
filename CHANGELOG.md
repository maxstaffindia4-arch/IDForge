# Changelog

## v1.2.0 — 2026-11-21

### Fixed
- **Re-issue did nothing** — the dialog's Re-issue button had no click handler,
  so `ShowDialog()` never returned true and the handler exited silently. The
  date is now a plain text field parsed strictly as `dd/MM/yyyy` (en-GB; `1/7/2027`
  = 1 July 2027) with inline validation and a format hint; empty/unparseable
  dates and dates before the last print date are rejected with a message.
  Re-issue now always asks for confirmation (name, UID, print date, valid-until)
  and runs through the same `CardIssuer` transaction as Approve & Issue —
  render, verify files, one DB transaction, audit entry; failures save nothing
  and are logged.
- **Photo edge artifacts in Model mode** — the u2net cutout was written into a
  premultiplied-alpha bitmap with straight-alpha colors, producing dark/cyan
  fringes on hair and head edges, plus border-leak arcs in the photo window
  corners. The matte is now straight alpha, and a new post-process keeps only
  the face's connected component, erodes + feathers the edge, clamps the outer
  4 px border, and decontaminates edge colors from the original background.
  Plain-white mode gets the same cleanup.

### Changed
- **Batch Review** — window title and status line now show the current person's
  name + UID. Approve marks a row ready without issuing; "Issue card" remains
  the optional single-card path; "Batch Issue (N)" appears only after all rows
  are approved/skipped and issues them sequentially.

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
