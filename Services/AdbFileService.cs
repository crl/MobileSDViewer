using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MobileSDViewer.Models;

namespace MobileSDViewer.Services
{
    /// <summary>
    /// 基于 adb 的设备文件操作：列举、传输、mkdir/rm/mv。
    /// </summary>
    public class AdbFileService
    {
        private static readonly Regex DeviceExtra = new Regex(@"(\w+):(\S+)", RegexOptions.Compiled);
        private static readonly Regex LsIso = new Regex(
            @"^(?<type>[-dlbcps])(?<perm>\S+)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<datetime>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:\s+[+-]\d{4})?)\s+(?<name>.+)$",
            RegexOptions.Compiled);
        private static readonly Regex LsOld = new Regex(
            @"^(?<type>[-dlbcps])(?<perm>\S+)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<datetime>[A-Za-z]{3}\s+\d{1,2}\s+[\d:]+)\s+(?<name>.+)$",
            RegexOptions.Compiled);

        private readonly AdbProcess _proc = new AdbProcess();
        private string _adbPath;

        public string AdbPath
        {
            get => _adbPath;
            set => _adbPath = value;
        }

        public bool HasAdb => !string.IsNullOrEmpty(_adbPath) && File.Exists(_adbPath);

        public Task<AdbResult> RawAsync(IEnumerable<string> args, CancellationToken ct = default, int timeoutMs = 60000)
        {
            return _proc.RunAsync(_adbPath, args, ct, timeoutMs);
        }

        public async Task StartServerAsync(CancellationToken ct = default)
        {
            await _proc.RunAsync(_adbPath, new[] { "start-server" }, ct, 30000).ConfigureAwait(false);
        }

        public async Task<List<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
        {
            var result = await _proc.RunAsync(_adbPath, new[] { "devices", "-l" }, ct, 20000).ConfigureAwait(false);
            var list = new List<AdbDevice>();
            foreach (var raw in SplitLines(result.StdOut))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                var device = new AdbDevice
                {
                    Serial = parts[0],
                    State = parts[1]
                };
                foreach (Match m in DeviceExtra.Matches(line))
                {
                    var key = m.Groups[1].Value;
                    var val = m.Groups[2].Value;
                    if (key == "model") device.Model = val;
                    else if (key == "product") device.Product = val;
                }
                list.Add(device);
            }
            return list;
        }

