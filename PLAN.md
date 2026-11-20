# Staff ID Card Generator — C# Build Plan

**Target:** single-machine Windows desktop app (`.exe`), one office PC, front side only.
**Source template:** `F:\Max Files\Other\New ID Card.psd` — 637×1012 px, 300 DPI, Staff UK branding.
**Status:** decisions confirmed (see Section 11). Build starts at Phase 0.

---

## 1. Scope

**In scope (v1)**
- Single-card creation through a form with live preview.
- **Batch mode:** import/enter multiple employees at once → queued generation, one-by-one review/edit → export each card or all as a batch (individual PNG/PDF files or a ZIP).
- Automatic validity calculation from contract type and print date.
- Automatic Code 128 barcode generated from the UID.
- Photo upload, automatic background removal, automatic face-based positioning, manual nudge + "Reset to auto".
- Export to PNG (300 DPI) and PDF (CR80, 53.98 × 85.6 mm).
- SQLite record store; reprint and re-issue with version history.
- Required visual check of the processed photo before save/export.

**Out of scope (v1)**
- Card back side, Bangla text, multi-user login/roles.
- ~~Batch import~~ — moved into v1 scope per user request (see Batch Mode, Section 3b).

---

## 2. Inputs

| # | Field | Type | Required | Rules |
|---|---|---|---|---|
| 1 | Full name | Text | Yes | 2–40 chars. Must fit 427 px box at 50 px; auto-shrink to min 34 px, then reject. |
| 2 | Blood group | Dropdown | Yes | A+, A−, B+, B−, AB+, AB−, O+, O−, N/A. Default N/A. List lives in config, not code. |
| 3 | Position | Dropdown | Yes | Junior Executive, General Executive, Technical Executive, Supervisor, Manager, HR Manager, Intern. |
| 4 | UID | Text | Yes | **Full string, e.g. `UID20250318`** (decision D1). Barcode encodes it verbatim; the card's ID line shows it **without the `UID` prefix** → `ID. 20250318`. Unique across records. |
| 5 | Contract type | Dropdown | Yes | Contractual (6 months) or Permanent (1 year). |
| 6 | Print date | Date | Yes | Defaults to today (Asia/Dhaka). Editable for back-dating / re-issue. **Internal only — never printed on the card.** |
| 7 | Photo | File | Yes | JPG/PNG, min 600 px short side, max 10 MB. Any background accepted. |

Calculated/fixed (not operator-entered): validity text, barcode, contact/address, logo/border/colors/signature — all static in the background artwork.

---

## 3. User Flow

```
[Open app]
    │
    ▼
[New Card form] ── validation errors ──► inline messages, stay on form
    │ ok
    ▼
[Live preview] (right pane) — photo: bg removed + auto-positioned
    │ nudge zoom/x/y only if needed; "Reset to auto"
    ▼
[Photo approval] — operator must visually confirm before Save enables
    │
    ▼
[Duplicate UID check]
    │ exists? ──► show record: Open / Re-issue (new print date) / Cancel
    │ no
    ▼
[Save record] ──► [Export PNG 300 DPI and/or PDF CR80] ──► [Done: download / print / create another]
```

**Alternate paths**
- **Reprint:** same data, same validity, cached photo cutout reused (model not rerun).
- **Re-issue:** new print date → validity recalculated → new CardIssue version.
- **Edit before printing:** changes saved as a new version; audit trail preserved.
- **Photo problems:** too-small image → warning, continue allowed. Export blocked only if no photo.

### 3b. Batch Mode

For issuing many cards at once (e.g. a new joiner batch):

