namespace Drilling.UI.Menu.Menus
{
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Runtime.CompilerServices;
    using System.IO;
    using Drilling.Common.Log;

    public sealed class CMonitorLogViewerPanel : INotifyPropertyChanged
    {
        private readonly string _logDirectory;
        private readonly CLogFileScope _fileScope;
        private string? _lastOpenedDirectory;
        private CancellationTokenSource? _readCts;
        private long _readVersion;
        private Task? _initializeTask;
        private bool _isTreeBusy;
        public CMonitorLogViewerPanel(string logDirectory)
        {
            _fileScope = new CLogFileScope(logDirectory);
            _logDirectory = _fileScope.RootDirectory;
            TreeRoots = [new CLogTreeNode(_logDirectory, "Log", true)
            {
                IsExpanded = true
            }

            ];
            RefreshTreeCommand = new(async _ => await RefreshTreeAsync(), _ => !IsTreeBusy);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string OpenDirectory => _lastOpenedDirectory ?? _logDirectory;
        public string FilePath { get; private set; } = "";
        public string Text { get; private set; } = "";
        public string Status { get; private set; } = "왼쪽 트리에서 로그 파일을 선택해 주세요.";
        public string TreeStatus { get; private set; } = "폴더를 펼친 뒤 파일을 선택하세요.";
        public IReadOnlyList<CLogTreeNode> TreeRoots { get; }
        public bool IsTreeBusy => _isTreeBusy;
        public bool IsBusy { get; private set; }
        public double LoadProgress { get; private set; }
        public string LoadProgressText { get; private set; } = "0% · 로그 파일 준비 중";
        public CButtonCommand RefreshTreeCommand { get; }

        public Task InitializeAsync() => _initializeTask ??= ExpandDirectoryAsync(TreeRoots[0]);
        public Task ExpandDirectoryAsync(CLogTreeNode node) => EnsureDirectoryAsync(node, false);
        public Task SelectNodeAsync(CLogTreeNode? node) => node is { IsDirectory: false, IsPlaceholder: false } ? LoadFileAsync(node.Path) : Task.CompletedTask;
        private Task EnsureDirectoryAsync(CLogTreeNode node, bool refresh)
        {
            if (!node.IsDirectory || (!refresh && node.IsLoaded))
                return Task.CompletedTask;
            if (node.LoadingTask is { IsCompleted: false } pending)
                return pending;
            return node.LoadingTask = ReadDirectoryAsync(node);
        }

        private async Task ReadDirectoryAsync(CLogTreeNode node)
        {
            try
            {
                // One level only, on expansion or explicit refresh. Never scan the entire Log tree.
                var entries = await Task.Run(() => new DirectoryInfo(_fileScope.ResolveDirectory(node.Path)).EnumerateFileSystemInfos().Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0 && !CLogFileScope.IsAlarm(info.Name)).Where(info => info is DirectoryInfo || CLogFileScope.IsLogFile(info.Name)).Select(info => (info.FullName, info.Name, IsDirectory: info is DirectoryInfo)).OrderByDescending(info => info.IsDirectory).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase).ToArray());
                var existing = node.Children.Where(child => !child.IsPlaceholder).ToDictionary(child => child.Path, StringComparer.OrdinalIgnoreCase);
                var children = entries.Select(entry => existing.TryGetValue(entry.FullName, out var child) && child.IsDirectory == entry.IsDirectory ? child : new CLogTreeNode(entry.FullName, entry.Name, entry.IsDirectory)).ToArray();
                var present = node.Children.ToHashSet();
                // Keep unchanged containers/selection/expansion when refreshing a directory.
                for (var index = 0; index < children.Length; index++)
                {
                    if (index < node.Children.Count && ReferenceEquals(node.Children[index], children[index]))
                        continue;
                    var previous = present.Contains(children[index]) ? node.Children.IndexOf(children[index]) : -1;
                    if (previous >= 0)
                        node.Children.Move(previous, index);
                    else
                        node.Children.Insert(index, children[index]);
                }

                while (node.Children.Count > children.Length)
                    node.Children.RemoveAt(node.Children.Count - 1);
                node.IsLoaded = true;
                node.SetError("");
                TreeStatus = "파일을 선택하면 오른쪽에 내용이 표시됩니다.";
            }
            catch (Exception exception)when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                node.IsLoaded = false;
                node.SetError(exception.Message);
                node.Children.Clear();
                node.Children.Add(CLogTreeNode.Placeholder("조회 실패 · 다시 펼쳐서 재시도"));
                TreeStatus = $"폴더를 읽지 못했습니다: {exception.Message}";
            }

