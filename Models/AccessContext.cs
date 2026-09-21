using MobileSDViewer.Services;

namespace MobileSDViewer.Models
{
    /// <summary>
    /// 当前远程命令的身份：普通 shell、run-as 某包、或 su。
    /// </summary>
    public enum AccessMode
    {
        Shell,
        RunAs,
        Root
    }

    /// <summary>
    /// 访问模式上下文；后续 mkdir/pull 等必须与列举时成功的模式一致。
    /// </summary>
    public class AccessContext
    {
        public AccessMode Mode { get; set; }
        /// <summary>run-as 时的应用包名</summary>
        public string Package { get; set; }

        public static AccessContext Shell => new AccessContext { Mode = AccessMode.Shell };

        public static AccessContext ForRunAs(string package) =>
            new AccessContext { Mode = AccessMode.RunAs, Package = package };

        public static AccessContext Root => new AccessContext { Mode = AccessMode.Root };

        public string DisplayLabel
        {
            get
            {
                switch (Mode)
                {
                    case AccessMode.RunAs:
                        return "run-as " + Package;
                    case AccessMode.Root:
                        return "root (su)";
                    default:
                        return "Shell";
                }
            }
        }

        /// <summary>
        /// 把一条 toybox/shell 命令包进当前身份。command 内路径应已按 POSIX 单引号转义。
        /// </summary>
        public string Wrap(string command)
        {
            if (string.IsNullOrEmpty(command))
                return command;
            switch (Mode)
            {
                case AccessMode.RunAs:
                    return "run-as " + Package + " sh -c " + AdbProcess.Quote(command);
                case AccessMode.Root:
                    return "su -c " + AdbProcess.Quote(command);
                default:
                    return command;
            }
        }
    }
}
