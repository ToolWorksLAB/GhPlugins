// File: Services/PluginScanner.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GhPlugins.Models;

namespace GhPlugins.Services
{
    public static class PluginScanner
    {
        public static List<PluginItem> ScanDefaultPluginFolders()
        {
            var pluginItems = new List<PluginItem>();

            // Standard Grasshopper user plugin location
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userLibPath = Path.Combine(roaming, "Grasshopper", "Libraries");
            if (Directory.Exists(userLibPath))
                pluginItems.AddRange(ScanDirectory(userLibPath));

            // Yak packages plugin location (optional)
            string yakPath = Path.Combine(roaming, "McNeel", "Rhinoceros", "packages");
            if (Directory.Exists(yakPath))
            {
                foreach (var pkg in Directory.GetDirectories(yakPath))
                {
                    string versionDir = Directory.GetDirectories(pkg).FirstOrDefault();
                    if (versionDir != null)
                        pluginItems.AddRange(ScanDirectory(versionDir));
                }
            }

            return pluginItems;
        }

        private static List<PluginItem> ScanDirectory(string path)
        {
            var list = new List<PluginItem>();

            var ghaFiles = Directory.GetFiles(path, "*.gha", SearchOption.TopDirectoryOnly);
            foreach (var gha in ghaFiles)
            {
                string name = Path.GetFileName(gha);
                list.Add(new PluginItem(name, gha));
            }

            return list;
        }
    }
}
