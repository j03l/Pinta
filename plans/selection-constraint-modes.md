# Selection Constraint Modes (Fixed Ratio / Fixed Size)

Feature request inspired by paint.net's rectangle selection modes.

## Overview

Add three selection constraint modes to the Rectangle and Ellipse selection tools:
1. **Any Size** (current default) — unconstrained
2. **Fixed Ratio** — width:height locked to a specified ratio
3. **Fixed Size** — width and height locked to specified pixel dimensions

## Architecture

### How selection drag works today

The drag lifecycle flows through:
1. `SelectTool.OnMouseDown()` (line 67) — calls `handle.BeginDrag()`
2. `SelectTool.OnMouseMove()` (line 92) — calls `handle.UpdateDrag(canvasPos, shiftPressed)`
3. `SelectTool.OnMouseUp()` (line 106) — calls `handle.EndDrag()`

The core geometry lives in `RectangleHandle.MoveActiveHandle()` (line 219), which updates the rectangle for 8 different handle points (corners + edges). The existing Shift-to-square constraint is applied here using `IsHigherThanWide()`, `ExpandUniformlyX()`, and `ExpandUniformlyY()` (lines 196-217).

### How tool toolbars are built

Tools override `OnBuildToolBar(Gtk.Box tb)` from `BaseTool` (line 204). Selection tools delegate to `SelectionModeHandler.BuildToolbar()` (line 54) for the combine mode dropdown (Union/Xor/Exclude/Replace/Intersect). The `LassoSelectTool` shows how to append additional controls after the shared toolbar.

Available UI widgets: `ToolBarDropDownButton`, `ToolBarComboBox`, `Gtk.Label`, `Gtk.SpinButton`.

Settings are persisted via `ISettingsService` with keys defined in `SettingNames.cs`.

## Files to Modify

| File | Change |
|------|--------|
| `Pinta.Tools/Handles/RectangleHandle.cs` | Extend `MoveActiveHandle()` with ratio/size constraint logic |
| `Pinta.Tools/Tools/SelectTool.cs` | Pass constraint mode to `UpdateDrag()`, add toolbar controls |
| `Pinta.Core/Classes/SelectionModeHandler.cs` | Add constraint mode dropdown + width/height fields to `BuildToolbar()` |
| `Pinta.Core/SettingNames.cs` | Add setting keys for constraint mode, ratio, and size values |

`RectangleSelectTool.cs` and `EllipseSelectTool.cs` are thin wrappers that just call `DrawShape()` — no changes needed there.

## Implementation Steps

### 1. Add constraint mode enum and settings

Add to `SelectionModeHandler` or a new file in `Pinta.Core`:

```csharp
public enum SelectionConstraintMode
{
    None,       // Any Size
    FixedRatio, // Lock width:height ratio
    FixedSize,  // Lock exact dimensions
}
```

Add setting keys in `SettingNames.cs`:
- `SELECTION_CONSTRAINT_MODE`
- `SELECTION_CONSTRAINT_WIDTH`
- `SELECTION_CONSTRAINT_HEIGHT`

### 2. Extend the toolbar

In `SelectionModeHandler.BuildToolbar()`, after the existing combine mode dropdown, add:
- Separator
- Constraint mode dropdown (Any Size / Fixed Ratio / Fixed Size)
- Width spin button (hidden in Any Size mode)
- Height spin button (hidden in Any Size mode)

Follow the `LassoSelectTool` pattern for appending extra controls.

### 3. Modify RectangleHandle constraint logic

`UpdateDrag()` currently passes `shiftPressed` to `MoveActiveHandle()`. Extend this to accept constraint mode and parameters:

```csharp
public RectangleI UpdateDrag(PointD canvasPos, bool shiftPressed,
    SelectionConstraintMode constraint, double constraintW, double constraintH)
```

In `MoveActiveHandle()`:
- **Fixed Ratio**: Replace the square constraint logic — instead of matching both dimensions, calculate one from the other using the ratio. The existing `IsHigherThanWide()` / `ExpandUniformly` pattern is the template.
- **Fixed Size**: Set the rectangle to exactly `constraintW x constraintH` from the drag start point. Clamp to canvas bounds.
- **Shift key**: When constraint is `None`, Shift still constrains to square (current behavior). When constraint is `FixedRatio` or `FixedSize`, Shift is ignored.

### 4. Wire SelectTool to pass constraint info

In `SelectTool.OnMouseMove()` (line 99), currently:
```csharp
handle.UpdateDrag(e.PointDouble, e.IsShiftPressed);
```

Change to read constraint mode from `SelectionModeHandler` and pass it through.

## Design Decisions

- **Shift key in Fixed Ratio mode**: Ignore Shift — the ratio is already constrained. This matches paint.net behavior.
- **Edge handle behavior**: In Fixed Ratio mode, edge handles should still constrain to ratio. In Fixed Size mode, edge handles could be disabled (only move allowed).
- **Canvas clamping**: In Fixed Ratio/Size modes, clamp selection to canvas edges while preserving the constraint (paint.net behavior).
- **Lasso/Magic Wand**: These tools don't use rectangle handles, so constraint modes don't apply. Only show the dropdown for Rectangle and Ellipse select tools.

## References

- paint.net selection modes: https://www.getpaint.net/doc/latest/RectangleSelectTool.html
- Key entry points: `RectangleHandle.MoveActiveHandle()` :219, `SelectTool.OnMouseMove()` :92, `SelectionModeHandler.BuildToolbar()` :54
