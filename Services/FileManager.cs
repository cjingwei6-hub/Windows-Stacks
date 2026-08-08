using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stacks.Models;

namespace Stacks.Services;

/// <summary>
/// Manages virtual desktop file representation. Never moves user files.
/// </summary>
public class FileManager
{
    public event Action? GroupsChanged;

    private readonly GroupEngine _engine = new();
    private Dictionary<string, DesktopItem> _files = new();
    private Dictionary<string, List<DesktopItem>> _groups = new();
    private readonly object _lock = new();
    private List<string> _desktopPaths = new();
    private int _batchDepth;

    public GroupEngine Engine => _engine;
    public int FileCount { get { lock (_lock) return _files.Count; } }
    public int GroupCount { get { lock (_lock) return _groups.Count; } }

    /// <summary>
    /// Begin a batch operation — suppresses GroupsChanged until EndBatch.
    /// Use with FlushPending to fire exactly one event for multiple file changes.
    /// </summary>
    public void BeginBatch()
    {
        System.Threading.Interlocked.Increment(ref _batchDepth);
    }

    /// <summary>
    /// End a batch operation — fires GroupsChanged if this was the outermost batch.
    /// </summary>
    public void EndBatch()
    {
        if (System.Threading.Interlocked.Decrement(ref _batchDepth) == 0)
            GroupsChanged?.Invoke();
    }

    private void FireGroupsChanged()
    {
        if (_batchDepth == 0)
            GroupsChanged?.Invoke();
    }

    public void Initialize()
    {
        _desktopPaths.Clear();
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (Directory.Exists(userDesktop))
            _desktopPaths.Add(userDesktop);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (Directory.Exists(publicDesktop) && !_desktopPaths.Contains(publicDesktop))
            _desktopPaths.Add(publicDesktop);

        FullScan();
    }

    public void FullScan()
    {
        var newFiles = new Dictionary<string, DesktopItem>();
        var allItems = new List<DesktopItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // dedup across desktop dirs

        foreach (var dp in _desktopPaths)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(dp))
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.')) continue;
                    if (!seenNames.Add(name)) continue; // skip duplicate filename
                    var item = new DesktopItem(path);
                    if (!item.IsHidden)
                    {
                        newFiles[path] = item;
                        allItems.Add(item);
                    }
                }
                foreach (var path in Directory.EnumerateDirectories(dp))
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.')) continue;
                    if (!seenNames.Add(name)) continue; // skip duplicate folder name
                    var item = new DesktopItem(path);
                    if (!item.IsHidden)
                    {
                        newFiles[path] = item;
                        allItems.Add(item);
                    }
                }
            }
            catch { }
        }

        lock (_lock)
        {
            _files = newFiles;
            _groups = _engine.Group(allItems);
        }

        FireGroupsChanged();
    }

    public Dictionary<string, List<DesktopItem>> GetGroups()
    {
        lock (_lock)
            return _groups.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
    }

    public List<(string Key, List<DesktopItem> Items)> GetSortedGroups()
    {
        var order = _engine.GetOrder();
        var groups = GetGroups();
        var result = new List<(string, List<DesktopItem>)>();

        foreach (var key in order)
        {
            if (groups.TryGetValue(key, out var items) && items.Count > 0)
                result.Add((key, items));
        }
        foreach (var (key, items) in groups)
        {
            if (!order.Contains(key) && items.Count > 0)
                result.Add((key, items));
        }
        return result;
    }

    public void SetGroupMode(GroupEngine.GroupMode mode)
    {
        if (_engine.Mode == mode) return;
        _engine.Mode = mode;

        lock (_lock)
        {
            _groups = _engine.Group(_files.Values);
        }
        FireGroupsChanged();
    }

    public void SetSortMode(GroupEngine.SortMode sortMode)
    {
        if (_engine.SortBy == sortMode) return;
        _engine.SortBy = sortMode;

        lock (_lock)
        {
            _groups = _engine.Group(_files.Values);
        }
        FireGroupsChanged();
    }

    public void HandleFileAdded(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.')) return;
        DesktopItem? newItem = null;
        try
        {
            newItem = new DesktopItem(path);
            if (newItem.IsHidden) return;
        }
        catch { return; }

        lock (_lock)
        {
            // Skip if we already have this file path
            if (_files.ContainsKey(path)) return;

            // Also skip if a file with same name exists from another desktop dir
            if (_files.Values.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                return;

            newItem.GroupKey = _engine.Classify(newItem);
            _files[path] = newItem;
            if (!_groups.ContainsKey(newItem.GroupKey))
                _groups[newItem.GroupKey] = new List<DesktopItem>();
            _groups[newItem.GroupKey].Add(newItem);
        }
        FireGroupsChanged();
    }

    public void HandleFileRemoved(string path)
    {
        bool removed = false;
        lock (_lock)
        {
            if (_files.TryGetValue(path, out var item))
            {
                _files.Remove(path);
                if (_groups.TryGetValue(item.GroupKey, out var group))
                {
                    group.RemoveAll(f => f.Path == path);
                    if (group.Count == 0)
                        _groups.Remove(item.GroupKey);
                }
                removed = true;
            }
        }
        // Only fire event if something was actually removed
        if (removed)
            FireGroupsChanged();
    }

    public void HandleFileChanged(string path)
    {
        // Do I/O outside the lock
        var name = Path.GetFileName(path);
        DesktopItem? newItem = null;
        bool shouldAdd = !name.StartsWith('.') && (File.Exists(path) || Directory.Exists(path));
        if (shouldAdd)
        {
            try
            {
                newItem = new DesktopItem(path);
                if (newItem.IsHidden) newItem = null;
            }
            catch { }
        }

        lock (_lock)
        {
            // Remove old entry
            if (_files.TryGetValue(path, out var oldItem))
            {
                _files.Remove(path);
                if (_groups.TryGetValue(oldItem.GroupKey, out var group))
                {
                    group.RemoveAll(f => f.Path == path);
                    if (group.Count == 0)
                        _groups.Remove(oldItem.GroupKey);
                }
            }

            // Add new entry
            if (newItem != null)
            {
                newItem.GroupKey = _engine.Classify(newItem);
                _files[path] = newItem;
                if (!_groups.ContainsKey(newItem.GroupKey))
                    _groups[newItem.GroupKey] = new List<DesktopItem>();
                _groups[newItem.GroupKey].Add(newItem);
            }
        }
        // Fire ONCE — not twice (old code called HandleFileRemoved + HandleFileAdded)
        FireGroupsChanged();
    }
}
