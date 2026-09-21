using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MobileSDViewer.Services
{
    /// <summary>
    /// 一次 adb 调用的文本结果。
    /// </summary>
    public class AdbResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public bool Success => ExitCode == 0;

        public string Combined => ((StdOut ?? "") + "\n" + (StdErr ?? "")).Trim();
    }

    /// <summary>
    /// 异步启动 adb.exe，不经过 cmd，避免路径与引号被二次解析。
    /// </summary>
    public class AdbProcess
    {
        /// <summary>POSIX 单引号转义，供 remote shell 使用。</summary>
        public static string Quote(string value)
        {
            if (value == null)
                return "''";
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        public Task<AdbResult> RunAsync(string adbPath, IEnumerable<string> args, CancellationToken ct = default, int timeoutMs = 60000)
        {
            return RunAsync(adbPath, args, null, null, ct, timeoutMs, 0);
        }

        /// <summary>
        /// 只取 stdout 前 maxBytes 字节（用于预览）；读满后结束进程，避免把整份大文件拉下来。
        /// </summary>
        public async Task<(byte[] Data, AdbResult Result)> RunStdoutPrefixAsync(
            string adbPath, IEnumerable<string> args, int maxBytes, CancellationToken ct = default, int timeoutMs = 60000)
        {
            using (var ms = new MemoryStream())
            {
                var result = await RunAsync(adbPath, args, null, ms, ct, timeoutMs, maxBytes).ConfigureAwait(false);
                return (ms.ToArray(), result);
            }
        }

        /// <summary>
        /// 把 stdout 写成文件（pull/cat 大文件），同时收集 stderr 文本。
        /// </summary>
        public async Task<AdbResult> RunToFileAsync(string adbPath, IEnumerable<string> args, string outputFile, CancellationToken ct = default, int timeoutMs = 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
            using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                return await RunAsync(adbPath, args, stdinFile: null, stdoutStream: fs, ct, timeoutMs, 0).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 把本地文件通过 stdin 喂给 `adb exec-in`（run-as/root 上传回退）。
        /// </summary>
        public async Task<AdbResult> RunWithInputFileAsync(string adbPath, IEnumerable<string> args, string inputFile, CancellationToken ct = default, int timeoutMs = 0)
        {
            return await RunAsync(adbPath, args, stdinFile: inputFile, stdoutStream: null, ct, timeoutMs, 0).ConfigureAwait(false);
        }

        private async Task<AdbResult> RunAsync(
            string adbPath,
            IEnumerable<string> args,
            string stdinFile,
            Stream stdoutStream,
            CancellationToken ct,
            int timeoutMs,
            int maxStdoutBytes)
        {
            if (string.IsNullOrEmpty(adbPath) || !File.Exists(adbPath))
                throw new InvalidOperationException("未找到 adb.exe，请在顶栏指定 platform-tools 路径。");

            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinFile != null,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
            {
                if (arg != null)
                    psi.ArgumentList.Add(arg);
            }

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var stdoutDone = new TaskCompletionSource<bool>();
                var stderrDone = new TaskCompletionSource<bool>();

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null)
                    {
                        stdoutDone.TrySetResult(true);
                        return;
                    }
                    if (stdoutStream == null)
                    {
                        if (stdout.Length > 0) stdout.Append('\n');
                        stdout.Append(e.Data);
                    }
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null)
                    {
                        stderrDone.TrySetResult(true);
                        return;
                    }
                    if (stderr.Length > 0) stderr.Append('\n');
                    stderr.Append(e.Data);
                };

                if (!proc.Start())
                    throw new InvalidOperationException("启动 adb 失败。");

                // 二进制 stdout 必须边读边等进程退出，否则管道填满会把 adb 卡死
                Task copyOut = Task.CompletedTask;
                if (stdoutStream != null)
                {
                    copyOut = maxStdoutBytes > 0
                        ? CopyPrefixAsync(proc.StandardOutput.BaseStream, stdoutStream, maxStdoutBytes, proc, ct)
                        : proc.StandardOutput.BaseStream.CopyToAsync(stdoutStream, 81920, ct);
                }
                else
                    proc.BeginOutputReadLine();

                proc.BeginErrorReadLine();

                Task copyIn = Task.CompletedTask;
                if (stdinFile != null)
                {
                    copyIn = Task.Run(async () =>
                    {
                        using (var input = File.OpenRead(stdinFile))
                        {
                            await input.CopyToAsync(proc.StandardInput.BaseStream, 81920, ct).ConfigureAwait(false);
                        }
                        proc.StandardInput.Close();
                    }, ct);
                }

                using (var timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null)
                using (var linked = timeoutCts != null
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                    : null)
                {
                    var waitToken = linked != null ? linked.Token : ct;
                    try
                    {
                        var waitTask = WaitForExitAsync(proc, waitToken);
                        await Task.WhenAll(copyOut, copyIn, waitTask).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        TryKill(proc);
                        if (timeoutCts != null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                            throw new TimeoutException("adb 命令超时。");
                        throw;
                    }
                }

                if (stdoutStream == null)
                    await stdoutDone.Task.ConfigureAwait(false);
                await stderrDone.Task.ConfigureAwait(false);

                return new AdbResult
                {
                    ExitCode = proc.ExitCode,
                    StdOut = stdout.ToString(),
                    StdErr = stderr.ToString()
                };
            }
        }

        private static async Task CopyPrefixAsync(Stream src, Stream dest, int maxBytes, Process proc, CancellationToken ct)
        {
            var buffer = new byte[81920];
            var remaining = maxBytes;
            while (remaining > 0)
            {
                var read = await src.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining), ct).ConfigureAwait(false);
                if (read <= 0)
                    return;
                await dest.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                remaining -= read;
            }
            TryKill(proc);
        }

        private static async Task WaitForExitAsync(Process proc, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            void Handler(object s, EventArgs e) => tcs.TrySetResult(true);
            proc.Exited += Handler;
            try
            {
                if (proc.HasExited)
                    return;
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                proc.Exited -= Handler;
            }
        }

        private static void TryKill(Process proc)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(true);
            }
            catch
            {
                // 进程已退出时 Kill 可能抛
            }
        }
    }
}