```
[Batch entry] — paste rows / import CSV / add rows manually
    columns: Name, BloodGroup, Position, UID, ContractType, PrintDate?, PhotoFolder?
    │ validation per row; bad rows flagged inline, good rows enter queue
    ▼
[Queue] — cards processed one by one in order
    │ for each: photo pipeline → auto-fit → REVIEW screen
    │   operator can edit any field, nudge photo, approve, or skip/defer
    │   duplicate UID → same prompt as single-card flow
    ▼
[Batch export]
    │ per card: PNG (300 DPI) and/or PDF (CR80), named e.g. UID20250318.png
    │ all at once: individual files in a folder, or one ZIP
    │ progress shown; failed cards listed with reason, can retry individually
```

Design notes:
- Batch is a **mode of the same app**, not a separate tool — same form fields, same renderer, same DB. Each row becomes a normal Employee + CardIssue, so single-card and batch records are indistinguishable afterwards.
- Queue state persisted in SQLite (`BatchJob` / `BatchItem` tables) so an interrupted batch can resume after app restart.
- Photos for batch: either a folder of images matched by filename = UID, or per-row file picker. Ambiguous matches are flagged for manual assignment.
- "Export all" runs after all items are approved; individual export always available per item.

---

## 4. Validity Rules

Based on **print date**, not joining date.

| Contract type | Rule | Example: print 2026-11-19 | Display |
|---|---|---|---|
| Contractual | print + 6 months | 2027-05-19 | `Valid Until May 2027` |
| Permanent | print + 1 year | 2027-11-19 | `Valid Until November 2027` |

