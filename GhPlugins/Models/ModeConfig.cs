using System.Collections.Generic;

namespace GhPlugins.Models
{
    public class ModeConfig
    {
        public string Name { get; set; }
        public List<PluginItem> Plugins { get; set; }

        public ModeConfig()
        {
            Plugins = new List<PluginItem>();
        }

        public ModeConfig(string name, List<PluginItem> plugins)
        {
            Name = name;
            Plugins = plugins;
        }
    }
}
