using System.IO;

namespace Drilling.UI.Menu.Menus;

/// <summary>
/// Watches authoritative result/offset folders, including saves made by other menus or processes.
/// It only signals that a display refresh is needed; no CSV parsing, state mutation or device command
/// runs in the filesystem callback. Delayed writes/renames are coalesced before the owner revalidates.
/// </summary>
public sealed class CManualInspectionUpdateMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string[] _roots;
    private readonly Action _changed;
    private readonly Timer _debounce;
    private int _disposed;

    public CManualInspectionUpdateMonitor(IEnumerable<string> roots, Action changed, TimeSpan? debounce = null)
    {
        _roots = roots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath)
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _changed = changed;
        _delay = debounce ?? TimeSpan.FromMilliseconds(650);
        _debounce = new Timer(_ => Notify(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var ancestor in _roots.Select(root => ExistingAncestor(Path.GetDirectoryName(root) ?? root)).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(ancestor!, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName
            };
            watcher.Created += FileChanged; watcher.Changed += FileChanged; watcher.Deleted += FileChanged;
            watcher.Renamed += (_, e) => { Schedule(e.FullPath, true); Schedule(e.OldFullPath, true); };
            watcher.Error += (_, _) => Schedule(null);
            _watchers.Add(watcher); watcher.EnableRaisingEvents = true;
        }
    }

    private readonly TimeSpan _delay;
    private static string? ExistingAncestor(string root)
    {
        for (var path = root; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (Directory.Exists(path)) return path;
        return null;
    }
    private void FileChanged(object sender, FileSystemEventArgs e) => Schedule(e.FullPath, e.ChangeType == WatcherChangeTypes.Deleted);
    private void Schedule(string? path, bool structural = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (path is not null && (!_roots.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || !(path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) || structural && !System.IO.File.Exists(path)))) return;
        try { _debounce.Change(_delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }
    private void Notify()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _changed(); }
        catch (Exception e) { System.Diagnostics.Trace.TraceError("Manual Inspection update notification: " + e.Message); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var watcher in _watchers) watcher.Dispose();
        _debounce.Dispose();
    }
}
