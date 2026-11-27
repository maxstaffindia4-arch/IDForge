# IDForge: Batch Visual Parity Plan

Branch: `feature/uxpilot-redesign`. Work in `F:\Web Design\ID Card Tool`. Don't touch `main`.
Source of truth: the UX Pilot Batch design (exported 26/11/2026, title "IDForge — Batch: June starters").
Queue: after Phase 7 and after `NEW_CARD_VISUAL_PARITY_PLAN.md`. Shared shell rules (nav rail, status bar, Help and About, colour tokens, icon font) come from the New card plan. This plan doesn't repeat them.

---

## 0. How to use this plan

- The code in this plan is the spec. Copy the values exactly. If a resource name doesn't exist, map it to the closest existing resource and report the mapping.
- Where the design conflicts with a decision already made, the decision wins. Those conflicts are listed in section 1 and marked "Decision needed" where the owner hasn't decided yet.
- Scope: the Batch screen only. The Review window, Records, and Settings are separate.
- Keep: the Position dropdown (V1), the Blood group column (V11 closed), and the per-card issue flow from Review (Option A).

---

## 1. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Issuing | Cards are issued one at a time from Review (Option A). The "Issue batch" button is removed. The progress card's "Approved" segment becomes "Issued". |
| 2 | Batches | Saved batches, named by purpose (for example Interns, New JE, Reissue). The "Batches" dropdown lists them. "+ New batch" asks for a name. This replaces V14 (one batch at a time). |
| 3 | Photo column | Removed. The photo thumbnail sits in the Employee cell, as in the design. An empty photo shows a placeholder. Clicking the placeholder opens Browse. This completes V12. |
| 4 | Blood group | Kept as a column (V11 closed). Placed after Employee ID. |
| 5 | Filter chips | Design: All, Needs attention, Ready to review, **Issued** (replaces "Approved"), Re-issues. The "All" chip is dark when selected, as in the design. |
| 6 | Scrolling | The grid scrolls. It does not page. The footer shows "Showing N of M" only while a filter or search is active. Otherwise it shows just the selected count, if any. This replaces the design's paging. |
| 7 | Paste order | Paste and the top of the CSV import change to the design's tip order: Name, Position, Employee ID, Blood group, Contract, Print date. CSV import keeps its photo column after Print date. The tip text stays as the design wording. |
| 8 | Batch badge | Blue with white text when Batch is selected. Grey with the count when it isn't (owner decision 10). |
| 9 | Search | A "Find name or Employee ID (Ctrl+F)" box at the right of the toolbar. |
| 10 | Status bar | "Preparing photos: N of M" with a spinner while photos are processing. Hidden otherwise. |
| 11 | Window title | "IDForge — Batch: <session name>". |
| 12 | Re-issue message | "Replaces card issued dd/MM/yyyy. New validity until dd/MM/yyyy." |

---

## 2. Layout

Top to bottom:

1. **Header** (24 px side margins, 20 px top): title and session controls.
2. **Progress card** (24 px margins): counts, progress bar, and the Review action.
3. **Toolbar** (24 px margins): row actions, then the search box on the right.
4. **Filter chips** (24 px margins).
5. **Grid** (24 px margins, scrolls inside its own section).
6. **Footer line** (24 px margins): "Showing N of M · K selected" on the left, tip on the right.
7. **Status bar** (same shell as New card).

### 2.1 Header

```xml
<Grid Margin="24,20,24,16">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>
  </Grid.ColumnDefinitions>
  <StackPanel>
    <StackPanel Orientation="Horizontal">
      <TextBlock x:Name="TxtBatchName" Text="June starters" FontSize="20" FontWeight="SemiBold"/>
      <Button Style="{StaticResource IconTextButton}" Content="&#xF304;" FontFamily="{StaticResource IconFont}"
              ToolTip="Rename batch" Margin="8,0,0,0" Click="BtnRename_Click"/>
      <Border Margin="10,0,0,0" Padding="8,2" CornerRadius="10" Background="{DynamicResource SecondaryBrush}"
              BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" VerticalAlignment="Center">
        <TextBlock x:Name="TxtEmployeeCount" Text="24 employees" FontSize="11"
                   Foreground="{DynamicResource SecondaryTextBrush}"/>
      </Border>
    </StackPanel>
    <TextBlock Text="Add or paste employees, prepare their photos, then review each card before issuing."
               FontSize="13" Foreground="{DynamicResource MutedBrush}" Margin="0,2,0,0" TextWrapping="Wrap"/>
  </StackPanel>
  <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Bottom">
    <StackPanel Orientation="Horizontal" Margin="0,0,12,0" VerticalAlignment="Center">
      <TextBlock Text="&#xF0C7;" FontFamily="{StaticResource IconFont}" FontSize="11"
                 Foreground="{DynamicResource MutedBrush}" Margin="0,0,6,0"/>
      <TextBlock x:Name="TxtSaved" Text="Saved 14:11" FontSize="12" Foreground="{DynamicResource MutedBrush}"/>
    </StackPanel>
    <Button Style="{StaticResource SecondaryButton}" Margin="0,0,8,0" Click="BtnBatches_Click">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="Batches "/>
        <TextBlock Text="&#xF078;" FontFamily="{StaticResource IconFont}" FontSize="10"/>
      </StackPanel>
    </Button>
    <Button Style="{StaticResource SecondaryButton}" Click="BtnNewBatch_Click">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xF067;" FontFamily="{StaticResource IconFont}" FontSize="11" Margin="0,0,6,0"/>
        <TextBlock Text="New batch"/>
      </StackPanel>
    </Button>
  </StackPanel>
</Grid>
```

