namespace Sieve.Models
{
    public sealed class DocumentPluginRequirement
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Hits { get; set; }
    }
}
