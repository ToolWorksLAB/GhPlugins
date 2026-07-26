using System.Collections.Generic;

namespace Sieve.Models
{
    public sealed class DocumentAnalysisResult
    {
        public string FileName { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public List<DocumentPluginRequirement> Requirements { get; set; } = new List<DocumentPluginRequirement>();
        public List<DocumentPluginMatch> Matches { get; set; } = new List<DocumentPluginMatch>();
        public List<string> Notes { get; set; } = new List<string>();
    }
}
