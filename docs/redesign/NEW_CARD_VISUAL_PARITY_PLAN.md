# IDForge: Visual Parity Plan (New card and app shell)

Branch: `feature/uxpilot-redesign`. Work in `F:\Web Design\ID Card Tool`. Don't touch `main`.
Source of truth: the UX Pilot New card design (exported 26/11/2026). Where this plan and the design differ, this plan wins, because the owner has already decided each difference below.

---

## 0. How to use this plan

- **The code in this plan is the spec.** Copy the values exactly. If a resource name in this plan doesn't exist in `Themes\Colors.xaml` or the other theme files, use the closest existing resource, and report the mapping in your report. Don't invent new colours.
- **Scope:** the New card screen and the app shell (nav rail, status bar, menus, Help and About). The Batch, Review, Records, and Settings visuals are not covered here. The Batch screen design comes later.
- **Keep unchanged:**
  - The Position field stays a dropdown (divergence V1).
  - The card preview render stays our own. The design's placeholder card is not copied.
  - The 40–300 % photo zoom range stays.
- **Order of work:** A (shell and icons), then B (New card form and header), C (preview), D (photo panel), E (footer, status bar, and responsive checks). Commit each part and push. Then run the checks in section 7.

---

## 1. Decisions (final)

| # | Topic | Decision |
|---|---|---|
| 1 | Approval step | None. The Issue card button is enabled when the details are valid and a photo is present. There is no "Approve photo" step. |
| 2 | Photo background | Three radio buttons, not a dropdown. All three options are visible at once, so one click is enough. Default: "Replace with plain white (recommended)". |
| 3 | Blood group and Print date | Side by side, in two columns. |
| 4 | Preview zoom | Three buttons: minus, "Fit", plus. (Photo zoom in the photo panel has the slider and percentage box, see 5.4.) |
| 5 | Show print edges | A checkbox in the preview header. It only affects the preview, never the saved PNG or PDF. Default: off. |
| 6 | Nav selected item | Design style: light blue fill, dark blue text, and a 3 px blue bar on the left edge of the item. The keyboard focus ring is separate and only shows on keyboard navigation. |
| 7 | Nav rail | Stays collapsible. Icons come from the design (Font Awesome). Expanded: icon, text, and shortcut. Collapsed: icon only. The backup card is part of the rail: a full card when expanded, an icon when collapsed. |
| 8 | Status bar and Help | The status bar shows the app version from the build. "About" in the status bar opens the About dialog. The Help menu holds Help. F1 opens Help. Separating them is the right call. |
| 9 | Print text | "Printed size 54 × 86 mm" and "Print-quality image" (with "+ PDF" only when PDF saving is on in Settings). Hint: "Click the photo and use the arrow keys to move it." |
| 10 | Batch count badge | Batch not selected: grey badge with the count. Batch selected: blue badge with white text. Provisional, to be checked against the Batch design later. |

---

## 2. Responsive layout

| Window width | Nav rail | New card columns |
|---|---|---|
| 1280 px and wider | Expanded (208 px) by default | Three columns: form (360) · preview (flexible) · photo (330) |
| 1100 to 1279 px | Expanded (208 px) by default | Two columns: left column (360) holds the form, then the photo panel; right column holds the preview |
| Below 1100 px | Collapsed (56 px) automatically | Two columns, as above |
| 860 px (minimum) | Collapsed (56 px) | Two columns, as above |

Rules:
- The minimum window size stays 860 × 620.
- Nothing is clipped at any size, and the page never scrolls sideways.
- The left column scrolls on its own. The preview scrolls on its own.
- The rail's expand toggle works at any width. When expanded at a narrow width, the rail overlays the content rather than pushing it.
- Maximised and full-screen windows use the same rules.

---

## 3. Colours, type, and icons

### 3.1 Colour tokens

| Design token | Hex | App resource |
|---|---|---|
| background | #FFFFFF | `BackgroundBrush` |
| foreground | #333333 | `TextBrush` |
| primary | #3B82F6 | `PrimaryBrush` |
| primary hover | #2563EB | `PrimaryHoverBrush` |
| primary foreground | #FFFFFF | `OnPrimaryBrush` |
| secondary | #F3F4F6 | `SecondaryBrush` (same as `PanelBrush`) |
| secondary foreground | #4B5563 | `SecondaryTextBrush` |
| muted | #F9FAFB | `PanelLightBrush` |
| muted foreground | #6B7280 | `MutedBrush` |
| accent | #E0F2FE | `AccentBrush` (add if missing; do not reuse `PrimaryTintBrush` if it differs) |
| accent foreground | #1E3A8A | `AccentTextBrush` (add if missing) |
| destructive | #EF4444 | `ErrorBrush` |
| border | #E5E7EB | `BorderBrush` |
| input border | #E5E7EB | `InputBrush` |
| ring | #3B82F6 | `PrimaryBrush` (focus ring) |
| sidebar | #F9FAFB | `SidebarBrush` |
| success | #16A34A | `SuccessBrush` |
| warning | #D97706 | `WarningBrush` |

