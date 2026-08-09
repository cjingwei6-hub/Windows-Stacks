using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stacks.Models;

namespace Stacks.Services;

/// <summary>
/// Saved position for a single stack group (canvas coordinates).
/// </summary>
public class PointData
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// Persisted app state. Stored in %LOCALAPPDATA%\Stacks\settings.json
/// (NOT the desktop — avoids FileSystemWatcher feedback loops).
/// </summary>
public class PositionData
{
    public Dictionary<string, PointData> Positions { get; set; } = new();
    public string GroupMode { get; set; } = "kind";
    public string Layout { get; set; } = "grid";
    public string SortBy { get; set; } = "name";
    public bool HideApps { get; set; }

    /// <summary>
    /// Whether the desktop is included as a classification source.
    /// Default: true (classify desktop files).
    /// </summary>
    public bool ClassifyDesktop { get; set; } = true;

    /// <summary>
    /// Additional folders to classify alongside (or instead of) the desktop.
    /// Supports multiple folders — each folder's direct children are grouped
    /// into the same stack view with deduplication by filename.
    /// </summary>
    public List<string> ClassifyFolders { get; set; } = new();

    /// <summary>
    /// User-defined display names per group key (e.g. "image" -> "图片墙").
    /// Empty string (or missing entry) means use the built-in default name.
    /// </summary>
    public Dictionary<string, string> CustomGroupNames { get; set; } = new();

    /// <summary>
    /// User-defined classification rules (only consulted in GroupMode.Custom).
    /// </summary>
    public List<CustomRule> CustomRules { get; set; } = new();

    // ── Legacy field kept for migration from v1.0.x single-folder mode ──
    [Obsolete("Use ClassifyDesktop + ClassifyFolders instead")]
    public string? ClassifyFolder { get; set; }
}

public static class SettingsStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stacks", "settings.json");

    public static PositionData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<PositionData>(json);
                if (data != null)
                {
                    // Migrate legacy single-folder mode → multi-source model
                    MigrateLegacy(data);
                    return data;
                }
            }
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
        return new PositionData();
    }

    /// <summary>
    /// Migrate v1.0.x single-folder setting to new multi-source model.
    /// If ClassifyFolder is set, it means the user was in folder-only mode:
    ///   - ClassifyDesktop = false (they explicitly chose a folder over desktop)
    ///   - ClassifyFolders = [that folder]
    /// After migration, the old field is cleared so it doesn't re-apply.
    /// </summary>
    private static void MigrateLegacy(PositionData data)
    {
        if (!string.IsNullOrEmpty(data.ClassifyFolder) &&
            System.IO.Directory.Exists(data.ClassifyFolder))
        {
            data.ClassifyDesktop = false;
            data.ClassifyFolders.Add(data.ClassifyFolder);
            data.ClassifyFolder = null;  // clear legacy field
            // Persist migration immediately so we don't migrate again
            Save(data);
        }
    }

    public static void Save(PositionData data)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* best-effort persistence; ignore write failures */ }
    }

    /// <summary>Remove a stale position entry (e.g. when a group no longer exists).</summary>
    public static void Forget(string groupKey)
    {
        try
        {
            var data = Load();
            if (data.Positions.Remove(groupKey))
                Save(data);
        }
        catch { }
    }
}
