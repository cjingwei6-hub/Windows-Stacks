using System;
using System.IO;

namespace Stacks.Models;

/// <summary>
/// Virtual representation of a desktop file. Never moves or modifies user files.
/// </summary>
public class DesktopItem
{
    public string Path { get; }
    public string Name { get; }
    public string Extension { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public DateTime Created { get; }
    public bool IsDirectory { get; }
    public bool IsHidden { get; }
    public bool IsLink { get; }
    public string GroupKey { get; set; } = "other";

    public DesktopItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        IsLink = Extension == ".lnk" || Extension == ".url";

        try
        {
            var info = new FileInfo(path);
            Size = info.Length;
            Modified = info.LastWriteTime;
            Created = info.CreationTime;
            IsHidden = (info.Attributes & FileAttributes.Hidden) != 0;
            IsDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        }
        catch
        {
            IsHidden = false;
            IsDirectory = false;
        }
    }

    /// <summary>
    /// Open file with default associated application.
    /// </summary>
    public void Open()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Get the display name (truncated for UI).
    /// </summary>
    public string DisplayName(int maxLen = 20)
    {
        if (Name.Length <= maxLen) return Name;
        return Name[..(maxLen - 3)] + "...";
    }
}