The card's own colours (navy #1A253C, red #A12A26, pink #FFF0F0) are used only on the card render, not in the UI.

### 3.2 Type scale

| Use | Size | Weight | Colour |
|---|---|---|---|
| Page title ("New card") | 20 | SemiBold | Text |
| Page subtitle | 13 | Regular | Muted |
| Section heading ("Employee details") | 14 | SemiBold | Text |
| Field label | 12 | Medium | Text |
| Input text | 13 | Regular | Text |
| Helper text | 11 | Regular | Muted |
| Nav item | 13 | Regular (Medium when selected) | Secondary text (accent text when selected) |
| Nav shortcut | 11 | Regular | Muted |
| Badge | 11 | Regular | Secondary text |
| Footer chips | 12 | Regular | Text |
| Issue card button | 14 | SemiBold | White |

Fonts: Inter for the UI, JetBrains Mono for the UID and numeric inputs. Roboto and Source Sans 3 stay on the card render only.

### 3.3 Icons

Use Font Awesome 6 Free, Solid style, embedded as a font file, the same way Inter is embedded. Include the font's licence file in `assets\fonts\`. Use the `IconFont` resource below for every icon.

```xml
<FontFamily x:Key="IconFont">/IDForge;component/assets/fonts/fa-solid-900.ttf#Font Awesome 6 Free</FontFamily>
```

Codepoints (write them as `&#xF2C2;` in XAML):

| Icon | Codepoint | Used for |
|---|---|---|
| id-card | F2C2 | New card nav |
| layer-group | F5FD | Batch nav |
| folder-open | F07C | Records nav |
| gear | F013 | Settings nav |
| shield-halved | F3ED | Backup |
| floppy-disk | F0C7 | Draft saved |
| print | F02F | Issue card |
| circle-check | F058 | Completed chips, approved states |
| circle-dot | F192 | Selected contract option |
| circle | F111 | Unselected contract option |
| calendar | F133 | Print date picker |
| calendar-check | F274 | Validity box |
| keyboard | F11C | Hint box |
| ruler-combined | F545 | Printed size |
| image | F03E | Print quality |
| circle-question | F059 | About |
| rotate-left | F2EA | Reset |
| magnifying-glass-plus | F00E | (reference only) |
| magnifying-glass-minus | F010 | (reference only) |
| minus | F068 | Preview zoom out |
| plus | F067 | Preview zoom in |
| chevron-up / down / left / right | F077 / F078 / F053 / F054 | Nudge pad |
| arrows-to-dot | E4BA | Nudge pad centre |
| caret-up / caret-down | F0D8 / F0D7 | Spinner buttons |

Use the Solid style for every icon. The design uses a few Regular-style icons, but we only embed the Solid file. Check each codepoint renders in the embedded font, and report any that don't.

---

## 4. App shell

### 4.1 Nav rail layout

Set the width in code by calling `SetNavCollapsed(bool)`. That single method controls labels, headings, badges, and the backup card together. Expanded is 208 px, collapsed is 56 px.

```xml
<Border x:Name="NavCol" Width="208" Background="{DynamicResource SidebarBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,1,0">
  <DockPanel Margin="0,12">

    <!-- Backup: full card when expanded, icon when collapsed -->
    <Border DockPanel.Dock="Bottom" x:Name="BackupFull" Margin="12,8" Padding="12" CornerRadius="4"
            Background="{DynamicResource BackgroundBrush}"
            BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1">
      <StackPanel>
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="&#xF3ED;" FontFamily="{StaticResource IconFont}" FontSize="12"
                     Foreground="{DynamicResource SuccessBrush}" Margin="0,0,8,0"/>
          <TextBlock Text="Backed up" FontSize="12" FontWeight="Medium"/>
        </StackPanel>
        <TextBlock Text="Last backup dd/MM/yyyy at HH:mm" FontSize="11"
                   Foreground="{DynamicResource MutedBrush}" Margin="0,4,0,0" TextWrapping="Wrap"/>
        <Button Content="Back up now" Style="{StaticResource SecondaryButton}" Height="28" Margin="0,8,0,0"/>
      </StackPanel>
    </Border>

    <StackPanel>
      <TextBlock x:Name="LblIssue" Text="ISSUE CARDS" Margin="16,0,0,8" FontSize="11" FontWeight="SemiBold"
                 Foreground="{DynamicResource MutedBrush}"/>
      <!-- nav items: see 4.2 -->
      <Rectangle x:Name="DvdIssue" Height="1" Margin="12,12" Fill="{DynamicResource BorderBrush}" Visibility="Collapsed"/>
      <TextBlock x:Name="LblManage" Text="MANAGE" Margin="16,12,0,8" FontSize="11" FontWeight="SemiBold"
                 Foreground="{DynamicResource MutedBrush}"/>
      <!-- Records, Settings -->
    </StackPanel>
  </DockPanel>
</Border>
```

