using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MobileSDViewer
{
    /// <summary>
    /// 应用程序入口；未处理异常写入 crash.log，避免无控制台时无法排查。
    /// </summary>
    public partial class App
    {
        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.Exception);
            MessageBox.Show($"error:{e.Exception.Message}\r\n stackTrace:{e.Exception.StackTrace}", "Error");
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = (Exception)e.ExceptionObject;
            WriteCrashLog(ex);
            MessageBox.Show($"error:{ex.Message}\r\n stackTrace:{ex.StackTrace}\r\n {e.IsTerminating}", "Error");
        }

        /// <summary>
        /// 将未处理异常写入程序目录 crash.log。
        /// </summary>
        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                var sb = new StringBuilder();
                sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(ex?.ToString() ?? "(null)");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 写盘失败时不再抛，避免二次崩溃
            }
        }
    }
}