- The "Batches" menu lists saved batches with their row count and date. Picking one opens it. "New batch" creates an empty batch and asks for a name. (Decision 2.)
- `SecondaryButton` is the same style as on New card.
- `IconTextButton`: no border, muted icon, `PrimaryBrush` on hover.

### 2.2 Progress card

```xml
<Border Margin="24,0,24,16" Padding="16" CornerRadius="6" Background="{DynamicResource PanelLightBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1">
  <DockPanel>
    <!-- right: review action -->
    <StackPanel DockPanel.Dock="Right" VerticalAlignment="Center" Margin="16,0,0,0">
      <Button Style="{StaticResource PrimaryButton}" Click="BtnReview_Click">
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="&#xF4AC;" FontFamily="{StaticResource IconFont}" Margin="0,0,8,0"/>
          <TextBlock Text="Review cards"/>
          <TextBlock Text="Ctrl+R" FontSize="11" FontWeight="Normal" Opacity="0.8" Margin="8,0,0,0" VerticalAlignment="Center"/>
        </StackPanel>
      </Button>
      <TextBlock x:Name="TxtReviewHint" Text="Opens the first card that needs review" FontSize="11"
                 Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" Margin="0,4,0,0"/>
    </StackPanel>
    <StackPanel>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Bottom">
        <TextBlock x:Name="TxtIssuedCount" FontSize="15" FontWeight="SemiBold"/>  <!-- "18 of 24 issued" -->
        <TextBlock x:Name="TxtRemaining" FontSize="12" Foreground="{DynamicResource MutedBrush}" Margin="8,0,0,0"/>  <!-- "— 6 to review" -->
      </StackPanel>
      <!-- progress bar: one column per state, width = count (star units) -->
      <Border Margin="0,10,0,0" Height="8" CornerRadius="4" ClipToBounds="True"
              Background="{DynamicResource SecondaryBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1">
        <Grid x:Name="GridProgress">
          <Grid.ColumnDefinitions>
            <ColumnDefinition x:Name="ColIssued" Width="0*"/>      <!-- SuccessBrush -->
            <ColumnDefinition x:Name="ColReview" Width="0*"/>     <!-- PrimaryBrush -->
            <ColumnDefinition x:Name="ColAttention" Width="0*"/>  <!-- ErrorBrush -->
            <ColumnDefinition x:Name="ColProcessing" Width="0*"/> <!-- WarningBrush -->
          </Grid.ColumnDefinitions>
          <Border Grid.Column="0" Background="{DynamicResource SuccessBrush}"/>
          <Border Grid.Column="1" Background="{DynamicResource PrimaryBrush}"/>
          <Border Grid.Column="2" Background="{DynamicResource ErrorBrush}"/>
          <Border Grid.Column="3" Background="{DynamicResource WarningBrush}"/>
        </Grid>
      </Border>
      <!-- legend -->
      <WrapPanel Margin="0,10,0,0">
        <!-- each: 8x8 square in the state colour + label, e.g. "Issued 18" -->
      </WrapPanel>
    </StackPanel>
  </DockPanel>
</Border>
```

Code:
- Set each `ColumnDefinition.Width` to `new GridLength(count, GridUnitType.Star)`. A zero count gives a zero-width column, so the segment disappears.
- Draft rows aren't shown in the bar. They appear in the legend only, as in the design.
- The legend lists: Issued, Ready to review, Needs attention, Processing photo, Draft. Each label shows its count.
- When the batch is empty, the bar is empty and the text reads "No rows yet".
- `TxtRemaining` says "— N to review". It counts only rows that have content (as fixed in Fix H-B).

