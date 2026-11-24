# IDForge Redesign: Phased Plan

Repository: https://github.com/maxstaffindia4-arch/IDForge
Branch: `feature/uxpilot-redesign` (created from the latest `main` in phase 0)
Design reference: UX Pilot exports (`uxpilot_spec_text.md`) and the five screen mockups (New Card, Batch, Review, Records, Settings). The mockups are app UI designs. Their placeholder ID card is not a design reference. The real card render never changes.

---

## 1. Rules for every phase

1. Work only on `feature/uxpilot-redesign`. Never push to `main`.
2. Do only the phase you are given. If a task needs a later phase, stop and report it.
3. Behaviour, business rules, and the card render do not change unless the phase says so:
   - Card layout, fonts, barcode, validity maths, UID digits rule.
   - Output file naming, unless decision D1 says otherwise.
   - PNG 300 DPI always. PDF only when enabled.
   - Re-issue always confirms.
   - Approve before issue. Records only change on issue.
   - Offline and local processing only.
4. Commit in small steps. Each commit message starts with the phase, for example `Phase 2: add left navigation shell`.
5. Build and run all tests after each commit. Fix failures you introduced.
6. At the end of every phase, run `tools\review.ps1` (section 2) to confirm the app starts, and push.
7. Report using the template in section 5. The owner checks the phase by running the app, using the checklist in that phase's report.

---

## 2. Review method (used after every phase)

The owner reviews each phase by running the app. No installer is needed.

