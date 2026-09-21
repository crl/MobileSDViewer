namespace MobileSDViewer.Models
{
    /// <summary>
    /// 本地记住的窗口状态：adb 路径、上次设备与目录。
    /// </summary>
    public class AppSettings
    {
        public string AdbPath { get; set; }
        public string LastDeviceSerial { get; set; }
        public string LastRemotePath { get; set; }
        public string LastDownloadDir { get; set; }
    }
}