### 2.3 Toolbar

Buttons are 32 px high, with a 1 px border, 12 px text, and the shortcut in muted text. Separators are 1 px wide, 20 px high, in `BorderBrush`.

Order (left to right):
1. Add row (Ctrl+N), with a plus icon in `PrimaryBrush`.
2. Duplicate (Ctrl+D).
3. Paste from Excel (Ctrl+V).
4. Separator.
5. Import CSV…
6. Export CSV….
7. Separator.
8. Prepare photos (F5), with the wand icon `&#xF0D0;`.
9. Remove N selected (Del). Destructive: `ErrorBrush` text, a 40% `ErrorBrush` border, and a 5% red fill on hover. The label shows the selected count, e.g. "Remove 1 selected".

Search box on the right: 256 px wide, 32 px high, `InputBrush` border, magnifier icon `&#xF002;` in muted colour, placeholder "Find name or Employee ID  (Ctrl+F)". Typing filters the grid.

```xml
<Style x:Key="ToolButton" TargetType="Button" BasedOn="{StaticResource SecondaryButton}">
  <Setter Property="FontSize" Value="12"/>
  <Setter Property="Padding" Value="12,0"/>
</Style>

<Style x:Key="ToolButtonDanger" TargetType="Button" BasedOn="{StaticResource ToolButton}">
  <Setter Property="Foreground" Value="{DynamicResource ErrorBrush}"/>
  <Setter Property="BorderBrush" Value="#66EF4444"/>  <!-- 40 % of ErrorBrush -->
</Style>
```

### 2.4 Filter chips

```xml
<Style x:Key="FilterChip" TargetType="RadioButton">
  <Setter Property="Height" Value="28"/>
  <Setter Property="Padding" Value="12,0"/>
  <Setter Property="FontSize" Value="12"/>
  <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="RadioButton">
        <Border x:Name="Bd" CornerRadius="14" BorderThickness="1"
                BorderBrush="{DynamicResource BorderBrush}" Background="{DynamicResource BackgroundBrush}"
                Padding="{TemplateBinding Padding}">
          <ContentPresenter VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource SecondaryBrush}"/>
          </Trigger>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource TextBrush}"/>
            <Setter Property="Foreground" Value="White"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<!-- Needs attention: red outline, light red fill -->
<Style x:Key="FilterChipAttention" TargetType="RadioButton" BasedOn="{StaticResource FilterChip}">
  <Setter Property="Foreground" Value="{DynamicResource ErrorBrush}"/>
  <Setter Property="BorderBrush" Value="#66EF4444"/>
  <Setter Property="Background" Value="#0DEF4444"/>  <!-- 5 % -->
</Style>

<!-- Re-issues: amber outline, light amber fill -->
<Style x:Key="FilterChipReissue" TargetType="RadioButton" BasedOn="{StaticResource FilterChip}">
  <Setter Property="Foreground" Value="{DynamicResource WarningBrush}"/>
  <Setter Property="BorderBrush" Value="#66D97706"/>  <!-- 40 % of WarningBrush -->
  <Setter Property="Background" Value="#0DD97706"/>
</Style>
```

- Chip labels include the count: "All 24", "Needs attention 2", "Ready to review 2", "Issued 18", "Re-issues 3".
- The grid filters on the same chip values. Counts in chips must match the grid.
- Chip order: All, Needs attention, Ready to review, Issued, Re-issues. Only one chip is checked at a time.

### 2.5 Grid

The grid section has a 1 px border, a 6 px corner radius, and scrolls inside itself. It does not scroll the page.

```xml
<Border Margin="24,0,24,12" CornerRadius="6" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
        ClipToBounds="True">
  <DataGrid x:Name="DgBatch" AutoGenerateColumns="False" HeadersVisibility="Column"
            GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="{DynamicResource BorderBrush}"
            RowHeight="44" MinHeight="200" MinWidth="1080" CanUserResizeRows="False"
            ColumnHeaderStyle="{StaticResource BatchHeader}" RowStyle="{StaticResource BatchRow}"
            Background="{DynamicResource BackgroundBrush}" BorderThickness="0"
            ScrollViewer.HorizontalScrollBarVisibility="Auto">
    <!-- columns: see 2.6 -->
  </DataGrid>
</Border>
```