When collapsed:
- `LblIssue` and `LblManage` are hidden, and `DvdIssue` (the divider) is shown.
- Item labels and shortcuts are hidden.
- `BackupFull` is hidden, and a 40 × 36 icon button (shield icon, tooltip "Last backup dd/MM/yyyy HH:mm. Back up now") is shown in its place.

### 4.2 Nav item

Each nav item is a `RadioButton` in a group named `Nav`. The content is an icon, a label, and a shortcut.

```xml
<Style x:Key="NavItem" TargetType="RadioButton">
  <Setter Property="Height" Value="36"/>
  <Setter Property="Margin" Value="8,2"/>
  <Setter Property="Foreground" Value="{DynamicResource SecondaryTextBrush}"/>
  <Setter Property="FontSize" Value="13"/>
  <Setter Property="FocusVisualStyle" Value="{StaticResource NavFocusRing}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="RadioButton">
        <Grid>
          <Border x:Name="Bg" CornerRadius="4" Background="Transparent"/>
          <!-- 3 px bar on the left edge, inset 8 px top and bottom -->
          <Border x:Name="Bar" Width="3" HorizontalAlignment="Left" VerticalAlignment="Stretch"
                  Margin="0,8,0,8" CornerRadius="0,2,2,0"
                  Background="{DynamicResource PrimaryBrush}" Visibility="Collapsed"/>
          <ContentPresenter Margin="12,0" VerticalAlignment="Center"/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="Bg" Property="Background" Value="{DynamicResource SecondaryBrush}"/>
          </Trigger>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Bg" Property="Background" Value="{DynamicResource AccentBrush}"/>
            <Setter TargetName="Bar" Property="Visibility" Value="Visible"/>
            <Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/>
            <Setter Property="FontWeight" Value="Medium"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

Item content (New card shown; the other items follow the same pattern):

```xml
<RadioButton Style="{StaticResource NavItem}" GroupName="Nav" IsChecked="True">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="20"/>
      <ColumnDefinition Width="*"/>
      <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="&#xF2C2;" FontFamily="{StaticResource IconFont}" FontSize="14"
               HorizontalAlignment="Center" VerticalAlignment="Center"/>
    <TextBlock x:Name="LblNew" Grid.Column="1" Text="New card" Margin="10,0,0,0" VerticalAlignment="Center"/>
    <TextBlock x:Name="ShortNew" Grid.Column="2" Text="Ctrl+1" FontSize="11"
               Foreground="{DynamicResource MutedBrush}" VerticalAlignment="Center"/>
  </Grid>
</RadioButton>
```

Focus ring: keep the keyboard-only `NavFocusRing` from Fix J-A. It shows the 2 px `PrimaryBrush` outline on Tab or arrow keys only. A mouse click must not show it.

### 4.3 Batch badge

Show the count on the Batch item in both states. The badge follows the Batch item's checked state, and `SetNavCollapsed` controls whether it sits beside the label or on the icon's top-right corner.

```xml
<Border x:Name="BatchBadge" MinWidth="18" Height="18" CornerRadius="9" Padding="6,0"
        Background="{DynamicResource SecondaryBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1">
  <TextBlock x:Name="BatchBadgeText" FontSize="11" HorizontalAlignment="Center" VerticalAlignment="Center"
             Foreground="{DynamicResource SecondaryTextBrush}"/>
</Border>
```

Code: when Batch is checked, set `BatchBadge.Background` to `PrimaryBrush`, `BatchBadge.BorderThickness` to 0, and `BatchBadgeText.Foreground` to white. When Batch is not checked, restore the grey style above. Hide the badge when the count is zero.

### 4.4 Status bar

```xml
<Border Height="28" Background="{DynamicResource SidebarBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0">
  <DockPanel Margin="12,0">
    <!-- right side: version and About -->
    <Button DockPanel.Dock="Right" x:Name="BtnAbout" Style="{StaticResource LinkButton}" Click="BtnAbout_Click">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xF059;" FontFamily="{StaticResource IconFont}" FontSize="11" Margin="0,0,6,0"/>
        <TextBlock x:Name="TxtVersion" FontSize="11"/>  <!-- "IDForge 1.2.3 · About", from the build version -->
      </StackPanel>
    </Button>
    <!-- left side: status -->
    <StackPanel Orientation="Horizontal" FontSize="11" Foreground="{DynamicResource SecondaryTextBrush}">
      <Ellipse Width="6" Height="6" Fill="{DynamicResource SuccessBrush}" Margin="0,0,6,0"/>
      <TextBlock Text="Offline — all data stays on this PC"/>
      <TextBlock Text="  |  " Foreground="{DynamicResource BorderBrush}"/>
      <TextBlock x:Name="TxtRecords" Text="1,284 cards on record"/>
      <TextBlock Text="  |  " Foreground="{DynamicResource BorderBrush}"/>
      <TextBlock x:Name="TxtLastBackup" Text="Last backup dd/MM/yyyy"/>
    </StackPanel>
  </DockPanel>
