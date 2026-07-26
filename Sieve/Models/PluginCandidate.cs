using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Sieve.Models
{
    public sealed class PluginCandidate
    {
        public const string DisabledSuffix = ".sieve-disabled";

        public string OriginalPath { get; set; } = string.Empty;
        public string CurrentPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string SourceRoot { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;
        public string ComponentName { get; set; } = string.Empty;
        public string IconDataUrl { get; set; } = string.Empty;
        public bool Load { get; set; } = true;
        public bool IsDisabled { get; set; }
        public string Warning { get; set; } = string.Empty;

        [JsonIgnore]
        public string Folder => Path.GetDirectoryName(OriginalPath) ?? string.Empty;

        public static string NormalizeOriginalPath(string path)
        {
            return path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(0, path.Length - DisabledSuffix.Length)
                : path;
        }
    }
}
