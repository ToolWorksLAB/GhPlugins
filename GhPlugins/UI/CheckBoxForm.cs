// File: UI/CheckBoxForm.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Forms;
using Eto.Drawing;
using GhPlugins.Models;

namespace GhPlugins.UI
{
    public class CheckBoxForm : Dialog<DialogResult>
    {
        private readonly GridView grid;
        private readonly TextBox search;
        private readonly IList<PluginItem> source;
        private List<PluginItem> view;
        private int? anchorRow;
        private List<int> selectionSnapshot; // <- snapshot of selected rows when edit starts
        private const int SelectedColIndex = 0;

        public CheckBoxForm(IList<PluginItem> plugins, bool startUnchecked = true)
        {
            Title = "Select Plugins";
            ClientSize = new Size(550, 550);
            MinimumSize = new Size(460, 460);
            Resizable = true;

            source = plugins ?? new List<PluginItem>();
            if (startUnchecked)
                foreach (var p in source) if (p != null) p.IsSelected = false;

            view = source.ToList();

            // --- GRID ---
            grid = new GridView
            {
                AllowMultipleSelection = true,
                ShowCellBorders = false,
                RowHeight = 24,
                Height = 420 // fixed so the internal scrollbar appears
            };

            grid.Columns.Add(new GridColumn
            {
                HeaderText = "Keep",
                Editable = true,
                DataCell = new CheckBoxCell
                {
                    Binding = Binding.Property<PluginItem, bool>(x => x.IsSelected)
                                     .Convert<bool?>(
                                         b => (bool?)b,
                                         nb => nb ?? false
                                     )
                },
                Width = 60
            });

            grid.Columns.Add(new GridColumn
            {
                HeaderText = "Name",
                DataCell = new TextBoxCell { Binding = Binding.Property<PluginItem, string>(x => x.Name) },
                Expand = true
            });

            grid.Columns.Add(new GridColumn
            {
                HeaderText = "Type",
                DataCell = new TextBoxCell { Binding = Binding.Delegate<PluginItem, string>(PluginType) },
                Width = 90
            });

            grid.DataStore = view;

            // ----- selection snapshot + shift-range + multi-apply -----
            grid.CellEditing += (s, e) =>
            {
                if (e.Column == SelectedColIndex)
                {
                    anchorRow = e.Row;
                    // snapshot selection now, before the click potentially changes it
                    selectionSnapshot = grid.SelectedRows?.ToList() ?? new List<int>();
                }
                else
                {
                    selectionSnapshot = null;
                }
            };

            grid.CellEdited += (s, e) =>
            {
                if (e.Column != SelectedColIndex) return;

                bool newVal = view[e.Row].IsSelected;

                // Prefer the snapshot if it has multiple rows; otherwise use current selection
                var rows = (selectionSnapshot != null && selectionSnapshot.Count > 1)
                    ? selectionSnapshot
                    : (grid.SelectedRows?.ToList() ?? new List<int>());

                if (rows.Count > 1)
                {
                    foreach (var r in rows)
                        if (r >= 0 && r < view.Count)
                            view[r].IsSelected = newVal;

                    grid.Invalidate();
                    anchorRow = e.Row;
                    selectionSnapshot = null;
                    return;
                }

                // SHIFT range (works even if selection is single)
                if (Keyboard.Modifiers.HasFlag(Keys.Shift) && anchorRow.HasValue)
                {
                    int a = Math.Min(anchorRow.Value, e.Row);
                    int b = Math.Max(anchorRow.Value, e.Row);
                    for (int i = a; i <= b; i++)
                        view[i].IsSelected = newVal;

                    grid.Invalidate();
                }

                anchorRow = e.Row;
                selectionSnapshot = null;
            };

            // Space toggles all selected rows + Ctrl shortcuts
            grid.KeyDown += (s, e) =>
            {
                if (e.KeyData == Keys.Space && (grid.SelectedRows?.Any() ?? false))
                {
                    int first = grid.SelectedRows.First();
                    bool target = !view[first].IsSelected;
                    foreach (var r in grid.SelectedRows)
                        if (r >= 0 && r < view.Count)
                            view[r].IsSelected = target;

                    grid.Invalidate();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyData == Keys.A)
                {
                    ApplyToSelectionOrAll(true);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyData == Keys.D)
                {
                    ApplyToSelectionOrAll(false);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyData == Keys.I)
                {
                    InvertSelectionOrAll();
                    e.Handled = true;
                }
            };

            // --- SEARCH ---
            search = new TextBox { PlaceholderText = "Filter plugins (name contains…)" };
            search.TextChanged += (s, e) => ApplyFilter(search.Text);

            // --- FOOTER ---
            var btnAll = new Button { Text = "Select All  (Ctrl+A)" };
            var btnNone = new Button { Text = "Select None (Ctrl+D)" };
            var btnInvert = new Button { Text = "Invert      (Ctrl+I)" };
            var ok = new Button { Text = "OK", Font = new Font(SystemFont.Bold, 10) };
            var cancel = new Button { Text = "Cancel" };

            // NEW semantics: apply to selection if any, otherwise to all in current filtered view
            btnAll.Click += (s, e) => { ApplyToSelectionOrAll(true); };
            btnNone.Click += (s, e) => { ApplyToSelectionOrAll(false); };
            btnInvert.Click += (s, e) => { InvertSelectionOrAll(); };

            ok.Click += (s, e) => { Result = DialogResult.Ok; Close(); };
            cancel.Click += (s, e) => { Result = DialogResult.Cancel; Close(); };

            var footer = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Padding = new Padding(8),
                Items =
                {
                    btnAll, btnNone, btnInvert,
                    new StackLayoutItem(new Panel(), true),
                    ok, cancel
                }
            };

