// File: Services/GhPluginBlocker.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GhPlugins.Models;
using Rhino;

namespace GhPlugins.Services
{
    public static class GhPluginBlocker
    {
        private const string DisabledSuffix = ".disabled";

        public static void applyPluginDisable(List<PluginItem> allPlugins, ModeConfig mf)
        {
                foreach (var plugin in allPlugins)
            {
                if (mf.Plugins.Contains(plugin))
                {
                    plugin.IsSelected = true;
                }
                else
                {
                    plugin.IsSelected = false;
                }
            }
        }
            // Public entry point
            public static void ApplyBlocking(List<PluginItem> allPlugins)
        {
            if (allPlugins == null || allPlugins.Count == 0)
            {
                RhinoApp.WriteLine("[Gh Mode Manager] No plugins to process.");
                return;
            }

            foreach (var p in allPlugins)
            {
                try
                {
                    if (p == null) continue;

                    RhinoApp.WriteLine($"[Gh Mode Manager] Processing: {p.Name} (Selected={p.IsSelected}, ActiveIndex={p.ActiveVersionIndex})");

                    if (!p.IsSelected)
                    {
                        // Block everything
                        BlockMany(p.GhaPaths, ".gha");
                        BlockMany(p.UserobjectPath, ".ghuser");
                        BlockMany(p.ghpyPath, ".ghpy");
                    }
                    else
                    {
                        // GHA: unblock only ActiveVersionIndex; block the rest
                        if (p.GhaPaths != null && p.GhaPaths.Count > 0)
                        {
                            if (p.ActiveVersionIndex < 0 || p.ActiveVersionIndex >= p.GhaPaths.Count)
                            {
                                RhinoApp.WriteLine($"[Gh Mode Manager] ⚠️ {p.Name}: ActiveVersionIndex out of range → blocking all GHAs.");
                                BlockMany(p.GhaPaths, ".gha");
                            }
                            else
                            {
                                for (int i = 0; i < p.GhaPaths.Count; i++)
                                {
                                    var path = p.GhaPaths[i];
                                    if (string.IsNullOrWhiteSpace(path)) continue;

                                    if (i == p.ActiveVersionIndex)
                                        EnsureUnblocked(path, ".gha");
                                    else
                                        EnsureBlocked(path, ".gha");
                                }
                            }
                        }

                        // User objects & GHPY: always unblock all when selected
                        UnblockMany(p.UserobjectPath, ".ghuser");
                        UnblockMany(p.ghpyPath, ".ghpy");
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Gh Mode Manager] ❌ Error while processing '{p?.Name}': {ex.Message}");
                }
            }
        }

        // ---------- Helpers ----------
        private static void BlockMany(IEnumerable<string> paths, string expectedExt)
        {
            if (paths == null) return;
            foreach (var raw in paths.Where(s => !string.IsNullOrWhiteSpace(s)))
                EnsureBlocked(raw, expectedExt);
        }

        private static void UnblockMany(IEnumerable<string> paths, string expectedExt)
        {
            if (paths == null) return;
            foreach (var raw in paths.Where(s => !string.IsNullOrWhiteSpace(s)))
                EnsureUnblocked(raw, expectedExt);
        }

        private static bool EnsureBlocked(string originalPath, string expectedExt)
        {
            var (clean, disabled) = NormalizePaths(originalPath, expectedExt);
            try
            {
                // Already blocked?
                if (!File.Exists(clean) && File.Exists(disabled))
                {
                    RhinoApp.WriteLine($"[Gh Mode Manager] • Already blocked: {Path.GetFileName(originalPath)}");
                    return true;
                }

                // If the clean file exists, rename to .disabled
                if (File.Exists(clean))
                {
                    SafeMove(clean, disabled, $"block {Path.GetFileName(clean)}");
                    return true;
                }

                // Neither exists — nothing to do
                RhinoApp.WriteLine($"[Gh Mode Manager] • Skip (missing): {Path.GetFileName(originalPath)}");
                return false;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Gh Mode Manager] ⚠️ Block failed ({Path.GetFileName(originalPath)}): {ex.Message}");
                return false;
            }
        }

        private static bool EnsureUnblocked(string originalPath, string expectedExt)
        {
            var (clean, disabled) = NormalizePaths(originalPath, expectedExt);
            try
            {
                // Already unblocked?
                if (File.Exists(clean))
                {
                    RhinoApp.WriteLine($"[Gh Mode Manager] • Already unblocked: {Path.GetFileName(originalPath)}");
                    return true;
                }

                // If disabled exists, rename back
                if (File.Exists(disabled))
                {
                    SafeMove(disabled, clean, $"unblock {Path.GetFileName(clean)}");
                    return true;
                }

                // Neither exists — nothing to do
                RhinoApp.WriteLine($"[Gh Mode Manager] • Skip (missing): {Path.GetFileName(originalPath)}");
                return false;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Gh Mode Manager] ⚠️ Unblock failed ({Path.GetFileName(originalPath)}): {ex.Message}");
                return false;
            }
        }

        private static (string clean, string disabled) NormalizePaths(string input, string expectedExt)
        {
            var p = input.Trim();

            // If list contains a .disabled path, derive the clean path by removing suffix.
            if (p.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var clean = p.Substring(0, p.Length - DisabledSuffix.Length);
                return (clean, p);
            }

            // Ensure expected extension ends with ".", handle weird cases
            if (!string.IsNullOrEmpty(expectedExt) &&
                !p.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase) &&
                !p.EndsWith(expectedExt + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Don’t mutate file name if caller passes a non-matching path;
                // we still block/unblock by adding/removing .disabled from the given path.
            }

            return (p, p + DisabledSuffix);
        }

        private static void SafeMove(string src, string dst, string actionLabel)
        {
            try
            {
                // Ensure target directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(dst) ?? Path.GetTempPath());

                // If a stale target exists (rare), try to remove it
                if (File.Exists(dst))
                {
                    File.SetAttributes(dst, FileAttributes.Normal);
                    File.Delete(dst);
                }

                File.Move(src, dst);
                RhinoApp.WriteLine($"[Gh Mode Manager] • {actionLabel}");
            }
            catch (IOException ioEx)
            {
                // Common cause: file is in use (e.g., Rhino/Grasshopper already loaded it)
                RhinoApp.WriteLine($"[Gh Mode Manager] ⚠️ Move failed (in use?) {Path.GetFileName(src)} → {Path.GetFileName(dst)}: {ioEx.Message}");
            }
        }
    }
}
