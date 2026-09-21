using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MobileSDViewer.Models;

namespace MobileSDViewer.Services
{
    /// <summary>
    /// 将设置存到 %AppData%/MobileSDViewer/settings.json。
    /// </summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MobileSDViewer");
                return Path.Combine(dir, "settings.json");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path))
                    return new AppSettings();
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null) return;
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions), Encoding.UTF8);
        }
    }
}
