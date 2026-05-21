using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Sieve.Models;
using Sieve.services;

namespace Sieve.UI
{
    public class ModeManagerDialog : Dialog
    {
        private readonly Button createButton;
        private readonly Button selectPluginsButton;
        private readonly Button selectEnvironmentButton;
        private readonly Button launchButton;
        private readonly Label statusLabel;

        private List<PluginItem> allPlugins = new List<PluginItem>();
        private ModeConfig selectedEnvironment;

        public ModeManagerDialog()
        {
            Title = "Sieve";
            ClientSize = new Size(860, 410);
            Resizable = false;
            Padding = new Padding(14);
            BackgroundColor = Color.FromArgb(20, 24, 40);

            createButton = BuildTileButton("Create", "Create a new environment");
            selectPluginsButton = BuildTileButton("Plugins", "Select plugins manually");
            selectEnvironmentButton = BuildTileButton("Environments", "Open a saved profile");

            launchButton = new Button
            {
                Text = "Launch Grasshopper",
                Enabled = false,
                BackgroundColor = Color.FromArgb(69, 203, 133),
                TextColor = Colors.White,
                Font = new Font(SystemFont.Bold, 13),
                Height = 44
            };

            statusLabel = new Label
            {
                Text = "No environment selected",
                TextColor = Color.FromArgb(180, 190, 210)
            };

            createButton.Click += (s, e) => CreateEnvironment();
            selectPluginsButton.Click += (s, e) => ManualPluginSelection();
            selectEnvironmentButton.Click += (s, e) => SelectSavedEnvironment();
            launchButton.Click += (s, e) => LaunchGrasshopper();

            Control logoControl = CreateLogoView();

            Content = new TableLayout
            {
                Spacing = new Size(12, 12),
                Rows =
                {
                    BuildHeader(logoControl),
                    new TableRow(BuildTiles()),
                    new TableRow(BuildFooter())
                }
            };
        }

        private Control BuildHeader(Control logoControl)
        {
            return new Panel
            {
                Padding = new Padding(16, 12),
                BackgroundColor = Color.FromArgb(40, 47, 72),
                Content = new TableLayout
                {
                    Spacing = new Size(10, 0),
                    Rows =
                    {
                        new TableRow(
                            logoControl,
                            new TableCell(new StackLayout
                            {
                                Spacing = 2,
                                Items =
                                {
                                    new Label { Text = "Sieve", Font = new Font(SystemFont.Bold, 20), TextColor = Colors.White },
                                    new Label { Text = "Modern plugin environment manager", TextColor = Color.FromArgb(186, 195, 214) }
                                }
                            }, true)
                        )
                    }
                }
            };
        }

        private Control BuildTiles()
        {
            return new TableLayout
            {
                Spacing = new Size(12, 0),
                Rows =
                {
                    new TableRow(
                        new TableCell(createButton, true),
                        new TableCell(selectPluginsButton, true),
                        new TableCell(selectEnvironmentButton, true)
                    )
                }
            };
        }

        private Control BuildFooter()
        {
            return new Panel
            {
                Padding = new Padding(16, 10),
                BackgroundColor = Color.FromArgb(34, 39, 58),
                Content = new StackLayout
                {
                    Spacing = 8,
                    Items = { statusLabel, launchButton }
                }
            };
        }

        private static Button BuildTileButton(string title, string subtitle)
        {
            return new Button
            {
                Text = title + "\n" + subtitle,
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb(60, 76, 110),
                Font = new Font(SystemFont.Bold, 11),
                Height = 130
            };
        }

        private static Control CreateLogoView()
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Sieve.Resources.logo.png");
            if (stream == null)
            {
                return new Panel { Size = new Size(60, 60) };
            }

            return new ImageView
            {
                Image = new Bitmap(stream),
                Size = new Size(60, 60)
            };
        }

