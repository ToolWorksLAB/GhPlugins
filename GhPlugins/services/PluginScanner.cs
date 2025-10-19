using GhPlugins.Models;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GhPlugins.Services
{
    public static class PluginScanner
    {
        public static List<PluginItem> pluginItems = new List<PluginItem>();

        public static void ScanDefaultPluginFolders()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            string userLibPath = Path.Combine(roaming, "Grasshopper", "Libraries");
            if (Directory.Exists(userLibPath))
                ScanDirectory(userLibPath);

            string userobjPath = Path.Combine(roaming, "Grasshopper", "UserObjects");
            if (Directory.Exists(userobjPath))
                ScanDirectory(userobjPath);

            string yakPath = Path.Combine(roaming, "McNeel", "Rhinoceros", "packages");
            if (Directory.Exists(yakPath))
                ScanDirectory(yakPath);
        }

        // ---------- helpers ----------
        private static IEnumerable<string> GetFilesWithDisabled(string path, string ext)
        {
            return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(ext + ".disabled", StringComparison.OrdinalIgnoreCase));
        }

        private static string FileNameWithoutDoubleExtension(string fullPath, string ext)
        {
            string p = fullPath;
            if (p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - ".disabled".Length);
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(p);
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        private static string GetGhpyName(string filePath)
        {
            return FileNameWithoutDoubleExtension(filePath, ".ghpy");
        }

        private static Version ParseYakVersionFromPath(string ghaPath)
        {
            try
            {
                // ...\packages\<major>\<pkg>\<version>\file.gha -> <version>
                var dir = Path.GetDirectoryName(ghaPath);
                var verFolder = Path.GetFileName(dir);
                Version v;
                return Version.TryParse(verFolder, out v) ? v : new Version(0, 0, 0, 0);
            }
            catch { return new Version(0, 0, 0, 0); }
        }

        private static Version ParseVersionSafe(string versionString, string ghaPath)
        {
            if (!string.IsNullOrWhiteSpace(versionString))
            {
                Version v;
                if (Version.TryParse(versionString, out v))
                    return v;
            }
            return ParseYakVersionFromPath(ghaPath);
        }

        private static void EnsureParallelAdd(PluginItem item, string path, string versionStr)
        {
            if (!item.HasGhaPath(path))
            {
                item.GhaPaths.Add(path);
                item.Versions.Add(versionStr ?? "");
            }
        }

        private static void MaybeUpdateActiveIndexToNewest(PluginItem item)
        {
            if (item == null || item.GhaPaths == null || item.GhaPaths.Count == 0) return;

            int currentMajor = RhinoApp.ExeVersion; // 7 or 8

            var bestIdx = 0;
            Version bestVer = ParseVersionSafe(item.Versions.Count > 0 ? item.Versions[0] : null, item.GhaPaths[0]);
            bool bestYakCurrent = item.GhaPaths[0].IndexOf("\\packages\\" + currentMajor + ".", StringComparison.OrdinalIgnoreCase) >= 0;

            for (int i = 1; i < item.GhaPaths.Count; i++)
            {
                var p = item.GhaPaths[i];
                var vStr = (i < item.Versions.Count) ? item.Versions[i] : null;
                var v = ParseVersionSafe(vStr, p);
                bool yakCurr = p.IndexOf("\\packages\\" + currentMajor + ".", StringComparison.OrdinalIgnoreCase) >= 0;

                // Newer version OR same version but Yak-current-major wins
                if (v > bestVer || (v == bestVer && yakCurr && !bestYakCurrent))
                {
                    bestVer = v;
                    bestYakCurrent = yakCurr;
                    bestIdx = i;
                }
            }

            // Set default if not set, or move to newest by policy
            if (item.ActiveVersionIndex < 0 || item.ActiveVersionIndex >= item.GhaPaths.Count)
                item.ActiveVersionIndex = bestIdx;

            // Keep Path in sync with ActiveVersionIndex
            
                
        }

        private static void ScanDirectory(string path)
        {
            // ---------- GHA ----------
            foreach (var gha in GetFilesWithDisabled(path, ".gha"))
            {
                bool wasDisabled = gha.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = gha;

                if (wasDisabled)
                {
                    // If you switch to an unloadable loader, remove this rename.
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

                int idx = pluginItems.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    var item = pluginItems[idx];

                    EnsureParallelAdd(item, activePath, version);
                    MaybeUpdateActiveIndexToNewest(item);

                    // If any install is enabled, consider plugin enabled
                    item.IsSelected = item.IsSelected || !wasDisabled;
                }
                else
                {
                    var item = new PluginItem(name)
                    {
                        IsSelected = !wasDisabled
                    };
                    EnsureParallelAdd(item, activePath, version);
                    MaybeUpdateActiveIndexToNewest(item);
                    pluginItems.Add(item);
                }
            }

            // ---------- GHUSER ----------
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
                    var orphan = new PluginItem(userObjectName)
                    {
                        IsSelected = !wasDisabled
                    };
                    orphan.UserobjectPath.Add(activePath);
                    pluginItems.Add(orphan);
                }
            }

            // ---------- GHPY ----------
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
                    var item = new PluginItem(ghpyName)
                    {
                        IsSelected = !wasDisabled
                    };
                    item.ghpyPath.Add(activePath);
                    pluginItems.Add(item);
                }
            }
        }
    }
}