- Store full end date; display Month + Year only.
- Month-end clamping: 31 Aug + 6 months → last day of February; display still "February 2027".
- "Today" uses **Asia/Dhaka** timezone (`TimeZoneInfo` / `TimeZoneConverter`), never the machine clock blindly.
- Format fixed: `Valid Until {Month} {Year}` — red (#A12A26), Source Sans 3 Regular, 25 px.

---

## 5. Barcode Rules

- **Code 128**, data = UID verbatim (e.g. `UID20250318`).
- Rendered fresh each render at exactly **570 × 70 px** inside the artwork's quiet zone — never a scaled bitmap.
- Library: **ZXing.Net** (`BarcodeWriter`/`BarcodeReader` for self-tests).

---

## 6. Layout Reference (637 × 1012 px)

| Element | Box (L,T → R,B) | Font/style | Color | Align |
|---|---|---|---|---|
| Photo window | 313×382 at (162,189), radius 20, white | — | — | Cutout composited over white |
| Blood pill | (229,546)→(409,580), min 180×34 | Roboto ExtraBold ~16.7 px | #A12A26 | Centered x≈319, width grows |
| Name | (106,592)→(533,631), 427×39 | Roboto Medium 50 px | #1A253C | Centered x≈318 |
| Position | (180,650)→(458,675), 278×25 | Source Sans 3 Semibold 33 px | #A12A26 | Centered x≈319 |
| ID line | (229,695)→(408,718), 179×23 | Source Sans 3 Semibold 33 px | #1A253C | Centered x≈318 — shows `ID. {UID minus "UID" prefix}` e.g. `ID. 20250318` |
| Barcode | (34,759)→(604,829), 570×70 | Code 128 | Black | Centered |
| Validity | (193,850)→(444,869), 251×19 | Source Sans 3 Regular 25 px | #A12A26 | Centered x≈319 |
| Contact / Address / Signature | static | — | — | In background.png |

Text rules: center each string in its box. Name shrinks 50→34 px to fit 427 px, then reject. Verify all 7 position labels fit 278 px at 33 px with a render test.

**Background prep (one-time, Phase 0):** hide Edit layers in PSD → export `background.png` 637×1012 @300 DPI with photo window white/transparent; export `logo.png` and `signature.png`; bundle Roboto + Source Sans 3 TTFs; recreate photo window in code.

---

## 7. Technical Stack (C#)

| Layer | Choice | Notes |
|---|---|---|
| Language / runtime | **C#, .NET 8**, self-contained single-file publish | True `.exe`; no runtime install needed on target PC |
| UI | **WPF** | Live preview via `WriteableBitmap`/SkiaSharp surface; easier print layout than WinForms |
| Rendering | **SkiaSharp** | Exact 637×1012 pixel control, rounded masks, text shaping |
| Barcode | **ZXing.Net** | Code 128 encode/decode for tests |
| PDF | **PdfSharpCore** (or QuestPDF) | CR80 page = 53.98 × 85.6 mm |
| Database | **Microsoft.Data.Sqlite** | Single `.db` file beside the exe |
| Background removal | **ONNX Runtime** + rembg **u2net/isnet** ONNX model | rembg models are ONNX; runs fully local in C# |
| Face detection | **OpenCvSharp4 + YuNet ONNX model** (OpenCV Zoo) | Gives face box for auto-fit; small model, offline |
| Imaging decode | SkiaSharp (native codecs) | EXIF orientation via `SKCodec`/`ExifOrientation` |
| Fonts | Roboto + Source Sans 3 (OFL) bundled in `assets/fonts` | Matches PSD |
| Packaging | `dotnet publish -r win-x64 --self-contained` | Produces folder; can wrap single-file exe |

Fallback note: the plan's MediaPipe fallback has no good C# binding. Fallback in C# = **YuNet face detection + simple center-crop auto-fit** with a warning, or a "manual position only" mode. Decide during the Phase 0b spike.

### Project structure
```
ID-Card-Tool/
├── PLAN.md                  # this file
├── src/
│   └── IdCardTool/
│       ├── IdCardTool.csproj
│       ├── App.xaml / MainWindow.xaml
│       ├── Views/           # NewCard, RecordList, RecordDetail, PhotoReview, Done
│       ├── ViewModels/
│       ├── Core/
│       │   ├── Validity.cs        # calculate_valid_until, format_validity
│       │   ├── Barcode.cs         # ZXing Code128 → SKBitmap 570×70
│       │   ├── PhotoPipeline.cs   # validate → EXIF → bg-remove → face-detect → auto-fit
│       │   ├── BgRemove.cs        # ONNX Runtime session (u2net/isnet)
│       │   ├── FaceDetect.cs      # OpenCvSharp YuNet
│       │   ├── AutoFit.cs         # face box → scale/x/y (config constants)
│       │   ├── CardRenderer.cs    # compose 637×1012 SKBitmap
│       │   └── Exporter.cs        # PNG @300 DPI, PDF CR80
│       ├── Data/
│       │   ├── Db.cs              # Microsoft.Data.Sqlite
│       │   └── Models.cs          # Employee, CardIssue, BatchJob, BatchItem
│       ├── Config.cs              # positions, blood groups, geometry, ratios, TZ
│       └── assets/
│           ├── background.png  logo.png  signature.png
│           ├── fonts/  models/   # ONNX models (gitignored or bundled per size)
│           └── photos/           # cached cutouts per employee
├── tests/
│   └── IdCardTool.Tests/      # xUnit: validity, barcode round-trip, render golden
└── output/                    # generated cards (gitignored)
```

### Data model
- **Employee**: `Id, Uid (unique), Name, BloodGroup, Position, ContractType, PhotoPath, CreatedAt`
- **CardIssue**: `Id, EmployeeId, Version, PrintDate, ValidUntil (full date), IssuedAt, OutputPdfPath, OutputPngPath`
- Re-issue → new CardIssue (version+1). Reprint → reuse existing. No operator field (D10 = skip).

---

## 8. Photo Pipeline (per upload)

1. **Validate** type/size/≥600 px; EXIF-rotate.
2. **Background removal** via ONNX Runtime (u2net human-seg or isnet). Output RGBA cutout, cached per employee — reprints never rerun the model.
3. **Face detect** via YuNet on the original. No face → warning + center-subject fallback.
4. **Auto-fit** (constants in `Config.cs`):
   - `FACE_HEIGHT_RATIO ≈ 0.72` of window height (calibrate in Phase 0b)
   - face centered horizontally; hair top near window top; cutout covers whole window (no white gaps)
5. **Composite** cutout over white rounded window (313×382 @ (162,189), r=20).
6. **Nudge** zoom/x/y sliders + "Reset to auto". **Save is blocked until operator approves the preview** (D14).

Reference values from PSD sample: face ≈70–75% window height, head width ≈59% window width, face center within ~3 px of window center.

---

## 9. Confirmed Decisions

| ID | Decision |
|---|---|
| D1 | UID = **full string** (`UID20250318`); barcode encodes verbatim; card ID line shows UID **minus the `UID` prefix** → `ID. 20250318` |
| D1b | **Print date is internal only** — stored in DB, never rendered on the card |
| D2 | Validity displays **Month Year** only; full date stored |
| D3 | Re-issue **restarts validity** from new print date; reprint keeps it |
| D4 | Contact/address **static** in background |
| D5 | Signature **static**, same for all |
| D6 | Blood group = **dropdown**, N/A default, configurable list |
| D7 | **UK logo only** |
| D8 | **One office PC**, local SQLite |
| D9 | **Auto bg removal** (ONNX) + fallback, manual nudge |
| D10 | **No operator/audit field** for v1 |
| D11 | **Front only** |
| D12 | **Local processing only**, no third-party APIs |
| D13 | Machine is **online**; models downloaded once at setup |
| D14 | **Auto-fit + required visual approval** before save |
| D15 | **Batch mode in v1**: multi-row entry, queued one-by-one review/edit, per-card or batch export (PNG/PDF/ZIP) |

---

## 10. Build Phases

| Phase | Work | Est. |
|---|---|---|
| **0 — Assets** | Export `background.png`, `logo.png`, `signature.png` from PSD (hide Edit layers); copy fonts; verify photo-window geometry & barcode quiet zone | 0.5 day |
| **0b — Photo spike** | Get u2net/isnet ONNX + YuNet running via ONNX Runtime/OpenCvSharp; run 10–20 real photos; calibrate `FACE_HEIGHT_RATIO` & centering; pick model | 1 day |
| **1 — Rendering core** | `Validity.cs` + tests (incl. month-end), `Barcode.cs` round-trip test, `CardRenderer.cs` vs golden image | 1–2 days |
| **2 — Export** | PNG @300 DPI; PDF CR80 | 0.5 day |
| **3 — UI** | WPF form, live preview, photo review panel, duplicate-UID flow, approval gate | 1–2 days |
| **3b — Batch UI** | Multi-row entry (paste/CSV), queue view, per-item review reusing single-card screen, batch export (folder/ZIP), resume-after-restart | 1–2 days |
| **4 — Records** | SQLite store, list/search, reprint, re-issue versioning | 1 day |
| **5 — Test/handover** | Physical print test (colors, barcode scan, photo window); README | 1 day |

**Prereq:** install .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`) — not currently on this machine.

---

## 11. Test Cases (key)

- Validity: contractual 2026-11-19 → `May 2027`; permanent → `November 2027`; 2026-08-31 → `February 2027`; 2028-02-29 → `February 2029`.
- Name: 40-char shrink/reject; non-Latin chars flagged (v1 English only).
- UID duplicate → prompt, no second record.
- Barcode: ZXing decode round-trip returns UID (`UID20250318`).
- ID line: UID `UID20250318` renders `ID. 20250318`; UID without prefix renders as entered.
- Print date never appears anywhere on the rendered card.
- Batch: mixed valid/invalid rows → only valid rows queued; kill app mid-batch → resume works; batch export produces one file per approved card + ZIP option.
- Photo: landscape crop, <600 px warning, white/busy backgrounds, no-face fallback, two-faces (largest used), cached cutout on reprint, nudge + reset.
- All 7 positions fit 278 px @ 33 px; `N/A` pill renders `Blood Group: N/A`.
- PDF = 53.98 × 85.6 mm; PNG has 300 DPI metadata.
- Golden render test vs reference image with tolerance.
