//
// SelectTool.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

public abstract class SelectTool : BaseTool
{
	private readonly IToolService tools;
	private readonly IWorkspaceService workspace;

	private SelectionHistoryItem? hist = default;
	private CombineMode combine_mode = default;
	private bool pending_outside_drag = false;
	private PointD pending_start_point;
	private bool is_placing_fixed_size = false;

	public override Gdk.Key ShortcutKey => new (Gdk.Constants.KEY_S);
	public override bool IsSelectionTool => true;
	protected override bool ShowAntialiasingButton => false;
	private readonly RectangleHandle handle;
	public override IEnumerable<IToolHandle> Handles => [handle];

	public SelectTool (IServiceProvider services) : base (services)
	{
		tools = services.GetService<IToolService> ();
		workspace = services.GetService<IWorkspaceService> ();

		handle = new (workspace) { InvertIfNegative = true };

		workspace.SelectionChanged += AfterSelectionChange;
	}

	protected abstract void DrawShape (Document document, RectangleD r, Layer l);

	protected override void OnBuildToolBar (Gtk.Box tb)
	{
		base.OnBuildToolBar (tb);
		workspace.SelectionHandler.BuildToolbar (tb, Settings);

		tb.Append (ConstraintSeparator);
		tb.Append (ConstraintModeButton);
		tb.Append (ConstraintWidthLabel);
		tb.Append (ConstraintWidthSpinButton);
		tb.Append (ConstraintHeightLabel);
		tb.Append (ConstraintHeightSpinButton);

		UpdateConstraintControlsVisibility ();
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		// Ignore extra button clicks while drawing
		if (handle.IsDragging)
			return;

		hist = new SelectionHistoryItem (workspace, Icon, Name);
		hist.TakeSnapshot ();

		if (handle.BeginDrag (e.PointDouble, document.ImageSize)) {
			// User grabbed an existing handle — if in Fixed Size mode, switch to Any Size
			// so the user can freely resize the selection by dragging.
			if (ConstraintModeIndex == 2 && constraint_mode is not null)
				constraint_mode.SelectedIndex = 0;
		} else {
			PointD p = e.PointDouble;
			bool insideCanvas = p.X >= 0 && p.Y >= 0 &&
				p.X <= document.ImageSize.Width && p.Y <= document.ImageSize.Height;

			if (!insideCanvas) {
				// Defer starting the selection until the mouse enters the canvas.
				// Save the clamped edge point so the selection starts right at the boundary.
				pending_outside_drag = true;
				pending_start_point = new PointD (
					Math.Round (Math.Clamp (p.X, 0, document.ImageSize.Width)),
					Math.Round (Math.Clamp (p.Y, 0, document.ImageSize.Height)));
				combine_mode = PintaCore.Workspace.SelectionHandler.DetermineCombineMode (e);
				return;
			}

			// Start drawing a new rectangle.
			combine_mode = PintaCore.Workspace.SelectionHandler.DetermineCombineMode (e);

			if (ConstraintModeIndex == 2) {
				// Fixed Size: create the selection immediately without dragging.
				PlaceFixedSizeSelection (document, p);
				return;
			}

			BeginNewSelection (document, p);
		}
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		if (pending_outside_drag) {
			PointD p = e.PointDouble;
			bool insideCanvas = p.X >= 0 && p.Y >= 0 &&
				p.X <= document.ImageSize.Width && p.Y <= document.ImageSize.Height;

			if (!insideCanvas) return;

			// Mouse just entered the canvas — start the selection from the saved edge point.
			pending_outside_drag = false;

			if (ConstraintModeIndex == 2) {
				PlaceFixedSizeSelection (document, pending_start_point);
			} else {
				BeginNewSelection (document, pending_start_point);
			}
			return;
		}

		if (!handle.IsDragging) {
			UpdateCursor (e.WindowPoint);
			return;
		}

		handle.UpdateDrag (e.PointDouble, e.IsShiftPressed,
			ConstraintModeIndex, ConstraintWidth, ConstraintHeight);

		ReDraw (document);

		SelectionModeHandler.PerformSelectionMode (document, combine_mode, document.Selection.SelectionPolygons);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (pending_outside_drag) {
			// Mouse never entered the canvas — discard.
			pending_outside_drag = false;
			hist = null;
			return;
		}

		if (!handle.IsDragging)
			return;

		if (handle.HasDragged (e.PointDouble)) {
			ReDraw (document);

			SelectionModeHandler.PerformSelectionMode (document, combine_mode, document.Selection.SelectionPolygons);

			document.Selection.HandleBounds = handle.Rectangle;

			if (hist != null) {
				document.History.PushNewItem (hist);
				hist = null;
			}

			handle.EndDrag ();
		} else {
			// If the user didn't move the mouse, they want to deselect

			// Mark as being done interactive drawing before invoking the deselect action.
			// This will allow AfterSelectionChanged() to clear the selection.
			handle.EndDrag ();

			if (hist != null) {
				// Roll back any changes made to the selection, e.g. in OnMouseDown().
				hist.Undo ();

				hist = null;
			}

			PintaCore.Actions.Edit.Deselect.Activate ();
		}

		// Update the mouse cursor.
		UpdateCursor (e.WindowPoint);
	}

