using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MobileSDViewer.Models;

namespace MobileSDViewer.Services
{
    /// <summary>
    /// 列出 Android/data 失败时，需要用户挑选一个应用包再走 run-as。
    /// </summary>
    public class NeedPackageException : Exception
    {
        public NeedPackageException(string message) : base(message) { }
    }

    /// <summary>
    /// Android/data 与 /data/data 的权限回退：Shell → run-as → su。
    /// </summary>
    public class AndroidDataAccess
    {
        private static readonly Regex AndroidDataPkg = new Regex(
            @"/(?:sdcard|storage/emulated/\d+)/Android/data/([^/]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataDataPkg = new Regex(
            @"^/data/data/([^/]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AdbFileService _files;

        public AndroidDataAccess(AdbFileService files)
        {
            _files = files;
        }

        public static bool IsAndroidDataRoot(string path)
        {
            path = AdbFileService.NormalizePath(path);
            return path.Equals(KnownPaths.AndroidData, StringComparison.OrdinalIgnoreCase)
                || path.Equals(KnownPaths.EmulatedAndroidData, StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractPackage(string path)
        {
            path = AdbFileService.NormalizePath(path);
            var m = AndroidDataPkg.Match(path);
            if (m.Success)
                return m.Groups[1].Value;
            m = DataDataPkg.Match(path);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// 按权限链列举目录，并返回真正成功的访问模式供后续操作复用。
        /// </summary>
        public async Task<(List<RemoteFileEntry> Entries, AccessContext Access)> ListSmartAsync(
            string serial, string path, CancellationToken ct = default)
        {
            path = AdbFileService.NormalizePath(path);
            UnauthorizedAccessException shellDenied = null;

            try
            {
                var entries = await _files.ListAsync(serial, path, AccessContext.Shell, ct).ConfigureAwait(false);
                return (entries, AccessContext.Shell);
            }
            catch (UnauthorizedAccessException ex)
            {
                shellDenied = ex;
            }

            var pkg = ExtractPackage(path);
            if (string.IsNullOrEmpty(pkg) && IsAndroidDataRoot(path))
                throw new NeedPackageException("无法直接列出 Android/data。请选择一个应用包（debug 包可用 run-as）。");

            if (!string.IsNullOrEmpty(pkg))
            {
                try
                {
                    var runAs = AccessContext.ForRunAs(pkg);
                    var entries = await _files.ListAsync(serial, path, runAs, ct).ConfigureAwait(false);
                    return (entries, runAs);
                }
                catch (UnauthorizedAccessException)
                {
                    // 继续尝试 root
                }
                catch (IOException ex) when (IsRunAsFailure(ex.Message))
                {
                    // 包不可调试时仍试 su
                }
            }

            try
            {
                var entries = await _files.ListAsync(serial, path, AccessContext.Root, ct).ConfigureAwait(false);
                return (entries, AccessContext.Root);
            }
            catch (Exception rootEx)
            {
                throw new UnauthorizedAccessException(BuildHelpMessage(path, pkg, shellDenied, rootEx));
            }
        }

        /// <summary>
        /// 选包后依次尝试该包的外部 files、外部根、内部 files、内部根。
        /// </summary>
        public async Task<(string Path, List<RemoteFileEntry> Entries, AccessContext Access)> OpenPackageAsync(
            string serial, string package, CancellationToken ct = default)
        {
            string[] candidates =
            {
                "/sdcard/Android/data/" + package + "/files",
                "/sdcard/Android/data/" + package,
                "/storage/emulated/0/Android/data/" + package + "/files",
                "/storage/emulated/0/Android/data/" + package,
                "/data/data/" + package + "/files",
                "/data/data/" + package
            };

            Exception last = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var result = await ListSmartAsync(serial, candidate, ct).ConfigureAwait(false);
                    return (candidate, result.Entries, result.Access);
                }
                catch (NeedPackageException)
                {
                    // 不应出现：候选已含包名
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new UnauthorizedAccessException(
                "无法打开包 " + package + " 的数据目录。需要 debuggable 安装包或 root 设备。"
                + (last == null ? "" : "\n" + last.Message));
        }

        private static bool IsRunAsFailure(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("not debuggable", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("run-as:", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildHelpMessage(string path, string pkg, Exception shellDenied, Exception rootEx)
        {
            var reason = shellDenied?.Message;
            if (string.IsNullOrEmpty(reason))
                reason = rootEx?.Message;
            if (string.IsNullOrEmpty(pkg))
            {
                return "无法访问 " + path + "。Android 11+ 或该 ROM 限制了 shell 用户。"
                    + "请用快捷入口打开具体应用，或安装 debug 包 / 使用 root 设备。"
                    + (string.IsNullOrEmpty(reason) ? "" : "\n" + reason);
            }
            return "无法访问 " + path + "（包 " + pkg + "）。"
                + "run-as 仅对 debuggable 应用有效；su 需要 root。"
                + "当前包若为正式签名包，请改用 debug 包（如 " + KnownPaths.GamePackage + "）。"
                + (string.IsNullOrEmpty(reason) ? "" : "\n" + reason);
        }
    }
}
