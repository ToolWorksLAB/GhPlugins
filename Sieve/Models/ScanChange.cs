namespace Sieve.Models
{
    public sealed class ScanChange
    {
        public string Kind { get; set; } = string.Empty;
        public string PluginName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }
}
