// File: Services/PluginScanner.cs
using GhPlugins.Models;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace GhPlugins.Services
{
    public static class PluginScanner
    {
        public static List<PluginItem> pluginItems = new List<PluginItem>();
        public static void ScanDefaultPluginFolders()
        {
            

            // Standard Grasshopper user plugin location
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userLibPath = Path.Combine(roaming, "Grasshopper", "Libraries");
            if (Directory.Exists(userLibPath))
                ScanDirectory(userLibPath);
            //userobjects:
            string userobjPath = Path.Combine(roaming, "Grasshopper", "UserObjects");
            if (Directory.Exists(userLibPath))
                ScanDirectory(userobjPath);
            // Yak packages plugin location (optional)
            string yakPath = Path.Combine(roaming, "McNeel", "Rhinoceros", "packages");
            if (Directory.Exists(yakPath))
            {
                foreach (var pkg in Directory.GetDirectories(yakPath))
                {
                    string[] versionDir = Directory.GetDirectories(pkg);
                    if (versionDir != null)
                        for (int i = 0; i < versionDir.Length; i++)
                        {
                            ScanDirectory(versionDir[i]);    
                                }
                }
            }

            
        }

        private static IEnumerable<string> GetFilesWithDisabled(string path, string ext)
        {
            return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(ext + ".disabled", StringComparison.OrdinalIgnoreCase));
        }

        private static void ScanDirectory(string path)
        {
            // --- GHA files (active + disabled)
            foreach (var gha in GetFilesWithDisabled(path, ".gha"))
            {
                bool wasDisabled = gha.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = gha;

                if (wasDisabled)
                {
                    // Remove .disabled → physically rename file
                    string newPath = gha.Substring(0, gha.Length - ".disabled".Length);
                    try
                    {
                        if (File.Exists(newPath))
                            File.Delete(newPath); // avoid duplicates

                        File.Move(gha, newPath);
                        activePath = newPath;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"⚠️ Failed to rename disabled file '{gha}': {ex.Message}");
                        continue;
                    }
                }

                var info = GhaInfoReader.ReadPluginInfo(activePath);
                if (info == null)
                    continue;

                int existingIdx = pluginItems.FindIndex(p =>
                    p.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase));

                if (existingIdx >= 0)
                {
                    pluginItems[existingIdx].Version = info.Version;
                    pluginItems[existingIdx].Path = activePath;
                    pluginItems[existingIdx].IsSelected = !wasDisabled;
                   
                }
                else
                {
                    var item = new PluginItem(info.Name, activePath)
                    {
                        Version = info.Version,
                        IsSelected = !wasDisabled,
                    };
                    pluginItems.Add(item);
                }
            }

            // --- UserObjects (active + disabled)
            foreach (var uo in GetFilesWithDisabled(path, ".ghuser"))
            {
                bool wasDisabled = uo.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                string activePath = uo;

                if (wasDisabled)
                {
                    // Remove .disabled → physically rename
                    string newPath = uo.Substring(0, uo.Length - ".disabled".Length);
                    try
                    {
                        if (File.Exists(newPath))
                            File.Delete(newPath);
                        File.Move(uo, newPath);
                        activePath = newPath;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"⚠️ Failed to rename disabled file '{uo}': {ex.Message}");
                        continue;
                    }
                }

                var userObjectName = PluginReader.ReadUserObject(activePath);
                if (string.IsNullOrWhiteSpace(userObjectName))
                    continue;

                int index = pluginItems.FindIndex(o =>
                    o.Name.Equals(userObjectName, StringComparison.OrdinalIgnoreCase));

                if (index >= 0)
                {
                    pluginItems[index].UserobjectPath.Add(activePath);
                }
                else
                {
                    var orphan = new PluginItem(userObjectName, activePath)
                    {
                        IsSelected = !wasDisabled,
                       
                    };
                    orphan.UserobjectPath.Add(activePath);
                    pluginItems.Add(orphan);
                }
            }
        }



    }
}
