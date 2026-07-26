using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sieve.Models;

namespace Sieve.Services
{
    internal static class GrasshopperIconCache
    {
        private const int MaximumIconBytes = 128 * 1024;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static int CaptureLoadedLibraryIcons()
        {
            try
            {
                var grasshopper = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "Grasshopper", StringComparison.OrdinalIgnoreCase));
                var instances = grasshopper?.GetType("Grasshopper.Instances");
                if (instances == null || !GetBooleanProperty(instances, null, "IsComponentServer"))
                    return 0;

                var server = GetProperty(instances, null, "ComponentServer");
                var libraries = GetProperty(server?.GetType(), server, "Libraries") as IEnumerable;
                if (libraries == null)
                    return 0;

                var captured = new List<PluginIconCacheEntry>();
                foreach (var library in libraries)
                {
                    if (library == null || GetBooleanProperty(library.GetType(), library, "IsCoreLibrary"))
                        continue;

                    var icon = GetProperty(library.GetType(), library, "Icon");
                    var png = EncodePng(icon);
                    if (png.Length == 0)
                        continue;

                    var location = GetStringProperty(library.GetType(), library, "Location");
                    var name = GetStringProperty(library.GetType(), library, "Name");
                    if (string.IsNullOrWhiteSpace(location) && string.IsNullOrWhiteSpace(name))
                        continue;

                    captured.Add(new PluginIconCacheEntry
                    {
                        PluginPath = string.IsNullOrWhiteSpace(location) ? string.Empty : PluginCandidate.NormalizeOriginalPath(location),
                        LibraryName = name,
                        IconDataUrl = "data:image/png;base64," + Convert.ToBase64String(png),
                        CapturedUtc = DateTime.UtcNow.ToString("O")
                    });
                }

                if (captured.Count == 0)
                    return 0;

                var settings = SettingsStore.Load();
                settings.PluginIconCache ??= new List<PluginIconCacheEntry>();
                foreach (var entry in captured)
                {
                    var existing = settings.PluginIconCache.FirstOrDefault(item =>
                        !string.IsNullOrWhiteSpace(entry.PluginPath) &&
                        string.Equals(item.PluginPath, entry.PluginPath, StringComparison.OrdinalIgnoreCase));
                    existing ??= settings.PluginIconCache.FirstOrDefault(item =>
                        string.IsNullOrWhiteSpace(entry.PluginPath) &&
                        string.Equals(item.LibraryName, entry.LibraryName, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                        settings.PluginIconCache.Add(entry);
                    else
                    {
                        existing.PluginPath = entry.PluginPath;
                        existing.LibraryName = entry.LibraryName;
                        existing.IconDataUrl = entry.IconDataUrl;
                        existing.CapturedUtc = entry.CapturedUtc;
                    }
                }

                settings.PluginIconCache = settings.PluginIconCache
                    .Where(entry => IsSafeDataUrl(entry.IconDataUrl))
                    .OrderByDescending(entry => entry.CapturedUtc, StringComparer.Ordinal)
                    .Take(256)
                    .ToList();
                SettingsStore.Save(settings);
                return captured.Count;
            }
            catch
            {
                // Icon capture is opportunistic and must never affect Grasshopper startup.
                return 0;
            }
        }

        private static object GetProperty(Type type, object instance, string name)
        {
            try
            {
                return type?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)?.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string GetStringProperty(Type type, object instance, string name)
        {
            return GetProperty(type, instance, name)?.ToString() ?? string.Empty;
        }

        private static bool GetBooleanProperty(Type type, object instance, string name)
        {
            return GetProperty(type, instance, name) is bool value && value;
        }

        private static byte[] EncodePng(object icon)
        {
            if (icon == null)
                return Array.Empty<byte>();

            try
            {
                foreach (var method in icon.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => string.Equals(method.Name, "Save", StringComparison.Ordinal)))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 2 || !typeof(Stream).IsAssignableFrom(parameters[0].ParameterType))
                        continue;

                    var pngFormat = parameters[1].ParameterType
                        .GetProperty("Png", BindingFlags.Public | BindingFlags.Static)
                        ?.GetValue(null);
                    if (pngFormat == null)
                        continue;

                    using var output = new MemoryStream();
                    method.Invoke(icon, new[] { output, pngFormat });
                    var bytes = output.ToArray();
                    return IsPng(bytes) && bytes.Length <= MaximumIconBytes ? bytes : Array.Empty<byte>();
                }
            }
            catch
            {
                return Array.Empty<byte>();
            }

            return Array.Empty<byte>();
        }

        private static bool IsPng(byte[] bytes)
        {
            if (bytes == null || bytes.Length <= PngSignature.Length)
                return false;
            for (var index = 0; index < PngSignature.Length; index++)
            {
                if (bytes[index] != PngSignature[index])
                    return false;
            }
            return true;
        }

        private static bool IsSafeDataUrl(string value)
        {
            const string prefix = "data:image/png;base64,";
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                var bytes = Convert.FromBase64String(value.Substring(prefix.Length));
                return bytes.Length <= MaximumIconBytes && IsPng(bytes);
            }
            catch
            {
                return false;
            }
        }
    }
}
