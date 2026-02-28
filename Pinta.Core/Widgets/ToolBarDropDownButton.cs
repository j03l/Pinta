using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Pinta.Core;

public sealed class ToolBarDropDownButton : Gtk.MenuButton
{
	private readonly bool show_label;
	private readonly Gtk.ListBox listbox;
	private readonly Gtk.Popover popover;
	private ToolBarItem? selected_item;

	private readonly List<ToolBarItem> items;
	public ReadOnlyCollection<ToolBarItem> Items { get; }

	public ToolBarDropDownButton (bool showLabel = false)
	{
		show_label = showLabel;

		items = [];
		Items = new ReadOnlyCollection<ToolBarItem> (items);
		AlwaysShowArrow = true;
		FocusOnClick = false;

		listbox = Gtk.ListBox.New ();
		listbox.SelectionMode = Gtk.SelectionMode.Browse;
		listbox.AddCssClass ("pinta-dropdown");
		listbox.OnRowActivated += OnRowActivated;

		popover = Gtk.Popover.New ();
		popover.HasArrow = false;
		popover.SetOffset (0, 6);
		popover.Child = listbox;

		Popover = popover;
	}

	public ToolBarItem AddItem (string text, string imageId)
	{
		return AddItem (text, imageId, null);
	}

	public ToolBarItem AddItem (string text, string imageId, object? tag)
	{
		ToolBarItem item = new ToolBarItem (text, imageId, tag);

		var row = new Gtk.Box ();
		row.SetOrientation (Gtk.Orientation.Horizontal);
		row.Spacing = 6;

		var icon = Gtk.Image.NewFromIconName (imageId);
		row.Append (icon);

		var label = Gtk.Label.New (text);
		label.Halign = Gtk.Align.Start;
		row.Append (label);

		var listBoxRow = new Gtk.ListBoxRow ();
		listBoxRow.Child = row;
		listbox.Append (listBoxRow);

		item.Row = listBoxRow;
		items.Add (item);

		if (selected_item == null)
			SetSelectedItem (item);

		return item;
	}

	public ToolBarItem SelectedItem {
		get =>
			selected_item is not null
			? selected_item
			: throw new InvalidOperationException ("Attempted to get SelectedItem from a drop down with no items.");
		set {
			if (selected_item != value)
				SetSelectedItem (value);
		}
	}

	public int SelectedIndex {
		get => selected_item is null ? -1 : items.IndexOf (selected_item);
		set {
			if (value < 0 || value >= items.Count)
				return;

			var item = items[value];

			if (item != selected_item)
				SetSelectedItem (item);
		}
	}

	private void SetSelectedItem (ToolBarItem item)
	{
		IconName = item.ImageId;

		selected_item = item;
		TooltipText = item.Text;

		if (item.Row is not null)
			listbox.SelectRow (item.Row);

		if (show_label)
			Label = item.Text;

		OnSelectedItemChanged ();
	}

	private void OnRowActivated (Gtk.ListBox sender, Gtk.ListBox.RowActivatedSignalArgs args)
	{
		var item = items.FirstOrDefault (i => i.Row == args.Row);
		if (item is not null && item != selected_item) {
			SetSelectedItem (item);
		}

		popover.Popdown ();
	}

	private void OnSelectedItemChanged ()
	{
		SelectedItemChanged?.Invoke (this, EventArgs.Empty);
	}

	public event EventHandler? SelectedItemChanged;
}

public sealed class ToolBarItem
{
	public ToolBarItem (string text, string imageId) : this (text, imageId, null) { }

	public ToolBarItem (string text, string imageId, object? tag)
	{
		Text = text;
		ImageId = imageId;
		Tag = tag;
	}

	private static string AdjustName (string baseName)
		=> string.Concat (baseName.Where (c => !char.IsWhiteSpace (c)));

	public string ImageId { get; }
	public object? Tag { get; }
	public string Text { get; }

	/// <summary>
	/// The ListBoxRow associated with this item (set internally by ToolBarDropDownButton).
	/// </summary>
	internal Gtk.ListBoxRow? Row { get; set; }

	public T GetTagOrDefault<T> (T defaultValue)
		=> Tag is T value ? value : defaultValue;
}
