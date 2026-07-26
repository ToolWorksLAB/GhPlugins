using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;
using Sieve.Models;

namespace Sieve.Services
{
    public static class PluginGate
    {
        public static IReadOnlyList<string> ApplySelection(IEnumerable<PluginCandidate> candidates)
        {
            var messages = new List<string>();
            var settings = SettingsStore.Load();
            var disabledPaths = new HashSet<string>(settings.DisabledPaths, StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (!PluginPolicy.IsManageablePath(candidate.OriginalPath))
                    continue;

                var disabledPath = GetDisabledPath(candidate.OriginalPath);

                try
                {
                    if (candidate.Load)
                    {
                        if (File.Exists(disabledPath) && !File.Exists(candidate.OriginalPath))
                            File.Move(disabledPath, candidate.OriginalPath);
                        disabledPaths.Remove(candidate.OriginalPath);
                        candidate.IsDisabled = false;
                        candidate.CurrentPath = candidate.OriginalPath;
                    }
                    else
                    {
                        if (File.Exists(candidate.OriginalPath) && !File.Exists(disabledPath))
                            File.Move(candidate.OriginalPath, disabledPath);
                        disabledPaths.Add(candidate.OriginalPath);
                        candidate.IsDisabled = true;
                        candidate.CurrentPath = disabledPath;
                    }
                }
                catch (Exception ex)
                {
                    messages.Add($"{candidate.Name}: {ex.Message}");
                }
            }

            settings.DisabledPaths = disabledPaths
                .Where(PluginPolicy.IsManageablePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SettingsStore.Save(settings);
            return messages;
        }

        public static IReadOnlyList<string> RestoreAllDisabled()
        {
            var settings = SettingsStore.Load();
            var messages = new List<string>();

            foreach (var originalPath in settings.DisabledPaths.ToList())
            {
                try
                {
                    var disabledPath = GetDisabledPath(originalPath);
                    if (File.Exists(disabledPath) && !File.Exists(originalPath))
                        File.Move(disabledPath, originalPath);
                }
                catch (Exception ex)
                {
                    messages.Add($"{Path.GetFileNameWithoutExtension(originalPath)}: {ex.Message}");
                }
            }

            if (messages.Count == 0)
            {
                settings.DisabledPaths.Clear();
                SettingsStore.Save(settings);
            }

            return messages;
        }

        public static IReadOnlyList<string> RestoreUnsupportedDisabled(SieveSettings settings)
        {
            var messages = new List<string>();
            var unsupported = settings.DisabledPaths
                .Where(PluginPolicy.IsUnsupportedManagedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var originalPath in unsupported)
            {
                try
                {
                    var disabledPath = GetDisabledPath(originalPath);
                    if (File.Exists(disabledPath) && !File.Exists(originalPath))
                        File.Move(disabledPath, originalPath);
                }
                catch (Exception ex)
                {
                    messages.Add($"{Path.GetFileNameWithoutExtension(originalPath)}: {ex.Message}");
                }
            }

            settings.DisabledPaths = settings.DisabledPaths
                .Where(PluginPolicy.IsManageablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return messages;
        }

        public static string QueueGrasshopperLaunch(string label, int pluginCount)
        {
            var loadError = EnsureGrasshopperPluginLoaded();
            if (!string.IsNullOrWhiteSpace(loadError))
                return loadError;

            EventHandler idleHandler = null;
            idleHandler = (_, _) =>
            {
                RhinoApp.Idle -= idleHandler;
                if (!RhinoApp.RunScript("_Grasshopper", false))
                    RhinoApp.WriteLine("Sieve: Rhino could not queue the Grasshopper command.");
                else
                    LaunchTracker.WatchForCanvas(label, pluginCount);
            };
            RhinoApp.Idle += idleHandler;
            return string.Empty;
        }

        private static string EnsureGrasshopperPluginLoaded()
        {
            var pluginPath = FindGrasshopperPluginPath();
            if (string.IsNullOrWhiteSpace(pluginPath))
                return "Sieve could not find Rhino's built-in GrasshopperPlugin.rhp. Repair the Rhino installation, then try again.";

            try
            {
                var pluginId = PlugIn.IdFromPath(pluginPath);
                if (pluginId == Guid.Empty)
                {
                    PlugIn.LoadPlugIn(pluginPath, out pluginId);
                    if (pluginId == Guid.Empty)
                        return "Rhino could not register its built-in Grasshopper plugin. Repair the Rhino installation, then try again.";
                }

                if (!PlugIn.LoadPlugIn(pluginId, true, true))
                    return "Rhino could not load its built-in Grasshopper plugin. Repair the Rhino installation, then try again.";
            }
            catch (Exception ex)
            {
                return "Rhino could not load Grasshopper: " + ex.Message;
            }

            return string.Empty;
        }

        private static string FindGrasshopperPluginPath()
        {
            var paths = new List<string>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(programFiles))
                    paths.Add(Path.Combine(programFiles, "Rhino 8", "Plug-ins", "Grasshopper", "GrasshopperPlugin.rhp"));
            }
            else
            {
                paths.Add("/Applications/Rhino 8.app/Contents/PlugIns/GrasshopperPlugin.rhp");
                paths.Add("/Applications/Rhino 8.app/Contents/PlugIns/Grasshopper/GrasshopperPlugin.rhp");
            }

            return paths.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static string GetDisabledPath(string originalPath)
        {
            return originalPath + PluginCandidate.DisabledSuffix;
        }
    }
}