</Border>
```

- `TxtVersion` is set from the assembly's informational version at startup, so it updates with each build. Don't type the version number by hand.
- `BtnAbout_Click` opens the About dialog.

### 4.5 Help and About

- Status bar: the "About" link opens the About dialog.
- Help menu: Keyboard shortcuts, Quick guide, Troubleshooting, separator, About IDForge….
- F1 opens Help.

---

## 5. New card screen

### 5.1 Header

```xml
<Grid Margin="24,20,24,16">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>
  </Grid.ColumnDefinitions>
  <StackPanel>
    <TextBlock Text="New card" FontSize="20" FontWeight="SemiBold"/>
    <TextBlock Text="Fill in the employee's details, adjust the photo, then issue the card."
               FontSize="13" Foreground="{DynamicResource MutedBrush}" TextWrapping="Wrap" Margin="0,2,0,0"/>
  </StackPanel>
  <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Bottom">
    <StackPanel Orientation="Horizontal" Margin="0,0,12,0" VerticalAlignment="Center">
      <TextBlock Text="&#xF0C7;" FontFamily="{StaticResource IconFont}" FontSize="11"
                 Foreground="{DynamicResource MutedBrush}" Margin="0,0,6,0"/>
      <TextBlock x:Name="TxtDraftSaved" Text="Draft saved automatically 14:06" FontSize="12"
                 Foreground="{DynamicResource MutedBrush}"/>
    </StackPanel>
    <Button Content="Clear form" Style="{StaticResource SecondaryButton}" Margin="0,0,8,0"/>
    <Button Style="{StaticResource SecondaryButton}">
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="Save draft"/>
        <TextBlock Text="Ctrl+S" FontSize="11" Foreground="{DynamicResource MutedBrush}" Margin="6,0,0,0" VerticalAlignment="Center"/>
      </StackPanel>
    </Button>
  </StackPanel>
</Grid>
```

The subtitle wraps in its column and never runs under the buttons. The draft-saved note sits in the button group, as in the design.

### 5.2 Buttons

```xml
<Style x:Key="SecondaryButton" TargetType="Button">
  <Setter Property="Height" Value="32"/>
  <Setter Property="Padding" Value="12,0"/>
  <Setter Property="FontSize" Value="13"/>
  <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
  <Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Border x:Name="Bd" CornerRadius="4" BorderThickness="1"
                Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                Padding="{TemplateBinding Padding}">
          <ContentPresenter VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource SecondaryBrush}"/>
          </Trigger>
          <Trigger Property="IsEnabled" Value="False">
            <Setter Property="Opacity" Value="0.5"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

`PrimaryButton` uses the same template, with `Height="40"`, `Padding="20,0"`, `FontSize="14"`, `FontWeight="SemiBold"`, `Background="{DynamicResource PrimaryBrush}"`, `Foreground="White"`, and `PrimaryHoverBrush` on hover.

### 5.3 Form

**Step heading**

```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,16">
  <Border Width="20" Height="20" CornerRadius="10" Background="{DynamicResource PrimaryBrush}">
    <TextBlock Text="1" Foreground="White" FontSize="11" FontWeight="SemiBold"
               HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <TextBlock Text="Employee details" FontSize="14" FontWeight="SemiBold" Margin="8,0,0,0" VerticalAlignment="Center"/>
  <TextBlock Text="&#xF058;" FontFamily="{StaticResource IconFont}" Foreground="{DynamicResource SuccessBrush}"
             Margin="8,0,0,0" VerticalAlignment="Center"/>
</StackPanel>
```

The photo panel's heading is "2 · Photo", with the same pattern. There is no step 3.

**Field label and input**

```xml
<TextBlock FontSize="12" FontWeight="Medium" Margin="0,0,0,6">
  <Run Text="Full name"/><Run Text=" *" Foreground="{DynamicResource ErrorBrush}"/>
</TextBlock>
<TextBox Style="{StaticResource FieldInput}" Text="Amara Okafor"/>
```

```xml
<Style x:Key="FieldInput" TargetType="TextBox">
  <Setter Property="Height" Value="36"/>
  <Setter Property="Padding" Value="12,0"/>
  <Setter Property="FontSize" Value="13"/>
  <Setter Property="VerticalContentAlignment" Value="Center"/>
  <Setter Property="BorderBrush" Value="{DynamicResource InputBrush}"/>
  <Setter Property="BorderThickness" Value="1"/>
  <Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/>
  <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="TextBox">
        <Grid>
          <!-- focus ring: 2 px outer halo at 20 % opacity -->
          <Border x:Name="Halo" CornerRadius="6" BorderThickness="2"
                  BorderBrush="{DynamicResource PrimaryBrush}" Opacity="0.2" Visibility="Collapsed"/>
          <Border x:Name="Bd" CornerRadius="4" BorderThickness="{TemplateBinding BorderThickness}"
                  BorderBrush="{TemplateBinding BorderBrush}" Background="{TemplateBinding Background}">
            <ScrollViewer x:Name="PART_ContentHost" Padding="{TemplateBinding Padding}"
                          VerticalContentAlignment="Center"/>
          </Border>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsKeyboardFocused" Value="True">
            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource PrimaryBrush}"/>
            <Setter TargetName="Halo" Property="Visibility" Value="Visible"/>
          </Trigger>
          <Trigger Property="IsEnabled" Value="False">
            <Setter Property="Opacity" Value="0.5"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
```

