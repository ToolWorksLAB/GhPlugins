// File: Services/PluginScanner.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using GhPlugins.Models;

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

        private static void ScanDirectory(string path)
        {
          
            
            var ghaFiles = Directory.GetFiles(path, "*.gha", SearchOption.AllDirectories);
            foreach (var gha in ghaFiles)
            {
                string name = Path.GetFileName(gha);
                pluginItems.Add(new PluginItem(name, gha));
               
            }

            var userObjectFiles = Directory.GetFiles(path, "*.ghuser", SearchOption.AllDirectories);
            foreach (var userObject in userObjectFiles)
            {
              string userObjectName= PluginReader.ReadUserObject(userObject);
                int index = pluginItems.FindIndex(o => o.Name == userObjectName);
                if (index >= 0)
                {
                    pluginItems[index].UserobjectPath.Add(userObject);
                }
                else
                {
                    pluginItems.Add(new PluginItem(userObjectName, userObject));
                    
                }

            }
          
        }

    }
}
