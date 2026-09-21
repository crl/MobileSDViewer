using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MobileSDViewer.Models;

namespace MobileSDViewer.Services
{
    public enum PreviewKind
    {
        None,
        Image,
        Text,
        Meta
    }

    /// <summary>
    /// 选中文件后的预览结果。
    /// </summary>
    public class PreviewResult
    {
        public PreviewKind Kind { get; set; }
        public ImageSource Image { get; set; }
        public string Text { get; set; }
        public string Meta { get; set; }
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// 把远程文件拉一段到内存做图片/文本预览；大文件截断。
    /// </summary>
    public class PreviewService
    {
        public const int TextLimitBytes = 512 * 1024;
        public const int ImageLimitBytes = 20 * 1024 * 1024;

        private static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
        };
        private static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".json", ".xml", ".csv", ".md", ".lua", ".cs", ".cfg", ".ini",
            ".yaml", ".yml", ".html", ".htm", ".js", ".ts", ".properties", ".conf",
            ".dat", ".bytes", ".info", ".manifest", ".plist"
        };

        private readonly AdbFileService _files;

        public PreviewService(AdbFileService files)
        {
            _files = files;
        }

        public async Task<PreviewResult> PreviewAsync(
            string serial, RemoteFileEntry entry, AccessContext access, CancellationToken ct = default)
        {
            if (entry == null || entry.IsDirectory)
            {
                return new PreviewResult { Kind = PreviewKind.None, Meta = "选择一个文件以预览。" };
            }

            var ext = Path.GetExtension(entry.Name) ?? "";
            var meta = BuildMeta(entry);

            if (ImageExt.Contains(ext))
            {
                var imageBytes = await _files.ReadPrefixAsync(serial, entry.FullPath, ImageLimitBytes, access, ct).ConfigureAwait(false);
                var image = TryDecodeImage(imageBytes);
                if (image != null)
                {
                    return new PreviewResult
                    {
                        Kind = PreviewKind.Image,
                        Image = image,
                        Meta = meta,
                        Truncated = entry.Size > ImageLimitBytes
                    };
                }
                return new PreviewResult
                {
                    Kind = PreviewKind.Meta,
                    Meta = meta + "\n无法解码该图片（WPF 可能不支持 " + ext + "）。"
                };
            }

            var forceText = TextExt.Contains(ext) || LooksLikeTextName(entry.Name) || ext.Length == 0;
            var bytes = await _files.ReadPrefixAsync(serial, entry.FullPath, TextLimitBytes, access, ct).ConfigureAwait(false);
            if (forceText || LooksLikeText(bytes))
            {
                var truncated = entry.Size > TextLimitBytes || bytes.Length >= TextLimitBytes;
                var text = DecodeText(bytes);
                if (truncated)
                    text += "\n\n—— 已截断，仅显示前 " + RemoteFileEntry.FormatSize(TextLimitBytes) + " ——";
                return new PreviewResult
                {
                    Kind = PreviewKind.Text,
                    Text = text,
                    Meta = meta,
                    Truncated = truncated
                };
            }

            return new PreviewResult
            {
                Kind = PreviewKind.Meta,
                Meta = meta + "\n\n内容不像文本（含二进制数据），请下载后用外部程序打开。"
            };
        }

        private static string BuildMeta(RemoteFileEntry entry)
        {
            return entry.Name + "\n"
                //+ entry.FullPath + "\n"
                + "类型：" + entry.TypeLabel + "    大小：" + (entry.IsDirectory ? "-" : RemoteFileEntry.FormatSize(entry.Size)) + "\t"
                + "修改时间：" + (string.IsNullOrEmpty(entry.DisplayTime) ? "-" : entry.DisplayTime) + "\t"
                + "权限：" + (entry.Permissions ?? "-") + "    " + (entry.Owner ?? "") + " " + (entry.Group ?? "");
        }

        private static bool LooksLikeTextName(string name)
        {
            return name.EndsWith(".log.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("log", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 未知扩展名先当文本：有 NUL 或控制字符过多则视为二进制。
        /// </summary>
        private static bool LooksLikeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return true;
            var sample = bytes.Length > 4096 ? 4096 : bytes.Length;
            var control = 0;
            for (var i = 0; i < sample; i++)
            {
                var b = bytes[i];
                if (b == 0)
                    return false;
                if (b < 32 && b != 9 && b != 10 && b != 13)
                    control++;
            }
            return control * 20 <= sample;
        }

        private static ImageSource TryDecodeImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8)
                return null;
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes, writable: false))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
