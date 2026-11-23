# Design divergences and precedence

## Design precedence

1. **UX Pilot spec and mockups** define the app shell, navigation, Settings,
   and message text.
2. **Existing behaviour and business rules always win.** Where the mockup
   implies a behaviour change that is not explicitly approved, keep the
   current behaviour and log the divergence here.
3. The **card render never changes** — its colours live in
   `src/IDForge/Themes/Colors.Card.xaml` for documentation only; the
   renderer (SkiaSharp) owns the real values.
4. Open decisions D1–D8 in `PLAN.md` section 4 take precedence over mockup
   details until the owner answers.

## Logged divergences

| ID | Mockup says | We keep | Reason |
|----|-------------|---------|--------|
| V1 | Position as free-text field | Position stays a dropdown (`Config.Positions`) | V1 decision — dropdown prevents invalid positions; revisit only if owner asks |
| V2 | Icon glyphs in collapsed nav | ~~Single letters~~ → Segoe MDL2 Assets icons (E8C8/E8FD/E8F1/E713, E898 backup); icon-only + tooltip when collapsed | Closed in Phase 2 fix pass — system font, no embedding needed |
| V3 | Page-header action buttons | Headers show title+description only | Area actions still live inside each page (phases 3–6 move them) |
| V4 | "• unsaved changes" per area | Tracked for New Card field edits only | Batch rows persist immediately; Records/Settings have no draft state yet |
| V5 | Date-format setting | Display-only text "dd/MM/yyyy" | Entry formats change in phase 3 (New Card) |
| V6 | Menu items Edit/Records | Minimal placeholders (no-op / Go to Records) | Full menu commands arrive with the phases that own each area |
| V7 | Photo background shown as a single option | All three modes kept as radio buttons (plain white / model / keep original) | D2 — removing modes would drop existing behaviour |
| V8 | "Show print edges" toggle | Omitted | D8 — deferred/left out by decision |
| V9 | Position nudge as drag/slider only | Shared PhotoPositionPanel: arrows (1 px, Shift = 10 px), X/Y numeric boxes, zoom slider + % box, Reset | Existing transform values and clamps unchanged; slider kept per V4-open note |
| V10 | Settings sections Backup & restore, Data protection, About | Deferred to phase 7 | Phase 3 scope — settings page keeps Phase 2 sections only |