	protected override void OnActivated (Document? document)
	{
		base.OnActivated (document);

		// When entering the tool, update the selection handles from the
		// document's current selection.
		if (document is null) return;

		LoadFromDocument (document);
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		workspace.SelectionHandler.OnSaveSettings (settings);

		if (constraint_mode is not null)
			settings.PutSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_MODE}", constraint_mode.SelectedIndex);
		if (constraint_width is not null)
			settings.PutSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_WIDTH}", (int) constraint_width.Value);
		if (constraint_height is not null)
			settings.PutSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_HEIGHT}", (int) constraint_height.Value);
	}

	private void BeginNewSelection (Document document, PointD p)
	{
		double x = Math.Round (Math.Clamp (p.X, 0, document.ImageSize.Width));
		double y = Math.Round (Math.Clamp (p.Y, 0, document.ImageSize.Height));
		handle.Rectangle = new (x, y, 0.0, 0.0);

		document.PreviousSelection = document.Selection.Clone ();
		document.Selection.SelectionPolygons.Clear ();

		if (!handle.BeginDrag (new PointD (x, y), document.ImageSize))
			throw new InvalidOperationException ("Should be able to start drawing a new rectangle!");
	}

	/// <summary>
	/// Places a fixed-size selection at the click point and immediately finalizes it.
	/// </summary>
	private void PlaceFixedSizeSelection (Document document, PointD p)
	{
		double x = Math.Round (Math.Clamp (p.X, 0, document.ImageSize.Width));
		double y = Math.Round (Math.Clamp (p.Y, 0, document.ImageSize.Height));
		double w = ConstraintWidth;
		double h = ConstraintHeight;

		// If the fixed size doesn't fit at this position, do nothing.
		if (x + w > document.ImageSize.Width || y + h > document.ImageSize.Height) {
			hist = null;
			return;
		}

		RectangleD fixed_rect = new (x, y, w, h);

		// Suppress AfterSelectionChange for the duration of this method.
		// Without this, ReDraw and PerformSelectionMode fire SelectionChanged,
		// and LoadFromDocument overwrites handle.Rectangle with stale HandleBounds.
		is_placing_fixed_size = true;
		try {
			handle.Rectangle = fixed_rect;

			document.PreviousSelection = document.Selection.Clone ();
			document.Selection.SelectionPolygons.Clear ();

			ReDraw (document);
			SelectionModeHandler.PerformSelectionMode (document, combine_mode, document.Selection.SelectionPolygons);

			document.Selection.HandleBounds = fixed_rect;
			handle.Rectangle = fixed_rect;

			if (hist != null) {
				document.History.PushNewItem (hist);
				hist = null;
			}
		} finally {
			is_placing_fixed_size = false;
		}
	}

	private void ReDraw (Document document)
	{
		document.Selection.Visible = true;

		ShowHandles (true);

		RectangleD rect = handle.Rectangle;
		DrawShape (document, rect, document.Layers.SelectionLayer);
	}

	private void ShowHandles (bool visible)
	{
		handle.Active = visible;
	}

	private void UpdateCursor (PointD viewPos)
	{
		Gdk.Cursor? cursor = handle.Active ?
			handle.GetCursorAtPoint (viewPos) :
			null;

		SetCursor (cursor ?? DefaultCursor);
	}

	protected override void OnAfterUndo (Document document)
	{
		base.OnAfterUndo (document);
		LoadFromDocument (document);
	}

	protected override void OnAfterRedo (Document document)
	{
		base.OnAfterRedo (document);
		LoadFromDocument (document);
	}

	private void AfterSelectionChange (object? sender, EventArgs event_args)
	{
		if (handle.IsDragging || is_placing_fixed_size || !workspace.HasOpenDocuments)
			return;

		// TODO: Try to remove this ActiveDocument call
		LoadFromDocument (workspace.ActiveDocument);
	}

	/// <summary>
	/// Initialize from the document's selection.
	/// </summary>
	private void LoadFromDocument (Document document)
	{
		DocumentSelection selection = document.Selection;
		handle.Rectangle = selection.HandleBounds;

		bool hasHandleBounds = selection.HandleBounds is { Width: > 0, Height: > 0 };
		ShowHandles (hasHandleBounds && document.Selection.Visible && tools.CurrentTool == this);
	}

	#region Constraint Controls

	private Separator? constraint_sep;
	private ToolBarDropDownButton? constraint_mode;
	private Label? constraint_width_label;
	private SpinButton? constraint_width;
	private Label? constraint_height_label;
	private SpinButton? constraint_height;

	private int ConstraintModeIndex => constraint_mode?.SelectedIndex ?? 0;
	private int ConstraintWidth => (int) (constraint_width?.Value ?? 1);
	private int ConstraintHeight => (int) (constraint_height?.Value ?? 1);

	private Separator ConstraintSeparator => constraint_sep ??= GtkExtensions.CreateToolBarSeparator ();

	private ToolBarDropDownButton ConstraintModeButton {
		get {
			if (constraint_mode is null) {
				constraint_mode = new ToolBarDropDownButton ();

				constraint_mode.AddItem (Translations.GetString ("Any Size"), Pinta.Resources.Icons.SelectConstraintAnySize, 0);
				constraint_mode.AddItem (Translations.GetString ("Fixed Ratio"), Pinta.Resources.Icons.SelectConstraintRatio, 1);
				constraint_mode.AddItem (Translations.GetString ("Fixed Size"), Pinta.Resources.Icons.SelectConstraintFixedSize, 2);

				constraint_mode.SelectedIndex = Settings.GetSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_MODE}", 0);

				constraint_mode.SelectedItemChanged += (_, _) => {
					UpdateConstraintControlsVisibility ();
					UpdateConstraintDefaults ();
				};
			}

			return constraint_mode;
		}
	}

	private Label ConstraintWidthLabel => constraint_width_label ??= Label.New (string.Format (" {0}: ", Translations.GetString ("Width")));

	private SpinButton ConstraintWidthSpinButton => constraint_width ??= GtkExtensions.CreateToolBarSpinButton (
		1, 1e5, 1,
		Settings.GetSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_WIDTH}", ConstraintModeIndex == 2 ? 100 : 1));

	private Label ConstraintHeightLabel => constraint_height_label ??= Label.New (string.Format (" {0}: ", Translations.GetString ("Height")));

	private SpinButton ConstraintHeightSpinButton => constraint_height ??= GtkExtensions.CreateToolBarSpinButton (
		1, 1e5, 1,
		Settings.GetSetting ($"{Name}-{SettingNames.SELECTION_CONSTRAINT_HEIGHT}", ConstraintModeIndex == 2 ? 100 : 1));

	private void UpdateConstraintControlsVisibility ()
	{
		bool showDimensions = ConstraintModeIndex != 0;

		ConstraintWidthLabel.Visible = showDimensions;
		ConstraintWidthSpinButton.Visible = showDimensions;
		ConstraintHeightLabel.Visible = showDimensions;
		ConstraintHeightSpinButton.Visible = showDimensions;
	}

	private void UpdateConstraintDefaults ()
	{
		switch (ConstraintModeIndex) {
			case 1: // Fixed Ratio
				ConstraintWidthSpinButton.Value = 1;
				ConstraintHeightSpinButton.Value = 1;
				break;
			case 2: // Fixed Size
				ConstraintWidthSpinButton.Value = 100;
				ConstraintHeightSpinButton.Value = 100;
				break;
		}
	}

	#endregion
}