Spacing: 16 px between fields.

**Employee ID (UID) field**

Label row: "Employee ID *" on the left, and "8 / 15" in 11 px muted text on the right.

```xml
<Grid Height="36">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="48"/>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="32"/>
  </Grid.ColumnDefinitions>
  <Border Grid.Column="0" Background="{DynamicResource SecondaryBrush}" CornerRadius="4,0,0,4"
          BorderBrush="{DynamicResource InputBrush}" BorderThickness="1,1,0,1">
    <TextBlock Text="UID" FontSize="12" FontWeight="Medium" HorizontalAlignment="Center" VerticalAlignment="Center"
               Foreground="{DynamicResource SecondaryTextBrush}"/>
  </Border>
  <TextBox Grid.Column="1" Style="{StaticResource FieldInput}" FontFamily="JetBrains Mono"
           BorderThickness="1,1,1,1" CornerRadius="0,4,4,0"/>
  <TextBlock Grid.Column="2" Text="&#xF058;" FontFamily="{StaticResource IconFont}"
             Foreground="{DynamicResource SuccessBrush}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Grid>
```

Helper lines under the field:
- "Numbers only, 4–15 digits. Don't type "UID" — it's added to the card for you." (11 px, muted)
- "New employee — no existing card with this ID." (11 px, success, with a check icon `&#xF00C;`)

**Blood group and Print date (side by side)**

```xml
<Grid>
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="12"/>
    <ColumnDefinition Width="*"/>
  </Grid.ColumnDefinitions>
  <StackPanel Grid.Column="0"><!-- Blood group label + dropdown (36 px high) --></StackPanel>
  <StackPanel Grid.Column="2"><!-- Print date label + date box with calendar button --></StackPanel>
</Grid>
```

The Print date box is a 36 px input with a 36 px calendar button on the right (`&#xF133;`, bordered on its left edge). Dates display as dd/MM/yyyy.

**Contract type (segmented control)**

```xml
<Border Background="{DynamicResource PanelBrush}" BorderBrush="{DynamicResource BorderBrush}"
        BorderThickness="1" CornerRadius="4" Padding="4">
  <UniformGrid Columns="2">
    <RadioButton GroupName="Contract" IsChecked="True" Style="{StaticResource SegOption}">
      <StackPanel>
        <TextBlock Text="Permanent" FontSize="13" FontWeight="Medium"/>
        <TextBlock Text="Valid 12 months" FontSize="11" Foreground="{DynamicResource MutedBrush}"/>
      </StackPanel>
    </RadioButton>
    <RadioButton GroupName="Contract" Style="{StaticResource SegOption}">
      <StackPanel>
        <TextBlock Text="Contractual" FontSize="13" FontWeight="Medium"/>
        <TextBlock Text="Valid 6 months" FontSize="11" Foreground="{DynamicResource MutedBrush}"/>
      </StackPanel>
    </RadioButton>
  </UniformGrid>
</Border>
```

`SegOption`: height 48, padding 12,0, left-aligned content. When checked, the option gets `BackgroundBrush` fill, a `BorderBrush` border, a 4 px corner radius, and a soft shadow (Effect `DropShadowEffect` Opacity 0.1, BlurRadius 2, ShadowDepth 1). When unchecked, the background is transparent.

**Validity box**

```xml
<Border Background="{DynamicResource PanelLightBrush}" BorderBrush="{DynamicResource BorderBrush}"
        BorderThickness="1" CornerRadius="4" Padding="12,10">
  <StackPanel Orientation="Horizontal">
    <TextBlock Text="&#xF274;" FontFamily="{StaticResource IconFont}" FontSize="14"
               Foreground="{DynamicResource PrimaryBrush}" Margin="0,0,10,0" VerticalAlignment="Center"/>
    <StackPanel>
      <TextBlock Text="Card will be valid until" FontSize="12" Foreground="{DynamicResource MutedBrush}"/>
      <TextBlock FontSize="13" FontWeight="SemiBold">
        <Run x:Name="RunValidUntil" Text="24/11/2027"/>
        <Run Text=" · 12 months from print date" FontWeight="Normal" Foreground="{DynamicResource MutedBrush}"/>
      </TextBlock>
    </StackPanel>
  </StackPanel>
</Border>
```

Set `RunValidUntil` and the "12 months" / "6 months" text from the contract type and print date, as now.

### 5.4 Preview

**Header**

