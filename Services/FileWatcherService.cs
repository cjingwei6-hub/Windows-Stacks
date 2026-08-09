using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Stacks.Services;

/// <summary>
/// Dual-track file system monitoring for desktop changes.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly FileManager _fileManager;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DateTime> _pendingChanges = new();
    private readonly object _pendingLock = new();
    private Timer? _debounceTimer;
    private DateTime _firstEventTime;

    public FileWatcherService(FileManager fileManager)
    {
        _fileManager = fileManager;
    }

    public void Start(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 65536
                };

                watcher.Created += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;

                _watchers.Add(watcher);
            }
            catch { }
        }

        _debounceTimer = new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.StartsWith('.')) return;
        ScheduleDebounce(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.StartsWith('.')) return;
        ScheduleDebounce(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldName = Path.GetFileName(e.OldFullPath);
        var newName = Path.GetFileName(e.FullPath);
        if (!oldName.StartsWith('.')) ScheduleDebounce(e.OldFullPath);
        if (!newName.StartsWith('.')) ScheduleDebounce(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _fileManager.FullScan(); // Fallback: full rescan on buffer overflow
    }

    private void ScheduleDebounce(string path)
    {
        lock (_pendingLock)
        {
            _pendingChanges[path] = DateTime.UtcNow;
            if (_firstEventTime == default)
                _firstEventTime = DateTime.UtcNow;

            var elapsed = (DateTime.UtcNow - _firstEventTime).TotalMilliseconds;
            var delay = Math.Max(50, Math.Min(200, 500 - (int)elapsed));

            _debounceTimer?.Change(delay, Timeout.Infinite);
        }
    }

    private void FlushPending()
    {
        HashSet<string> paths;
        lock (_pendingLock)
        {
            paths = new HashSet<string>(_pendingChanges.Keys);
            _pendingChanges.Clear();
            _firstEventTime = default;
        }

        // Batch all changes — fire GroupsChanged exactly once
        _fileManager.BeginBatch();
        try
        {
            foreach (var path in paths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    _fileManager.HandleFileChanged(path);
                else
                    _fileManager.HandleFileRemoved(path);
            }
        }
        finally
        {
            _fileManager.EndBatch();
        }
    }

    public void Stop()
    {
        _debounceTimer?.Dispose();
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    /// <summary>Stop watching current paths and start watching new ones.</summary>
    public void Restart(IEnumerable<string> paths)
    {
        Stop();
        Start(paths);
    }

    public void Dispose() => Stop();
}
