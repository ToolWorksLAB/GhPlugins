using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Rhino;
using Sieve.Models;

namespace Sieve.Services
{
    public sealed class GrasshopperDocumentAnalyzer
    {
        private static readonly Regex AssemblyNamePattern = new Regex(@"([A-Za-z][A-Za-z0-9_. +\-]{1,80}),\s*Version=([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.Compiled);
        private static readonly Regex TokenPattern = new Regex(@"(?:Assembly|Library|Plugin|Category|Name|NickName|SubCategory)(?:FullName)?[\x00-\x20.]{1,24}([A-Za-z][A-Za-z0-9_. +\-]{1,80})", RegexOptions.Compiled);
        private static readonly HashSet<string> IgnoredAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Grasshopper",
            "GH_IO",
            "GH_Util",
            "BitmapComponent",
            "CurveComponents",
            "FieldComponents",
            "GalapagosComponents",
            "GhPython",
            "IOComponents",
            "Kangaroo2Component",
            "MathComponents",
            "RhinoCodePluginGH",
            "ScriptComponents",
            "SurfaceComponents",
            "TriangulationComponents",
            "VectorComponents",
            "XformComponents",
            "RhinoCommon",
            "System",
            "System.Core",
            "mscorlib",
            "netstandard"
        };

        public DocumentAnalysisResult Analyze(string fileName, byte[] bytes, IEnumerable<PluginCandidate> installedCandidates)
        {
            var result = new DocumentAnalysisResult { FileName = fileName };
            var text = ExtractReadableText(bytes);
            var requirements = ExtractRequirements(text)
                .Where(requirement => !IgnoredAssemblies.Contains(requirement.Name))
                .OrderBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.Requirements.AddRange(requirements);
            result.Matches.AddRange(MatchRequirements(requirements, installedCandidates));

            if (!requirements.Any())
                result.Notes.Add("No external plugin signatures were found. This can happen with native-only files or heavily compressed/obfuscated archives.");

            if (text.IndexOf("Cluster", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Notes.Add("Cluster markers were found and included in the recursive text scan. Embedded cluster archives are scanned as part of the file bytes.");

            return result;
        }

        private static string ExtractReadableText(byte[] bytes)
        {
            var parts = new List<string>
            {
                Encoding.UTF8.GetString(bytes),
                Encoding.Unicode.GetString(bytes)
            };

            var archiveXml = TrySerializeWithGhIo(bytes);
            if (!string.IsNullOrWhiteSpace(archiveXml))
                parts.Add(archiveXml);

            if (LooksLikeZip(bytes))
            {
                try
                {
                    using var memory = new MemoryStream(bytes);
                    using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        using var stream = entry.Open();
                        using var copy = new MemoryStream();
                        stream.CopyTo(copy);
                        var entryBytes = copy.ToArray();
                        parts.Add(Encoding.UTF8.GetString(entryBytes));
                        parts.Add(Encoding.Unicode.GetString(entryBytes));
                    }
                }
                catch
                {
                    // Non-critical. The raw byte text is still scanned.
                }
            }

            return string.Join("\n", parts);
        }

        private static string TrySerializeWithGhIo(byte[] bytes)
        {
            try
            {
                var ghIo = LoadGhIoAssembly();
                if (ghIo == null)
                    return string.Empty;

                var archiveType = ghIo.GetType("GH_IO.Serialization.GH_Archive");
                if (archiveType == null)
                    return string.Empty;

                var archive = Activator.CreateInstance(archiveType);
                var deserializeBinary = archiveType.GetMethod("Deserialize_Binary", new[] { typeof(byte[]) });
                var deserializeXml = archiveType.GetMethod("Deserialize_Xml", new[] { typeof(string) });
                var serializeXml = archiveType.GetMethod("Serialize_Xml", Type.EmptyTypes);

                var ok = false;
                if (LooksLikeXml(bytes) && deserializeXml != null)
                    ok = (bool)deserializeXml.Invoke(archive, new object[] { Encoding.UTF8.GetString(bytes) });

                if (!ok && deserializeBinary != null)
                    ok = (bool)deserializeBinary.Invoke(archive, new object[] { bytes });

                return ok && serializeXml != null
                    ? serializeXml.Invoke(archive, Array.Empty<object>()) as string ?? string.Empty
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Assembly LoadGhIoAssembly()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "GH_IO", StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
                return loaded;

            try
            {
                return Assembly.Load("GH_IO");
            }
            catch
            {
                // Try a Rhino 8 Windows install path as a fallback. On Mac/Rhino-loaded contexts, Assembly.Load above is expected to work.
            }

            try
            {
                var rhinoFolder = Path.GetDirectoryName(typeof(RhinoApp).Assembly.Location) ?? string.Empty;
                var candidate = Path.Combine(rhinoFolder, "Plug-ins", "Grasshopper", "GH_IO.dll");
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool LooksLikeXml(byte[] bytes)
        {
            var prefix = Encoding.UTF8.GetString(bytes.Take(Math.Min(bytes.Length, 256)).ToArray()).TrimStart();
            return prefix.StartsWith("<", StringComparison.Ordinal);
        }

        private static bool LooksLikeZip(byte[] bytes)
        {
            return bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B;
        }

        private static IEnumerable<DocumentPluginRequirement> ExtractRequirements(string text)
        {
            var requirements = new Dictionary<string, DocumentPluginRequirement>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in AssemblyNamePattern.Matches(text))
            {
                AddRequirement(requirements, Clean(match.Groups[1].Value), match.Groups[2].Value, string.Empty);
            }

            foreach (Match match in TokenPattern.Matches(text))
            {
                var value = Clean(match.Groups[1].Value);
                if (value.Length < 2 || value.Contains("Copyright", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddRequirement(requirements, value, string.Empty, value);
            }

            return requirements.Values;
        }

        private static void AddRequirement(Dictionary<string, DocumentPluginRequirement> requirements, string name, string version, string category)
        {
            if (string.IsNullOrWhiteSpace(name) || IgnoredAssemblies.Contains(name))
                return;

            var key = name.Trim();
            if (!requirements.TryGetValue(key, out var requirement))
            {
                requirement = new DocumentPluginRequirement { Name = key };
                requirements[key] = requirement;
            }

            if (string.IsNullOrWhiteSpace(requirement.Version) && !string.IsNullOrWhiteSpace(version))
                requirement.Version = version;
            if (string.IsNullOrWhiteSpace(requirement.Category) && !string.IsNullOrWhiteSpace(category))
                requirement.Category = category;
            requirement.Hits++;
        }

        private static IEnumerable<DocumentPluginMatch> MatchRequirements(IEnumerable<DocumentPluginRequirement> requirements, IEnumerable<PluginCandidate> installedCandidates)
        {
            var installed = installedCandidates.ToList();
            foreach (var requirement in requirements)
            {
                var candidates = installed
                    .Where(candidate => IsCandidateMatch(requirement, candidate))
                    .Where(candidate => PluginPolicy.IsManageablePath(candidate.OriginalPath))
                    .ToList();

                if (!candidates.Any())
                {
                    yield return new DocumentPluginMatch
                    {
                        Requirement = requirement,
                        Status = "Missing",
                        Note = "No installed plugin or user-object category matched this requirement."
                    };
                    continue;
                }

                var best = candidates
                    .OrderBy(candidate => candidate.Kind == "GHA" ? 0 : 1)
                    .ThenBy(candidate => PluginFamilyDistance(requirement.Name, candidate))
                    .ThenBy(candidate => VersionDistance(requirement.Version, candidate.Version))
                    .ThenBy(candidate => candidate.OriginalPath.Length)
                    .First();

                var exact = !string.IsNullOrWhiteSpace(requirement.Version) &&
                    Version.TryParse(requirement.Version, out var requiredVersion) &&
                    Version.TryParse(CleanVersion(best.Version), out var candidateVersion) &&
                    requiredVersion.Equals(candidateVersion);

                yield return new DocumentPluginMatch
                {
                    Requirement = requirement,
                    Candidate = best,
                    Status = exact || string.IsNullOrWhiteSpace(requirement.Version) ? "Matched" : "Closest",
                    Note = exact || string.IsNullOrWhiteSpace(requirement.Version)
                        ? "Matched installed plugin."
                        : $"Requested {requirement.Version}, using {best.Version}."
                };
            }
        }

        private static bool IsCandidateMatch(DocumentPluginRequirement requirement, PluginCandidate candidate)
        {
            var requirementFamily = GetPluginFamilyName(requirement.Name);
            return ContainsLoose(candidate.Name, requirement.Name) ||
                ContainsLoose(Path.GetFileNameWithoutExtension(candidate.OriginalPath), requirement.Name) ||
                ContainsLoose(candidate.Category, requirement.Name) ||
                ContainsLoose(requirement.Name, candidate.Name) ||
                ContainsLoose(candidate.Name, requirementFamily) ||
                ContainsLoose(Path.GetFileNameWithoutExtension(candidate.OriginalPath), requirementFamily);
        }

        private static int PluginFamilyDistance(string requirementName, PluginCandidate candidate)
        {
            var family = NormalizeName(GetPluginFamilyName(requirementName));
            var candidateName = NormalizeName(candidate.Name);
            var candidateFile = NormalizeName(Path.GetFileNameWithoutExtension(candidate.OriginalPath));

            if (string.Equals(candidateFile, NormalizeName(requirementName), StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(candidateName, family, StringComparison.OrdinalIgnoreCase) || string.Equals(candidateFile, family, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (candidateFile.Contains(family, StringComparison.OrdinalIgnoreCase))
                return 2;
            return 10;
        }

        private static string GetPluginFamilyName(string requirementName)
        {
            var value = requirementName ?? string.Empty;
            foreach (var marker in new[] { ".Gh.", ".GH.", ".Grasshopper.", ".CommonSdk", ".CommonSDK", ".Sdk", ".SDK" })
            {
                var index = value.IndexOf(marker, StringComparison.Ordinal);
                if (index > 0)
                    value = value.Substring(0, index);
            }

            return value.Replace(".Gh", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".GH", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".", "")
                .Trim();
        }

        private static bool ContainsLoose(string left, string right)
        {
            left = NormalizeName(left);
            right = NormalizeName(right);
            return !string.IsNullOrWhiteSpace(left) &&
                !string.IsNullOrWhiteSpace(right) &&
                (left.Contains(right, StringComparison.OrdinalIgnoreCase) || right.Contains(left, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeName(string value)
        {
            return new string((value ?? string.Empty).Where(character => char.IsLetterOrDigit(character)).ToArray());
        }

        private static long VersionDistance(string required, string candidate)
        {
            if (!Version.TryParse(CleanVersion(required), out var requiredVersion))
                return 0;
            if (!Version.TryParse(CleanVersion(candidate), out var candidateVersion))
                return long.MaxValue / 2;

            return Math.Abs(requiredVersion.Major - candidateVersion.Major) * 1_000_000_000L +
                Math.Abs(requiredVersion.Minor - candidateVersion.Minor) * 1_000_000L +
                Math.Abs(requiredVersion.Build - candidateVersion.Build) * 1_000L +
                Math.Abs(requiredVersion.Revision - candidateVersion.Revision);
        }

        private static string CleanVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var clean = new string(value.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray()).Trim('.');
            return clean;
        }

        private static string Clean(string value)
        {
            value = Regex.Replace(value ?? string.Empty, @"[^\w. +\-]", " ").Trim();
            return value.Length > 80 ? value.Substring(0, 80).Trim() : value;
        }
    }
}
