using System;
using System.Linq;
using System.Reflection;
using Rhino;
using Sieve.Models;

namespace Sieve.Services
{
    internal static class LaunchTracker
    {
        public static void WatchForCanvas(string label, int pluginCount)
        {
            var started = DateTime.UtcNow;
            var record = new LaunchRecord
            {
                StartedUtc = started.ToString("O"),
                Label = label ?? "Manual",
                PluginCount = pluginCount,
                Status = "Waiting for canvas"
            };
            Save(record);

            EventHandler idleHandler = null;
            idleHandler = (_, _) =>
            {
                var elapsed = DateTime.UtcNow - started;
                if (!IsCanvasAvailable() && elapsed < TimeSpan.FromSeconds(45))
                    return;

                RhinoApp.Idle -= idleHandler;
                record.CompletedUtc = DateTime.UtcNow.ToString("O");
                record.CanvasReadyMilliseconds = (long)elapsed.TotalMilliseconds;
                record.Status = IsCanvasAvailable() ? "Canvas ready" : "No canvas signal";
                Save(record);
                if (IsCanvasAvailable())
                    GrasshopperIconCache.CaptureLoadedLibraryIcons();
            };
            RhinoApp.Idle += idleHandler;
        }

        private static bool IsCanvasAvailable()
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(item => string.Equals(item.GetName().Name, "Grasshopper", StringComparison.OrdinalIgnoreCase));
                var instances = assembly?.GetType("Grasshopper.Instances");
                var activeCanvas = instances?.GetProperty("ActiveCanvas", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return activeCanvas != null;
            }
            catch
            {
                return false;
            }
        }

        private static void Save(LaunchRecord record)
        {
            try
            {
                var settings = SettingsStore.Load();
                settings.LaunchHistory ??= new System.Collections.Generic.List<LaunchRecord>();
                settings.LaunchHistory.RemoveAll(item => string.Equals(item.StartedUtc, record.StartedUtc, StringComparison.Ordinal));
                settings.LaunchHistory.Insert(0, record);
                settings.LaunchHistory = settings.LaunchHistory.Take(16).ToList();
                SettingsStore.Save(settings);
            }
            catch
            {
                // Telemetry cannot interrupt a Grasshopper launch.
            }
        }
    }
}
