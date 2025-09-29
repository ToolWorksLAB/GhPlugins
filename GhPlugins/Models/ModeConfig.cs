using System.Collections.Generic;

namespace GhPlugins.Models
{
    public class ModeConfig
    {
        public string Name { get; set; }
        public List<string> PluginPaths { get; set; }

        public ModeConfig()
        {
            PluginPaths = new List<string>();
        }

        public ModeConfig(string name, List<string> pluginPaths)
        {
            Name = name;
            PluginPaths = pluginPaths;
        }
    }
}
