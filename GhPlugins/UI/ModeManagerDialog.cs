using System;
using System.Collections.Generic;
using System.Reflection;
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
        private Button createButton;
        private Button selectPluginsButton;
        private Button selectEnvironmentButton;
        private Button launchButton;

        private List<PluginItem> allPlugins = new List<PluginItem>();
        private ModeConfig selectedEnvironment;

        public ModeManagerDialog()
        {
            Title = "Grasshopper Plugin Manager";
            ClientSize = new Size(730, 200);
            Resizable = false;

            var bigBtnSize = new Size(170, 68);
            var logoSize = new Size(60, 60);

            createButton = new Button
            {
                Text = "Create New\nEnvironment",
                BackgroundColor = Colors.HotPink,
                TextColor = Colors.Black,
                Font = new Font(SystemFont.Bold, 10),
                Size = bigBtnSize
            };
            selectPluginsButton = new Button
            {
                Text = "Select Plugins",
                BackgroundColor = Colors.CornflowerBlue,
                TextColor = Colors.Black,
                Font = new Font(SystemFont.Bold, 10),
                Size = bigBtnSize
            };
            selectEnvironmentButton = new Button
            {
                Text = "Select Environment",
                BackgroundColor = Colors.Gold,
                TextColor = Colors.Black,
                Font = new Font(SystemFont.Bold, 10),
                Size = bigBtnSize
            };
            launchButton = new Button
            {
                Text = "Launch Grasshopper",
                Enabled = false,
                Font = new Font(SystemFont.Bold, 16),
                MinimumSize = new Size(100, logoSize.Height)
            };

            createButton.Click += (s, e) => CreateEnvironment();
            selectPluginsButton.Click += (s, e) => ManualPluginSelection();
            selectEnvironmentButton.Click += (s, e) => SelectSavedEnvironment();
            launchButton.Click += (s, e) => LaunchGrasshopper();

            Control logoControl;
            var asm = Assembly.GetExecutingAssembly();
            using (var ls = asm.GetManifestResourceStream("GhPlugins.Resources.logo.png"))
                logoControl = ls != null ? (Control)new ImageView { Image = new Bitmap(ls), Size = logoSize }
                                         : new Panel { Size = logoSize };

            var topRow = new TableLayout
            {
                Padding = new Padding(24, 20, 24, 10),
                Spacing = new Size(24, 0),
                Rows =
                {
                    new TableRow(
                        new TableCell(new Panel(), true),
                        new TableCell(createButton),
                        new TableCell(selectPluginsButton),
                        new TableCell(selectEnvironmentButton),
                        new TableCell(new Panel(), true)
                    )
                }
            };

            var bottomStrip = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 24,
                Padding = new Padding(24, 0, 24, 24),
                Items =
                {
                    new StackLayoutItem(launchButton, true),
                    new StackLayoutItem(logoControl)
                }
            };
            var bottomHost = new Panel { Content = bottomStrip, MinimumSize = new Size(0, logoSize.Height) };

            var filler = new StackLayout();

            Content = new TableLayout
            {
                Rows =
                {
                    topRow,
                    new TableRow(new TableCell(filler, true)),
                    bottomHost
                }
            };
        }

        private void CreateEnvironment()
        {
            PluginScanner.ScanDefaultPluginFolders();
            allPlugins = PluginScanner.pluginItems;

            var checkForm = new CheckBoxForm(
                allPlugins,
                true,
                () =>
                {
                    PluginScanner.ScanDefaultPluginFolders();
                    return PluginScanner.pluginItems;
                });

            // If you don't have the 3-arg overload yet, use:
            // var checkForm = new CheckBoxForm(allPlugins, true);

            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                var selected = allPlugins.Where(p => p.IsSelected).ToList();
                if (selected.Count == 0) return;

                string envName = InputBox("Name this environment:");
                if (string.IsNullOrWhiteSpace(envName)) return;

                var environments = ModeManager.LoadEnvironments();
                environments.Add(new ModeConfig(envName, selected));
                ModeManager.SaveEnvironments(environments);

                RhinoApp.WriteLine("Environment '{0}' created with {1} plugins.", envName, selected.Count);
                launchButton.Enabled = true;
            }
        }

        private void ManualPluginSelection()
        {
            PluginScanner.ScanDefaultPluginFolders();
            allPlugins = PluginScanner.pluginItems;

            var checkForm = new CheckBoxForm(
                allPlugins,
                true,
                () =>
                {
                    PluginScanner.ScanDefaultPluginFolders();
                    return PluginScanner.pluginItems;
                });

            // Or without onRescan:
            // var checkForm = new CheckBoxForm(allPlugins, true);

            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                selectedEnvironment = new ModeConfig(
                    "Manual",
                    allPlugins.Where(p => p.IsSelected).ToList()
                );

                launchButton.Enabled = selectedEnvironment.Plugins != null && selectedEnvironment.Plugins.Count > 0;
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
                launchButton.Enabled = selectedEnvironment != null && selectedEnvironment.Plugins != null && selectedEnvironment.Plugins.Count > 0;
            }
        }

        public void LaunchGrasshopper()
        {
            var env = selectedEnvironment ?? new ModeConfig("Manual", allPlugins.Where(p => p.IsSelected).ToList());

            GhPluginBlocker.applyPluginDisable(allPlugins, env);
            GhPluginBlocker.ApplyBlocking(allPlugins);

            try
            {
                RhinoApp.WriteLine("[Gh Mode Manager] Environment applied.");
                RhinoApp.Idle += LaunchGrasshopperOnIdle;
                this.Close();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("ERROR in LaunchGrasshopper: " + ex);
                MessageBox.Show(this, "Failed to launch Grasshopper. See Rhino command line for details.", "Mode Manager");
            }
        }

        private void LaunchGrasshopperOnIdle(object sender, EventArgs e)
        {
            Rhino.RhinoApp.Idle -= LaunchGrasshopperOnIdle;

            try
            {
                Rhino.RhinoApp.RunScript("-_Grasshopper _Load _Enter", false);

                dynamic gh = null;
                try { gh = Rhino.RhinoApp.GetPlugInObject("Grasshopper"); } catch { }

                bool editorLoaded = false;
                if (gh != null)
                {
                    try { editorLoaded = gh.IsEditorLoaded(); } catch { }
                    if (!editorLoaded)
                    {
                        try { gh.LoadEditor(); } catch { }
                    }
                }

                var t = new Eto.Forms.UITimer { Interval = 0.60 };
                t.Elapsed += (s2, e2) =>
                {
                    t.Stop();
                    try { gh?.ShowEditor(true); } catch { }
                    Rhino.RhinoApp.RunScript("-_Grasshopper _Editor _Enter", false);
                    RhinoApp.RunScript("_Grasshopper", false);
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("ERROR launching Grasshopper: " + ex);
            }
        }

        private string InputBox(string message)
        {
            var prompt = new Dialog<string> { Title = message, ClientSize = new Size(300, 120), Resizable = false };
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
