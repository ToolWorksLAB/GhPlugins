using Eto.Forms;
using Eto.Drawing;
using System.Collections.Generic;
using GhPlugins.Models;

namespace GhPlugins.UI
{
    public class CheckBoxForm : Dialog<DialogResult>
    {
        private List<CheckBox> checkBoxes = new List<CheckBox>();

        public CheckBoxForm(List<PluginItem> plugins)
        {
            Title = "Select Plugins";
            ClientSize = new Size(400, 400);
            Resizable = true;

            var layout = new DynamicLayout { Padding = new Padding(10), Spacing = new Size(5, 5) };

            foreach (var plugin in plugins)
            {
                var cb = new CheckBox { Text = plugin.Name };
                layout.Add(cb);
                checkBoxes.Add(cb);
            }

            var okButton = new Button { Text = "OK" };
            okButton.Click += (s, e) =>
            {
                for (int i = 0; i < checkBoxes.Count; i++)
                    plugins[i].IsSelected = checkBoxes[i].Checked == true;

                Result = DialogResult.Ok;
                Close();
            };

            layout.AddSeparateRow(null, okButton, null);
            Content = layout;
        }
    }
}
