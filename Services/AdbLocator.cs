using System;
using System.IO;

namespace MobileSDViewer.Services
{
    /// <summary>
    /// 定位本机 adb.exe：设置项 → PATH → ANDROID_HOME → 常见 SDK 目录。
    /// </summary>
    public static class AdbLocator
    {
        public static string Find(string preferred)
        {
            if (IsAdb(preferred))
                return preferred;

            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "adb.exe");
                    if (IsAdb(candidate))
                        return candidate;
                }
            }

            var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (string.IsNullOrEmpty(androidHome))
                androidHome = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
            var fromHome = CombinePlatformTools(androidHome);
            if (IsAdb(fromHome))
                return fromHome;

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var fromLocal = Path.Combine(localApp, "Android", "Sdk", "platform-tools", "adb.exe");
            if (IsAdb(fromLocal))
                return fromLocal;

            string[] extras =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "android-sdk", "platform-tools", "adb.exe"),
                @"C:\Android\sdk\platform-tools\adb.exe",
                @"C:\Android\platform-tools\adb.exe"
            };
            foreach (var extra in extras)
            {
                if (IsAdb(extra))
                    return extra;
            }

            return null;
        }

        private static string CombinePlatformTools(string sdkRoot)
        {
            if (string.IsNullOrEmpty(sdkRoot))
                return null;
            return Path.Combine(sdkRoot, "platform-tools", "adb.exe");
        }

        private static bool IsAdb(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }
    }
}
