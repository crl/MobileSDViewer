using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MobileSDViewer.Models
{
    /// <summary>
    /// 左侧目录树节点；未展开时用占位子项触发懒加载。
    /// </summary>
    public class FolderTreeItem : INotifyPropertyChanged
    {
        private string _name;
        private bool _isExpanded;

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public string FullPath { get; set; }
        /// <summary>Segoe MDL2 图标码点，目录树用。</summary>
        public string Glyph { get; set; } = "\uE8B7";
        public bool IsDummy { get; set; }
        public bool IsLoaded { get; set; }
        public ObservableCollection<FolderTreeItem> Children { get; } = new ObservableCollection<FolderTreeItem>();

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void EnsurePlaceholder()
        {
            if (Children.Count == 0 && !IsDummy)
                Children.Add(CreateDummy());
        }

        public static FolderTreeItem CreateDummy() =>
            new FolderTreeItem { Name = "...", IsDummy = true, FullPath = "", Glyph = "\uE8B7" };
    }
}
