using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;

namespace GhPlugins.Services
{
    /// <summary>
    /// Applies a selected Grasshopper plugin set by:
    ///  - writing .no6/.no7/.no8 sidecars for all non-selected GHAs
    ///  - removing .no6/.no7/.no8 for selected GHAs
    ///  - writing a .ghlink for extra selected folders (optional)
    ///  - recording everything in a manifest so we can revert cleanly
    /// </summary>
    public class GhEnvironmentApplier
    {
        readonly string _appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhPlugins_ModeManager");

        // ---------------- paths ----------------

        string DefaultLibraries() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Grasshopper", "Libraries");

        IEnumerable<string> YakRoots()
        {
            // support both 7.0 and 8.0 side-by-side installs
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var major in new[] { "7.0", "8.0" })
                yield return Path.Combine(appData, "McNeel", "Rhinoceros", major, "packages");
        }

        IEnumerable<string> ExistingGhlinkTargets()
        {
            var libs = DefaultLibraries();
            if (!Directory.Exists(libs)) yield break;

            foreach (var ghlink in Directory.EnumerateFiles(libs, "*.ghlink", SearchOption.TopDirectoryOnly))
            {
                foreach (var line in File.ReadAllLines(ghlink))
                {
                    var d = line.Trim();
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    if (Directory.Exists(d)) yield return d;
                }
            }
        }

        IEnumerable<string> AllInstalledGhas(IEnumerable<string> extraProbeDirs)
        {
            var roots = new List<string> { DefaultLibraries() };
            roots.AddRange(YakRoots());
            roots.AddRange(ExistingGhlinkTargets());

            if (extraProbeDirs != null)
                roots.AddRange(extraProbeDirs.Where(Directory.Exists));

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots.Distinct())
            {
                if (!Directory.Exists(root)) continue;

                foreach (var f in Directory.EnumerateFiles(root, "*.gha", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("._")) continue; // ignore macOS resource-fork doubles
                    set.Add(Path.GetFullPath(f));
                }
            }
            return set;
        }

        // ---------------- manifest ----------------

        string ManifestPath(string envName)
        {
            Directory.CreateDirectory(_appRoot);
            return Path.Combine(_appRoot, $"{Sanitize(envName)}.manifest.txt");
        }

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        public void Revert(string envName)
        {
            var manifest = ManifestPath(envName);
            if (!File.Exists(manifest)) return;

            foreach (var line in File.ReadAllLines(manifest))
            {
                var path = line.Trim();
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { /* ignore */ }
            }

            try { File.Delete(manifest); } catch { /* ignore */ }
        }

        // ---------------- apply ----------------

        public void Apply(string envName, IEnumerable<string> selectedGhaPaths)
        {
            // C# 7.3: no '??='
            if (selectedGhaPaths == null)
                selectedGhaPaths = Enumerable.Empty<string>();

            // Clean slate for this env name
            Revert(envName);

            var manifest = new List<string>();
            var blockExts = new[] { ".no6", ".no7", ".no8" };

            // Normalize selections
            var selected = new HashSet<string>(
                selectedGhaPaths.Where(File.Exists).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);

            // Probe neighbors of selected + standard roots + ghlink targets
            var probeDirs = selected
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var allInstalled = AllInstalledGhas(probeDirs);

            // 1) Block every non-selected GHA by writing .no6/.no7/.no8
            int blockedCount = 0;
            foreach (var gha in allInstalled)
            {
                if (selected.Contains(gha)) continue;

                foreach (var ext in blockExts)
                {
                    var blockPath = Path.ChangeExtension(gha, ext);
                    if (!File.Exists(blockPath))
                    {
                        File.WriteAllText(blockPath, "// Created by GhPlugins Mode Manager");
                        manifest.Add(blockPath);
                    }
                }
                blockedCount++;
            }

            // 2) Ensure selected GHAs are unblocked (remove any existing .no6/.no7/.no8)
            foreach (var gha in selected)
            {
                foreach (var ext in blockExts)
                {
                    var s = Path.ChangeExtension(gha, ext);
                    if (File.Exists(s))
                    {
                        try { File.Delete(s); } catch { /* ignore */ }
                    }
                }
            }

            // 3) If any selected plugin lives outside default probe paths and Yak, add a .ghlink
            var defaultLib = DefaultLibraries();
            Directory.CreateDirectory(defaultLib);

            // Build Yak roots (7.0 & 8.0) without newer language features
            var yakRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var y in YakRoots()) yakRoots.Add(y);

            var extraDirs = selected
                .Select(Path.GetDirectoryName)
                .Where(d =>
                    !string.IsNullOrWhiteSpace(d) &&
                    !d.StartsWith(defaultLib, StringComparison.OrdinalIgnoreCase) &&
                    !yakRoots.Any(y => d.StartsWith(y, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (extraDirs.Count > 0)
            {
                var linkPath = Path.Combine(defaultLib, "GhPlugins_" + Sanitize(envName) + ".ghlink");
                File.WriteAllLines(linkPath, extraDirs);
                manifest.Add(linkPath);
            }

            // 4) Persist manifest so we can revert later
            File.WriteAllLines(ManifestPath(envName), manifest);

            // 5) Helpful proof in Rhino's command line
            Rhino.RhinoApp.WriteLine(
                "[Gh Mode Manager] Kept {0} plugin(s), blocked {1} plugin(s). {2}",
                selected.Count, blockedCount,
                extraDirs.Count > 0 ? ("Linked " + extraDirs.Count + " extra folder(s).") : "No extra folders.");
            Rhino.RhinoApp.WriteLine("[Gh Mode Manager] Libraries: " + defaultLib);
            foreach (var y in yakRoots) Rhino.RhinoApp.WriteLine("[Gh Mode Manager] Yak: " + y);
        }

    }
}
