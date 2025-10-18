using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace GhPlugins.Models
{
    public class PluginItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsSelected { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        

        public List<string> UserobjectPath { get; set; } = new List<string>();
        public List<string> ghpyPath { get; set; } = new List<string>();

        public PluginItem(string name, string path)
        {
            Name = name;
            Path = path;
            IsSelected = false;
            UserobjectPath = new List<string>();
            ghpyPath = new List<string>();
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
