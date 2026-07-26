using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sieve.Models;

namespace Sieve.Services
{
    public sealed class PluginScanner
    {
        public IReadOnlyList<string> GetDefaultRoots()
        {
            return GetDefaultRootOptions()
                .Where(Directory.Exists)
                .ToList();
        }

        public IReadOnlyList<string> GetDefaultRootOptions()
        {
            var roots = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AddIfNotEmpty(roots, Path.Combine(appData, "Grasshopper", "Libraries"));
                AddIfNotEmpty(roots, Path.Combine(appData, "Grasshopper", "UserObjects"));
                AddRhinoVersionedRoots(roots, appData, "McNeel", "Rhinoceros");
                AddRhinoVersionedRoots(roots, programData, "McNeel", "Rhinoceros");
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var mcneel = Path.Combine(home, "Library", "Application Support", "McNeel", "Rhinoceros");
                AddIfNotEmpty(roots, Path.Combine(home, "Library", "Application Support", "Grasshopper", "Libraries"));
                AddIfNotEmpty(roots, Path.Combine(home, "Library", "Application Support", "Grasshopper", "UserObjects"));
                AddIfNotEmpty(roots, Path.Combine(mcneel, "8.0", "Plug-ins"));
                AddIfNotEmpty(roots, Path.Combine(mcneel, "7.0", "Plug-ins"));
                AddIfNotEmpty(roots, Path.Combine(mcneel, "packages", "8.0"));
                AddIfNotEmpty(roots, Path.Combine(mcneel, "packages", "7.0"));
            }

