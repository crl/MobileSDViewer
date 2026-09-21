using System.Windows;

namespace MobileSDViewer.Dialogs
{
    /// <summary>
    /// 简单输入框：重命名、新建文件夹、adb connect。
    /// </summary>
    public partial class PromptWindow
    {
        public string Value => InputBox.Text;

        public PromptWindow(string title, string message, string initial)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            InputBox.Text = initial ?? "";
            Loaded += (s, e) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        public static string Show(Window owner, string title, string message, string initial)
        {
            var w = new PromptWindow(title, message, initial) { Owner = owner };
            return w.ShowDialog() == true ? w.Value : null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
