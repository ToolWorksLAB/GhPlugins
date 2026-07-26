using System;
using System.IO;
using System.Text.Json;
using Sieve.Models;

namespace Sieve.Services
{
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string SettingsDirectory
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var directory = Path.Combine(appData, "Sieve");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

        public static SieveSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new SieveSettings();

                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<SieveSettings>(json, JsonOptions) ?? new SieveSettings();
            }
            catch
            {
                return new SieveSettings();
            }
        }

        public static void Save(SieveSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
    }
}
