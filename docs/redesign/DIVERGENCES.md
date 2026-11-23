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