**`tools\review.ps1`** (created in phase 0):
1. Builds a self-contained Release copy into `publish\idforge\`:
   `dotnet publish src\IDForge -c Release -r win-x64 --self-contained true -o publish\idforge`
2. Launches `publish\idforge\IDForge.exe`.
3. Prints the branch name and the commit hash that is running.

**Sample data for review:** `tests\fixtures\batch_sample.csv` plus the photos in `tests\fixtures\photos\`. The owner imports it with Batch → Import CSV. It has 6 rows covering: approved, ready to review, missing photo, invalid ID, processing, and draft. One row is a re-issue of an existing card.

**Data safety:** the owner's real data is in `%LOCALAPPDATA%\IDForge`. Before a phase review, the owner backs up with Records → Back up now. Review uses the same data folder, so only use the sample file in a test session, or back up first.

**Limits:** the owner should check the manual items listed in each phase's checklist. These include file pickers, native message boxes, and the window at both screen sizes.

---

## 3. Phases

### Phase 0: Branch and review tool
**Goal:** create the branch, the review script, and the sample data.

**Scope:**
- Create or check out `feature/uxpilot-redesign` from the latest `main`.
- Add `tools\review.ps1` as described in section 2.
- Add `tests\fixtures\batch_sample.csv` and `tests\fixtures\photos\` with 6 rows covering the states listed in section 2. Use the sample photo for rows that need one, and leave the photo path empty for the "missing photo" row.
- Add a short `docs\redesign\REVIEW.md` with the owner's review steps.
- Make no change to app behaviour or visuals.

**Acceptance:**
- `tools\review.ps1` builds and starts the app.
- The sample CSV imports into Batch and shows all 6 rows with the expected states.
- Tests still pass. The card golden test still passes.

**Owner checklist:** run `tools\review.ps1`. Confirm the app opens with the current look. Import `batch_sample.csv` and confirm 6 rows appear.

**Commit:** `Phase 0: add review script and sample batch data`

---

### Phase 1: Design tokens and control styling
**Goal:** apply the UX Pilot colours, fonts, spacing, and control styles. Layout does not change.

**Scope:**
- Theme resource dictionaries: `Colors.xaml`, `Colors.Card.xaml` (card colours, used only by the render), `Typography.xaml`, `Buttons.xaml`, `Inputs.xaml`, `Badges.xaml`, `Lists.xaml`, `DataGrid.xaml`, `Dialog.xaml` (styles only).
- Embed Inter and JetBrains Mono (OFL, with licence files). Use Inter for app UI.
- Primary buttons are blue. Red is only for errors and destructive actions. Destructive solid buttons appear only inside confirm dialogs.
- Status badges have an icon or text, not colour alone.

**Out of scope:** moving controls, adding screens, changing text, or changing behaviour.

**Acceptance:**
- Card golden test still passes (pixel-identical).
- No hard-coded colours in XAML outside the theme files.

**Owner checklist:** run `tools\review.ps1`. Check every tab, the Review window, and each dialog. Confirm buttons are blue, errors are red, and text is readable. Check the card preview is unchanged.

**Commit:** `Phase 1: apply design tokens and control styles`

---

### Phase 2: Navigation shell and Settings
**Goal:** replace the three tabs with the navigation layout from the spec, and add the Settings page.

**Depends on decisions:** D3 (PDF default), D4 (output folder), D5 (version number). Use the default values in section 4 until decided.

**Scope:**
- Left navigation list: Issue cards (New card, Batch), Manage (Records, Settings). Shortcuts shown: Ctrl+1 New card, Ctrl+2 Batch, Ctrl+3 Records, Ctrl+, Settings.
- Menu bar: File, Edit, View, Records, Tools, Help. Help holds About (F1).
- Status bar: offline notice, record count, last backup, version.
- Navigation collapses to icons below 1100 px wide.
- Each area keeps its own unsaved state when you switch.
- Settings page with sections:
  - Issuing: confirm before issuing (on/off). Always confirm re-issues (locked on, with the explanation).
  - Saved files: PNG always on. Also save a PDF (on/off). Save cards to (folder with Browse).
  - New card defaults: contract type, print date default, photo background, date format shown as dd/MM/yyyy.
- Settings save instantly, and the status bar confirms "Setting saved".
- The checkboxes on New Card and Review are removed. Those screens link to Settings.

**Out of scope:** changing the content of New Card, Batch, Review, or Records (phases 3 to 6).

**Acceptance:**
- Ctrl+1, Ctrl+2, Ctrl+3, Ctrl+, switch areas.
- Re-issue confirmation cannot be turned off.
- Settings values persist across restart.

**Owner checklist:** switch between all areas with the menu, the nav list, and shortcuts. Change each setting, restart the app, and confirm the value is kept. Resize the window to 1366×768 and to 860×620, and confirm nothing is cut off.

**Visual checkpoint:** the owner compares the app with the UX Pilot layout screenshot. The IDE lists every visible difference it can find, in the report.

**Commit:** `Phase 2: add navigation shell and Settings page`

---

### Phase 3: New Card screen
**Goal:** redesign the single-card screen to match the mockup.

**Depends on decisions:** D2 (background modes), D1 (file name shown in the summary).

**Scope:**
- Two steps: "1 Employee details" and "2 Photo", then "3 Approve photo".
- Form fields with required markers. Employee ID accepts digits only, with the hint "Don't type UID, it's added for you".
- Photo step: choose photo (Ctrl+O), drag and drop onto the preview, background mode per D2.
- Position and size controls: left/right and up/down in pixels (±1 px, Shift for 10 px), zoom as a percentage with a numeric box, Reset (Ctrl+0). The underlying values stay the same as now.
- Live preview with Fit, 100%, and 200% zoom.
- Draft saved automatically, and restored after restart.
- Clear form and Save draft (Ctrl+S). Approve photo and Issue card (Ctrl+Enter).
- Messages match the message catalogue in the spec, with the exact text.

**Out of scope:** batch, review, and records.

**Acceptance:**
- Issuing a card still creates exactly one record and one set of files.
- A draft comes back after restart.

**Owner checklist:** enter a new card with a photo, adjust it, approve, and issue. Check the files and the Records entry. Close the app mid-entry, reopen it, and confirm the draft is restored. Try an invalid ID and a missing name, and check the messages.

**Visual checkpoint:** the owner compares the app with the UX Pilot layout screenshot. The IDE lists every visible difference it can find, in the report.

**Divergence V1:** Position stays a dropdown (see DIVERGENCES.md), not a free-text field.

**Commit:** `Phase 3: redesign New Card screen`

---

### Phase 4: Batch screen
**Goal:** redesign batch preparation.

**Depends on decisions:** D6 (paste column order).

**Scope:**
- Header with the batch name and a counts line, such as "18 of 24 approved, 6 more before the batch can be issued".
- Toolbar: Add row, Duplicate, Paste from Excel, Import CSV, Export CSV, Prepare photos (F5), Remove selected (Del).
- Filters: All, Needs attention, Ready to review, Approved, Re-issues.
- Grid columns: #, Employee, Employee ID, Position, Contract, Print date, Card type (New card or Re-issue), Status, Message. Status and message are separate.
- Re-issue rows show "Replaces card issued dd/MM/yyyy".
- Duplicate IDs inside the batch are flagged in the Message column with the row number.
- Progress while preparing photos: "Preparing photos: 15 of 24, about 20 seconds left".
- The Issue batch button stays disabled until every card is approved or skipped, and the reason is shown next to it.
- Keyboard: arrows move between cells, F2 edits, Enter commits.

**Out of scope:** the Review window (phase 5).

**Acceptance:**
- Paste from Excel follows the order in D6.
- Batch never approves or issues a card on its own.

**Owner checklist:** import `batch_sample.csv`. Check each row's status and message. Prepare photos and watch the progress. Use each filter. Confirm Issue batch is disabled and says why. Paste a few rows from Excel.

**Commit:** `Phase 4: redesign batch screen`

---

### Phase 5: Review window
**Goal:** redesign the per-card review window.

**Scope:**
- Window size: 90% of the work area, never larger, centred on the main window. Minimum 860×620. Maximised automatically below 1280×800. Size and position are remembered.
- Left list: name and ID on each row, status badges, filter, arrow keys to move.
- Centre: card preview.
- Right: Details & photo tab and History tab. Details fields with inline errors. Photo position controls as in phase 3.
- Bottom bar: Previous (Alt+←), Skip for now, Save draft (Ctrl+S), Issue this card only (Ctrl+I), Approve & next (Ctrl+Enter), Next (Alt+→).
- Below 1100 px, the right panel becomes tabs.

**Out of scope:** changing the approve rules.

**Acceptance:**
- The window fits a 1366×768 screen, and the title bar stays reachable.
- Approve & next moves to the next card and keeps its photo adjustments.

**Owner checklist:** open Review from the batch. Move between cards with the arrows and the buttons, and check that each card keeps its photo adjustment. Approve one card with Ctrl+Enter. Check the window at 1366×768 and at a maximised size.

**Commit:** `Phase 5: redesign review window`

> The batch/review flow this phase implements is specified in
> [`batch_review_flow_plan.md`](batch_review_flow_plan.md) (stages 1–6).

---

### Phase 6: Records screen
**Goal:** redesign records management.

**Depends on decisions:** D7 (history events).

**Scope:**
- Search (Ctrl+F) and filters: contract type, validity (Any, Active, Expires soon, Expired), and issued date.
- Result table: Employee, Employee ID, Position, Contract, Issued on, Valid until, and a validity badge.
- Detail pane: card preview, employee details, files, and a History tab.
- Actions: Reprint (Ctrl+P), Re-issue (Ctrl+Shift+R), Edit (F2), Save card as, Show card file, Delete (Del).
- Delete moves the record to Deleted records for 30 days, with an Undo banner for 10 seconds.
- Deleted records dialog: Restore, and Remove for good (with confirmation).
- Data tools: Export CSV, Export JSON, Back up now (Ctrl+B), Restore from a backup, Clear all data.

**Out of scope:** dialog text (phase 7) and any change to storage.

**Acceptance:**
- Every existing Records action still works.
- Restore and Clear all make a backup first.
- Delete then Undo restores the record exactly.

**Owner checklist:** search and use each filter. Reprint, re-issue, and edit one record. Delete a record, press Undo, and check it returns unchanged. Open Deleted records and restore one. Run a backup, and confirm the file is created.

**Commit:** `Phase 6: redesign Records screen`

---

### Phase 7: Dialogs and message catalogue
**Goal:** one dialog template, and every message uses the catalogue text.

**Scope:**
- Dialog template: plain title, what will happen, then buttons. Enter activates the highlighted button, Esc cancels, and destructive dialogs default to Cancel.
- Dialogs: confirm issue, issue result, batch issued, re-issue, edit record, delete, clear all (type DELETE ALL), restore from backup (with a preview of what will change), about, and keyboard shortcuts.
- Message catalogue from the spec, with the exact wording.
- Banners and the backup-age warning ("No backup for 7+ days").
- The code-built re-issue dialog becomes a XAML dialog.

**Acceptance:**
- Clear all still requires typing DELETE ALL exactly.

**Owner checklist:** open each dialog from the list above. Check the Enter and Esc behaviour. Trigger one error for each catalogue message (for example, an invalid ID or a missing photo) and check the wording.

**Commit:** `Phase 7: unify dialogs and message catalogue`

---

### Phase 8: Keyboard, accessibility, and polish
**Goal:** complete shortcuts, tab order, focus rings, and a final review.

**Scope:**
- Implement every shortcut in the spec, and show them in menus and tooltips.
- Tab order as in the spec for New card, Batch, and Review.
- Focus ring: blue, 2 px, visible on every control.
- Check text and status colour contrast against the spec.
- Remove dead code and unused resources from the redesign.

**Acceptance:**
- A keyboard checklist is written, and each shortcut is tested.

**Owner checklist:** go through the keyboard checklist on each screen. Check the focus ring with Tab only. Check the app at 1366×768, 1920×1080, and 860×620.

**Commit:** `Phase 8: keyboard shortcuts, focus, and polish`

After phase 8: version bump, installer build (`IDForge-Setup-<version>.exe`), and a merge request to `main`. Only the owner merges.

---

## 4. Open decisions

Phases 0 and 1 do not depend on these. Later phases use the default shown until the owner answers.

| ID | Question | Default until answered |
|---|---|---|
| D1 | Output file name: `UID<digits>_v<N>.png` (current) or `Name_UID_dd-MM-yyyy.png` (mockup)? | Keep current |
| D2 | Background modes: keep Model (complex background) as well as plain white and keep-original? | Keep all three |
| D3 | Save a PDF by default? | Off |
| D4 | Default output folder: `Documents\IDForge Cards` (current) or `D:\IDForge\Cards` (mockup)? | Keep current |
| D5 | Version number for this release: 1.2.3 (current) or 2.0 (mockup)? | Keep 1.2.3 until changed |
| D6 | Paste column order: current (UID, Name, Blood, Position, Contract, Print date, Photo path) or the mockup tip (Name, Position, Employee ID, Blood group, Contract, Print date)? | Keep current, add the photo path at the end |
| D7 | Records history: which events are shown (first issued, re-issued, reprinted)? | Issues and re-issues only |
| D8 | "Show print edges" option in the preview: include or leave out? | Leave out |

---

## 5. Phase report template

The IDE uses this format at the end of each phase:

```
PHASE N REPORT

Branch: feature/uxpilot-redesign
Commits: <hash> <message>, ...
Push: ok / failed (reason)

Done:
- ...

Not done (and why):
- ...

Tests: <passed>/<total>, golden card test: pass/fail
tools\review.ps1: ran / failed (reason)

Owner checklist for this phase:
- ... (copy from the phase in docs/redesign/PLAN.md)

Decisions used as defaults in this phase:
- D<id>: <default used>

Spec differences found and not changed:
- ...
```
