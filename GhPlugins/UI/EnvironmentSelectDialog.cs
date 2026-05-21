using Eto.Drawing;
using Eto.Forms;
using Sieve.Models;
using System.Collections.Generic;
using System.Linq;

namespace Sieve.UI
{
    public class EnvironmentSelectDialog : Dialog<EnvironmentSelectDialog.Result>
    {
        public class Result
        {
            public bool IsDelete { get; set; }
            public string SelectedName { get; set; }
        }

        private readonly IList<ModeConfig> environments;
        private readonly ListBox envList;
        private readonly ListBox pluginList;
        private readonly Label pluginHeader;

        public EnvironmentSelectDialog(IList<ModeConfig> environments)
        {
            this.environments = environments;
            Title = "Environment Library";
            ClientSize = new Size(700, 420);
            Resizable = false;
            BackgroundColor = Color.FromArgb(24, 28, 40);

            envList = new ListBox
            {
                DataStore = environments.Select(e => e.Name).ToList()
            };

            pluginList = new ListBox();
            pluginHeader = new Label { Text = "Plugins", Font = new Font(SystemFont.Bold, 10), TextColor = Colors.White };
            envList.SelectedIndexChanged += (s, e) => UpdatePluginPreview();

            var open = new Button { Text = "Open", BackgroundColor = Color.FromArgb(78, 173, 255), TextColor = Colors.White };
            var delete = new Button { Text = "Delete", BackgroundColor = Color.FromArgb(199, 91, 115), TextColor = Colors.White };
            var cancel = new Button { Text = "Cancel" };

            open.Click += (s, e) =>
            {
                var name = envList.SelectedValue as string;
                if (string.IsNullOrEmpty(name)) return;
                Close(new Result { IsDelete = false, SelectedName = name });
            };

            delete.Click += (s, e) =>
            {
                var name = envList.SelectedValue as string;
                if (string.IsNullOrEmpty(name)) return;
                if (MessageBox.Show(this, $"Delete environment '{name}'?", "Confirm", MessageBoxButtons.YesNo, MessageBoxType.Warning) == DialogResult.Yes)
                    Close(new Result { IsDelete = true, SelectedName = name });
            };

            cancel.Click += (s, e) => Close(null);

            Content = new TableLayout
            {
                Padding = 14,
                Spacing = new Size(10, 10),
                Rows =
                {
                    new TableRow(
                        new TableCell(new Panel { BackgroundColor = Color.FromArgb(38, 44, 62), Padding = new Padding(10), Content = envList }, true),
                        new TableCell(new Panel { BackgroundColor = Color.FromArgb(38, 44, 62), Padding = new Padding(10), Content = new StackLayout{Spacing = 6, Items={pluginHeader, new StackLayoutItem(pluginList, true)}} }, true)
                    ),
                    new TableRow(new StackLayout{Orientation = Orientation.Horizontal, Spacing = 8, HorizontalContentAlignment = HorizontalAlignment.Right, Items = { open, delete, cancel }})
                }
            };

            if (environments.Count > 0) envList.SelectedIndex = 0;
        }

        private void UpdatePluginPreview()
        {
            var idx = envList.SelectedIndex;
            if (idx < 0 || idx >= environments.Count)
            {
                pluginHeader.Text = "Plugins";
                pluginList.DataStore = null;
                return;
            }

            var plugins = environments[idx].Plugins ?? new List<PluginItem>();
            pluginHeader.Text = $"Plugins ({plugins.Count})";
            pluginList.DataStore = plugins.Select(p => p.Name).ToList();
        }
    }
}
