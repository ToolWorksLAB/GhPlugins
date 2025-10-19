using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GhPlugins.Models
{
    public class PluginItem
    {
        public string Name { get; set; }
        /// <summary>Primary path (kept as the first GHA path we encounter, can be null).</summary>
        public string Path { get; set; }
        public bool IsSelected { get; set; }
        public List<string> Versions { get; set; } = new List<string>();
        public string Author { get; set; }
        public string Description { get; set; }

        /// <summary>All user object FILE paths (.ghuser)</summary>
        public List<string> UserobjectPath { get; set; } = new List<string>();

        /// <summary>All GH Python script FILE paths (.ghpy)</summary>
        public List<string> ghpyPath { get; set; } = new List<string>();

        /// <summary>All install locations (FILE paths) for the plugin’s .gha</summary>
        public List<string> GhaPaths { get; set; } = new List<string>();

        public PluginItem(string name, string path)
        {
            Name = name;
            Path = path;
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
            ghpyPath.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
    }
}
