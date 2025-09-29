using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using GhPlugins.Models;
using GhPlugins.Services;
using Rhino;

namespace GhPlugins.UI
{
    public class ModeManagerDialog : Dialog
    {
        private ListBox pluginListBox;
        private Button createButton;
        private Button selectPluginsButton;
        private Button selectEnvironmentButton;
        private Button launchButton;
        private List<PluginItem> allPlugins = new List<PluginItem>();
        private ModeConfig selectedEnvironment;

        public ModeManagerDialog()
        {
            Title = "Grasshopper Mode Manager";
            ClientSize = new Size(600, 400);
            Resizable = false;

            // Buttons
            createButton = new Button { Text = "Create\nEnvironment", BackgroundColor = Colors.HotPink, TextColor = Colors.Black };
            selectPluginsButton = new Button { Text = "Select Plugins", BackgroundColor = Colors.CornflowerBlue, TextColor = Colors.Black };
            selectEnvironmentButton = new Button { Text = "Select Environment", BackgroundColor = Colors.Gold, TextColor = Colors.Black };
            launchButton = new Button { Text = "Launch\nGrasshopper", Font = new Font(SystemFont.Bold, 12), Enabled = false };

            createButton.Click += (s, e) => CreateEnvironment();
            selectPluginsButton.Click += (s, e) => ManualPluginSelection();
            selectEnvironmentButton.Click += (s, e) => SelectSavedEnvironment();
            launchButton.Click += (s, e) => LaunchGrasshopper();

            // Listbox (plugin names)
            pluginListBox = new ListBox();

            // Layout: Left buttons
            var leftPanel = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Width = 160,
                Padding = new Padding(10),
                Items =
                {
                    createButton,
                    selectPluginsButton,
                    selectEnvironmentButton
                }
            };

            // Right panel (plugin list)
            var rightPanel = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Padding = new Padding(10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { pluginListBox }
            };

            // Bottom layout
            var bottomPanel = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Padding(10),
                Spacing = 10,
                Items =
                {
                    new StackLayoutItem(launchButton, HorizontalAlignment.Stretch, true),
                    new StackLayoutItem(new Label { Text = "🧠 ToolWorks Lab", Font = new Font(SystemFont.Default, 10), VerticalAlignment = VerticalAlignment.Center }, HorizontalAlignment.Right)
                }
            };

            // Whole UI
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Items =
                {
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Items = { leftPanel, rightPanel }
                    },
                    bottomPanel
                }
            };
        }

        private void CreateEnvironment()
        {
            allPlugins = PluginScanner.ScanDefaultPluginFolders();

            var checkForm = new CheckBoxForm(allPlugins);
            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                var selected = allPlugins.Where(p => p.IsSelected).ToList();
                if (selected.Count == 0) return;

                string envName = InputBox("Name this environment:");
                if (string.IsNullOrWhiteSpace(envName)) return;

                var environments = ModeManager.LoadEnvironments();
                environments.Add(new ModeConfig(envName, selected.Select(p => p.Path).ToList()));
                ModeManager.SaveEnvironments(environments);

                RhinoApp.WriteLine("Environment '{0}' created with {1} plugins.", envName, selected.Count);
            }
        }

        private void ManualPluginSelection()
        {
            allPlugins = PluginScanner.ScanDefaultPluginFolders();

            var checkForm = new CheckBoxForm(allPlugins);
            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                selectedEnvironment = new ModeConfig("Manual Selection", allPlugins.Where(p => p.IsSelected).Select(p => p.Path).ToList());
                pluginListBox.DataStore = selectedEnvironment.PluginPaths;
                launchButton.Enabled = true;
            }
        }

        private void SelectSavedEnvironment()
        {
            var environments = ModeManager.LoadEnvironments();
            if (environments.Count == 0)
            {
                MessageBox.Show("No environments saved.");
                return;
            }

            var names = environments.Select(e => e.Name).ToArray();
            var dialog = new SelectListDialog("Select an Environment", names);
            if (dialog.ShowModal(this) == DialogResult.Ok)
            {
                string selectedName = dialog.SelectedItem;
                selectedEnvironment = environments.FirstOrDefault(e => e.Name == selectedName);
                pluginListBox.DataStore = selectedEnvironment?.PluginPaths ?? new List<string>();
                launchButton.Enabled = selectedEnvironment != null;
            }
        }

        private void LaunchGrasshopper()
        {
            if (selectedEnvironment == null || selectedEnvironment.PluginPaths.Count == 0)
            {
                MessageBox.Show("No plugin paths selected.");
                return;
            }

            string joinedPaths = string.Join(";", selectedEnvironment.PluginPaths);
            Environment.SetEnvironmentVariable("GH_PATH", joinedPaths);

            RhinoApp.RunScript("_Grasshopper", false);
        }

        private string InputBox(string message)
        {
            var prompt = new Dialog<string>
            {
                Title = message,
                ClientSize = new Size(300, 120),
                Resizable = false
            };

            var input = new TextBox();
            var ok = new Button { Text = "OK" };
            var cancel = new Button { Text = "Cancel" };

            string result = null;
            ok.Click += (s, e) => { result = input.Text; prompt.Close(); };
            cancel.Click += (s, e) => { prompt.Close(); };

            prompt.Content = new StackLayout
            {
                Padding = new Padding(10),
                Items =
                {
                    new Label { Text = message },
                    input,
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Items = { ok, cancel }
                    }
                }
            };

            prompt.ShowModal(this);
            return result;
        }
    }
}
