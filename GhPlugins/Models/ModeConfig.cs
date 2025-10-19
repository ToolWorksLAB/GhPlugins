using System.Collections.Generic;

namespace GhPlugins.Models
{
    public class ModeConfig
    {
        public string Name { get; set; }
        public List<PluginItem> PluginPaths { get; set; }

        public ModeConfig()
        {
            PluginPaths = new List<PluginItem>();
        }

        public ModeConfig(string name, List<PluginItem> pluginPaths)
        {
            Name = name;
            PluginPaths = pluginPaths;
        }
    }
}