```xml
<Border Height="44" Padding="20,0" Background="{DynamicResource BackgroundBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
  <DockPanel>
    <StackPanel DockPanel.Dock="Left" Orientation="Horizontal">
      <TextBlock Text="Live preview" FontSize="13" FontWeight="SemiBold"/>
      <Border Margin="8,0,0,0" Padding="8,2" CornerRadius="10" Background="{DynamicResource AccentBrush}">
        <TextBlock Text="Updates as you type" FontSize="11" Foreground="{DynamicResource AccentTextBrush}"/>
      </Border>
    </StackPanel>
    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" HorizontalAlignment="Right">
      <CheckBox Content="Show print edges" Style="{StaticResource DesignCheckBox}" Margin="0,0,16,0"/>
      <Button Style="{StaticResource ZoomGroupButton}" Content="&#xF068;" FontFamily="{StaticResource IconFont}"/>
      <Button Style="{StaticResource ZoomGroupButton}" Content="Fit" Width="48" Margin="-1,0,0,0"/>
      <Button Style="{StaticResource ZoomGroupButton}" Content="&#xF067;" FontFamily="{StaticResource IconFont}" Margin="-1,0,0,0"/>
    </StackPanel>
  </DockPanel>
</Border>
```

```xml
<Style x:Key="ZoomGroupButton" TargetType="Button">
  <Setter Property="Width" Value="28"/>
  <Setter Property="Height" Value="28"/>
  <Setter Property="FontSize" Value="11"/>
  <Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/>
  <Setter Property="BorderThickness" Value="1"/>
  <!-- template: same as SecondaryButton, CornerRadius 4 -->
</Style>
```

- The "Fit" button resets the preview zoom to fit the window. The minus and plus buttons step the zoom.
- The "Show print edges" checkbox draws a dashed outline at the card's trim line. It is preview-only. The saved PNG and PDF never change when this is on.

**Canvas and card**

- The card is centred in the preview pane, on a `PanelBrush` background.
- The card has a 2 px `PrimaryBrush` ring at 30 % opacity, offset 4 px from the card edge, and a drop shadow (BlurRadius 24, ShadowDepth 6, Opacity 0.25).
- Card render: unchanged.

**Footer texts under the card**

```xml
<StackPanel HorizontalAlignment="Center" Margin="0,16,0,0">
  <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
    <TextBlock Text="&#xF545;" FontFamily="{StaticResource IconFont}" FontSize="12" Margin="0,0,6,0"/>
    <TextBlock Text="Printed size 54 × 86 mm" FontSize="12" Foreground="{DynamicResource MutedBrush}"/>
    <Ellipse Width="4" Height="4" Fill="{DynamicResource BorderBrush}" Margin="12,0"/>
    <TextBlock Text="&#xF03E;" FontFamily="{StaticResource IconFont}" FontSize="12" Margin="0,0,6,0"/>
    <TextBlock x:Name="TxtPrintFormat" Text="Print-quality image" FontSize="12" Foreground="{DynamicResource MutedBrush}"/>
  </StackPanel>
  <TextBlock Text="Click the photo and use the arrow keys to move it." FontSize="11"
             Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Center" Margin="0,4,0,0"/>
</StackPanel>
```

`TxtPrintFormat` reads "Print-quality image" by default, and "Print-quality image + PDF" only when PDF saving is on in Settings.

### 5.5 Photo panel

**Photo file card**

```xml
<Border BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="10">
  <DockPanel>
    <Button DockPanel.Dock="Right" Style="{StaticResource SecondaryButton}" Content="Change…" Height="28"/>
    <Border Width="40" Height="48" CornerRadius="4" ClipToBounds="True" Margin="0,0,12,0">
      <Image x:Name="ImgThumb" Stretch="UniformToFill"/>
    </Border>
    <StackPanel VerticalAlignment="Center">
      <TextBlock x:Name="TxtFileName" FontSize="12" FontWeight="Medium" TextTrimming="CharacterEllipsis"/>
      <TextBlock x:Name="TxtFileMeta" FontSize="11" Foreground="{DynamicResource MutedBrush}"/>  <!-- "1.2 MB · added 14:02" -->
    </StackPanel>
  </DockPanel>
</Border>
<TextBlock Text="Ctrl+O to choose a photo, or drag one onto the preview." FontSize="11"
           Foreground="{DynamicResource MutedBrush}" Margin="0,6,0,0"/>
```

**Photo background (three radio buttons)**

```xml
<StackPanel Margin="0,16,0,0">
  <TextBlock Text="Photo background" FontSize="12" FontWeight="Medium" Margin="0,0,0,6"/>
  <RadioButton GroupName="PhotoBg" IsChecked="True" Style="{StaticResource DesignRadio}"
               Content="Replace with plain white (recommended)"/>
  <RadioButton GroupName="PhotoBg" Style="{StaticResource DesignRadio}"
               Content="Replace with model (complex background)"/>
  <RadioButton GroupName="PhotoBg" Style="{StaticResource DesignRadio}"
               Content="Keep the original background"/>
</StackPanel>
```

`DesignRadio`: 12 px text in `TextBrush`, 6 px bottom margin. The circle is 14 px, with a 1 px `BorderBrush` border. When checked, the border becomes `PrimaryBrush` and a 6 px `PrimaryBrush` dot appears inside.

