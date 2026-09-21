using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MobileSDViewer.Dialogs;
using MobileSDViewer.Models;
using MobileSDViewer.Services;

namespace MobileSDViewer
{
    /// <summary>
    /// ADB 设备文件管理主窗口：设备切换、目录浏览、传输与预览。
    /// </summary>
    public partial class MainWindow
    {
        private readonly AdbFileService _files = new AdbFileService();
        private readonly AndroidDataAccess _androidData;
        private readonly PreviewService _preview;
        private readonly ObservableCollection<AdbDevice> _devices = new ObservableCollection<AdbDevice>();
        private readonly ObservableCollection<RemoteFileEntry> _entries = new ObservableCollection<RemoteFileEntry>();
        private readonly ObservableCollection<FolderTreeItem> _treeRoots = new ObservableCollection<FolderTreeItem>();
        private AppSettings _settings;
        private AccessContext _access = AccessContext.Shell;
        private string _currentPath = KnownPaths.Sdcard;
        private bool _busy;
        private bool _syncingTree;
        private bool _loadingDevices;
        private int _previewSeq;
        private CancellationTokenSource _previewCts;
        private readonly List<int> _previewMatches = new List<int>();
        private int _previewMatchIndex = -1;
        private int _previewMatchLength;
        private string _previewPlainText = "";
        private static readonly SolidColorBrush PreviewHitBrush = CreateFrozenBrush(0x5A, 0x4A, 0x18);
        private static readonly SolidColorBrush PreviewHitCurrentBrush = CreateFrozenBrush(0xF7, 0xC9, 0x48);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public MainWindow()
        {
            InitializeComponent();
            _androidData = new AndroidDataAccess(_files);
            _preview = new PreviewService(_files);
            DeviceCombo.ItemsSource = _devices;
            FileList.ItemsSource = _entries;
            FolderTree.ItemsSource = _treeRoots;
            ConnectHostBox.Text = "192.168.1.1:5555";
        }

        private AdbDevice SelectedDevice => DeviceCombo.SelectedItem as AdbDevice;
        private string Serial => SelectedDevice?.Serial;
        private bool DeviceReady => SelectedDevice != null && SelectedDevice.IsReady;

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
            var found = AdbLocator.Find(_settings.AdbPath);
            AdbPathBox.Text = found ?? _settings.AdbPath ?? "";
            ApplyAdbPath(AdbPathBox.Text);
            InitTreeRoots();
            if (_files.HasAdb)
            {
                try
                {
                    await _files.StartServerAsync();
                }
                catch (Exception ex)
                {
                    Log("启动 adb server 失败：" + ex.Message);
                }
                await RefreshDevicesAsync(selectSerial: _settings.LastDeviceSerial);
            }
            else
            {
                SetAdbStatus("未找到 adb.exe", false);
                Log("未找到 adb.exe。请安装 Android SDK platform-tools，或点击「浏览」选择。");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_settings == null)
                _settings = new AppSettings();
            _settings.AdbPath = AdbPathBox.Text;
            _settings.LastDeviceSerial = Serial;
            _settings.LastRemotePath = _currentPath;
            SettingsStore.Save(_settings);
            _previewCts?.Cancel();
        }

        private void ApplyAdbPath(string path)
        {
            _files.AdbPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
            if (_files.HasAdb)
                SetAdbStatus("已找到", true);
            else
                SetAdbStatus("未找到 adb.exe", false);
        }

        private void SetAdbStatus(string text, bool ok)
        {
            AdbStatusText.Text = text;
            AdbStatusText.Foreground = ok
                ? (Brush)FindResource("BrushSuccess")
                : (Brush)FindResource("BrushDanger");
        }

