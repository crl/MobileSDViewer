namespace MobileSDViewer.Models
{
    /// <summary>
    /// `adb devices -l` 解析出的一台设备。
    /// </summary>
    public class AdbDevice
    {
        public string Serial { get; set; }
        /// <summary>device / unauthorized / offline / recovery 等</summary>
        public string State { get; set; }
        public string Model { get; set; }
        public string Product { get; set; }

        public bool IsReady => State == "device";

        public string DisplayName
        {
            get
            {
                var name = string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ');
                if (IsReady)
                    return string.IsNullOrEmpty(Model) ? Serial : $"{name}  ({Serial})";
                return $"{name}  [{State}]  ({Serial})";
            }
        }

        public override string ToString() => DisplayName;
    }
}