```xml
<Style x:Key="BatchHeader" TargetType="DataGridColumnHeader">
  <Setter Property="Background" Value="{DynamicResource PanelLightBrush}"/>
  <Setter Property="Foreground" Value="{DynamicResource SecondaryTextBrush}"/>
  <Setter Property="FontSize" Value="12"/>
  <Setter Property="FontWeight" Value="Medium"/>
  <Setter Property="Height" Value="36"/>
  <Setter Property="Padding" Value="8,0"/>
  <Setter Property="BorderThickness" Value="0,0,0,1"/>
  <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
</Style>

<Style x:Key="BatchRow" TargetType="DataGridRow">
  <Style.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
      <Setter Property="Background" Value="{DynamicResource PanelLightBrush}"/>
    </Trigger>
    <Trigger Property="IsSelected" Value="True">
      <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
      <Setter Property="BorderBrush" Value="#66" />  <!-- 40 % of PrimaryBrush: set as #663B82F6 -->
      <Setter Property="BorderThickness" Value="1"/>
    </Trigger>
  </Style.Triggers>
</Style>
```

Use `#663B82F6` for the selected row border (40 % of the primary blue). Keep the existing 2 px focus ring on cells (Fix H-A).

### 2.6 Columns

| # | Column | Width | Content |
|---|---|---|---|
| 1 | Select | 36 | Checkbox |
| 2 | # | 40 | Row number, muted |
| 3 | Employee | Star, min 220 | Thumbnail (28 × 32) + name (13 px SemiBold) |
| 4 | Employee ID | 130 | Mono text. Red border when invalid (see below) |
| 5 | Blood group | 90 | Dropdown (as now) |
| 6 | Position | 200 | Dropdown with "Choose position" placeholder |
| 7 | Contract | 120 | Dropdown |
| 8 | Print date | 110 | dd/MM/yyyy |
| 9 | Card type | 110 | Pill: "New card" (accent) or "Re-issue" (warning) |
| 10 | Status | 130 | Pill (see 2.7) |
| 11 | Message | Star, min 200 | Secondary text, or red for errors |

**Employee cell** (thumbnail plus name):

```xml
<StackPanel Orientation="Horizontal" VerticalAlignment="Center">
  <!-- photo present -->
  <Border Width="28" Height="32" CornerRadius="4" ClipToBounds="True" Margin="0,0,10,0"
          Opacity="{Binding ThumbOpacity}">   <!-- 0.6 while processing -->
    <Image Source="{Binding Thumb}" Stretch="UniformToFill"/>
  </Border>
  <!-- no photo: red dashed placeholder when required; grey when Draft -->
  <Border Width="28" Height="32" CornerRadius="4" Margin="0,0,10,0"
          BorderThickness="1" BorderBrush="#99EF4444" Background="#0DEF4444"
          Visibility="{Binding ShowNeedsPhotoPlaceholder}" ToolTip="Click to add a photo" Cursor="Hand">
    <Border.Style>
      <Style TargetType="Border"/>  <!-- dashed: use a DashStyle on a Rectangle overlay, see note -->
    </Border.Style>
    <TextBlock Text="&#xF007;" FontFamily="{StaticResource IconFont}" FontSize="10"
               Foreground="{DynamicResource ErrorBrush}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <TextBox Text="{Binding Name}" Style="{StaticResource GridInput}"/>
</StackPanel>
```

- The dashed border needs a `Rectangle` with `StrokeDashArray="3 2"`. Draw the placeholder as a rectangle, not as a border.
- Click on the placeholder opens the file picker (Browse). Nothing else opens it.
- The grey Draft placeholder: `SecondaryBrush` fill, a solid `BorderBrush` border, muted user icon.
- Processing rows show the thumbnail at 60 % opacity.

**Grid input** (name, ID, and inputs in the grid):
- Full cell width, 1 px `InputBrush` border always visible, height matches the dropdowns in the row (32 px).
- Placeholders: "Full name" and "Employee ID" (grey), as Fix J-B.
- Employee ID invalid (not 4–15 digits): `ErrorBrush` 1 px border on the cell, and the Message column shows "Employee ID can only contain numbers (4–15 digits)." in red.

### 2.7 Status and card type pills

Pill: 11 px text, 10 px corner radius, 8 px horizontal padding, 2 px vertical padding, icon 9 px before the label.

