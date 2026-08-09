using System;
using System.Collections.Generic;
using System.Linq;

namespace Stacks.Models;

/// <summary>
/// Smart file classification: 3-layer engine (extension whitelist => perceived type => heuristic).
/// </summary>
public class GroupEngine
{
    private static readonly Dictionary<string, HashSet<string>> ExtensionGroups = new()
    {
        ["image"] = new() { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".heif", ".psd", ".ai", ".raw", ".cr2", ".nef" },
        ["video"] = new() { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ogv", ".ts" },
        ["audio"] = new() { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".mid", ".midi", ".ape" },
        ["document"] = new() { ".pdf", ".doc", ".docx", ".txt", ".md", ".rtf", ".odt", ".pages", ".tex", ".log", ".epub", ".mobi" },
        ["spreadsheet"] = new() { ".xls", ".xlsx", ".csv", ".tsv", ".ods", ".numbers" },
        ["presentation"] = new() { ".ppt", ".pptx", ".key", ".odp" },
        ["archive"] = new() { ".zip", ".rar", ".7z", ".tar", ".gz", ".xz", ".bz2", ".iso", ".tgz", ".cab" },
        ["code"] = new() { ".py", ".js", ".ts", ".html", ".css", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".sh", ".bat", ".ps1", ".c", ".cpp", ".h", ".java", ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".sql", ".r" },
        ["font"] = new() { ".ttf", ".otf", ".woff", ".woff2", ".eot" },
        ["3d"] = new() { ".stl", ".obj", ".fbx", ".blend", ".3ds", ".step", ".gltf", ".glb" },
        ["executable"] = new() { ".exe", ".lnk", ".msi", ".cmd", ".bat", ".ps1", ".vbs", ".jar", ".appx", ".appxbundle" },
    };

    private static readonly Dictionary<string, string> ExtToGroup;

    static GroupEngine()
    {
        ExtToGroup = new Dictionary<string, string>();
        foreach (var (group, exts) in ExtensionGroups)
        foreach (var ext in exts)
            ExtToGroup[ext] = group;
    }

    public enum GroupMode { Kind, Date, None, Custom }
    public enum SortMode { Name, Date, Size, Type }

    public GroupMode Mode { get; set; } = GroupMode.Kind;
    public SortMode SortBy { get; set; } = SortMode.Name;

    /// <summary>
    /// User-defined classification rules. Each rule gathers its extensions into
    /// a single stack. Only consulted when Mode == GroupMode.Custom.
    /// </summary>
    public List<CustomRule> CustomRules { get; set; } = new();

    /// <summary>
    /// User-defined display-name overrides keyed by group key. Empty/null = use default.
    /// </summary>
    public Dictionary<string, string> CustomGroupNames { get; set; } = new();

    /// <summary>Prefix for all custom-rule group keys (avoids collision with built-in keys).</summary>
    public const string CustomKeyPrefix = "custom:";

    public string Classify(DesktopItem item)
    {
        if (Mode == GroupMode.None) return "all";
        if (Mode == GroupMode.Date) return ClassifyByDate(item);

        // Custom mode: walk rules in order, first match wins
        if (Mode == GroupMode.Custom)
        {
            if (item.IsDirectory) return "folder";
            if (item.IsHidden) return "other";
            foreach (var rule in CustomRules)
            {
                if (rule.Extensions.Contains(item.Extension))
                    return CustomKeyPrefix + rule.Id;
            }
            return "other";
        }

        // Kind mode (default)
        if (item.IsDirectory) return "folder";
        if (item.IsHidden) return "other";

        // Layer 1: extension whitelist
        if (ExtToGroup.TryGetValue(item.Extension, out var group))
            return group;

        // Layer 2+3: fallback
        if (item.IsLink) return "other";
        return "other";
    }

    private static string ClassifyByDate(DesktopItem item)
    {
        var now = DateTime.Now;
        var mtime = item.Modified;
        if (mtime.Date == now.Date) return "today";
        if (mtime.Date == now.AddDays(-1).Date) return "yesterday";
        if (mtime > now.AddDays(-7)) return "this_week";
        if (mtime > now.AddDays(-30)) return "this_month";
        if (mtime > now.AddDays(-365)) return "this_year";
        return "older";
    }

    public Dictionary<string, List<DesktopItem>> Group(IEnumerable<DesktopItem> items)
    {
        var groups = new Dictionary<string, List<DesktopItem>>();
        foreach (var item in items)
        {
            item.GroupKey = Classify(item);
            if (!groups.ContainsKey(item.GroupKey))
                groups[item.GroupKey] = new List<DesktopItem>();
            groups[item.GroupKey].Add(item);
        }

        // Sort items within each group
        foreach (var list in groups.Values)
        {
            switch (SortBy)
            {
                case SortMode.Name:
                    list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case SortMode.Date:
                    list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                    break;
                case SortMode.Size:
                    list.Sort((a, b) => b.Size.CompareTo(a.Size));
                    break;
                case SortMode.Type:
                    list.Sort((a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
        return groups;
    }

    // Display names
    public static readonly Dictionary<string, string> GroupNames = new()
    {
        ["folder"] = "文件夹", ["image"] = "图像", ["video"] = "影片", ["audio"] = "音乐",
        ["document"] = "文稿", ["spreadsheet"] = "电子表格", ["presentation"] = "演示文稿",
        ["archive"] = "压缩包", ["code"] = "代码",
        ["font"] = "字体", ["3d"] = "3D模型", ["executable"] = "应用", ["other"] = "其他", ["all"] = "全部",
        ["today"] = "今天", ["yesterday"] = "昨天", ["this_week"] = "本周",
        ["this_month"] = "本月", ["this_year"] = "今年", ["older"] = "更早",
    };

    public static readonly string[] GroupOrder = new[]
    {
        "folder", "image", "video", "audio", "document", "spreadsheet",
        "presentation", "archive", "code", "font", "3d", "executable", "other"
    };

    public static readonly string[] DateGroupOrder = new[]
    {
        "today", "yesterday", "this_week", "this_month", "this_year", "older"
    };

    public string GetDisplayName(string key) =>
        CustomGroupNames.TryGetValue(key, out var custom)
            && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : GroupNames.GetValueOrDefault(key, key);

    public string[] GetOrder() => Mode switch
    {
        GroupMode.Date => DateGroupOrder,
        GroupMode.Custom => BuildCustomGroupOrder(),
        _ => GroupOrder
    };

    /// <summary>
    /// Custom-mode ordering: "folder" first, then each user rule in the order
    /// they were added (always include "other" last as the catch-all).
    /// </summary>
    private string[] BuildCustomGroupOrder()
    {
        var order = new List<string> { "folder" };
        foreach (var rule in CustomRules)
            order.Add(CustomKeyPrefix + rule.Id);
        order.Add("other");
        return order.ToArray();
    }
}
