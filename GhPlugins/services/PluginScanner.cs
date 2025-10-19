using GhPlugins.Models;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Rhino.UI.Internal.DwgOptions;

namespace GhPlugins.Services
{
    public static class PluginScanner
    {
        public static List<PluginItem> pluginItems = new List<PluginItem>();

        public static void ScanDefaultPluginFolders()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // 1) Grasshopper \ Libraries
            string userLibPath = Path.Combine(roaming, "Grasshopper", "Libraries");
            if (Directory.Exists(userLibPath))
                ScanDirectory(userLibPath);

            // 2) Grasshopper \ UserObjects  (FIX: check the correct variable)
            string userobjPath = Path.Combine(roaming, "Grasshopper", "UserObjects");
            if (Directory.Exists(userobjPath))
                ScanDirectory(userobjPath);

            // 3) Yak packages (scan the whole tree once; this is recursive)
            string yakPath = Path.Combine(roaming, "McNeel", "Rhinoceros", "packages");
            if (Directory.Exists(yakPath))
                ScanDirectory(yakPath);
        }

        // ---------- helpers ----------
        private static IEnumerable<string> GetFilesWithDisabled(string path, string ext)
        {
            // ext is like ".gha", ".ghuser", ".ghpy"
            return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(ext + ".disabled", StringComparison.OrdinalIgnoreCase));
        }

        private static string FileNameWithoutDoubleExtension(string fullPath, string ext)
        {
            // Handles ".gha.disabled" -> ".gha" then strips ".gha" to get the base name
            string p = fullPath;
            if (p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - ".disabled".Length);
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(p); // strip .gha / .ghpy / .ghuser
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        private static string GetGhpyName(string filePath)
        {
            // "MyScript.ghpy" or "MyScript.ghpy.disabled" -> "MyScript"
            return FileNameWithoutDoubleExtension(filePath, ".ghpy");
        }

        private static void ScanDirectory(string path)
        {
            // ---------- GHA (active + disabled), grouped by plugin Name ----------
            foreach (var gha in GetFilesWithDisabled(path, ".gha"))
            {
                bool wasDisabled = gha.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = gha;

                if (wasDisabled)
                {
                    // enable temporarily by removing ".disabled" (your choice was to physically rename)
                    string newPath = gha.Substring(0, gha.Length - ".disabled".Length);
                    try
                    {
                        if (File.Exists(newPath)) File.Delete(newPath);
                        File.Move(gha, newPath);
                        activePath = newPath;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"⚠️ Failed to rename disabled file '{gha}': {ex.Message}");
                        continue;
                    }
                }

                var info = GhaInfoReader.ReadPluginInfo(activePath);

                string name = info != null ? info.Name : FileNameWithoutDoubleExtension(activePath, ".gha");
                string version = info != null ? info.Version : null;

                // Find by NAME (group all installs of the same plugin)
                int idx = pluginItems.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                {
                    var item = pluginItems[idx];

                    // primary Path: keep first seen; if empty, set it
                    if (string.IsNullOrWhiteSpace(item.Path))
                        item.Path = activePath;

                    // add unique GHA path
                    if (!item.HasGhaPath(activePath))
                    {
                        

                        item.GhaPaths.Add(activePath);
                        item.Versions.Add(GhaInfoReader.ReadPluginInfo(activePath).Version);
                    }
                    // fill version if missing
            

                    // consider selected if any install is enabled (optional policy)
                    item.IsSelected = item.IsSelected || !wasDisabled;
                }
                else
                {
                    var item = new PluginItem(name, activePath);
                    item.Versions.Add(version);
                        item.IsSelected = !wasDisabled;
                    if (!item.HasGhaPath(activePath))
                        

                    pluginItems.Add(item);
                }
            }

            // ---------- GHUSER (active + disabled), associated by Name ----------
            foreach (var uo in GetFilesWithDisabled(path, ".ghuser"))
            {
                bool wasDisabled = uo.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = uo;

                if (wasDisabled)
                {
                    string newPath = uo.Substring(0, uo.Length - ".disabled".Length);
                    try
                    {
                        if (File.Exists(newPath)) File.Delete(newPath);
                        File.Move(uo, newPath);
                        activePath = newPath;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"⚠️ Failed to rename disabled file '{uo}': {ex.Message}");
                        continue;
                    }
                }

                // Try to read name from the .ghuser; fallback to file name base
                var userObjectName = PluginReader.ReadUserObject(activePath);
                if (string.IsNullOrWhiteSpace(userObjectName))
                    userObjectName = FileNameWithoutDoubleExtension(activePath, ".ghuser");

                int idx = pluginItems.FindIndex(o =>
                    o.Name.Equals(userObjectName, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                {
                    if (!pluginItems[idx].HasUserObjectPath(activePath))
                        pluginItems[idx].UserobjectPath.Add(activePath);

                    pluginItems[idx].IsSelected = pluginItems[idx].IsSelected || !wasDisabled;
                }
                else
                {
                    var orphan = new PluginItem(userObjectName, null)
                    {
                        IsSelected = !wasDisabled
                    };
                    orphan.UserobjectPath.Add(activePath);
                    pluginItems.Add(orphan);
                }
            }

            // ---------- GHPY (active + disabled), associated by Name ----------
            foreach (var ghpy in GetFilesWithDisabled(path, ".ghpy"))
            {
                bool wasDisabled = ghpy.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = ghpy;

                if (wasDisabled)
                {
                    string newPath = ghpy.Substring(0, ghpy.Length - ".disabled".Length);
                    try
                    {
                        if (File.Exists(newPath)) File.Delete(newPath);
                        File.Move(ghpy, newPath);
                        activePath = newPath;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"⚠️ Failed to rename disabled file '{ghpy}': {ex.Message}");
                        continue;
                    }
                }

                string ghpyName = GetGhpyName(activePath);

                int idx = pluginItems.FindIndex(o =>
                    o.Name.Equals(ghpyName, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                {
                    if (!pluginItems[idx].HasGhpyPath(activePath))
                        pluginItems[idx].ghpyPath.Add(activePath);

                    pluginItems[idx].IsSelected = pluginItems[idx].IsSelected || !wasDisabled;
                }
                else
                {
                    var item = new PluginItem(ghpyName, null)
                    {
                        IsSelected = !wasDisabled
                    };
                    item.ghpyPath.Add(activePath);
                    pluginItems.Add(item);
                }

                // Optional: log
                // RhinoApp.WriteLine($"[GhPy] Found: {ghpyName} ({(wasDisabled ? "Disabled" : "Enabled")})");
            }
        }
    }
}
