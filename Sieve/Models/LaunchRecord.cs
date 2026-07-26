namespace Sieve.Models
{
    public sealed class LaunchRecord
    {
        public string StartedUtc { get; set; } = string.Empty;
        public string CompletedUtc { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int PluginCount { get; set; }
        public long CanvasReadyMilliseconds { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
