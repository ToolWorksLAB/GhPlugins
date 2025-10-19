using System;
using System.Collections.Generic;
using System.Linq;

namespace GhPlugins.Models
{
    public class PluginItem
    {
        public string Name { get; set; }
        /// <summary>Primary path (kept in sync with ActiveVersionIndex; may be null).</summary>
        
        public bool IsSelected { get; set; }

        /// <summary>Versions[i] belongs to GhaPaths[i]. Strings as discovered (e.g., GH_AssemblyInfo or Yak folder).</summary>
        public List<string> Versions { get; set; } = new List<string>();

        public string Author { get; set; }
        public string Description { get; set; }

        /// <summary>All user object FILE paths (.ghuser)</summary>
        public List<string> UserobjectPath { get; set; } = new List<string>();

        /// <summary>All GH Python script FILE paths (.ghpy)</summary>
        public List<string> ghpyPath { get; set; } = new List<string>();

        /// <summary>All install locations (.gha FILE paths). GhaPaths[i] ↔ Versions[i]</summary>
        public List<string> GhaPaths { get; set; } = new List<string>();

        /// <summary>Index into GhaPaths/Versions that is selected. -1 means “not set”.</summary>
        public int ActiveVersionIndex { get; set; } = -1;   

        public PluginItem(string name)
        {
            Name = name;
         
            IsSelected = false;
        }

        public override string ToString() => Name;

        public bool HasGhaPath(string p) =>
            !string.IsNullOrWhiteSpace(p) &&
            GhaPaths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

        public bool HasUserObjectPath(string p) =>
            !string.IsNullOrWhiteSpace(p) &&
            UserobjectPath.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));

        public bool HasGhpyPath(string p) =>
            !string.IsNullOrWhiteSpace(p) &&
            ghpyPath.Any(x => string.Equals(x, p, System.StringComparison.OrdinalIgnoreCase));
    }
}