        private void AdbPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyAdbPath(AdbPathBox.Text);
        }

        private void BrowseAdb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 adb.exe",
                Filter = "adb.exe|adb.exe|可执行文件|*.exe",
                FileName = "adb.exe"
            };
            if (dlg.ShowDialog(this) != true)
                return;
            AdbPathBox.Text = dlg.FileName;
            ApplyAdbPath(dlg.FileName);
            Log("已设置 ADB：" + dlg.FileName);
        }

        private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            ApplyAdbPath(AdbPathBox.Text);
            await RefreshDevicesAsync(Serial);
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureAdb()) return;
            var host = (ConnectHostBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(host))
            {
                host = PromptWindow.Show(this, "连接设备", "输入 IP:端口（需设备已执行 adb tcpip 或无线调试）：", "192.168.1.1:5555");
                if (string.IsNullOrWhiteSpace(host))
                    return;
                ConnectHostBox.Text = host.Trim();
            }
            await RunBusy("正在连接 " + host, async () =>
            {
                var r = await _files.ConnectAsync(host);
                Log(string.IsNullOrWhiteSpace(r.Combined) ? "connect 完成" : r.Combined);
            });
            await RefreshDevicesAsync(host);
        }

        private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingDevices) return;
            UpdateAccessLabel();
            if (!DeviceReady)
            {
                if (SelectedDevice != null && SelectedDevice.State == "unauthorized")
                    Log("设备未授权：请在手机上点「允许 USB 调试」。");
                return;
            }
            var target = !string.IsNullOrEmpty(_settings?.LastRemotePath) ? _settings.LastRemotePath : KnownPaths.Sdcard;
            await NavigateAsync(target, resetAccess: true);
        }

        private async void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            await NavigateAsync(path, resetAccess: true);
        }

        private async void AndroidData_Click(object sender, RoutedEventArgs e)
        {
            await NavigateAsync(KnownPaths.AndroidData, resetAccess: true);
        }

        private async void GamePackage_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            await RunBusy("打开本游戏数据目录", async () =>
            {
                var opened = await _androidData.OpenPackageAsync(Serial, KnownPaths.GamePackage);
                ApplyListing(opened.Path, opened.Entries, opened.Access);
            });
        }

        private async void InstallApk_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择要安装的 APK",
                Filter = "APK|*.apk"
            };
            if (dlg.ShowDialog(this) != true) return;
            var apkPath = dlg.FileName;
            var name = Path.GetFileName(apkPath);
            await RunBusy("安装 " + name, async () =>
            {
                Log("安装 " + apkPath + " → " + Serial);
                await _files.InstallApkAsync(Serial, apkPath);
                Log("安装完成：" + name);
            });
        }

        private async void Parent_Click(object sender, RoutedEventArgs e)
        {
            await NavigateAsync(AdbFileService.Parent(_currentPath));
        }

        private async void RefreshFolder_Click(object sender, RoutedEventArgs e)
        {
            await NavigateAsync(_currentPath);
        }

        private async void GoPath_Click(object sender, RoutedEventArgs e)
        {
            await NavigateAsync(PathBox.Text, resetAccess: true);
        }

        private async void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await NavigateAsync(PathBox.Text, resetAccess: true);
            }
        }

        private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
        {
            var path = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            await NavigateAsync(path, resetAccess: true);
        }

        private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_syncingTree) return;
            var node = FolderTree.SelectedItem as FolderTreeItem;
            if (node == null || node.IsDummy || string.IsNullOrEmpty(node.FullPath))
                return;
            await NavigateAsync(node.FullPath, resetAccess: true, selectTree: false);
        }

        private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
        {
            var item = e.OriginalSource as TreeViewItem;
            var node = item?.DataContext as FolderTreeItem;
            if (node == null || node.IsDummy || node.IsLoaded)
                return;
            await LoadTreeChildrenAsync(node);
        }

        private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var entry = FileList.SelectedItem as RemoteFileEntry;
            if (entry == null) return;
            if (entry.IsDirectory || entry.IsSymlink)
                await NavigateAsync(entry.FullPath);
        }

        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await UpdatePreviewAsync();
        }

        private async void FileList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await NavigateAsync(_currentPath);
            }
            else if (e.Key == Key.Back)
            {
                e.Handled = true;
                await NavigateAsync(AdbFileService.Parent(_currentPath));
            }
            else if (e.Key == Key.Enter)
            {
                var entry = FileList.SelectedItem as RemoteFileEntry;
                if (entry != null && (entry.IsDirectory || entry.IsSymlink))
                {
                    e.Handled = true;
                    await NavigateAsync(entry.FullPath);
                }
            }
            else if (e.Key == Key.F2)
            {
                e.Handled = true;
                Rename_Click(sender, e);
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                Delete_Click(sender, e);
            }
        }

        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void FileList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            await UploadLocalPathsAsync(paths);
        }

        private async void ToolbarDownload_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedEntries().Count > 0)
            {
                Download_Click(sender, e);
                return;
            }
            await DownloadCurrentFolderAsync();
        }

        /// <summary>
        /// 未选中列表项时，把当前浏览目录整夹 pull 到本地。
        /// </summary>
        private async Task DownloadCurrentFolderAsync()
        {
            if (!EnsureDevice()) return;
            var remote = AdbFileService.NormalizePath(_currentPath);
            if (remote == "/")
            {
                MessageBox.Show(this, "不能下载根目录 /，请先进入具体文件夹。", "下载");
                return;
            }

            var folderDlg = new OpenFolderDialog { Title = "下载当前文件夹到" };
            if (!string.IsNullOrEmpty(_settings.LastDownloadDir) && Directory.Exists(_settings.LastDownloadDir))
                folderDlg.InitialDirectory = _settings.LastDownloadDir;
            if (folderDlg.ShowDialog(this) != true) return;
            _settings.LastDownloadDir = folderDlg.FolderName;

            var name = remote.Trim('/').Replace('/', '_');
            if (string.IsNullOrEmpty(name))
                name = "android";
            var dest = Path.Combine(folderDlg.FolderName, name);
            await RunBusy("下载当前文件夹 " + remote, () =>
                _files.PullAsync(Serial, remote, dest, _access, true));
            Log("已下载当前文件夹：" + dest);
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var selected = SelectedEntries();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请先选择要下载的文件或文件夹。", "下载");
                return;
            }

            if (selected.Count == 1 && !selected[0].IsDirectory)
            {
                var dlg = new SaveFileDialog
                {
                    FileName = selected[0].Name,
                    Title = "下载到"
                };
                if (!string.IsNullOrEmpty(_settings.LastDownloadDir) && Directory.Exists(_settings.LastDownloadDir))
                    dlg.InitialDirectory = _settings.LastDownloadDir;
                if (dlg.ShowDialog(this) != true) return;
                _settings.LastDownloadDir = Path.GetDirectoryName(dlg.FileName);
                await RunBusy("下载 " + selected[0].Name, () =>
                    _files.PullAsync(Serial, selected[0].FullPath, dlg.FileName, _access, false));
                Log("已下载：" + dlg.FileName);
                return;
            }

            var folderDlg = new OpenFolderDialog { Title = "选择下载目录" };
            if (!string.IsNullOrEmpty(_settings.LastDownloadDir) && Directory.Exists(_settings.LastDownloadDir))
                folderDlg.InitialDirectory = _settings.LastDownloadDir;
            if (folderDlg.ShowDialog(this) != true) return;
            _settings.LastDownloadDir = folderDlg.FolderName;
            await RunBusy("下载 " + selected.Count + " 项", async () =>
            {
                foreach (var entry in selected)
                {
                    var dest = Path.Combine(folderDlg.FolderName, entry.Name);
                    Log("拉取 " + entry.FullPath);
                    await _files.PullAsync(Serial, entry.FullPath, dest, _access, entry.IsDirectory);
                }
            });
            Log("下载完成：" + folderDlg.FolderName);
        }

        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var dlg = new OpenFileDialog
            {
                Title = "上传到 " + _currentPath,
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;
            await UploadLocalPathsAsync(dlg.FileNames);
        }

        private async void Mkdir_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var name = PromptWindow.Show(this, "新建文件夹", "文件夹名称：", "NewFolder");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                MessageBox.Show(this, "名称不能包含路径分隔符。", "新建文件夹");
                return;
            }
            var dest = AdbFileService.Join(_currentPath, name.Trim());
            await RunBusy("创建文件夹", async () =>
            {
                await _files.MkdirAsync(Serial, dest, _access);
                await NavigateAsync(_currentPath);
            });
        }

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var entry = FileList.SelectedItem as RemoteFileEntry;
            if (entry == null)
            {
                MessageBox.Show(this, "请选择要重命名的项。", "重命名");
                return;
            }
            var name = PromptWindow.Show(this, "重命名", "新名称：", entry.Name);
            if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                MessageBox.Show(this, "名称不能包含路径分隔符。", "重命名");
                return;
            }
            var dest = AdbFileService.Join(_currentPath, name.Trim());
            await RunBusy("重命名", async () =>
            {
                await _files.RenameAsync(Serial, entry.FullPath, dest, _access);
                await NavigateAsync(_currentPath);
            });
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureDevice()) return;
            var selected = SelectedEntries();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请选择要删除的项。", "删除");
                return;
            }
            var names = string.Join("\n", selected.Select(s => s.FullPath));
            var confirm = MessageBox.Show(
                this,
                "确定删除以下 " + selected.Count + " 项？此操作不可恢复。\n\n" + names,
                "删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            await RunBusy("删除", async () =>
            {
                foreach (var entry in selected)
                    await _files.DeleteAsync(Serial, entry.FullPath, _access);
                await NavigateAsync(_currentPath);
            });
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var entry = FileList.SelectedItem as RemoteFileEntry;
            var path = entry != null ? entry.FullPath : _currentPath;
            Clipboard.SetText(path ?? "");
            Log("已复制路径：" + path);
        }

        private async Task UploadLocalPathsAsync(string[] localPaths)
        {
            if (!EnsureDevice()) return;
            await RunBusy("上传 " + localPaths.Length + " 项", async () =>
            {
                foreach (var local in localPaths)
                {
                    var name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var remote = AdbFileService.Join(_currentPath, name);
                    if (_entries.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var overwrite = MessageBox.Show(
                            this,
                            name + " 已存在，是否覆盖？",
                            "上传",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        if (overwrite != MessageBoxResult.Yes)
                            continue;
                    }
                    Log("推送 " + local + " → " + remote);
                    await _files.PushAsync(Serial, local, remote, _access);
                }
                await NavigateAsync(_currentPath);
            });
        }

        private async Task RefreshDevicesAsync(string selectSerial)
        {
            if (!EnsureAdb()) return;
            await RunBusy("刷新设备", async () =>
            {
                _loadingDevices = true;
                try
                {
                    var list = await _files.ListDevicesAsync();
                    _devices.Clear();
                    foreach (var d in list)
                        _devices.Add(d);
                    AdbDevice pick = null;
                    if (!string.IsNullOrEmpty(selectSerial))
                        pick = _devices.FirstOrDefault(d => d.Serial == selectSerial);
                    if (pick == null)
                        pick = _devices.FirstOrDefault(d => d.IsReady) ?? _devices.FirstOrDefault();
                    DeviceCombo.SelectedItem = pick;
                    Log(list.Count == 0 ? "未发现设备。请打开 USB 调试并授权。" : "发现 " + list.Count + " 台设备。");
                }
                finally
                {
                    _loadingDevices = false;
                }
            });
            if (DeviceReady)
            {
                var target = !string.IsNullOrEmpty(_settings?.LastRemotePath) ? _settings.LastRemotePath : KnownPaths.Sdcard;
                await NavigateAsync(target, resetAccess: true);
            }
        }

        private async Task NavigateAsync(string path, bool resetAccess = false, bool selectTree = true)
        {
            if (!EnsureDevice()) return;
            path = AdbFileService.NormalizePath(path);
            if (resetAccess)
                _access = AccessContext.Shell;

            await RunBusy("打开 " + path, async () =>
            {
                try
                {
                    var result = await _androidData.ListSmartAsync(Serial, path);
                    ApplyListing(path, result.Entries, result.Access);
                    if (selectTree)
                        TrySelectTree(path);
                }
                catch (NeedPackageException ex)
                {
                    Log(ex.Message);
                    var pkg = await PickPackageAsync();
                    if (string.IsNullOrEmpty(pkg))
                        return;
                    var opened = await _androidData.OpenPackageAsync(Serial, pkg);
                    ApplyListing(opened.Path, opened.Entries, opened.Access);
                    if (selectTree)
                        TrySelectTree(opened.Path);
                }
            });
        }

        private void ApplyListing(string path, List<RemoteFileEntry> entries, AccessContext access)
        {
            _currentPath = path;
            _access = access ?? AccessContext.Shell;
            _settings.LastRemotePath = path;
            PathBox.Text = path;
            UpdateAccessLabel();
            RebuildBreadcrumb(path);
            _entries.Clear();
            foreach (var entry in entries)
                _entries.Add(entry);
            StatusText.Text = path + "  ·  " + entries.Count + " 项  ·  " + _access.DisplayLabel;
            ClearPreview("选择一个文件以预览。");
        }

        private async Task<string> PickPackageAsync()
        {
            List<string> packages;
            try
            {
                packages = await _files.ListPackagesAsync(Serial, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取包列表失败：" + ex.Message, "Android/data");
                return null;
            }
            return PackagePickerWindow.Show(this, packages, thirdParty =>
            {
                try
                {
                    return _files.ListPackagesAsync(Serial, thirdParty).GetAwaiter().GetResult();
                }
                catch
                {
                    return new List<string>();
                }
            });
        }

        private async Task UpdatePreviewAsync()
        {
            var seq = ++_previewSeq;
            var entry = FileList.SelectedItem as RemoteFileEntry;
            if (entry == null || entry.IsDirectory)
            {
                ClearPreview(entry == null ? "选择一个文件以预览。" : "文件夹：" + entry.FullPath);
                return;
            }

            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var ct = _previewCts.Token;
            ClearPreview("正在预览 " + entry.Name + " …");
            try
            {
                await Task.Delay(180, ct);
                if (seq != _previewSeq) return;
                var result = await _preview.PreviewAsync(Serial, entry, _access, ct);
                if (seq != _previewSeq || ct.IsCancellationRequested) return;
                PreviewMetaBox.Text = result.Meta ?? "";
                PreviewImage.Source = result.Image;
                PreviewImage.Visibility = result.Kind == PreviewKind.Image ? Visibility.Visible : Visibility.Collapsed;
                SetPreviewPlainText(result.Text ?? "");
                PreviewTextBox.Visibility = result.Kind == PreviewKind.Text ? Visibility.Visible : Visibility.Collapsed;
                PreviewSearchBar.Visibility = result.Kind == PreviewKind.Text ? Visibility.Visible : Visibility.Collapsed;
                if (result.Kind == PreviewKind.Text && string.IsNullOrEmpty(result.Text))
                    PreviewMetaBox.Text = (result.Meta ?? "") + "\n（文件为空）";
                if (result.Kind == PreviewKind.Text)
                    ApplyPreviewSearch(resetIndex: true);
            }
            catch (OperationCanceledException)
            {
                // 切换选中项
            }
            catch (Exception ex)
            {
                if (seq == _previewSeq)
                    ClearPreview("预览失败：" + ex.Message);
            }
        }

        private void ClearPreview(string meta)
        {
            PreviewMetaBox.Text = meta ?? "";
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            SetPreviewPlainText("");
            PreviewTextBox.Visibility = Visibility.Collapsed;
            PreviewSearchBar.Visibility = Visibility.Collapsed;
            _previewMatches.Clear();
            _previewMatchIndex = -1;
            PreviewSearchCount.Text = "";
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                && PreviewTextBox.Visibility == Visibility.Visible)
            {
                PreviewSearchBar.Visibility = Visibility.Visible;
                PreviewSearchBox.Focus();
                PreviewSearchBox.SelectAll();
                e.Handled = true;
            }
        }

        private void PreviewSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyPreviewSearch(resetIndex: true);
        }

        private void PreviewSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    MovePreviewMatch(-1);
                else
                    MovePreviewMatch(1);
            }
            else if (e.Key == Key.Escape)
            {
                PreviewSearchBox.Text = "";
                e.Handled = true;
            }
        }

        private void PreviewSearchNext_Click(object sender, RoutedEventArgs e)
        {
            MovePreviewMatch(1);
        }

        private void PreviewSearchPrev_Click(object sender, RoutedEventArgs e)
        {
            MovePreviewMatch(-1);
        }

        /// <summary>
        /// 在当前预览文本中查找全部匹配（不区分大小写），并跳到第一条或保持当前位置。
        /// </summary>
        private void ApplyPreviewSearch(bool resetIndex)
        {
            var needle = PreviewSearchBox.Text ?? "";
            var hay = _previewPlainText ?? "";
            _previewMatches.Clear();
            _previewMatchLength = needle.Length;
            if (needle.Length > 0 && hay.Length > 0)
            {
                var start = 0;
                while (start <= hay.Length - needle.Length)
                {
                    var i = hay.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) break;
                    _previewMatches.Add(i);
                    start = i + needle.Length;
                }
            }

            if (resetIndex || _previewMatchIndex < 0 || _previewMatchIndex >= _previewMatches.Count)
                _previewMatchIndex = _previewMatches.Count == 0 ? -1 : 0;

            if (_previewMatches.Count == 0)
                PreviewSearchCount.Text = needle.Length == 0 ? "" : "0/0";
            else
                PreviewSearchCount.Text = (_previewMatchIndex + 1) + "/" + _previewMatches.Count;

            SetPreviewPlainText(hay);
            if (_previewMatches.Count == 0)
                return;
            HighlightPreviewMatch();
        }

        private void MovePreviewMatch(int delta)
        {
            if (_previewMatches.Count == 0)
            {
                ApplyPreviewSearch(resetIndex: true);
                return;
            }
            _previewMatchIndex = (_previewMatchIndex + delta) % _previewMatches.Count;
            if (_previewMatchIndex < 0)
                _previewMatchIndex += _previewMatches.Count;
            SetPreviewPlainText(_previewPlainText);
            HighlightPreviewMatch();
        }

        private void HighlightPreviewMatch()
        {
            if (_previewMatchIndex < 0 || _previewMatchIndex >= _previewMatches.Count)
                return;
            PreviewSearchCount.Text = (_previewMatchIndex + 1) + "/" + _previewMatches.Count;
            ApplyPreviewHighlights();
            var current = _previewMatches[_previewMatchIndex];
            var start = PointerFromCharIndex(PreviewTextBox.Document, current);
            Dispatcher.BeginInvoke(new Action(() => ScrollPreviewToPointer(start)), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 用单个 Run 装入纯文本，保证搜索下标与 TextPointer 一一对应。
        /// </summary>
        private void SetPreviewPlainText(string text)
        {
            _previewPlainText = text ?? "";
            var para = new Paragraph { Margin = new Thickness(0) };
            para.Inlines.Add(new Run(_previewPlainText));
            PreviewTextBox.Document.Blocks.Clear();
            PreviewTextBox.Document.Blocks.Add(para);
        }

        private void ApplyPreviewHighlights()
        {
            var doc = PreviewTextBox.Document;
            var shown = 0;
            const int maxOther = 80;
            for (var n = 0; n < _previewMatches.Count; n++)
            {
                if (n != _previewMatchIndex && shown >= maxOther)
                    continue;
                var i = _previewMatches[n];
                var a = PointerFromCharIndex(doc, i);
                var b = PointerFromCharIndex(doc, i + _previewMatchLength);
                if (a == null || b == null)
                    continue;
                var range = new TextRange(a, b);
                range.ApplyPropertyValue(TextElement.BackgroundProperty,
                    n == _previewMatchIndex ? PreviewHitCurrentBrush : PreviewHitBrush);
                if (n != _previewMatchIndex)
                    shown++;
            }
        }

        private void ScrollPreviewToPointer(TextPointer pointer)
        {
            if (pointer == null)
                return;
            PreviewTextBox.UpdateLayout();
            var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
            var sv = FindVisualChild<ScrollViewer>(PreviewTextBox);
            if (sv == null || rect.IsEmpty || double.IsInfinity(rect.Top))
                return;
            var offset = sv.VerticalOffset + rect.Top - 32;
            if (offset < 0)
                offset = 0;
            sv.ScrollToVerticalOffset(offset);
        }

        private static TextPointer PointerFromCharIndex(FlowDocument doc, int index)
        {
            if (doc == null || index < 0)
                return doc == null ? null : doc.ContentStart;
            var nav = doc.ContentStart;
            var remaining = index;
            while (nav != null)
            {
                if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    var len = nav.GetTextRunLength(LogicalDirection.Forward);
                    if (remaining <= len)
                        return nav.GetPositionAtOffset(remaining);
                    remaining -= len;
                    nav = nav.GetPositionAtOffset(len);
                }
                else
                {
                    nav = nav.GetNextContextPosition(LogicalDirection.Forward);
                }
            }
            return doc.ContentEnd;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var typed = child as T;
                if (typed != null)
                    return typed;
                var nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static FolderTreeItem CreateRoot(string name, string path, string glyph)
        {
            var node = new FolderTreeItem { Name = name, FullPath = path, Glyph = glyph };
            node.EnsurePlaceholder();
            return node;
        }

        private void InitTreeRoots()
        {
            _treeRoots.Clear();
            _treeRoots.Add(CreateRoot("SD卡", KnownPaths.Sdcard, "\uE7F1"));
            _treeRoots.Add(CreateRoot("Android/data", KnownPaths.AndroidData, "\uE8F1"));
            _treeRoots.Add(CreateRoot("内部数据", "/data/data", "\uE8D7"));
            _treeRoots.Add(CreateRoot("/", "/", "\uE80F"));
        }

        private async Task LoadTreeChildrenAsync(FolderTreeItem node)
        {
            if (!DeviceReady || _files.HasAdb == false)
                return;
            try
            {
                var result = await _androidData.ListSmartAsync(Serial, node.FullPath);
                node.Children.Clear();
                node.IsLoaded = true;
                foreach (var dir in result.Entries.Where(e => e.IsDirectory))
                {
                    var child = new FolderTreeItem { Name = dir.Name, FullPath = dir.FullPath };
                    child.EnsurePlaceholder();
                    node.Children.Add(child);
                }
            }
            catch (NeedPackageException)
            {
                node.Children.Clear();
                node.IsLoaded = true;
                node.Children.Add(new FolderTreeItem { Name = "(需选择应用包)", FullPath = node.FullPath, IsDummy = true, Glyph = "\uE783" });
            }
            catch (Exception ex)
            {
                node.Children.Clear();
                node.IsLoaded = true;
                node.Children.Add(new FolderTreeItem { Name = "(无法打开)", FullPath = "", IsDummy = true, Glyph = "\uE783" });
                Log("树节点 " + node.FullPath + "：" + ex.Message);
            }
        }

        private void TrySelectTree(string path)
        {
            _syncingTree = true;
            try
            {
                foreach (var root in _treeRoots)
                {
                    if (SelectTreePath(root, path))
                        return;
                }
            }
            finally
            {
                _syncingTree = false;
            }
        }

        private bool SelectTreePath(FolderTreeItem node, string path)
        {
            if (node == null || node.IsDummy) return false;
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                SelectTreeViewItem(node);
                return true;
            }
            if (path.StartsWith(node.FullPath == "/" ? "/" : node.FullPath + "/", StringComparison.OrdinalIgnoreCase)
                || (node.FullPath == "/" && path.StartsWith("/")))
            {
                foreach (var child in node.Children)
                {
                    if (SelectTreePath(child, path))
                        return true;
                }
            }
            return false;
        }

        private void SelectTreeViewItem(FolderTreeItem node)
        {
            var container = FindTreeViewItem(FolderTree, node);
            if (container != null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }

        private static TreeViewItem FindTreeViewItem(ItemsControl parent, object item)
        {
            if (parent == null) return null;
            parent.UpdateLayout();
            var direct = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (direct != null) return direct;
            foreach (var child in parent.Items)
            {
                var childContainer = parent.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem;
                var found = FindTreeViewItem(childContainer, item);
                if (found != null) return found;
            }
            return null;
        }

        private void RebuildBreadcrumb(string path)
        {
            BreadcrumbPanel.Children.Clear();
            BreadcrumbPanel.Children.Add(MakeCrumb("/", "/"));
            var acc = "";
            foreach (var part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "/" + part;
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "\uE76C",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 1, 2, 0),
                    Foreground = (Brush)FindResource("BrushMuted")
                });
                BreadcrumbPanel.Children.Add(MakeCrumb(part, acc));
            }
        }

        private Button MakeCrumb(string text, string path)
        {
            var btn = new Button
            {
                Content = text,
                Tag = path,
                Style = (Style)FindResource("CrumbButton")
            };
            btn.Click += Breadcrumb_Click;
            return btn;
        }

        private void UpdateAccessLabel()
        {
            if (!DeviceReady)
            {
                AccessModeBadge.Visibility = Visibility.Collapsed;
                return;
            }
            AccessModeBadge.Visibility = Visibility.Visible;
            AccessModeText.Text = _access.DisplayLabel;
            switch (_access.Mode)
            {
                case AccessMode.RunAs:
                    AccessModeBadge.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x7A, 0x12));
                    AccessModeText.Foreground = Brushes.White;
                    break;
                case AccessMode.Root:
                    AccessModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
                    AccessModeText.Foreground = Brushes.White;
                    break;
                default:
                    AccessModeBadge.Background = (Brush)FindResource("BrushAccent");
                    AccessModeText.Foreground = Brushes.White;
                    break;
            }
        }

        private List<RemoteFileEntry> SelectedEntries()
        {
            return FileList.SelectedItems.Cast<RemoteFileEntry>().ToList();
        }

        private bool EnsureAdb()
        {
            ApplyAdbPath(AdbPathBox.Text);
            if (_files.HasAdb) return true;
            MessageBox.Show(this, "未找到 adb.exe。请安装 platform-tools 或浏览选择。", "MobileSDViewer");
            return false;
        }

        private bool EnsureDevice()
        {
            if (!EnsureAdb()) return false;
            if (DeviceReady) return true;
            MessageBox.Show(this, "请先选择一台状态为 device 的设备。", "MobileSDViewer");
            return false;
        }

        private async Task RunBusy(string status, Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            WorkProgress.IsIndeterminate = true;
            StatusText.Text = status;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                MessageBox.Show(this, ex.Message, "MobileSDViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
                WorkProgress.IsIndeterminate = false;
            }
        }

        private void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            if (LogBox.Text.Length > 0)
                LogBox.AppendText(Environment.NewLine);
            LogBox.AppendText(line);
            LogBox.ScrollToEnd();
            if (LogBox.LineCount > 400)
            {
                var text = LogBox.Text;
                var cut = text.IndexOf('\n');
                if (cut > 0 && text.Length > 12000)
                    LogBox.Text = text.Substring(text.Length / 2);
            }
        }
    }
}
