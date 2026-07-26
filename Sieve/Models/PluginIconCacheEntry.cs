namespace Sieve.Models
{
    public sealed class PluginIconCacheEntry
    {
        public string PluginPath { get; set; } = string.Empty;
        public string LibraryName { get; set; } = string.Empty;
        public string IconDataUrl { get; set; } = string.Empty;
        public string CapturedUtc { get; set; } = string.Empty;
    }
}
