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
using Rhino.PlugIns;

namespace GhPlugins.UI
{
    public class ModeManagerDialog : Dialog
    {
      //  private readonly GhEnvironmentApplier _envApplier = new GhEnvironmentApplier();
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
            var info = GhaInfoReader.ReadPluginInfo("C:/Users/erfan/AppData/Roaming/Grasshopper/Libraries/chromodoris.gha");
            if (info != null)
            {
                RhinoApp.WriteLine($"Name: {info.Name}");
                RhinoApp.WriteLine($"Version: {info.Version}");
                RhinoApp.WriteLine($"Author: {info.AuthorName} ({info.AuthorContact})");
                RhinoApp.WriteLine($"Description: {info.Description}");
                RhinoApp.WriteLine($"Id: {info.Id}");
                RhinoApp.WriteLine($"Location: {info.Location}");
            }
            else
            {
                Console.WriteLine("No GH_AssemblyInfo metadata found in that .gha");
            }

            PluginScanner.ScanDefaultPluginFolders();
            allPlugins = PluginScanner.pluginItems;
            var checkForm = new CheckBoxForm(allPlugins);

            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                var selected = allPlugins.Where(p => p.IsSelected).ToList();
                if (selected.Count == 0) return;

                string envName = InputBox("Name this environment:");
                if (string.IsNullOrWhiteSpace(envName)) return;

                var environments = ModeManager.LoadEnvironments();

                environments.Add(new ModeConfig(envName, selected));
                ModeManager.SaveEnvironments(environments);

                RhinoApp.WriteLine($"Environment '{envName}' created with {selected.Count} plugins.");
            }
        }

        private void ManualPluginSelection()
        {
            PluginScanner.ScanDefaultPluginFolders();

            allPlugins = PluginScanner.pluginItems;


            var checkForm = new CheckBoxForm(allPlugins);

            if (checkForm.ShowModal(this) == DialogResult.Ok)
            {
                selectedEnvironment = new ModeConfig(
                    "Manual",
                    allPlugins.Where(p => p.IsSelected).ToList()
                );
                launchButton.Enabled = selectedEnvironment.Plugins?.Count > 0;
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
                launchButton.Enabled = selectedEnvironment != null && selectedEnvironment.Plugins?.Count > 0;
            }
        }

        // -------- THE IMPORTANT PART --------
        // -------- THE IMPORTANT PART --------


        public void LaunchGrasshopper()
        {
            var savedAt = GhPlugins.Services.ScanReport.Save(PluginScanner.pluginItems, "after_scan");
            RhinoApp.WriteLine("[Gh Mode Manager] Scan report saved: " + savedAt);
            GhPluginBlocker.applyPluginDisable(allPlugins, selectedEnvironment);
            GhPluginBlocker.ApplyBlocking(allPlugins);

            try
            {
                // 1) Apply the environment (writes .disabled + optional .ghlink)
                //_envApplier.Apply(selectedEnvironment.Name, allPlugins);
                RhinoApp.WriteLine($"[Gh Mode Manager] Environment '{selectedEnvironment.Name}' applied.");

                // 2) Close this modal dialog FIRST so GH can show its window,
                //    then launch on the next Rhino idle tick (keep original structure).
                RhinoApp.Idle += LaunchGrasshopperOnIdle;
                this.Close();


            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("ERROR in LaunchGrasshopper: " + ex);
                MessageBox.Show(this, "Failed to launch Grasshopper. See Rhino command line for details.", "Mode Manager");
            }
            RhinoApp.Idle += LaunchGrasshopperOnIdle;
        }
         
        private void LaunchGrasshopperOnIdle(object sender, EventArgs e)
        {
            Rhino.RhinoApp.Idle -= LaunchGrasshopperOnIdle;

            try
            { 
                // Make sure GH is actually loaded (safe across R7/R8; avoids GUID overload issues)
                Rhino.RhinoApp.RunScript("-_Grasshopper _Load _Enter", false);

                // Then get the scripting object (may be null briefly)
                dynamic gh = null;
                try { gh = Rhino.RhinoApp.GetPlugInObject("Grasshopper"); } catch { /* ignore */ }

                // Try to detect/load the editor, but guard calls (some builds lack these members)
                bool editorLoaded = false;
                if (gh != null)
                {
                    try { editorLoaded = gh.IsEditorLoaded(); } catch { /* method may not exist */ }
                    if (!editorLoaded)
                    {
                        try { gh.LoadEditor(); } catch { /* command fallback below will handle it */ }
                    }
                }

                // Nudge the editor to show on next UI tick (the “flash”)
                var t = new Eto.Forms.UITimer { Interval = 0.60};
                t.Elapsed += (s2, e2) =>
                {
                    t.Stop();
                    try { gh?.ShowEditor(true); } catch {

                        
                        RhinoApp.WriteLine("Shit");/* not on all builds */ }

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
