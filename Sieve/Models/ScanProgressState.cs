using System.Collections.Generic;

namespace Sieve.Models
{
    public sealed class ScanProgressState
    {
        public string Phase { get; set; } = "Idle";
        public string Message { get; set; } = "Ready to scan.";
        public string CurrentPath { get; set; } = string.Empty;
        public int Percent { get; set; }
        public int FilesDiscovered { get; set; }
        public int FilesProcessed { get; set; }
        public int TotalFiles { get; set; }
        public bool IsRunning { get; set; }
        public bool IsComplete { get; set; }
        public bool IsCancelled { get; set; }
        public bool HasError { get; set; }
        public List<string> RecentMessages { get; set; } = new List<string>();
    }
}
