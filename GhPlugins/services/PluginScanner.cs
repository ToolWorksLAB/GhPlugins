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
            // Always start fresh
            pluginItems.Clear();

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            string userLibPath = Path.Combine(roaming, "Grasshopper", "Libraries");
            if (Directory.Exists(userLibPath))
                ScanDirectory(userLibPath);

            string userobjPath = Path.Combine(roaming, "Grasshopper", "UserObjects");
            if (Directory.Exists(userobjPath))
                ScanDirectory(userobjPath);

            // Yak (7 + 8 trees)
            string yakRoot = Path.Combine(roaming, "McNeel", "Rhinoceros", "packages");
            if (Directory.Exists(yakRoot))
                ScanDirectory(yakRoot);
        }

        // ---------- helpers ----------
        private static IEnumerable<string> GetFilesWithDisabled(string path, string ext)
        {
            return Directory
                .EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                    f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
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

        private static string RemoveDisabledSuffix(string path)
        {
            return path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(0, path.Length - ".disabled".Length)
                : path;
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
                string dir = Path.GetDirectoryName(ghaPath);
                string verFolder = Path.GetFileName(dir);
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

            int bestIdx = 0;
            Version bestVer = ParseVersionSafe(item.Versions.Count > 0 ? item.Versions[0] : null, item.GhaPaths[0]);
            bool bestYakCurrent = item.GhaPaths[0].IndexOf("\\packages\\" + currentMajor + ".", StringComparison.OrdinalIgnoreCase) >= 0;

            for (int i = 1; i < item.GhaPaths.Count; i++)
            {
                string p = item.GhaPaths[i];
                string vStr = (i < item.Versions.Count) ? item.Versions[i] : null;
                Version v = ParseVersionSafe(vStr, p);
                bool yakCurr = p.IndexOf("\\packages\\" + currentMajor + ".", StringComparison.OrdinalIgnoreCase) >= 0;

                if (v > bestVer || (v == bestVer && yakCurr && !bestYakCurrent))
                {
                    bestVer = v;
                    bestYakCurrent = yakCurr;
                    bestIdx = i;
                }
            }

            if (item.ActiveVersionIndex < 0 || item.ActiveVersionIndex >= item.GhaPaths.Count)
                item.ActiveVersionIndex = bestIdx;
        }

        private static void ScanDirectory(string path)
        {
            // ---------- GHA ----------
            foreach (string gha in GetFilesWithDisabled(path, ".gha"))
            {
                bool wasDisabled = gha.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string cleanPath = RemoveDisabledSuffix(gha);

                string name;
                string version = null;

                if (File.Exists(cleanPath))
                {
                    var info = GhaInfoReader.ReadPluginInfo(cleanPath);
                    name = (info != null && !string.IsNullOrWhiteSpace(info.Name))
                        ? info.Name
                        : FileNameWithoutDoubleExtension(gha, ".gha");
                    if (info != null) version = info.Version;
                }
                else
                {
                    name = FileNameWithoutDoubleExtension(gha, ".gha");
                }

                int idx = pluginItems.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    PluginItem item = pluginItems[idx];
                    EnsureParallelAdd(item, cleanPath, version);
                    MaybeUpdateActiveIndexToNewest(item);
                    item.IsSelected = item.IsSelected || (!wasDisabled && File.Exists(cleanPath));
                }
                else
                {
                    PluginItem item = new PluginItem(name)
                    {
                        IsSelected = (!wasDisabled && File.Exists(cleanPath))
                    };
                    EnsureParallelAdd(item, cleanPath, version);
                    MaybeUpdateActiveIndexToNewest(item);
                    pluginItems.Add(item);
                }
            }

            // ---------- GHUSER ----------
            foreach (string uo in GetFilesWithDisabled(path, ".ghuser"))
            {
                bool wasDisabled = uo.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string cleanPath = RemoveDisabledSuffix(uo);

                string userObjectName = PluginReader.ReadUserObject(File.Exists(cleanPath) ? cleanPath : uo);
                if (string.IsNullOrWhiteSpace(userObjectName))
                    userObjectName = FileNameWithoutDoubleExtension(uo, ".ghuser");

                int idx = pluginItems.FindIndex(o => o.Name.Equals(userObjectName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    if (!pluginItems[idx].HasUserObjectPath(cleanPath))
                        pluginItems[idx].UserobjectPath.Add(cleanPath);

                    pluginItems[idx].IsSelected = pluginItems[idx].IsSelected || (!wasDisabled && File.Exists(cleanPath));
                }
                else
                {
                    PluginItem orphan = new PluginItem(userObjectName)
                    {
                        IsSelected = (!wasDisabled && File.Exists(cleanPath))
                    };
                    orphan.UserobjectPath.Add(cleanPath);
                    pluginItems.Add(orphan);
                }
            }

            // ---------- GHPY ----------
            foreach (string ghpy in GetFilesWithDisabled(path, ".ghpy"))
            {
                bool wasDisabled = ghpy.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string cleanPath = RemoveDisabledSuffix(ghpy);

                string ghpyName = GetGhpyName(ghpy);

                int idx = pluginItems.FindIndex(o => o.Name.Equals(ghpyName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    if (!pluginItems[idx].HasGhpyPath(cleanPath))
                        pluginItems[idx].ghpyPath.Add(cleanPath);

                    pluginItems[idx].IsSelected = pluginItems[idx].IsSelected || (!wasDisabled && File.Exists(cleanPath));
                }
                else
                {
                    PluginItem item = new PluginItem(ghpyName)
                    {
                        IsSelected = (!wasDisabled && File.Exists(cleanPath))
                    };
                    item.ghpyPath.Add(cleanPath);
                    pluginItems.Add(item);
                }
            }
        }
    }
}