            // --- LAYOUT (stretch search + grid to full width) ---
            Content = new TableLayout
            {
                Padding = new Padding(10),
                Spacing = new Size(6, 6),
                Rows =
                {
                    new TableRow(new TableCell(search, true)),
                    new TableRow(new TableCell(grid,   true)),  // full width; fixed height drives scrollbar
                    new TableRow(new TableCell(footer))
                }
            };
        }

        // NEW overload to support the onRescan callback
        public CheckBoxForm(IList<PluginItem> plugins, bool startUnchecked, Func<IList<PluginItem>> onRescan)
            : this(plugins, startUnchecked)
        {
            // Optional: if you want a Rescan button in the footer
            if (onRescan != null)
            {
                var rescanButton = new Button { Text = "Rescan" };
                rescanButton.Click += (s, e) =>
                {
                    var newList = onRescan();
                    if (newList != null)
                    {
                        // update the grid’s source with the new scan
                        source.Clear();
                        foreach (var p in newList) source.Add(p);
                        grid.DataStore = null;
                        grid.DataStore = source;
                        grid.Invalidate();
                    }
                };

                // add it to the existing footer row
                // (only if you already have footer buttons like OK/Cancel)
                if (Content is TableLayout layout && layout.Rows.Count > 0)
                {
                    var footerRow = layout.Rows.Last() as TableRow;
                    if (footerRow?.Cells?.FirstOrDefault()?.Control is StackLayout footerStack)
                    {
                        footerStack.Items.Insert(0, rescanButton);
                    }
                }
            }
        }


        // Apply a value (true/false) to current grid selection; if none selected, apply to all filtered items
        private void ApplyToSelectionOrAll(bool value)
        {
            var rows = grid.SelectedRows?.ToList() ?? new List<int>();
            if (rows.Count > 0)
            {
                foreach (var r in rows)
                    if (r >= 0 && r < view.Count)
                        view[r].IsSelected = value;
            }
            else
            {
                foreach (var it in view) it.IsSelected = value;
            }
            grid.Invalidate();
        }

        // Invert selection; if nothing selected, invert all filtered items
        private void InvertSelectionOrAll()
        {
            var rows = grid.SelectedRows?.ToList() ?? new List<int>();
            if (rows.Count > 0)
            {
                foreach (var r in rows)
                    if (r >= 0 && r < view.Count)
                        view[r].IsSelected = !view[r].IsSelected;
            }
            else
            {
                foreach (var it in view) it.IsSelected = !it.IsSelected;
            }
            grid.Invalidate();
        }

        private static string PluginType(PluginItem pi)
        {
            if (pi == null) return "";
            if (pi.GhaPaths != null && pi.GhaPaths.Count > 0) return "GHA";
            if (pi.UserobjectPath != null && pi.UserobjectPath.Count > 0) return "UserObj";
            if (pi.ghpyPath != null && pi.ghpyPath.Count > 0) return "GhPy";
            return "";
        }

        private void ApplyFilter(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                view = source.ToList();
            else
            {
                var t = text.Trim();
                view = source.Where(p => (p?.Name ?? "")
                    .IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            grid.DataStore = null;
            grid.DataStore = view;
            anchorRow = null;
            selectionSnapshot = null;
        }
    }
}
