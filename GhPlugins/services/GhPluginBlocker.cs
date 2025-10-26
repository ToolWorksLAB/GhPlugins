using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GhPlugins.Models;
using Rhino;

namespace GhPlugins.Services
{
    public static class GhPluginBlocker
    {
        private const string DisabledSuffix = ".disabled";

        public static void applyPluginDisable(List<PluginItem> allPlugins, ModeConfig selectedEnvironment)
        {
            if (allPlugins == null || selectedEnvironment == null) return;

            var selectedNames = new HashSet<string>(
                selectedEnvironment.Plugins.Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var p in allPlugins)
                p.IsSelected = (p != null && selectedNames.Contains(p.Name));

            ExpandSelectionToFamilies(allPlugins);
        }

        public static void ApplyBlocking(List<PluginItem> allPlugins)
        {
            if (allPlugins == null) return;

            foreach (var plugin in allPlugins)
            {
                try
                {
                    bool selected = plugin.IsSelected;

                    if (plugin.GhaPaths != null)
                        foreach (var path in plugin.GhaPaths.Where(s => !string.IsNullOrWhiteSpace(s)))
                            Toggle(path, selected);

                    if (plugin.UserobjectPath != null)
                        foreach (var path in plugin.UserobjectPath.Where(s => !string.IsNullOrWhiteSpace(s)))
                            Toggle(path, selected);

                    if (plugin.ghpyPath != null)
                        foreach (var path in plugin.ghpyPath.Where(s => !string.IsNullOrWhiteSpace(s)))
                            Toggle(path, selected);
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("⚠️ Error toggling {0}: {1}", plugin != null ? plugin.Name : "<null>", ex.Message);
                }
            }
        }

        private static void Toggle(string cleanPath, bool enable)
        {
            string disabledPath = cleanPath + DisabledSuffix;

            if (enable)
            {
                if (File.Exists(disabledPath))
                {
                    if (File.Exists(cleanPath)) File.Delete(cleanPath);
                    File.Move(disabledPath, cleanPath);
                }
            }
            else
            {
                if (File.Exists(cleanPath))
                {
                    if (File.Exists(disabledPath)) File.Delete(disabledPath);
                    File.Move(cleanPath, disabledPath);
                }
            }
        }

        private static void ExpandSelectionToFamilies(List<PluginItem> all)
        {
            if (all == null || all.Count == 0) return;

            Func<string, string> Key = s =>
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                var chars = s.Where(char.IsLetterOrDigit).ToArray();
                return new string(chars).ToLowerInvariant();
            };

            // A) family-name linking
            var byKey = new Dictionary<string, List<PluginItem>>();
            foreach (var p in all)
            {
                string k = Key(p != null ? p.Name : "");
                List<PluginItem> list;
                if (!byKey.TryGetValue(k, out list))
                {
                    list = new List<PluginItem>();
                    byKey[k] = list;
                }
                list.Add(p);
            }
            foreach (var kv in byKey)
                if (kv.Value.Any(pi => pi != null && pi.IsSelected))
                    foreach (var pi in kv.Value) pi.IsSelected = true;

            // B) assembly-name affinity (GHAs)
            var asmMap = new Dictionary<string, List<PluginItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                if (p == null || p.GhaPaths == null) continue;
                foreach (var gha in p.GhaPaths.Where(File.Exists))
                {
                    try
                    {
                        var an = AssemblyName.GetAssemblyName(gha).Name;
                        List<PluginItem> list;
                        if (!asmMap.TryGetValue(an, out list))
                        {
                            list = new List<PluginItem>();
                            asmMap[an] = list;
                        }
                        if (!list.Contains(p)) list.Add(p);
                    }
                    catch { /* ignore */ }
                }
            }

            foreach (var p in all.Where(x => x != null && x.IsSelected))
            {
                bool hasGha = (p.GhaPaths != null && p.GhaPaths.Count > 0);
                bool hasUo = (p.UserobjectPath != null && p.UserobjectPath.Count > 0);
                if (!hasUo || hasGha) continue;

                string k = Key(p.Name ?? "");
                foreach (var kv in asmMap)
                {
                    string ak = Key(kv.Key);
                    if (ak == k || ak.Contains(k) || k.Contains(ak))
                        foreach (var prov in kv.Value)
                            prov.IsSelected = true;
                }
            }

            // C) Yak: if a selected user-object is in a package, pull sibling GHAs in /Components
            PullProvidersFromYakSiblings(all);

            // D) Multi-GHA package: if any GHA in a Components folder is selected, select all GHAs in that folder
            SelectAllGhasInSameComponentsFolder(all);
        }

        private static void PullProvidersFromYakSiblings(List<PluginItem> all)
        {
            if (all == null || all.Count == 0) return;

            var ghaOwner = new Dictionary<string, PluginItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                if (p == null || p.GhaPaths == null) continue;
                foreach (var g in p.GhaPaths)
                    if (!string.IsNullOrWhiteSpace(g) && !ghaOwner.ContainsKey(g))
                        ghaOwner[g] = p;
            }

            foreach (var p in all.Where(x => x != null && x.IsSelected))
            {
                var uoList = p.UserobjectPath;
                if (uoList == null || uoList.Count == 0) continue;

                foreach (var uo in uoList.Where(File.Exists))
                {
                    string uoDir = Path.GetDirectoryName(uo);
                    string grasshopperDir = !string.IsNullOrEmpty(uoDir) ? Directory.GetParent(uoDir).FullName : null;
                    if (string.IsNullOrEmpty(grasshopperDir)) continue;

                    string compDir = Path.Combine(grasshopperDir, "Components");
                    if (!Directory.Exists(compDir)) continue;

                    foreach (var gha in Directory.EnumerateFiles(compDir, "*.gha", SearchOption.AllDirectories))
                    {
                        PluginItem owner;
                        if (ghaOwner.TryGetValue(gha, out owner))
                            owner.IsSelected = true;
                    }
                }
            }
        }

        private static void SelectAllGhasInSameComponentsFolder(List<PluginItem> all)
        {
            if (all == null || all.Count == 0) return;

            var byDir = new Dictionary<string, List<PluginItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in all)
            {
                if (p == null || p.GhaPaths == null) continue;
                foreach (var gha in p.GhaPaths.Where(File.Exists))
                {
                    string dir = Path.GetDirectoryName(gha);
                    if (string.IsNullOrEmpty(dir)) continue;

                    List<PluginItem> list;
                    if (!byDir.TryGetValue(dir, out list))
                    {
                        list = new List<PluginItem>();
                        byDir[dir] = list;
                    }
                    if (!list.Contains(p)) list.Add(p);
                }
            }

            foreach (var kv in byDir)
                if (kv.Value.Any(pi => pi != null && pi.IsSelected))
                    foreach (var pi in kv.Value)
                        pi.IsSelected = true;
        }

        public static void UnblockEverything()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] roots = new string[]
            {
                Path.Combine(roaming, "Grasshopper", "Libraries"),
                Path.Combine(roaming, "Grasshopper", "UserObjects"),
                Path.Combine(roaming, "McNeel", "Rhinoceros", "packages")
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.disabled", SearchOption.AllDirectories))
                {
                    try
                    {
                        string restored = file.Substring(0, file.Length - ".disabled".Length);
                        if (File.Exists(restored)) File.Delete(restored);
                        File.Move(file, restored);
                        RhinoApp.WriteLine("✅ Restored: {0}", Path.GetFileName(restored));
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine("⚠️ Failed to restore {0}: {1}", file, ex.Message);
                    }
                }
            }
        }
    }
}