            Notify(nameof(TreeStatus));
        }

        public async Task RefreshTreeAsync()
        {
            if (_isTreeBusy)
                return;
            _isTreeBusy = true;
            Notify(nameof(IsTreeBusy));
            RefreshTreeCommand.NotifyCanExecuteChanged();
            try
            {
                await RefreshExpandedAsync(TreeRoots[0]);
            }
            finally
            {
                _isTreeBusy = false;
                Notify(nameof(IsTreeBusy));
                RefreshTreeCommand.NotifyCanExecuteChanged();
            }
        }

        private async Task RefreshExpandedAsync(CLogTreeNode node)
        {
            await EnsureDirectoryAsync(node, true);
            foreach (var child in node.Children.Where(child => child.IsDirectory && child.IsExpanded).ToArray())
                await RefreshExpandedAsync(child);
        }

        public async Task LoadFileAsync(string path)
        {
            _readCts?.Cancel();
            var cts = new CancellationTokenSource();
            _readCts = cts;
            var version = ++_readVersion;
            FilePath = "";
            Text = "";
            IsBusy = true;
            LoadProgress = 0.0;
            LoadProgressText = "0% · 로그 파일 준비 중";
            Status = "로그를 읽고 있습니다...";
            NotifyView();
            try
            {
                // Read/decode off the UI thread. Cancel an old selection; only publish the latest file.
                var progress = new Progress<double>(value =>
                {
                    if (version != _readVersion || !IsBusy)
                        return;
                    LoadProgress = Math.Clamp(value, 0.0, 100.0);
                    LoadProgressText = $"{LoadProgress:0}% · 로그 파일 읽는 중";
                    Notify(nameof(LoadProgress));
                    Notify(nameof(LoadProgressText));
                });
                var file = await CLogTextReader.ReadAllAsync(_fileScope.ResolveFile(path), progress, cts.Token);
                if (version != _readVersion)
                    return;
                FilePath = Path.GetFullPath(path);
                _lastOpenedDirectory = Path.GetDirectoryName(FilePath);
                Text = file.Text;
                LoadProgress = 100.0;
                LoadProgressText = "100% · 로그 표시 완료";
                Status = Text.Length == 0 ? "내용이 없는 파일입니다." : $"{file.FileLength / 1024.0:N1} KB · 읽기 전용 · 전체 내용";
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A different file was selected while this read was in progress.
            }
            catch (Exception exception)when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (version != _readVersion)
                    return;
                Status = $"로그를 읽지 못했습니다: {exception.Message}";
            }
            finally
            {
                if (version == _readVersion)
                {
                    _readCts = null;
                    IsBusy = false;
                    NotifyView();
                }
                cts.Dispose();
            }
        }

        private void NotifyView()
        {
            foreach (var property in new[]
            {
                nameof(FilePath),
                nameof(Text),
                nameof(Status),
                nameof(IsBusy),
                nameof(LoadProgress),
                nameof(LoadProgressText)
            }

            )
                Notify(property);
        }

        private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    }

    public sealed class CLogTreeNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private string _error = "";
        internal CLogTreeNode(string path, string name, bool isDirectory, bool isPlaceholder = false)
        {
            Path = path;
            Name = name;
            IsDirectory = isDirectory;
            IsPlaceholder = isPlaceholder;
            if (isDirectory)
                Children.Add(Placeholder("펼쳐서 보기"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Path { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public bool IsPlaceholder { get; }
        public string ToolTip => _error.Length == 0 ? Path : _error;
        public ObservableCollection<CLogTreeNode> Children { get; } = [];

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                Notify();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                Notify();
            }
        }

        public bool IsLoaded { get; internal set; }
        internal Task? LoadingTask { get; set; }

        internal void SetError(string error)
        {
            _error = error;
            Notify(nameof(ToolTip));
        }

        internal static CLogTreeNode Placeholder(string name) => new("", name, false, true);
        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