            return roots.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path)
                .ToList();
        }

        public IReadOnlyList<PluginCandidate> Scan(IEnumerable<string> customRoots)
        {
            var roots = GetDefaultRoots()
                .Concat(customRoots ?? Enumerable.Empty<string>())
                .ToList();
            return ScanRoots(roots, null, CancellationToken.None);
        }

        public IReadOnlyList<PluginCandidate> ScanRoots(
            IEnumerable<string> selectedRoots,
            Action<ScanProgressState> report,
            CancellationToken cancellationToken)
        {
            var roots = (selectedRoots ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidates = new Dictionary<string, PluginCandidate>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(string Path, string Root)>();

            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                var root = roots[rootIndex];
                foreach (var path in EnumerateLoadableFiles(root, directory =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    report?.Invoke(new ScanProgressState
                    {
                        Phase = "Discovering",
                        Message = $"Searching folder {rootIndex + 1} of {roots.Count}",
                        CurrentPath = directory,
                        Percent = roots.Count == 0 ? 0 : Math.Min(24, (int)Math.Round((rootIndex + 0.5) * 25d / roots.Count)),
                        FilesDiscovered = files.Count,
                        IsRunning = true
                    });
                }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add((path, root));
                }
            }

            report?.Invoke(new ScanProgressState
            {
                Phase = "Reading plugins",
                Message = $"Found {files.Count} loadable files",
                Percent = files.Count == 0 ? 100 : 25,
                FilesDiscovered = files.Count,
                TotalFiles = files.Count,
                IsRunning = files.Count > 0
            });

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var originalPath = PluginCandidate.NormalizeOriginalPath(file.Path);
                if (!candidates.ContainsKey(originalPath))
                    candidates[originalPath] = CreateCandidate(file.Path, originalPath, file.Root);

                report?.Invoke(new ScanProgressState
                {
                    Phase = "Reading plugins",
                    Message = $"Reading plugin {index + 1} of {files.Count}",
                    CurrentPath = originalPath,
                    Percent = 25 + (int)Math.Round((index + 1) * 73d / Math.Max(1, files.Count)),
                    FilesDiscovered = files.Count,
                    FilesProcessed = index + 1,
                    TotalFiles = files.Count,
                    IsRunning = true
                });
            }

            report?.Invoke(new ScanProgressState
            {
                Phase = "Finalizing",
                Message = $"Grouping {candidates.Count} plugin files",
                Percent = 99,
                FilesDiscovered = files.Count,
                FilesProcessed = files.Count,
                TotalFiles = files.Count,
                IsRunning = true
            });

            return candidates.Values
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static PluginCandidate CreateCandidate(string currentPath, string originalPath, string sourceRoot)
        {
            var extension = Path.GetExtension(originalPath).ToLowerInvariant();
            var disabled = currentPath.EndsWith(PluginCandidate.DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var userObject = extension == ".ghuser" ? ReadUserObjectMetadata(currentPath, originalPath, sourceRoot) : null;

            return new PluginCandidate
            {
                OriginalPath = originalPath,
                CurrentPath = currentPath,
                Name = userObject?.GroupName ?? Path.GetFileNameWithoutExtension(originalPath),
                Version = GetVersion(currentPath, extension),
                Kind = extension.TrimStart('.').ToUpperInvariant(),
                SourceRoot = sourceRoot,
                Category = userObject?.Category ?? string.Empty,
                SubCategory = userObject?.SubCategory ?? string.Empty,
                ComponentName = userObject?.ComponentName ?? Path.GetFileNameWithoutExtension(originalPath),
                IconDataUrl = extension == ".gha"
                    ? EmbeddedIconReader.ReadPngDataUrl(currentPath, Path.GetFileNameWithoutExtension(originalPath))
                    : string.Empty,
                Load = !disabled,
                IsDisabled = disabled,
                Warning = string.Empty
            };
        }

        private static UserObjectMetadata ReadUserObjectMetadata(string currentPath, string originalPath, string sourceRoot)
        {
            var componentName = Path.GetFileNameWithoutExtension(originalPath);
            var category = string.Empty;
            var subCategory = string.Empty;

            try
            {
                var bytes = File.Exists(currentPath) ? File.ReadAllBytes(currentPath) : Array.Empty<byte>();
                var text = Encoding.UTF8.GetString(bytes);
                category = ExtractArchiveString(text, "Category", exactToken: true);
                subCategory = ExtractArchiveString(text, "SubCategory", exactToken: false);
                componentName = FirstNonEmpty(
                    ExtractArchiveString(text, "Name", exactToken: true),
                    ExtractArchiveString(text, "NickName", exactToken: true),
                    componentName);
            }
            catch
            {
                // Fall back to path/package inference below.
            }

            category = CleanMetadata(category);
            subCategory = CleanMetadata(subCategory);
            componentName = CleanMetadata(componentName);

            var inferred = InferUserObjectGroupName(originalPath, sourceRoot);
            var groupName = FirstNonEmpty(category, inferred, "User Objects");
            if (string.IsNullOrWhiteSpace(category))
                category = groupName;

            return new UserObjectMetadata
            {
                GroupName = groupName,
                Category = category,
                SubCategory = subCategory,
                ComponentName = string.IsNullOrWhiteSpace(componentName) ? Path.GetFileNameWithoutExtension(originalPath) : componentName
            };
        }

        private static string ExtractArchiveString(string text, string token, bool exactToken)
        {
            var index = -1;
            var searchStart = 0;
            while (searchStart < text.Length)
            {
                index = text.IndexOf(token, searchStart, StringComparison.Ordinal);
                if (index < 0)
                    return string.Empty;

                var before = index == 0 ? '\0' : text[index - 1];
                var after = index + token.Length >= text.Length ? '\0' : text[index + token.Length];
                var insideLongerToken = exactToken && (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after));
                if (!insideLongerToken)
                    break;

                searchStart = index + token.Length;
            }

            var cursor = index + token.Length;
            while (cursor < text.Length && !IsMetadataValueCharacter(text[cursor]))
                cursor++;

            var start = cursor;
            while (cursor < text.Length && IsMetadataValueCharacter(text[cursor]))
                cursor++;

            return start < cursor ? text.Substring(start, cursor - start) : string.Empty;
        }

        private static bool IsMetadataValueCharacter(char value)
        {
            return value >= 32 && value <= 126;
        }

        private static string CleanMetadata(string value)
        {
            value = (value ?? string.Empty).Trim();
            foreach (var marker in new[] { "SubCategory", "Category", "NickName", "Description", "InstanceGuid", "BaseID", "Exposure", "Icon" })
            {
                var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex > 0)
                    value = value.Substring(0, markerIndex).Trim('.', ' ', '\t', '\r', '\n');
            }
            if (value.Length > 80)
                value = value.Substring(0, 80).Trim();
            return value;
        }

        private static string InferUserObjectGroupName(string originalPath, string sourceRoot)
        {
            var parts = originalPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            var packagesIndex = parts.FindIndex(part => string.Equals(part, "packages", StringComparison.OrdinalIgnoreCase));
            if (packagesIndex >= 0 && packagesIndex + 2 < parts.Count)
                return parts[packagesIndex + 2];

            var librariesIndex = parts.FindIndex(part => string.Equals(part, "Libraries", StringComparison.OrdinalIgnoreCase));
            if (librariesIndex >= 0 && librariesIndex + 1 < parts.Count)
                return parts[librariesIndex + 1];

            var userObjectsIndex = parts.FindIndex(part => string.Equals(part, "UserObjects", StringComparison.OrdinalIgnoreCase));
            if (userObjectsIndex > 0 && !string.Equals(parts[userObjectsIndex - 1], "Grasshopper", StringComparison.OrdinalIgnoreCase))
                return parts[userObjectsIndex - 1];

            return "User Objects";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static IEnumerable<string> EnumerateLoadableFiles(string root, Action<string> directoryVisited = null)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                directoryVisited?.Invoke(directory);

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(directory);
                }
                catch
                {
                    children = Enumerable.Empty<string>();
                }

                foreach (var child in children)
                    pending.Push(child);

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory);
                }
                catch
                {
                    files = Enumerable.Empty<string>();
                }

                foreach (var file in files)
                {
                    var pathToCheck = PluginCandidate.NormalizeOriginalPath(file);
                    if (PluginPolicy.IsManageablePath(pathToCheck))
                        yield return file;
                }
            }
        }

        private static string GetVersion(string path, string extension)
        {
            if (extension == ".ghpy")
                return string.Empty;

            try
            {
                if (File.Exists(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                        return info.ProductVersion;
                    if (!string.IsNullOrWhiteSpace(info.FileVersion))
                        return info.FileVersion;
                }
            }
            catch
            {
                return string.Empty;
            }

            try
            {
                if (File.Exists(path))
                    return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }

            return string.Empty;
        }

        private static void AddRhinoVersionedRoots(List<string> roots, string baseFolder, params string[] segments)
        {
            var rhinoRoot = Path.Combine(new[] { baseFolder }.Concat(segments).ToArray());
            AddIfNotEmpty(roots, Path.Combine(rhinoRoot, "8.0", "Plug-ins"));
            AddIfNotEmpty(roots, Path.Combine(rhinoRoot, "7.0", "Plug-ins"));
            AddIfNotEmpty(roots, Path.Combine(rhinoRoot, "packages", "8.0"));
            AddIfNotEmpty(roots, Path.Combine(rhinoRoot, "packages", "7.0"));
        }

        private static void AddIfNotEmpty(List<string> roots, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                roots.Add(path);
        }

        private sealed class UserObjectMetadata
        {
            public string GroupName { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string SubCategory { get; set; } = string.Empty;
            public string ComponentName { get; set; } = string.Empty;
        }
    }
}
