using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MobileSDViewer.Models;

namespace MobileSDViewer.Dialogs
{
    /// <summary>
    /// 从 `pm list packages` 选择一个包，用于 Android/data 回退。
    /// </summary>
    public partial class PackagePickerWindow
    {
        private readonly List<string> _thirdParty;
        private readonly Func<bool, List<string>> _loadAll;
        private List<string> _all;

        public string SelectedPackage { get; private set; }

        public PackagePickerWindow(List<string> thirdParty, Func<bool, List<string>> loadAll)
        {
            InitializeComponent();
            _thirdParty = thirdParty ?? new List<string>();
            _loadAll = loadAll;
            Bind(CurrentSource());
        }

        public static string Show(Window owner, List<string> thirdParty, Func<bool, List<string>> loadAll)
        {
            var w = new PackagePickerWindow(thirdParty, loadAll) { Owner = owner };
            return w.ShowDialog() == true ? w.SelectedPackage : null;
        }

        private List<string> CurrentSource()
        {
            if (SystemCheck.IsChecked == true)
            {
                if (_all == null && _loadAll != null)
                    _all = _loadAll(false) ?? new List<string>();
                return _all ?? _thirdParty;
            }
            return _thirdParty;
        }

        private void Bind(List<string> source)
        {
            var filter = (FilterBox.Text ?? "").Trim();
            var list = string.IsNullOrEmpty(filter)
                ? source.ToList()
                : source.Where(p => p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (list.Remove(KnownPaths.GamePackage))
                list.Insert(0, KnownPaths.GamePackage);
            PackageList.ItemsSource = list;
            if (list.Count > 0)
                PackageList.SelectedIndex = 0;
        }

        private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            Bind(CurrentSource());
        }

        private void SystemCheck_Click(object sender, RoutedEventArgs e)
        {
            Bind(CurrentSource());
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Accept();
        }

        private void PackageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Accept();
        }

        private void Accept()
        {
            var pkg = PackageList.SelectedItem as string;
            if (string.IsNullOrEmpty(pkg))
            {
                MessageBox.Show(this, "请选择一个包名。", "MobileSDViewer");
                return;
            }
            SelectedPackage = pkg;
            DialogResult = true;
        }
    }
}
