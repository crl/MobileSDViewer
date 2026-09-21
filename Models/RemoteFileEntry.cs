using System;

namespace MobileSDViewer.Models
{
    /// <summary>
    /// 设备上的一个目录项（来自 `ls -l`）。
    /// </summary>
    public class RemoteFileEntry
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsSymlink { get; set; }
        public long Size { get; set; }
        public DateTime? Modified { get; set; }
        public string Permissions { get; set; }
        public string Owner { get; set; }
        public string Group { get; set; }

        public string TypeLabel
        {
            get
            {
                if (IsDirectory) return "文件夹";
                if (IsSymlink) return "链接";
                return "文件";
            }
        }

        public string DisplaySize
        {
            get
            {
                if (IsDirectory) return "";
                return FormatSize(Size);
            }
        }

        public string DisplayTime => Modified.HasValue ? Modified.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }
            return v.ToString(i == 0 ? "0" : "0.##") + " " + units[i];
        }
    }
}
