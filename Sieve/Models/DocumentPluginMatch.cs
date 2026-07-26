namespace Sieve.Models
{
    public sealed class DocumentPluginMatch
    {
        public DocumentPluginRequirement Requirement { get; set; } = new DocumentPluginRequirement();
        public PluginCandidate Candidate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }
}
