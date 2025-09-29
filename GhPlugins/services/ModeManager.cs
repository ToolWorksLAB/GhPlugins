using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using GhPlugins.Models;

namespace GhPlugins.Services
{
    public static class ModeManager
    {
        private static string ConfigFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "GhPlugins", "ghplugin_envs.json");

        static ModeManager()
        {
            // Ensure directory exists
            string dir = Path.GetDirectoryName(ConfigFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public static List<ModeConfig> LoadEnvironments()
        {
            if (!File.Exists(ConfigFilePath))
                return new List<ModeConfig>();

            try
            {
                string json = File.ReadAllText(ConfigFilePath);
                return JsonConvert.DeserializeObject<List<ModeConfig>>(json)
                       ?? new List<ModeConfig>();
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("Error loading environments: " + ex.Message);
                return new List<ModeConfig>();
            }
        }

        public static void SaveEnvironments(List<ModeConfig> environments)
        {
            try
            {
                string json = JsonConvert.SerializeObject(environments, Formatting.Indented);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("Error saving environments: " + ex.Message);
            }
        }
    }
}