Changing the option re-runs the preview. The three options map to the existing background modes, and the choice is saved with the record.

**Position and size**

```xml
<DockPanel Margin="0,20,0,12">
  <Button DockPanel.Dock="Right" Style="{StaticResource TextButton}" Height="28" Padding="10,0">
    <StackPanel Orientation="Horizontal">
      <TextBlock Text="&#xF2EA;" FontFamily="{StaticResource IconFont}" FontSize="11" Margin="0,0,6,0"/>
      <TextBlock Text="Reset"/>
      <TextBlock Text="Ctrl+0" FontSize="11" Foreground="{DynamicResource MutedBrush}" Margin="6,0,0,0"/>
    </StackPanel>
  </Button>
  <TextBlock Text="Position and size" FontSize="12" FontWeight="Medium" VerticalAlignment="Center"/>
</DockPanel>
```

`TextButton`: no border, `PrimaryBrush` text, `AccentBrush` on hover.

**Nudge pad**

```xml
<Grid Width="96" Height="96">
  <Grid.RowDefinitions><RowDefinition/><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
  <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
  <Button Grid.Row="0" Grid.Column="1" Style="{StaticResource NudgeButton}" Content="&#xF077;" FontFamily="{StaticResource IconFont}"/>
  <Button Grid.Row="1" Grid.Column="0" Style="{StaticResource NudgeButton}" Content="&#xF053;" FontFamily="{StaticResource IconFont}"/>
  <Button Grid.Row="1" Grid.Column="1" Style="{StaticResource NudgeCentreButton}" Content="&#xE4BA;" FontFamily="{StaticResource IconFont}" ToolTip="Centre photo"/>
  <Button Grid.Row="1" Grid.Column="2" Style="{StaticResource NudgeButton}" Content="&#xF054;" FontFamily="{StaticResource IconFont}"/>
  <Button Grid.Row="2" Grid.Column="1" Style="{StaticResource NudgeButton}" Content="&#xF078;" FontFamily="{StaticResource IconFont}"/>
</Grid>
```

- `NudgeButton`: fills its cell, 1 px `BorderBrush` border, 4 px corner radius, 11 px text. Hover: `SecondaryBrush`.
- `NudgeCentreButton`: `SecondaryBrush` fill, muted icon colour. Click resets the photo position to centre.
- Each arrow moves the photo 1 px. Shift + arrow moves 10 px.

**Left/right and up/down inputs**

Each input is a 32 px high row: a 12 px JetBrains Mono text box, a "px" suffix in muted text, and a 16 px spinner column on the right with caret-up and caret-down buttons.

**Zoom (photo)**

```xml
<Grid Margin="0,16,0,0">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="Auto"/>  <!-- minus icon -->
    <ColumnDefinition Width="*"/>     <!-- slider -->
    <ColumnDefinition Width="Auto"/>  <!-- plus icon -->
    <ColumnDefinition Width="72"/>    <!-- percentage box -->
  </Grid.ColumnDefinitions>
  <TextBlock Grid.Column="0" Text="&#xF010;" FontFamily="{StaticResource IconFont}" FontSize="11"
             Foreground="{DynamicResource MutedBrush}" VerticalAlignment="Center" Margin="0,0,8,0"/>
  <!-- slider: 4 px track in SecondaryBrush with a 1 px border, filled PrimaryBrush up to the value,
       thumb 16 px circle, BackgroundBrush fill, 2 px PrimaryBrush border -->
  <TextBlock Grid.Column="2" Text="&#xF00E;" FontFamily="{StaticResource IconFont}" FontSize="11"
             Foreground="{DynamicResource MutedBrush}" VerticalAlignment="Center" Margin="8,0,0,0"/>
  <Border Grid.Column="3" Height="32" Margin="12,0,0,0" BorderBrush="{DynamicResource InputBrush}"
          BorderThickness="1" CornerRadius="4">
    <DockPanel>
      <TextBlock DockPanel.Dock="Right" Text="%" Margin="0,0,8,0" VerticalAlignment="Center"
                 Foreground="{DynamicResource MutedBrush}" FontSize="12"/>
      <TextBox Text="112" FontFamily="JetBrains Mono" FontSize="12" BorderThickness="0" Padding="8,0"
               VerticalContentAlignment="Center" Background="Transparent"/>
    </DockPanel>
  </Border>
</Grid>
```

Labels under the slider: "40 %" at the left end, "300 %" at the right end. Keep the 100 % tick, and hide its label when the panel is too narrow to show it (as now). Reset (Ctrl+0) restores zoom to 100 % and position to centre.

**Hint box**

```xml
<Border Margin="0,12,0,0" Padding="8" CornerRadius="4" Background="{DynamicResource PanelLightBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1">
  <TextBlock FontSize="11" Foreground="{DynamicResource MutedBrush}" TextWrapping="Wrap"
             Text="&#xF11C;  Arrow keys move 1 px · Shift + arrows move 10 px · Ctrl + mouse wheel zooms"/>
</Border>
```

