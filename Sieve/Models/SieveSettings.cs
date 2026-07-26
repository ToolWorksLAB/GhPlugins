using System.Collections.Generic;

namespace Sieve.Models
{
    public sealed class SieveSettings
    {
        public List<string> CustomPaths { get; set; } = new List<string>();
        public List<string> DisabledScanPaths { get; set; } = new List<string>();
        public List<SievePreset> Presets { get; set; } = new List<SievePreset>();
        public List<PluginCandidate> LastScan { get; set; } = new List<PluginCandidate>();
        public List<string> DisabledPaths { get; set; } = new List<string>();
        public List<string> PinnedPluginPaths { get; set; } = new List<string>();
        public List<ScanChange> LastScanChanges { get; set; } = new List<ScanChange>();
        public List<LaunchRecord> LaunchHistory { get; set; } = new List<LaunchRecord>();
        public List<PluginIconCacheEntry> PluginIconCache { get; set; } = new List<PluginIconCacheEntry>();
        public string LastScanUtc { get; set; } = string.Empty;
        public string LastScanReport { get; set; } = string.Empty;
        public string PluginViewMode { get; set; } = "grid";
        public string IconExtractionVersion { get; set; } = string.Empty;
    }
}
