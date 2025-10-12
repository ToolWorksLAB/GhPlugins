// File: UI/ModeManagerDialog.cs
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
        private readonly GhEnvironmentApplier _envApplier = new GhEnvironmentApplier();
        private Button createButton;
        private Button selectPluginsButton;
        private Button selectEnvironmentButton;
        private Button launchButton;
        private List<PluginItem> allPlugins = new List<PluginItem>();
        private ModeConfig selectedEnvironment;

        public ModeManagerDialog()
        {
            Title = "Grasshopper Mode Manager";
            ClientSize = new Size(600, 300);
            Resizable = false;

            createButton = new Button { Text = "Create New\nEnvironment", BackgroundColor = Colors.HotPink, TextColor = Colors.Black };
            selectPluginsButton = new Button { Text = "Select Plugins", BackgroundColor = Colors.CornflowerBlue, TextColor = Colors.Black };
            selectEnvironmentButton = new Button { Text = "Select Environment", BackgroundColor = Colors.Gold, TextColor = Colors.Black };
            launchButton = new Button { Text = "Launch\nGrasshopper", Font = new Font(SystemFont.Bold, 12), Enabled = false };

            createButton.Click += (s, e) => CreateEnvironment();
            selectPluginsButton.Click += (s, e) => ManualPluginSelection();
            selectEnvironmentButton.Click += (s, e) => SelectSavedEnvironment();
            launchButton.Click += (s, e) => LaunchGrasshopper();

            var topButtons = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(createButton, true),
                    new StackLayoutItem(selectPluginsButton, true),
                    new StackLayoutItem(selectEnvironmentButton, true)
                }
            };

            // Optional logo
            ImageView logo = null;
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("GhPlugins.Resources.logo.png"))
            {
                if (stream != null)
                {
                    var logoBitmap = new Bitmap(stream);
                    logo = new ImageView { Image = logoBitmap, Size = new Size(80, 80) };
                }
            }

            var bottomPanel = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Padding = new Padding(10),
                Items = { new StackLayoutItem(launchButton, HorizontalAlignment.Stretch, true) }
            };
            if (logo != null) bottomPanel.Items.Add(new StackLayoutItem(logo, HorizontalAlignment.Right));

            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Padding = new Padding(10),
                Items = { topButtons, bottomPanel }
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

                RhinoApp.WriteLine($"Environment '{envName}' created with {selected.Count} plugins.");
            }
        }

        private void ManualPluginSelection()
        {
            allPlugins = PluginScanner.ScanDefaultPluginFolders();
            var checkForm = new CheckBoxForm(allPlugins);

            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                selectedEnvironment = new ModeConfig(
                    "Manual",
                    allPlugins.Where(p => p.IsSelected).Select(p => p.Path).ToList()
                );
                launchButton.Enabled = selectedEnvironment.PluginPaths?.Count > 0;
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
                launchButton.Enabled = selectedEnvironment != null && selectedEnvironment.PluginPaths?.Count > 0;
            }
        }

        // -------- THE IMPORTANT PART --------
        public void LaunchGrasshopper()
        {
            if (selectedEnvironment == null || selectedEnvironment.PluginPaths == null || selectedEnvironment.PluginPaths.Count == 0)
            {
                MessageBox.Show(this, "Select an Environment first.", "Mode Manager", MessageBoxButtons.OK);
                return;
            }

            try
            {
                // 1) Apply the environment (writes .no6/.no7/.no8 + optional .ghlink)
                _envApplier.Apply(selectedEnvironment.Name, selectedEnvironment.PluginPaths);
                RhinoApp.WriteLine($"[Gh Mode Manager] Environment '{selectedEnvironment.Name}' applied.");

                // 2) Close this modal dialog FIRST so GH can show its window
                //    Then we will launch GH on the next Rhino idle tick.
                RhinoApp.Idle += LaunchGrasshopperOnIdle;
                this.Close();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("ERROR in LaunchGrasshopper: " + ex);
                MessageBox.Show(this, "Failed to launch Grasshopper. See Rhino command line for details.", "Mode Manager");
            }
        }

        // one-shot idle handler to run AFTER the dialog has fully closed
        // one-shot idle handler to run AFTER the dialog has fully closed
        private void LaunchGrasshopperOnIdle(object sender, EventArgs e)
        {
            Rhino.RhinoApp.Idle -= LaunchGrasshopperOnIdle;
            try
            {
                dynamic gh = Rhino.RhinoApp.GetPlugInObject("Grasshopper");
                if (!gh.IsEditorLoaded())
                    gh.LoadEditor();

                // Try to show editor explicitly (if available)
                try { gh.ShowEditor(true); } catch { /* not on all builds */ }

                // Fallback to command to force the editor window visible
                Rhino.RhinoApp.RunScript("-_Grasshopper _Editor _Enter", false);
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("ERROR launching Grasshopper: " + ex);
            }
        }

        // ------------------------------------

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
