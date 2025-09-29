using System;
using System.Collections.Generic;
using System.IO;
using GhPlugins.Models;

namespace GhPlugins.Services
{
    public static class PluginScanner
    {

        private static List<PluginItem> ScanFolder(string folder)
        {
            var list = new List<PluginItem>();
            if (Directory.Exists(folder))
            {
                string[] extensions = new[] { ".gha", ".ghpy", ".dll" };

                foreach (string file in Directory.GetFiles(folder))
                {
                    string ext = Path.GetExtension(file).ToLower();
                    if (Array.Exists(extensions, e => e == ext))
                    {
                        list.Add(new PluginItem(Path.GetFileName(file), file));
                    }
                }
            }
            return list;
        }

        private static List<PluginItem> ScanYakPackages(string yakFolder)
        {
            var list = new List<PluginItem>();
            if (!Directory.Exists(yakFolder)) return list;

            foreach (string packageDir in Directory.GetDirectories(yakFolder))
            {
                foreach (string versionDir in Directory.GetDirectories(packageDir))
                {
                    list.AddRange(ScanFolder(versionDir));
                }
            }

            return list;
        }
    }
}
