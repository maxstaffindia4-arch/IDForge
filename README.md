# Staff ID Card Generator

Windows desktop tool (C# / .NET 8 / WPF) that generates print-ready staff ID cards
from `New_ID_Card.psd` (637×1012 px, 300 DPI, CR80 output).

## Features

- New card form with live preview (name auto-shrinks 50→34 px to fit).
- Automatic validity: Contractual = print date + 6 months, Permanent = + 1 year
  (month-end clamped; "today" uses Asia/Dhaka).
- Code 128 barcode generated from the UID (`UID20250318`).
- Card shows `ID. 20250318` (UID prefix stripped for display only).
- Photo pipeline: automatic background removal (u2net ONNX, local),
  face detection (YuNet), auto-fit into the photo window, manual nudge +
  "Reset to auto", required visual approval before saving.
- Export: PNG at 300 DPI and/or PDF at CR80 (53.98 × 85.6 mm).
- Records: SQLite store, search, reprint, re-issue (versioned).
- Batch mode: paste CSV rows → queued photo processing → per-row export.

## Requirements

- Windows 10/11, .NET 8 SDK to build (runtime not needed — self-contained publish possible).

## Build & run

```powershell
# one-time: install SDK if missing
# winget install Microsoft.DotNet.SDK.8   (or use dotnet-install.ps1)

dotnet build IDForge.sln
dotnet run --project src/IDForge
# or run the exe:
src/IDForge/bin/Debug/net8.0-windows/IDForge.exe
```

Tests:

```powershell
dotnet test tests/IDForge.Tests
```

## Layout

- `assets/` — background.png, logo.png, signature.png, fonts, ONNX models (copied to output).
- `src/IDForge.Core` — rendering, barcode, validity, photo pipeline, SQLite store, export.
- `src/IDForge` — WPF UI.
- `output/` (under app dir) — generated PNG/PDF cards.
- `photos/` (under app dir) — cached cutouts per employee.
- `idcard.db` (under app dir) — SQLite database.

## Batch format

One employee per line, comma-separated:

```
UID20260001, Ahmed Rahman, O+, Supervisor, Permanent, 2026-12-01, C:\photos\ahmed.jpg
```

Columns: UID, Full Name, Blood Group, Position, Contract Type,
Print Date (optional, default today), Photo Path (optional but required to issue).

## Photo pipeline notes

- `FACE_HEIGHT_RATIO` (0.72), window geometry, and model paths live in
  `src/IDForge.Core/Config.cs` — calibrate with real staff photos.
- Models are NOT in git (too large for GitHub). After cloning, run
  `tools\fetch-models.ps1` to download them into `assets/models/`
  (u2net ~176 MB, YuNet ~230 KB). No third-party uploads; everything runs locally.
- If the ONNX model is missing, the app falls back to using the photo as-is
  with a warning.
