using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using GhPlugins.Models;

namespace GhPlugins.UI
{
    public class CheckBoxForm : Dialog<DialogResult>
    {
        public CheckBoxForm(List<PluginItem> plugins)
        {
            Title = "Select Plugins";
            ClientSize = new Size(400, 500);
            Resizable = true;

            var layout = new DynamicLayout { Padding = 10, Spacing = new Size(5, 5) };

            foreach (var plugin in plugins)
            {
                var checkbox = new CheckBox { Text = plugin.Name, Checked = plugin.IsSelected };
                checkbox.CheckedChanged += (s, e) =>
                {
                    plugin.IsSelected = checkbox.Checked ?? false;
                };
                layout.Add(checkbox);
            }

            var doneButton = new Button { Text = "Done" };
            doneButton.Click += (s, e) => Close(DialogResult.Ok);

            layout.Add(null); // spacer
            layout.Add(doneButton, yscale: false);

            Content = new Scrollable
            {
                Content = layout
            };
        }
    }
}