        private void CreateEnvironment()
        {
            if (PluginScanner.pluginItems == null || PluginScanner.pluginItems.Count == 0)
            {
                var loaded = Info.Tools.LoadScan();
                if (loaded != null && loaded.Count > 0) PluginScanner.pluginItems = loaded;
                else
                {
                    PluginScanner.ScanDefaultPluginFolders();
                    Info.Tools.SaveScan(PluginScanner.pluginItems);
                }
            }

            allPlugins = PluginScanner.pluginItems;

            var checkForm = new CheckBoxForm(PluginScanner.pluginItems, true, () =>
            {
                PluginScanner.ScanDefaultPluginFolders();
                Info.Tools.SaveScan(PluginScanner.pluginItems);
                allPlugins = PluginScanner.pluginItems;
                return PluginScanner.pluginItems.ToList();
            });

            if (checkForm.ShowModal(this) != DialogResult.Ok) return;

            var selected = PluginScanner.pluginItems.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "No plugins selected. Environment was not created.", "Sieve");
                return;
            }

            var envName = InputBox("Name this environment:");
            if (string.IsNullOrWhiteSpace(envName)) return;

            var environments = ModeManager.LoadEnvironments();
            var newEnv = new ModeConfig(envName, selected);
            environments.Add(newEnv);
            ModeManager.SaveEnvironments(environments);

            selectedEnvironment = newEnv;
            launchButton.Enabled = true;
            statusLabel.Text = $"Environment: {envName} ({selected.Count} plugins)";
            RhinoApp.WriteLine("Environment '{0}' created with {1} plugins.", envName, selected.Count);
        }

        private void ManualPluginSelection(){CreateEnvironment();}

        private void SelectSavedEnvironment()
        {
            var environments = ModeManager.LoadEnvironments();
            if (environments.Count == 0)
            {
                MessageBox.Show("No environments saved.");
                return;
            }

            var dialog = new EnvironmentSelectDialog(environments);
            var result = dialog.ShowModal(this);
            if (result == null) return;
            if (result.IsDelete)
            {
                var toDelete = environments.FirstOrDefault(e => e.Name == result.SelectedName);
                if (toDelete != null)
                {
                    environments.Remove(toDelete);
                    ModeManager.SaveEnvironments(environments);
                }
                return;
            }

            selectedEnvironment = environments.FirstOrDefault(e => e.Name == result.SelectedName);
            if (selectedEnvironment == null) return;
            statusLabel.Text = $"Environment: {selectedEnvironment.Name} ({selectedEnvironment.Plugins.Count} plugins)";
            launchButton.Enabled = selectedEnvironment.Plugins?.Count > 0;
        }

        public void LaunchGrasshopper()
        {
            GhPluginBlocker.ApplyBlocking(allPlugins);
            ScanReport.Save(allPlugins);
            RhinoApp.Idle += LaunchGrasshopperOnIdle;
            Close();
        }

        private void LaunchGrasshopperOnIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= LaunchGrasshopperOnIdle;
            RhinoApp.RunScript("-_Grasshopper _Load _Enter", false);
        }

        private string InputBox(string message)
        {
            var prompt = new Dialog<string> { Title = message, ClientSize = new Size(360, 170), Resizable = false, BackgroundColor = Color.FromArgb(28, 31, 46) };
            var input = new TextBox { Width = 310, PlaceholderText = "Environment name" };
            var ok = new Button { Text = "Save", BackgroundColor = Color.FromArgb(78, 173, 255), TextColor = Colors.White };
            var cancel = new Button { Text = "Cancel" };
            string result = null;
            ok.Click += (s, e) => { result = input.Text; prompt.Close(); };
            cancel.Click += (s, e) => prompt.Close();

            prompt.Content = new StackLayout
            {
                Padding = 16,
                Spacing = 10,
                Items =
                {
                    new Label { Text = message, TextColor = Color.FromArgb(228, 233, 245) },
                    input,
                    new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { ok, cancel } }
                }
            };

            prompt.ShowModal(this);
            return result;
        }
    }
}