| State | Label | Icon | Pill background | Text |
|---|---|---|---|---|
| Issued | Issued | check `&#xF00C;` | `SuccessBrush` 10 % | `SuccessBrush` |
| Needs review | Ready to review | eye `&#xF06E;` | `PrimaryBrush` 10 % | `PrimaryHoverBrush` |
| Failed | Needs attention | circle-exclamation `&#xF06A;` | `ErrorBrush` 10 % | `ErrorBrush` |
| Processing | Processing | spinner `&#xF110;` (spinning) | `WarningBrush` 10 % | `WarningBrush` |
| Needs photo | Needs photo | camera `&#xF030;` | `ErrorBrush` 10 % | `ErrorBrush` |
| Draft | Draft | file `&#xF15B;` | `SecondaryBrush`, 1 px border | `SecondaryTextBrush` |
| Skipped | Skipped | circle-minus `&#xF056;` | `SecondaryBrush`, 1 px border | `MutedBrush` |

Card type:
- New card: `AccentBrush` background, `AccentTextBrush` text.
- Re-issue: `WarningBrush` 10 % background, `WarningBrush` text, rotate icon `&#xF2F1;`.

The processing spinner keeps spinning while the row is processing. Stop it when the state changes.

### 2.8 Message column

- Normal text: `SecondaryTextBrush`, 12 px.
- Errors: `ErrorBrush`, with links underlined ("Add photo…").
- Re-issue message: "Replaces card issued dd/MM/yyyy. New validity until dd/MM/yyyy." (decision 12).
- Duplicates: "Duplicate of row N".
- Empty rows show nothing. Rows with some content show "Missing: …" (Fix H-B).

### 2.9 Footer line and bottom

Under the grid, 24 px margins:
- Left: "Showing N of M" only while a filter or search is active, e.g. "Showing 3 of 24". Add "· K selected" when rows are selected. With no filter, no search, and no selection, the line is empty (decision 6).
- Right: the tip, muted, with the lightbulb icon `&#xF0EB;`. The tip text must match the paste order decided in decision 7.

Status bar (shared shell):
- Left: offline dot, "1,284 cards on record", "Last backup dd/MM/yyyy", then "Preparing photos: N of M" with a spinner while the photos are processing (decision 10). Cancel for photo preparation sits next to the progress text.
- Right: version and About link (New card plan, section 4.4).

"Open output" moves to the Batches menu, under an "Open output folder" item.

---

## 3. Responsive rules

- Minimum window: 860 × 620 (shared).
- The grid is the only element that scrolls sideways. It scrolls inside its own border.
- Toolbar buttons wrap onto a second line before they clip. The search box moves below the buttons at narrow widths.
- The progress card stacks its Review button under the counts at widths below 1100 px.
- The status bar shows the version and About link at every size.

---

## 4. Acceptance checks

**Rendered checks:**
- At 860 × 620, 1331 × 700, and 1920 × 1080: no button text is clipped, the toolbar wraps before it clips, and the grid scrolls only inside its border.
- Progress bar column widths match the counts. Zero-count segments are not visible.
- Each row's thumbnail, placeholder, and status pill sit fully inside their cells.

**Behaviour checks:**
- Chip counts match the rows each chip shows.
- Review (Ctrl+R) opens the first card that needs review. It is disabled with a reason when none is left.
- There is no "Issue batch" button.
- Clicking a missing photo placeholder opens the file picker. Clicking anywhere else in the cell does not.
- An invalid Employee ID shows the red border and the message.
- Batch badge: blue when Batch is selected, grey when it isn't.
- Status bar: "Preparing photos: N of M" appears while photos are processing, and disappears when they finish.
- Shortcuts: Ctrl+N, Ctrl+D, Ctrl+V, F5, Del, Ctrl+F, Ctrl+R.

**Screenshots** (save to `docs/redesign/shots/`, commit, and list the paths in the report):
- Batch at 1331 × 700 with rows in several states.
- Batch at 860 × 620.
- Batch with no rows.

---

## 5. Divergences to keep

| ID | Item | Status |
|---|---|---|
| V1 | Position is a dropdown | Kept |
| V11 | Blood group column | Kept (closed) |
| V12 | Photo column | Replaced by thumbnail and placeholder (this plan) |
| V13 | Issued, Skipped, and Failed states | Kept. Design says "Needs attention" for Failed. |
| V14 | One batch at a time | Replaced by saved, named batches (decision 2) |
| V15 | Scrolling or paging | Scrolling kept (decision 6) |
| — | Blood group in the design grid | Not in the design. Kept by decision 4. |

---

## 6. Open decisions

All four decisions are made (section 1, decisions 2, 4, 6, and 7). Apply this plan after the New card plan.
