# Batch, Review, and Issue Flow: Revised Plan

Project: Staff ID Card Generator (WPF, F:\Web Design\ID Card Tool)

---

## 1. Principles

1. **Automation prepares, a person approves.** The app can remove backgrounds and auto-position photos. It cannot judge whether a photo looks right. Every card needs a human check before it is issued.
2. **Records only change when a card is issued.** A draft, a failed row, or a skipped row never appears in Records.
3. **Work survives a restart.** Every batch row is saved as it is edited. If the app closes, the operator can resume.
4. **One bad row never blocks the others.**
5. **One issue path.** The New Card tab and the Batch review use the same review panel and the same issue function, so both behave identically.

---

## 2. Stages

| Stage | Where | What happens |
|---|---|---|
| 1. Import | Batch tab | Enter rows in a grid, paste from Excel or CSV, or import a CSV. Dropdowns are always visible. Basic validation runs per row. |
| 2. Process photos | Batch tab | The app processes every row that has a photo: background removal, face detection, and auto-positioning. Nothing is approved or issued. |
| 3. Review | Batch Review screen | The operator reviews one card at a time. They can fix any field, adjust the photo, and skip or approve. |
| 4. Issue | Review screen, per card | Approving a card issues it. Files are written and the record is saved in a single step. |
| 5. Summary | Batch tab | Shows issued, skipped, and failed counts. Exports the issued list and opens the output folder. |

---

## 3. Row States

```
Draft ──► Needs photo ──► Processing ──► Needs review ──► Approved ──► Issuing ──► Issued
  │                          │                │                           │
  │                          ▼                ▼                           ▼
  └──────────────────────► Failed ◄──────── Skipped                      Failed
                          (reason shown)                              (reason shown)
```

- **Draft**: the row is incomplete (missing fields or photo). It cannot be processed.
- **Needs review**: processing finished. The row is ready for the operator's check.
- **Approved**: the operator approved it. The row is queued to issue.
- **Issued**: files are written and the record is saved. This is the only state that touches Records.
- **Failed**: shows the reason. The operator fixes it and the row returns to Needs review.
- **Skipped**: the operator chose not to issue it now. It stays in the batch for later.

---

## 4. Review Screen

Opened from the Batch tab with "Review Next" or by double-clicking a row. It can also open from the New Card tab, using the same layout.

**Left panel: row list**
- All rows, with a status icon.
- Filter: All, Needs review, Failed, Issued, Skipped.

**Center: full card preview**
- The complete card, scaled to fit the window with its aspect ratio kept. Nothing is cut off, including the validity line, barcode, contact, and address.

**Right panel: fields and photo**
- Editable fields: name, blood group, position, UID, contract type, print date.
- Validation messages next to each field.
- Photo panel:
  - Remove background toggle, with the mode selector (Plain white, Model, Keep as is).
  - Zoom, Horizontal, and Vertical sliders, each with a numeric box.
  - Reset to auto.
  - Replace photo.
- The photo panel changes the preview immediately, using the debounced preview path.

**Bottom bar**
- `◄ Previous` | `Save draft` | `Skip` | `Approve & Issue` | `Next ►`
- Keyboard: Left and Right arrows move between rows. Ctrl+Enter approves.

---

## 5. Issue Transaction

Runs when the operator clicks Approve & Issue. It is one unit: either everything is saved or nothing is.

1. **Revalidate** the row: all fields valid, UID still unique, photo present.
2. **Render** the final PNG at 300 DPI and the PDF at CR80 size into the output folder, with a versioned file name (for example `UID20250318_v2.png`).
3. **Verify** the files exist and are not empty.
4. **Save** in one database transaction: create the CardIssue, create or update the Employee, set the row to Issued, and write an audit entry.
5. **Commit.** Show the success message.

If step 1, 2, or 3 fails, nothing is saved to Records. The row becomes Failed with the reason.

If step 4 fails, the transaction rolls back. The written files are removed, or marked as orphaned for cleanup. The row becomes Failed.

Issue is idempotent. Each attempt has an issue key (row ID plus attempt number). A repeated attempt with the same key does not create a second record.

---

## 6. Confirmations

**Before issuing**
- Dialog: "Issue card for [Name], UID [UID], valid until [Month Year]?"
- Buttons: Issue, Cancel.
- Default: on. An operator can turn it off in Settings for faster issuing.
- Always shown for re-issue and for any UID change, even if the setting is off.

**After a successful issue**
- Message: "Saved to Records: [UID], valid until [date]. Files: [PNG path], [PDF path]."
- Buttons: Open folder, Next card.
- The Records tab updates immediately.

**After a failed issue**
- Message: the reason and the step that failed.
- Nothing appears in Records. The row shows Failed with the same reason.

---

## 7. Persistence and Resume

- Every change to a batch row is saved to a `BatchRow` table as it happens. Closing or crashing the app does not lose work.
- On launch, if an unfinished batch exists, show: "Resume batch (N rows, M need review)?"
- Draft and failed rows never create Employee or CardIssue records.

---

## 8. Data Model Additions

| Table | Key fields |
|---|---|
| `BatchSession` | id, created_at, name, status |
| `BatchRow` | id, session_id, uid, name, blood_group, position, contract_type, print_date, photo_path, bg_mode, zoom, offset_x, offset_y, status, error, issued_card_id, updated_at |
| `CardIssue` (existing) | adds issue_key (unique), file paths |
| `AuditLog` (existing) | records every issue, failure, edit, delete, backup, and restore |

---

## 9. Single-Card Flow (New Card tab)

Same as a batch row with one item:

Enter fields → Photo processed → Review panel → Approve & Issue → Confirmation → Records updated.

The New Card tab uses the same review panel, so there is only one place to fix a photo.

---

## 10. Static Artwork Correction: Contact Number

- Current card shows: `Contact: 01712-109-683, 07103-790-950`
- Correct to: `Contact: 01712-109-683, 01703-790-950`
- This text is in the PSD "Don't Edit" group. Fix the text layer in the PSD, then re-export `background.png`.
- Search the code and config for `07103` to make sure no second copy exists.
- Add a golden-image test so the card is checked against the corrected background.

---

## 11. Acceptance Tests

| Area | Test | Expected |
|---|---|---|
| Grid | Position dropdown visible without double-click | Shows the selected value and opens on one click |
| Batch | Add 5 rows, 1 with a bad position | Bad row shows an error, the other 4 process |
| Batch | Start processing | No row is approved or issued automatically |
| Review | Navigate Previous and Next | Preview and fields update for each row |
| Review | Adjust zoom on a row, go to the next row, come back | Adjustment is kept |
| Issue | Approve a valid row | Files written, Records updated, success message shown |
| Issue | Force a render failure | Nothing in Records, row Failed with reason |
| Issue | Approve the same row twice | One CardIssue only |
| Issue | UID taken since the row was drafted | Blocked at Issue with a message |
| Resume | Close the app mid-review, reopen | Batch offered for resume with correct row states |
| Contact | Render a card | Shows `01703-790-950` |

---

## 12. Open Decisions

| ID | Question | Recommended default |
|---|---|---|
| B1 | Show a confirmation before every issue? | Yes, default on, with a setting to turn it off |
| B2 | Keep a batch after all rows are issued? | Keep for 30 days, then archive |
| B3 | Can a card be issued without a photo? | No |
| B4 | Who can approve? | Anyone using the app for now. Add user login later if needed. |