        public async Task<AdbResult> ConnectAsync(string hostPort, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostPort))
                throw new ArgumentException("请输入 IP:端口，例如 192.168.1.8:5555");
            return await _proc.RunAsync(_adbPath, new[] { "connect", hostPort.Trim() }, ct, 15000).ConfigureAwait(false);
        }

        public async Task<List<RemoteFileEntry>> ListAsync(string serial, string path, AccessContext access, CancellationToken ct = default)
        {
            access = access ?? AccessContext.Shell;
            path = NormalizePath(path);
            var quoted = AdbProcess.Quote(path);

            string[] variants =
            {
                "ls -lA --time-style=full-iso -- " + quoted,
                "ls -lAe -- " + quoted,
                "ls -lA -- " + quoted,
                "ls -lA " + quoted
            };

            AdbResult last = null;
            foreach (var cmd in variants)
            {
                last = await ShellAsync(serial, access.Wrap(cmd), ct, 30000).ConfigureAwait(false);
                if (IsUnknownLsOption(last))
                    continue;
                if (IsNoSuchFile(last))
                    throw new IOException("路径不存在：" + path);
                if (IsPermissionDenied(last))
                    throw new UnauthorizedAccessException(last.Combined);
                if (!last.Success && string.IsNullOrWhiteSpace(last.StdOut))
                    throw new IOException(string.IsNullOrWhiteSpace(last.Combined) ? "列举失败" : last.Combined);

                var entries = ParseLs(last.StdOut, path);
                return entries;
            }

            throw new IOException(last == null || string.IsNullOrWhiteSpace(last.Combined) ? "列举失败" : last.Combined);
        }

        public async Task MkdirAsync(string serial, string path, AccessContext access, CancellationToken ct = default)
        {
            EnsureSafePath(path, "创建");
            var cmd = "mkdir -p -- " + AdbProcess.Quote(NormalizePath(path));
            var r = await ShellAsync(serial, access.Wrap(cmd), ct).ConfigureAwait(false);
            EnsureOk(r, "创建文件夹失败");
        }

        public async Task DeleteAsync(string serial, string path, AccessContext access, CancellationToken ct = default)
        {
            EnsureSafePath(path, "删除");
            if (IsProtectedRoot(path))
                throw new InvalidOperationException("拒绝删除系统根路径：" + path);
            var cmd = "rm -rf -- " + AdbProcess.Quote(NormalizePath(path));
            var r = await ShellAsync(serial, access.Wrap(cmd), ct).ConfigureAwait(false);
            EnsureOk(r, "删除失败");
        }

        public async Task RenameAsync(string serial, string from, string to, AccessContext access, CancellationToken ct = default)
        {
            EnsureSafePath(from, "重命名");
            EnsureSafePath(to, "重命名");
            var cmd = "mv -f -- " + AdbProcess.Quote(NormalizePath(from)) + " " + AdbProcess.Quote(NormalizePath(to));
            var r = await ShellAsync(serial, access.Wrap(cmd), ct).ConfigureAwait(false);
            EnsureOk(r, "重命名失败");
        }

        public async Task PullAsync(string serial, string remote, string local, AccessContext access, bool isDirectory, CancellationToken ct = default)
        {
            remote = NormalizePath(remote);
            access = access ?? AccessContext.Shell;
            if (access.Mode == AccessMode.Shell)
            {
                var r = await _proc.RunAsync(_adbPath, new[] { "-s", serial, "pull", remote, local }, ct, 0).ConfigureAwait(false);
                if (r.Success) return;
                throw new IOException(string.IsNullOrWhiteSpace(r.Combined) ? "下载失败" : r.Combined);
            }

            if (isDirectory)
                await PullDirectoryRecursive(serial, remote, local, access, ct).ConfigureAwait(false);
            else
                await PullFileViaCat(serial, remote, local, access, ct).ConfigureAwait(false);
        }

        public async Task PushAsync(string serial, string local, string remote, AccessContext access, CancellationToken ct = default)
        {
            remote = NormalizePath(remote);
            access = access ?? AccessContext.Shell;
            if (!File.Exists(local) && !Directory.Exists(local))
                throw new FileNotFoundException("本地路径不存在", local);

            if (access.Mode == AccessMode.Shell)
            {
                var r = await _proc.RunAsync(_adbPath, new[] { "-s", serial, "push", local, remote }, ct, 0).ConfigureAwait(false);
                if (r.Success) return;
                throw new IOException(string.IsNullOrWhiteSpace(r.Combined) ? "上传失败" : r.Combined);
            }

            if (Directory.Exists(local))
            {
                await MkdirAsync(serial, remote, access, ct).ConfigureAwait(false);
                foreach (var dir in Directory.GetDirectories(local))
                    await PushAsync(serial, dir, Join(remote, Path.GetFileName(dir)), access, ct).ConfigureAwait(false);
                foreach (var file in Directory.GetFiles(local))
                    await PushFileViaStdin(serial, file, Join(remote, Path.GetFileName(file)), access, ct).ConfigureAwait(false);
                return;
            }

            await PushFileViaStdin(serial, local, remote, access, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 将本地 APK 安装到指定设备；-r 覆盖已装同包名。
        /// </summary>
        public async Task InstallApkAsync(string serial, string apkPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath))
                throw new FileNotFoundException("APK 不存在", apkPath);
            var r = await _proc.RunAsync(_adbPath, new[] { "-s", serial, "install", "-r", apkPath }, ct, 0).ConfigureAwait(false);
            EnsureOk(r, "安装失败");
        }

        public async Task<byte[]> ReadPrefixAsync(string serial, string path, int maxBytes, AccessContext access, CancellationToken ct = default)
        {
            path = NormalizePath(path);
            access = access ?? AccessContext.Shell;
            if (maxBytes <= 0)
                maxBytes = 512 * 1024;

            byte[] lastPayload = null;
            AdbResult lastResult = null;
            foreach (var argv in BuildReadArgv(path, access))
            {
                var args = new List<string> { "-s", serial };
                args.AddRange(argv);
                var pair = await _proc.RunStdoutPrefixAsync(_adbPath, args, maxBytes, ct, 60000).ConfigureAwait(false);
                lastPayload = pair.Data;
                lastResult = pair.Result;
                if (IsMissingToolOutput(pair.Data, pair.Result))
                    continue;
                if (pair.Data != null && pair.Data.Length > 0)
                    return TruncateBytes(pair.Data, maxBytes);
                if (pair.Result != null && pair.Result.Success)
                    return pair.Data ?? new byte[0];
            }

            if (access.Mode == AccessMode.Shell)
            {
                var pulled = await TryPullPrefixAsync(serial, path, maxBytes, ct).ConfigureAwait(false);
                if (pulled != null)
                    return pulled;
            }

            var msg = lastResult == null ? "" : lastResult.Combined;
            if (lastPayload != null && lastPayload.Length > 0 && lastPayload.Length < 256)
                msg = Encoding.UTF8.GetString(lastPayload);
            throw new IOException(string.IsNullOrWhiteSpace(msg) ? "读取文件失败" : msg.Trim());
        }

        private static IEnumerable<string>[] BuildReadArgv(string path, AccessContext access)
        {
            var quoted = AdbProcess.Quote(path);
            if (access.Mode == AccessMode.RunAs)
            {
                return new[]
                {
                    new[] { "exec-out", "run-as", access.Package, "/system/bin/toybox", "cat", path },
                    new[] { "exec-out", "run-as", access.Package, "cat", path },
                    new[] { "exec-out", "sh", "-c", access.Wrap("cat -- " + quoted) }
                };
            }
            if (access.Mode == AccessMode.Root)
            {
                return new[]
                {
                    new[] { "exec-out", "su", "-c", "cat -- " + quoted },
                    new[] { "exec-out", "sh", "-c", access.Wrap("cat -- " + quoted) }
                };
            }
            return new[]
            {
                new[] { "exec-out", "/system/bin/toybox", "cat", path },
                new[] { "exec-out", "/system/bin/cat", path },
                new[] { "exec-out", "cat", path },
                new[] { "exec-out", "sh", "-c", "cat -- " + quoted }
            };
        }

        private async Task<byte[]> TryPullPrefixAsync(string serial, string path, int maxBytes, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "msdv-preview-" + Guid.NewGuid().ToString("n"));
            try
            {
                var r = await _proc.RunAsync(_adbPath, new[] { "-s", serial, "pull", path, temp }, ct, 120000).ConfigureAwait(false);
                if (!File.Exists(temp) || new FileInfo(temp).Length <= 0)
                    return null;
                return TruncateBytes(File.ReadAllBytes(temp), maxBytes);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* 预览临时文件 */ }
            }
        }

        private static bool IsMissingToolOutput(byte[] data, AdbResult result)
        {
            if (data != null && data.Length > 256)
                return false;
            var text = Encoding.UTF8.GetString(data ?? new byte[0]) + "\n" + (result == null ? "" : result.Combined);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return ContainsIgnore(text, "no such tool")
                || ContainsIgnore(text, "applet not found")
                || (ContainsIgnore(text, "not found") && !ContainsIgnore(text, "No such file or directory"));
        }

        private static byte[] TruncateBytes(byte[] bytes, int maxBytes)
        {
            if (bytes == null || bytes.Length <= maxBytes)
                return bytes ?? new byte[0];
            var cut = new byte[maxBytes];
            Buffer.BlockCopy(bytes, 0, cut, 0, maxBytes);
            return cut;
        }

        public async Task<List<string>> ListPackagesAsync(string serial, bool thirdPartyOnly, CancellationToken ct = default)
        {
            var args = thirdPartyOnly
                ? new[] { "-s", serial, "shell", "pm", "list", "packages", "-3" }
                : new[] { "-s", serial, "shell", "pm", "list", "packages" };
            var r = await _proc.RunAsync(_adbPath, args, ct, 30000).ConfigureAwait(false);
            var list = new List<string>();
            foreach (var line in SplitLines(r.StdOut))
            {
                var s = line.Trim();
                const string prefix = "package:";
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(s.Substring(prefix.Length).Trim());
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "/";
            path = path.Replace('\\', '/').Trim();
            if (path.Length > 1 && path.EndsWith("/"))
                path = path.TrimEnd('/');
            if (!path.StartsWith("/"))
                path = "/" + path;
            return path;
        }

        public static string Join(string dir, string name)
        {
            dir = NormalizePath(dir);
            name = (name ?? "").Replace('\\', '/').Trim('/');
            if (dir == "/")
                return "/" + name;
            return dir + "/" + name;
        }

        public static string Parent(string path)
        {
            path = NormalizePath(path);
            if (path == "/") return "/";
            var i = path.LastIndexOf('/');
            if (i <= 0) return "/";
            return path.Substring(0, i);
        }

        public static bool IsPermissionDenied(AdbResult r)
        {
            var t = r == null ? "" : r.Combined;
            if (string.IsNullOrEmpty(t)) return false;
            return ContainsIgnore(t, "Permission denied")
                || ContainsIgnore(t, "permission denied")
                || ContainsIgnore(t, "Not allowed")
                || ContainsIgnore(t, "Security exception")
                || ContainsIgnore(t, "not debuggable")
                || ContainsIgnore(t, "Package '") && ContainsIgnore(t, "is unknown")
                || ContainsIgnore(t, "run-as:");
        }

        public static bool IsNoSuchFile(AdbResult r)
        {
            var t = r == null ? "" : r.Combined;
            return ContainsIgnore(t, "No such file") || ContainsIgnore(t, "No such file or directory");
        }

        private async Task PullDirectoryRecursive(string serial, string remote, string local, AccessContext access, CancellationToken ct)
        {
            Directory.CreateDirectory(local);
            List<RemoteFileEntry> entries;
            try
            {
                entries = await ListAsync(serial, remote, access, ct).ConfigureAwait(false);
            }
            catch
            {
                entries = new List<RemoteFileEntry>();
            }
            foreach (var entry in entries)
            {
                var dest = Path.Combine(local, entry.Name);
                if (entry.IsDirectory)
                    await PullDirectoryRecursive(serial, entry.FullPath, dest, access, ct).ConfigureAwait(false);
                else
                    await PullFileViaCat(serial, entry.FullPath, dest, access, ct).ConfigureAwait(false);
            }
        }

        private async Task PullFileViaCat(string serial, string remote, string local, AccessContext access, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local) ?? ".");
            var cmd = "cat -- " + AdbProcess.Quote(remote);
            var r = await _proc.RunToFileAsync(
                _adbPath,
                new[] { "-s", serial, "exec-out", "sh", "-c", access.Wrap(cmd) },
                local, ct, 0).ConfigureAwait(false);
            if (!File.Exists(local))
                throw new IOException(string.IsNullOrWhiteSpace(r.Combined) ? "下载失败" : r.Combined);
        }

        private async Task PushFileViaStdin(string serial, string local, string remote, AccessContext access, CancellationToken ct)
        {
            var inner = "cat > " + AdbProcess.Quote(remote);
            var r = await _proc.RunWithInputFileAsync(
                _adbPath,
                new[] { "-s", serial, "exec-in", "sh", "-c", access.Wrap(inner) },
                local, ct, 0).ConfigureAwait(false);
            if (!r.Success && !string.IsNullOrWhiteSpace(r.Combined))
            {
                // 部分平台没有 exec-in：先 push 到 /data/local/tmp 再 cp
                var tmp = "/data/local/tmp/.msdv_" + Guid.NewGuid().ToString("n");
                var push = await _proc.RunAsync(_adbPath, new[] { "-s", serial, "push", local, tmp }, ct, 0).ConfigureAwait(false);
                if (!push.Success)
                    throw new IOException(string.IsNullOrWhiteSpace(r.Combined) ? push.Combined : r.Combined);
                var cp = await ShellAsync(serial, access.Wrap("cp -- " + AdbProcess.Quote(tmp) + " " + AdbProcess.Quote(remote)), ct).ConfigureAwait(false);
                await ShellAsync(serial, "rm -f -- " + AdbProcess.Quote(tmp), ct).ConfigureAwait(false);
                EnsureOk(cp, "上传失败");
            }
        }

        private Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct, int timeoutMs = 60000)
        {
            return _proc.RunAsync(_adbPath, new[] { "-s", serial, "shell", command }, ct, timeoutMs);
        }

        private static void EnsureOk(AdbResult r, string prefix)
        {
            if (r.Success) return;
            var msg = string.IsNullOrWhiteSpace(r.Combined) ? prefix : prefix + "：" + r.Combined;
            if (IsPermissionDenied(r))
                throw new UnauthorizedAccessException(msg);
            throw new IOException(msg);
        }

        private static bool IsUnknownLsOption(AdbResult r)
        {
            var t = r == null ? "" : r.Combined;
            return ContainsIgnore(t, "Unknown option")
                || ContainsIgnore(t, "unrecognized option")
                || ContainsIgnore(t, "illegal option")
                || ContainsIgnore(t, "invalid option");
        }

        private static List<RemoteFileEntry> ParseLs(string stdout, string dir)
        {
            var list = new List<RemoteFileEntry>();
            foreach (var raw in SplitLines(stdout))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("ls:", StringComparison.OrdinalIgnoreCase)) continue;

                var m = LsIso.Match(line);
                if (!m.Success)
                    m = LsOld.Match(line);
                if (!m.Success)
                    continue;

                var type = m.Groups["type"].Value[0];
                var name = m.Groups["name"].Value;
                if (type == 'l')
                {
                    var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow >= 0)
                        name = name.Substring(0, arrow);
                }
                if (name == "." || name == "..")
                    continue;

                long size;
                long.TryParse(m.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                DateTime dt;
                DateTime? modified = DateTime.TryParse(
                    m.Groups["datetime"].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out dt)
                    ? dt
                    : (DateTime?)null;

                list.Add(new RemoteFileEntry
                {
                    Name = name,
                    FullPath = Join(dir, name),
                    IsDirectory = type == 'd',
                    IsSymlink = type == 'l',
                    Size = size,
                    Modified = modified,
                    Permissions = type + m.Groups["perm"].Value,
                    Owner = m.Groups["owner"].Value,
                    Group = m.Groups["group"].Value
                });
            }

            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;
            using (var reader = new StringReader(text.Replace("\r\n", "\n").Replace('\r', '\n')))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    yield return line;
            }
        }

        private static void EnsureSafePath(string path, string op)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Trim() == "/" || path.Trim() == ".")
                throw new InvalidOperationException("拒绝" + op + "无效路径。");
        }

        private static bool IsProtectedRoot(string path)
        {
            path = NormalizePath(path);
            switch (path)
            {
                case "/":
                case "/sdcard":
                case "/storage":
                case "/storage/emulated":
                case "/storage/emulated/0":
                case "/data":
                case "/data/data":
                case "/system":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ContainsIgnore(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
