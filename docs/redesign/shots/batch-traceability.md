# Batch visual parity — traceability

Spec: `docs/redesign/BATCH_VISUAL_PARITY_PLAN.md` + prompt overrides (prompt wins).
One row per plan section/decision. "Shot" = committed PNG under `docs/redesign/shots/`.
Status: ✅ done · 🚧 in progress · ⬜ not done.

## Decisions (plan §1)

| # | Requirement | Implementation | Test | Shot | Status |
|---|---|---|---|---|---|
| D1 | Cards issued one at a time from Review; no grid issue actions (superseded by override 5 header Issue batch) | `ReviewWindow.IssueThis_Click`/`ApproveNext_Click`; `BtnIssueBatch` header only (no issue control in grid/footer) | `Batch_Issue_Only_From_Header_And_Review` | batch-locked/unlocked | ✅ |
| D2 | Saved named batches; Batches dropdown lists them; "+ New batch" asks for a name; replaces V14 | `BtnBatches_Click` menu, `BtnNewBatch_Click`, `BtnRenameBatch_Click`, `CardStore` session APIs, `OpenBatchSession` | `Batch_Saved_Batches_Rename_New_And_Switch`, `Batch_Saved_Indicator_Updates_On_Persist` | batch-header | ✅ |
| D3 | Photo column removed; thumbnail in Employee cell; placeholder opens Browse | `DgBatch` Employee column (`ImgRowThumb`→`Thumb`, red/grey placeholders → `BatchPlaceholder_Click`) | `Batch_Photo_Placeholder_And_Thumbnail`, `Batch_Grid_Columns_Order_Matches_Plan` | batch-grid | ✅ |
| D4 | Blood group kept as column after Employee ID | `DgBatch` column order (Select, #, Employee, Employee ID, Blood group, …) | `Batch_Grid_Columns_Order_Matches_Plan`, `Blood_Group_Column_Sits_After_Employee_Id` | batch-grid | ✅ |
| D5 | Chips: All, Needs attention, Ready to review, Issued, Re-issues; All dark when selected (override 6) | `BatchFilterChip` style, `ChkFilter*` radios, `UpdateBatchChips` | `Batch_Chips_Counts_Match_Grid` | batch-toolbar, batch-filter-attention | ✅ |
| D6 | Grid scrolls, no paging; "Showing N of M" only while filter/search active | `TxtBatchShowing`, `BatchRowFilter`, `TxtBatchSearch`, `UpdateBatchFooter` | `Batch_Chips_Counts_Match_Grid`, `Batch_Search_Filters_Grid_And_CtrlF`, `Batch_Footer_StatusBar_And_Responsive` | batch-footer | ✅ |
| D7 | Paste/CSV order: Name, Position, Employee ID, Blood, Contract, Print date; CSV photo column after | `BatchValidator.Parse`, `BatchExportCsv_Click`, `tests/fixtures/batch_sample.csv` | `Batch_Paste_Order_Name_Position_Uid_Blood_Contract_Date_Photo`, `Parse_Tsv_And_Csv_With_And_Without_Header` | — (behaviour) | ✅ |
| D8 | Batch badge blue when Batch active, grey w/ count otherwise | `UpdateBatchBadge` (existing) | `Nav_Batch_Badge_Blue_When_Active` | batch-header | ✅ exists |
| D9 | Search "Find name or Employee ID (Ctrl+F)" at right of toolbar | `TxtBatchSearch`, Ctrl+F in `TryHandleShortcut` | `Batch_Search_Filters_Grid_And_CtrlF` | batch-toolbar | ✅ |
| D10 | Status bar "Preparing photos: N of M" + spinner while processing; cancel beside | `PnlStatusPhotos`, `TxtStatusPhotos`, `BtnCancelPhotos`, `TxtStatusSpinner` | `Batch_Footer_StatusBar_And_Responsive` | batch-header (state) | ✅ |
| D11 | Window title "IDForge — Batch: <name>" | `UpdateWindowTitle` on `ShowArea`/rename | `Batch_Window_Title_Shows_Batch_Name` | — | ✅ |
| D12 | Re-issue message "Replaces card issued dd/MM/yyyy. New validity until dd/MM/yyyy." | `RefreshBatchDerived` (`ReplacesText`) | `Batch_Reissue_Message_Shows_Previous_And_New_Dates` | batch-grid | ✅ |

## Layout (§2)

| Section | Requirement | Implementation | Test | Shot | Status |
|---|---|---|---|---|---|
| §2.1 | Header: title+rename+pencil, employee-count badge, subtitle, Saved HH:mm, Batches, New batch; 24px margins | `PageBatch` row 0 (`TxtBatchName`, `BtnRenameBatch`, `PnlEmployeeCount`, `TxtBatchCounts`, `TxtSaved`, `BtnBatches`, `BtnNewBatch`) | `BatchHeader_Elements_Inside_Window_At_All_Sizes`, `Batch_Progress_Card_Counts_And_Bar` | batch-header-1280x800, batch-header-860x620 | ✅ |
| §2.2 | Progress card: issued count, remaining, 4-segment bar, legend (Issued/Ready/Attention/Processing/Draft), Review cards btn + hint; empty = "No rows yet" | `PnlBatchProgress`, `TxtIssuedCount`, `TxtRemaining`, `PnlBatchLegend`, `BtnReview`, `TxtReviewReason` | `Batch_Progress_Card_Counts_And_Bar` | batch-header | ✅ |
| §2.3 | Toolbar: Add row/Duplicate/Paste/Import/Export/Prepare photos/Remove N selected; shortcuts; danger style; 256px search right | `PnlBatchToolbar`, `BtnAddRow`…`BtnRemoveSelected`, `TxtBatchSearch`, `BtnBatchSearchHint` | `Batch_Toolbar_And_Chips_Inside_Window_At_All_Sizes`, `Batch_Toolbar_Shortcuts_Add_Dup_Paste` | batch-toolbar-1280x800 | ✅ |
| §2.4 | Filter chips w/ counts, red/amber variants, single-check | `PnlBatchChips`, `ChkFilter*` (`BatchFilterChip` style), `UpdateBatchChips`/`UpdateBatchFilter` | `Batch_Chips_Counts_Match_Grid`, `Batch_Toolbar_And_Filter_Screenshots` | batch-filter-attention | ✅ |
| §2.5 | Grid in 1px border, 6px radius, own scroll, MinWidth 1080; header/row styles; selected row accent+40% border | `BorderDgBatch` wrapper, `DgBatch` (`BatchHeader`/`BatchRow` styles, MinWidth 1080, H-scroll Auto) | `Batch_Grid_Columns_Order_Matches_Plan`, `Collapsed_Rail_And_Batch_Rows_Have_No_Rendered_Clipping` | batch-grid-1280x800, batch-grid-1100x700 | ✅ |
| §2.6 | Columns: Select/#/Employee(star,min220)/EmployeeID 130/Blood 90/Position 200/Contract 120/Print date 110/Card type pill 110/Status pill 130/Message(star,min200); red ID border when invalid | `DgBatch` columns, `GridInput` style, `UidInvalidBrush`, placeholders | `Batch_Grid_Columns_Order_Matches_Plan`, `Batch_Uid_Invalid_Red_Border_And_Message`, `Batch_Photo_Placeholder_And_Thumbnail` | batch-grid | ✅ |
| §2.7 | Status pills per state (icon+label+colours); card-type pills; processing spinner | inline pill templates, `StatusPill*`/`CardTypePill*` props, `_spinnerTimer`/`SpinBatchIcons` | `Batch_Status_Pills_Per_State` | batch-grid | ✅ |
| §2.8 | Message column: secondary text, red errors w/ links, re-issue wording, "Duplicate of row N", "Missing: …", empty rows blank | `TxtRowMessage`→Message cell (`MessageBrush`, `ShowAddPhotoLink`→`BatchAddPhotoLink_Click`), `RefreshBatchDerived` | `Batch_Reissue_Message_Shows_Previous_And_New_Dates`, `Batch_Uid_Invalid_Red_Border_And_Message` | batch-grid | ✅ |
| §2.9 | Footer "Showing N of M · K selected" / tip right; status bar photo progress; "Open output" into Batches menu | footer Grid row 5 (`TxtBatchShowing` + paste-order tip); `PnlStatusPhotos`/`TxtStatusPhotos`/`BtnCancelPhotos`; "Open output folder" item in `BtnBatches_Click` menu | `Batch_Footer_StatusBar_And_Responsive`, `Batch_Toolbar_And_Filter_Screenshots` | batch-footer-860x620 | ✅ |

## Responsive (§3)

| Rule | Implementation | Test | Shot | Status |
|---|---|---|---|---|
| Min 860×620; grid only sideways scroller | `MinWidth=1080` inside border | bounds tests @860 | batch-header-860x620, batch-footer-860x620 | ✅ |
| Toolbar wraps before clipping; search drops below | `WrapPanel` toolbar (`PnlBatchButtons`, `PnlBatchSearch` Grid row) | `Batch_Toolbar_And_Chips_Inside_Window_At_All_Sizes` | batch-toolbar | ✅ |
| Progress card stacks Review under counts <1100px | `UpdateBatchProgressLayout` size handler | `Batch_Footer_StatusBar_And_Responsive` | batch-header-860x620, batch-footer-860x620 | ✅ |
| Status bar version+About at every size | existing shell | existing tests | all shots | ✅ |

## Overrides (prompt)

| # | Requirement | Implementation | Test | Status |
|---|---|---|---|---|
| O1 | No menu bar | already removed | existing | ✅ |
| O2 | No Save draft/Ctrl+S | autosave | existing | ✅ |
| O3 | About in nav rail | existing | existing | ✅ |
| O4 | Clear all only in Settings | existing | existing | ✅ |
| O5 | Approve & next / Issue this card only in Review; Issue batch (N) Ctrl+Shift+I gated, hint "Approve or skip N more to unlock", one transaction+confirm | `BtnIssueBatch`/`TxtIssueBatchN`/`TxtIssueBatchHint`, `UpdateIssueBatchButton`, `IssueApprovedRowsAsync`, `IssueBatchTargets`/`PendingCount`, Review `BtnApproveNext`/`BtnIssueThis`, Ctrl+Shift+I | `Batch_Issue_Batch_Gate_And_Transaction`, `Batch_Issue_Only_From_Header_And_Review`, `Bottom_Bar_Has_Spec_Buttons` | ✅ |
| O6 | Chips per D5 (not Review dropdown) | `ChkFilter*` chips | `Batch_Chips_Counts_Match_Grid` | ✅ |
| O7 | Version number only | `AppInfo.Version` | existing | ✅ |
| O8 | Review cards (Ctrl+R) opens ReviewWindow if exists; exists → enabled; disabled+tooltip otherwise | `BtnReview`/`TxtReviewReason` + `TryHandleShortcut` Ctrl+R gated on `BtnReview.IsEnabled` (ReviewWindow exists) | `Batch_Toolbar_Shortcuts_Add_Dup_Paste` (Ctrl+R path), `Counts_Line_And_Review_Gating` | ✅ |

## Behaviour list (prompt)

| Requirement | Implementation | Test | Status |
|---|---|---|---|
| Saved batches dropdown/new/rename/Saved HH:mm/auto-save | `BtnBatches_Click` menu, `BtnNewBatch_Click`, `BtnRenameBatch_Click`, `TxtSaved`, `MarkBatchSaved` | `Batch_Saved_Batches_Rename_New_And_Switch`, `Batch_Saved_Indicator_Updates_On_Persist` | ✅ |
| Paste order | `BatchValidator.Parse`, export CSV, sample fixture | `Batch_Paste_Order_Name_Position_Uid_Blood_Contract_Date_Photo` | ✅ |
| Row states + pills; empty rows not counted | `BatchRow.Status`, pill props, `BatchRowHasContent`, `PhotoPlaceholderKind` | `Batch_Status_Pills_Per_State`, `Batch_Photo_Placeholder_And_Thumbnail` | ✅ |
| Batch issue header-only, gated | `BtnIssueBatch`, `IssueApprovedRowsAsync`, `UpdateIssueBatchButton` | `Batch_Issue_Batch_Gate_And_Transaction`, `Batch_Issue_Only_From_Header_And_Review` | ✅ |
| Re-issue message prev+new dates | `RefreshBatchDerived` (`ReplacesText`) | `Batch_Reissue_Message_Shows_Previous_And_New_Dates` | ✅ |
| Double-click selects only | `DgBatch` (no open handler; `FixHPartBTests` guard) | `FixHPartBTests` | ✅ existing |
| Ctrl+F search | `TryHandleShortcut` Ctrl+F → `TxtBatchSearch.Focus()` | `Batch_Search_Filters_Grid_And_CtrlF` | ✅ |
| Batch badge blue when active | `UpdateBatchBadge` | `Nav_Batch_Badge_*` | ✅ |
| Window title | `UpdateWindowTitle`/`UpdateTitle` on `ShowArea`/rename | `Batch_Window_Title_Shows_Batch_Name` | ✅ |
| Status bar photo progress | `PnlStatusPhotos`, `TxtStatusPhotos`, `BtnCancelPhotos`, `TxtStatusSpinner` | `Batch_Footer_StatusBar_And_Responsive` | ✅ |
| "Showing N of M" only when filtered | `UpdateBatchFooter` (`TxtBatchShowing`, selected count) | `Batch_Chips_Counts_Match_Grid` | ✅ (filter part; selected-count text Part D) |
| Grid scrolls not pages | DataGrid + `ScrollViewer.HorizontalScrollBarVisibility=Auto`, MinWidth 1080 | `Batch_Grid_Columns_Order_Matches_Plan` | ✅ |

## Screenshots required

| Shot | Shows | Status |
|---|---|---|
| batch-header-1280x800.png | header+progress card | ✅ |
| batch-header-860x620.png | responsive header | ✅ |
| batch-toolbar-1280x800.png | toolbar+chips+search | ✅ |
| batch-grid-1280x800.png | grid/pills/thumbs/messages | ✅ |
| batch-grid-1100x700.png | grid at mid width | ✅ |
| batch-footer-860x620.png | footer/status bar | ✅ |
| batch-locked-1280x800.png | Issue batch locked + hint | ✅ |
| batch-unlocked-1280x800.png | Issue batch enabled | ✅ |
| batch-filter-attention-1280x800.png | Needs attention chip + Showing N of M | ✅ |
