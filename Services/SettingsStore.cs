using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
                if (data != null) return data;
            }
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
        return new PositionData();
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