There is no approval step. The photo panel ends after the hint box.

---

## 6. Footer (issue bar)

```xml
<Border DockPanel.Dock="Bottom" Padding="24,12" Background="{DynamicResource BackgroundBrush}"
        BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0">
  <Grid>
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
      <!-- chips: check icon + text, no border or pill -->
      <StackPanel Orientation="Horizontal" Margin="0,0,16,0">
        <TextBlock Text="&#xF058;" FontFamily="{StaticResource IconFont}" Foreground="{DynamicResource SuccessBrush}" Margin="0,0,6,0"/>
        <TextBlock Text="Details complete" FontSize="12"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Margin="0,0,16,0">
        <TextBlock Text="&#xF058;" FontFamily="{StaticResource IconFont}" Foreground="{DynamicResource SuccessBrush}" Margin="0,0,6,0"/>
        <TextBlock Text="Employee ID is new" FontSize="12"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xF058;" FontFamily="{StaticResource IconFont}" Foreground="{DynamicResource SuccessBrush}" Margin="0,0,6,0"/>
        <TextBlock Text="Photo ready" FontSize="12"/>
      </StackPanel>
    </StackPanel>
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
      <TextBlock FontSize="12" Foreground="{DynamicResource MutedBrush}" VerticalAlignment="Center">
        <Run x:Name="RunSaves" Text="Saves image + PDF · "/>  <!-- "Saves image · " when PDF is off -->
        <Hyperlink Click="BtnSettings_Click"><Run Text="change in Settings"/></Hyperlink>
      </TextBlock>
      <Button Style="{StaticResource PrimaryButton}" Margin="20,0,0,0" x:Name="BtnIssue" Click="BtnIssue_Click">
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="&#xF02F;" FontFamily="{StaticResource IconFont}" Margin="0,0,8,0"/>
          <TextBlock Text="Issue card"/>
          <TextBlock Text="Ctrl+Enter" FontSize="11" FontWeight="Normal" Opacity="0.8" Margin="8,0,0,0" VerticalAlignment="Center"/>
        </StackPanel>
      </Button>
    </StackPanel>
  </Grid>
</Border>
```

- The chips are plain text with icons, as in the design. Remove the pill borders.
- The chips must all share one vertical centre line with the text and button (within 1 px).
- The footer stays visible at every window size.
- The Issue card button is disabled until the details are valid and a photo is present. Its tooltip gives the reason.

---

## 7. Acceptance checks

**Rendered checks** (not structure-only tests):
- At each size in section 2, every control's actual size is at least its desired size, and nothing sits outside its container.
- At 56 px, all four nav icons and the backup icon are fully inside the rail, with no text drawn.
- Collapsed: the section headings are hidden and the divider is shown.
- Expanded: all labels, shortcuts, and the full backup card are visible.

**Behaviour checks:**
- The selected nav item has the light blue fill and the 3 px left bar, and no keyboard-focus ring after a mouse click.
- Tab or arrow keys show the focus ring on the focused nav item.
- Show print edges changes only the preview. The saved PNG's bytes are unchanged after toggling it.
- The three background radio buttons work. The default is plain white, and changing the option refreshes the preview.
- Preview − and + step the zoom. "Fit" fits the card to the pane.
- The photo % box accepts values from 40 to 300.
- The arrow keys move the photo 1 px, Shift + arrows move it 10 px, and Ctrl + wheel zooms.
- Status bar: the version matches the build. The "About" link opens About. F1 opens Help.
- Batch badge: grey when Batch is not selected, blue when it is.
- Issue card is enabled only when the details are valid and a photo is present. There is no approval step.

**Screenshots** (save to `docs/redesign/shots/`, commit, and list the paths in the report):
- New card at 860 × 620, 1100 × 700, 1280 × 800, and 1920 × 1080.
- Collapsed nav and expanded nav.
- The footer.

**Report:** each part's commit hash, the test count, whether `tools\review.ps1` launched the app, the screenshot paths, any resource names that differed from this plan and how you mapped them, and anything not done with the reason.

---

## 8. Divergences to keep

| ID | Item | Status |
|---|---|---|
| V1 | Position is a dropdown | Kept |
| V7 | Three background modes | Now shown as three radio buttons (this plan) |
| V8 | Print edges | Now available as a preview-only checkbox (this plan) |
| V6 | Version | Shown from the build, not a fixed "2.0" |
| — | Card render | Our own card, not the design's placeholder |
| — | Batch badge colour | Provisional, to check against the Batch design |
| — | Batch, Review, Records, and Settings visuals | Not in this plan; Batch design to follow |

---

## 9. Open items

- Footer text "Saves image + PDF": this plan shows "+ PDF" only when PDF saving is on in Settings. Confirm this, or always show it.
- Font Awesome licence file: include it in `assets\fonts\` with the same care as the Inter and JetBrains Mono licence files.
- Batch screen visuals and the Batch badge colour will be updated from the Batch design when it's shared.
