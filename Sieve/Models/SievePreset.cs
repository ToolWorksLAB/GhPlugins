using System.Collections.Generic;

namespace Sieve.Models
{
    public sealed class SievePreset
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> PluginPaths { get; set; } = new List<string>();
        public List<string> ProjectFolders { get; set; } = new List<string>();
    }
}
