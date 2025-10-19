// File: Services/GhEnvironmentApplier.cs (C# 7.3 compatible, now with .ghpy support)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GhPlugins.Models; // PluginItem: Name, Path, IsSelected, List<string> UserobjectPath, List<string> ghpyPath
using Rhino;

namespace GhPlugins.Services
{
    public class GhEnvironmentApplier
    {
        private readonly string _appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhPlugins_ModeManager");

        private static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
        private const string DisabledSuffix = ".disabled";

        private string DefaultLibraries()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Grasshopper", "Libraries");
        }

        private string DefaultUserObjects()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Grasshopper", "UserObjects");
        }

        private IEnumerable<string> YakRoots()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var baseRhi = Path.Combine(appData, "McNeel", "Rhinoceros");
            foreach (var major in new[] { "7.0", "8.0" })
            {
                var a = Path.Combine(baseRhi, major, "packages");
                var b = Path.Combine(baseRhi, "packages", major);
                if (Directory.Exists(a)) yield return a;
                if (Directory.Exists(b)) yield return b;
            }
        }

        private IEnumerable<string> ExistingGhlinkTargets(string baseFolder)
        {
            if (!Directory.Exists(baseFolder)) yield break;

            foreach (var ghlink in Directory.EnumerateFiles(baseFolder, "*.ghlink", SearchOption.TopDirectoryOnly))
            {
                foreach (var raw in File.ReadAllLines(ghlink))
                {
                    var d = (raw ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    if (d.StartsWith("#", StringComparison.Ordinal)) continue; // allow comments
                    if (Directory.Exists(d)) yield return d;
                }
            }
        }

        private string ManifestPath(string envName)
        {
            Directory.CreateDirectory(_appRoot);
            return Path.Combine(_appRoot, Sanitize(envName) + ".manifest.txt");
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "_");
            return name;
        }

        private static string StripDisabledSuffix(string path)
        {
            if (path != null && path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                return path.Substring(0, path.Length - DisabledSuffix.Length);
            return path;
        }

        /// <summary>Revert file operations recorded in the manifest (best-effort).</summary>
        public void Revert(string envName)
        {
            var path = ManifestPath(envName);
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                var op = parts[0];
                try
                {
                    if (op == "REN" && parts.Length == 3)
                    {
                        var from = parts[1];
                        var to = parts[2];
                        if (File.Exists(to))
                        {
                            if (File.Exists(from)) File.Delete(from);
                            File.Move(to, from);
                        }
                    }
                    else if (op == "DEL" && parts.Length == 2)
                    {
                        var f = parts[1];
                        if (File.Exists(f)) File.Delete(f);
                    }
                }
                catch
                {
                    // ignore individual failures
                }
            }

            try { File.Delete(path); } catch { /* ignore */ }
        }

        /// <summary>
        /// Applies the selected environment: renames GHAs/GHPYs/UserObjects and writes .ghlink files.
        /// </summary>
        public void Apply(string envName, IEnumerable<PluginItem> items)
        {
            items = items ?? Enumerable.Empty<PluginItem>();

            // ---- Normalize base paths (strip ".disabled" when present) ----
            Func<string, string> BaseGha = p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return p;
                if (p.EndsWith(".gha" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)) p = StripDisabledSuffix(p);
                return p.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(p) : null;
            };

            Func<string, string> BaseGhpy = p =>
            {
                if (string.IsNullOrWhiteSpace(p)) return p;
                if (p.EndsWith(".ghpy" + DisabledSuffix, StringComparison.OrdinalIgnoreCase)) p = StripDisabledSuffix(p);
                return p.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(p) : null;
            };

            // --- All GHAs referenced by items (include disabled ones; do NOT filter by File.Exists)
            var allGhas = items
                .Select(i => i != null ? i.Path : null)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => p.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".gha" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(BaseGha)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(CI)
                .ToList();

            // --- Selected GHAs (by IsSelected)
            var selectedGhas = new HashSet<string>(
                items.Where(i => i != null && i.IsSelected)
                     .Select(i => i.Path)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Where(p => p.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".gha" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                     .Select(BaseGha)
                     .Where(p => !string.IsNullOrWhiteSpace(p)),
                CI);

            // --- All GHPYs from items
            var allGhpy = items
                .SelectMany(i => i != null ? (i.ghpyPath ?? Enumerable.Empty<string>()) : Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => p.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".ghpy" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(BaseGhpy)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(CI)
                .ToList();

            // --- Selected GHPYs (only from selected items)
            var selectedGhpy = new HashSet<string>(
                items.Where(i => i != null && i.IsSelected)
                     .SelectMany(i => i.ghpyPath ?? Enumerable.Empty<string>())
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Where(p => p.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".ghpy" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                     .Select(BaseGhpy)
                     .Where(p => !string.IsNullOrWhiteSpace(p)),
                CI);

            // --- Selected UserObject parent directories (convert from FILE paths)
            var selectedUserObjDirs = items
                .Where(i => i != null && i.IsSelected)
                .SelectMany(i => i.UserobjectPath ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f =>
                {
                    var dir = Path.GetDirectoryName(f);
                    return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir);
                })
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(CI)
                .ToList();

            // Start clean for this env
            Revert(envName);

            var manifest = new List<string>();
            var defaultLib = DefaultLibraries();
            var defaultUO = DefaultUserObjects();
            Directory.CreateDirectory(defaultLib);
            Directory.CreateDirectory(defaultUO);

            // --------- GHAs: block/unblock by rename ---------
            int blockedGha = 0, unblockedGha = 0;

            foreach (var ghaBase in allGhas)
            {
                var normal = ghaBase;                      // *.gha
                var disabled = ghaBase + DisabledSuffix;   // *.gha.disabled
                var isSelected = selectedGhas.Contains(ghaBase);

                try
                {
                    if (!isSelected)
                    {
                        if (File.Exists(normal) && !File.Exists(disabled))
                        {
                            File.Move(normal, disabled);
                            manifest.Add("REN\t" + normal + "\t" + disabled);
                            blockedGha++;
                        }
                    }
                    else
                    {
                        if (!File.Exists(normal) && File.Exists(disabled))
                        {
                            File.Move(disabled, normal);
                            manifest.Add("REN\t" + disabled + "\t" + normal);
                            unblockedGha++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("[Gh Mode Manager] ⚠️ GHA rename failed ({0}): {1}", ghaBase, ex.Message);
                }
            }

            // --------- GHPYs: block/unblock by rename (same logic as GHAs) ---------
            int blockedGhpy = 0, unblockedGhpy = 0;

            foreach (var ghpyBase in allGhpy)
            {
                var normal = ghpyBase;                      // *.ghpy
                var disabled = ghpyBase + DisabledSuffix;   // *.ghpy.disabled
                var isSelected = selectedGhpy.Contains(ghpyBase);

                try
                {
                    if (!isSelected)
                    {
                        if (File.Exists(normal) && !File.Exists(disabled))
                        {
                            File.Move(normal, disabled);
                            manifest.Add("REN\t" + normal + "\t" + disabled);
                            blockedGhpy++;
                        }
                    }
                    else
                    {
                        if (!File.Exists(normal) && File.Exists(disabled))
                        {
                            File.Move(disabled, normal);
                            manifest.Add("REN\t" + disabled + "\t" + normal);
                            unblockedGhpy++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("[Gh Mode Manager] ⚠️ GHPY rename failed ({0}): {1}", ghpyBase, ex.Message);
                }
            }

            // --------- UserObjects: block/unblock by rename ---------
            // Build universe of UO files: default UO + ghlink targets + selected UO dirs
            var uoRoots = new HashSet<string>(CI) { defaultUO };
            foreach (var t in ExistingGhlinkTargets(defaultUO)) uoRoots.Add(t);
            foreach (var d in selectedUserObjDirs) uoRoots.Add(d);

            var allGhusers = new HashSet<string>(CI);
            foreach (var root in uoRoots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var f in Directory.EnumerateFiles(root, "*.ghuser", SearchOption.AllDirectories))
                    allGhusers.Add(Path.GetFullPath(f));

                foreach (var f in Directory.EnumerateFiles(root, "*.ghuser" + DisabledSuffix, SearchOption.AllDirectories))
                    allGhusers.Add(Path.GetFullPath(f)); // include disabled so we can unblock
            }

            int blockedUO = 0, unblockedUO = 0;

            foreach (var f in allGhusers)
            {
                var isDisabled = f.EndsWith(".ghuser" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                var normal = isDisabled ? StripDisabledSuffix(f) : f;   // *.ghuser
                var disabled = normal + DisabledSuffix;                 // *.ghuser.disabled

                // Selected if its parent directory is under any selectedUserObjDirs
                var parent = Path.GetDirectoryName(normal) ?? string.Empty;
                var underSelectedFolder = selectedUserObjDirs.Any(sel => parent.StartsWith(sel, StringComparison.OrdinalIgnoreCase));

                try
                {
                    if (!underSelectedFolder)
                    {
                        if (File.Exists(normal) && !File.Exists(disabled))
                        {
                            File.Move(normal, disabled);
                            manifest.Add("REN\t" + normal + "\t" + disabled);
                            blockedUO++;
                        }
                    }
                    else
                    {
                        if (!File.Exists(normal) && File.Exists(disabled))
                        {
                            File.Move(disabled, normal);
                            manifest.Add("REN\t" + disabled + "\t" + normal);
                            unblockedUO++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine("[Gh Mode Manager] ⚠️ UserObject rename failed ({0}): {1}", f, ex.Message);
                }
            }

            // --------- .ghlink for extra plugin & userobject folders ---------
            var yakRoots = new HashSet<string>(YakRoots(), CI);

            // Include dirs of selected GHAs and selected GHPYs
            var extraPluginDirs = new HashSet<string>(CI);

            foreach (var s in selectedGhas)
            {
                var d = Path.GetDirectoryName(s);
                if (!string.IsNullOrWhiteSpace(d)) extraPluginDirs.Add(d);
            }

            foreach (var s in selectedGhpy)
            {
                var d = Path.GetDirectoryName(s);
                if (!string.IsNullOrWhiteSpace(d)) extraPluginDirs.Add(d);
            }

            // Filter out default Libraries and Yak roots
            extraPluginDirs = new HashSet<string>(
                extraPluginDirs.Where(d =>
                    !d.StartsWith(DefaultLibraries(), StringComparison.OrdinalIgnoreCase) &&
                    !yakRoots.Any(y => d.StartsWith(y, StringComparison.OrdinalIgnoreCase))),
                CI);

            if (extraPluginDirs.Count > 0)
            {
                var linkPath = Path.Combine(DefaultLibraries(), "GhPlugins_" + Sanitize(envName) + ".ghlink");
                File.WriteAllLines(linkPath, extraPluginDirs.ToArray());
                manifest.Add("DEL\t" + linkPath); // delete on revert
            }

            var extraUserObjDirs = selectedUserObjDirs
                .Where(d => !d.StartsWith(DefaultUserObjects(), StringComparison.OrdinalIgnoreCase))
                .Distinct(CI)
                .ToList();

            if (extraUserObjDirs.Count > 0)
            {
                var uoLinkPath = Path.Combine(DefaultUserObjects(), "GhUserObjects_" + Sanitize(envName) + ".ghlink");
                File.WriteAllLines(uoLinkPath, extraUserObjDirs);
                manifest.Add("DEL\t" + uoLinkPath); // delete on revert
            }

            // --------- Save manifest ---------
            File.WriteAllLines(ManifestPath(envName), manifest);

            // (Optional) summary logs
            // RhinoApp.WriteLine("[Gh Mode Manager] GHAs: blocked {0}, unblocked {1}.", blockedGha, unblockedGha);
            // RhinoApp.WriteLine("[Gh Mode Manager] GHPYs: blocked {0}, unblocked {1}.", blockedGhpy, unblockedGhpy);
            // RhinoApp.WriteLine("[Gh Mode Manager] UserObjects: blocked {0}, unblocked {1}.", blockedUO, unblockedUO);
        }
    }
}
