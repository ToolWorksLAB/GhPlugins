using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sieve.Services
{
    public static class PluginPolicy
    {
        private static readonly HashSet<string> LoadableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".gha",
            ".ghpy",
            ".ghuser"
        };

        private static readonly HashSet<string> CoreGrasshopperAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BitmapComponent",
            "CurveComponents",
            "FieldComponents",
            "GalapagosComponents",
            "GhPython",
            "Grasshopper",
            "GrasshopperPlugin",
            "GH_IO",
            "GH_Util",
            "IOComponents",
            "Kangaroo2Component",
            "MathComponents",
            "RhinoCodePluginGH",
            "ScriptComponents",
            "SurfaceComponents",
            "TriangulationComponents",
            "VectorComponents",
            "XformComponents"
        };

        public static bool IsLoadableExtension(string path)
        {
            var originalPath = Models.PluginCandidate.NormalizeOriginalPath(path);
            return LoadableExtensions.Contains(Path.GetExtension(originalPath));
        }

        public static bool IsManageablePath(string path)
        {
            var originalPath = Models.PluginCandidate.NormalizeOriginalPath(path);
            return IsLoadableExtension(originalPath) &&
                !IsRhinoInstallPath(originalPath) &&
                !IsCoreGrasshopperAssembly(Path.GetFileNameWithoutExtension(originalPath));
        }

        public static bool IsUnsupportedManagedPath(string path)
        {
            var originalPath = Models.PluginCandidate.NormalizeOriginalPath(path);
            return !IsLoadableExtension(originalPath) ||
                IsRhinoInstallPath(originalPath) ||
                IsCoreGrasshopperAssembly(Path.GetFileNameWithoutExtension(originalPath));
        }

        public static bool IsCoreGrasshopperAssembly(string name)
        {
            name = Path.GetFileNameWithoutExtension(name ?? string.Empty);
            return CoreGrasshopperAssemblyNames.Contains(name);
        }

        private static bool IsRhinoInstallPath(string path)
        {
            var normalized = Normalize(path);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var root in GetWindowsProgramRoots())
                {
                    if (normalized.StartsWith(root + "\\rhino ", StringComparison.OrdinalIgnoreCase) ||
                        normalized.StartsWith(root + "\\mcneel\\rhino ", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return normalized.Contains("\\program files\\rhino ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/applications/rhino ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/applications/rhino.app/", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetWindowsProgramRoots()
        {
            return new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetEnvironmentVariable("ProgramW6432")
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                path ??= string.Empty;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
