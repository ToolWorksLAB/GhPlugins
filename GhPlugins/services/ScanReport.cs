// File: Services/ScanReport.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GhPlugins.Models;
using Newtonsoft.Json;
using Rhino;

namespace GhPlugins.Services
{
    public static class ScanReport
    {
        static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhPlugins_ModeManager", "reports");

        /// <summary>
        /// Saves a JSON report for the scanned plugins. Returns the file path.
        /// </summary>
        public static string Save(List<PluginItem> allPlugins, string label = null)
        {
            Directory.CreateDirectory(Root);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var name = "scan_" + (string.IsNullOrWhiteSpace(label) ? "" : (Sanitize(label) + "_")) + stamp + ".json";
            var path = Path.Combine(Root, name);

            var report = new
            {
                schema = "ghplugins.scan.v1",
                generatedUtc = DateTime.UtcNow,
                machine = Environment.MachineName,
                user = Environment.UserName,
                rhinoVersion = RhinoApp.ExeVersion,
                count = allPlugins != null ? allPlugins.Count : 0,
                summary = new
                {
                   // gha = allPlugins?.Count(p => p.Path != null && p.Path.EndsWith(".gha", StringComparison.OrdinalIgnoreCase)) ?? 0,
                    ghpy = allPlugins?.Sum(p => p.ghpyPath != null ? p.ghpyPath.Count : 0) ?? 0,
                    ghuser = allPlugins?.Sum(p => p.UserobjectPath != null ? p.UserobjectPath.Count : 0) ?? 0
                },
                plugins = allPlugins?.Select(p => new
                {
                    p.Name,
                   // p.Path,
                    p.IsSelected,
                    
                    Author = p.Author,
                    p.Description,
                    ghaPath  = p.GhaPaths ?? new List<string>(),
                    Versions = p.Versions ?? new List<string>(),
                    ghpy = p.ghpyPath ?? new List<string>(),
                    userobjects = p.UserobjectPath ?? new List<string>()
                }).ToList()
            };

            var json = JsonConvert.SerializeObject(report, Formatting.Indented);
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>Reads a previously saved scan report. Returns null on failure.</summary>
        public static List<PluginItem> LoadPlugins(string reportPath)
        {
            try
            {
                if (!File.Exists(reportPath)) return null;

                // We stored a wrapper object. Extract back to PluginItem list.
                var root = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(reportPath));
                var list = new List<PluginItem>();
                foreach (var p in root.plugins)
                {
                    string name = (string)p.Name;
                    string mainPath = (string)p.Path;
                    var item = new PluginItem(name)
                    {
                        IsSelected = (bool)(p.IsSelected ?? false),
                        Versions = p.Versions,
                        Author = (string)p.Author,
                        Description = (string)p.Description
                    };

                    if (p.ghpy != null)
                    {
                        foreach (var s in p.ghpy)
                            item.ghpyPath.Add((string)s);
                    }
                    if (p.userobjects != null)
                    {
                        foreach (var s in p.userobjects)
                            item.UserobjectPath.Add((string)s);
                    }

                    list.Add(item);
                }
                return list;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("[Gh Mode Manager] Failed to load report: " + ex.Message);
                return null;
            }
        }

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            return name;
        }
    }
}
