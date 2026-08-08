using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Stacks.Services;

/// <summary>
/// Manages Windows "launch at login" via the HKCU Run registry key.
/// Points at the current exe (no --debug flag, so it runs in normal overlay mode).
///
/// SINGLE-FILE PUBLISH GOTCHA:
/// .NET 6 single-file compiled apps do NOT have MainModule.FileName returning the
/// real exe path — it points at a temp extraction folder (e.g. %TEMP%\.net\Stacks\)
/// that is wiped at process exit. Writing that to Run causes the auto-start to
/// silently fail on next boot. Must use Environment.ProcessPath (.NET 6+) instead.
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Stacks";

    /// <summary>
    /// Cached at startup — see <see cref="CacheExePath"/>. Never re-read at write
    /// time, because by then the temp path may already be gone.
    /// </summary>
    private static string? _cachedExePath;

    /// <summary>
    /// Call once at startup (before any HKCU read/write) to lock in the real exe
    /// path. Idempotent — safe to call repeatedly.
    /// </summary>
    public static void CacheExePath()
    {
        if (!string.IsNullOrEmpty(_cachedExePath) && File.Exists(_cachedExePath))
            return;

        string? candidate = null;

        // 1. Environment.ProcessPath is reliable for single-file .NET 6+ apps
        try { candidate = Environment.ProcessPath; } catch { }

        // 2. Fallback: Process.MainModule — works for framework-dependent builds
        if (string.IsNullOrEmpty(candidate))
        {
            try
            {
                var mod = Process.GetCurrentProcess().MainModule?.FileName;
                // Reject the .NET temp extraction path
                if (!string.IsNullOrEmpty(mod) && !IsTempExtractionPath(mod))
                    candidate = mod;
            }
            catch { }
        }

        // 3. Last resort: AppContext.BaseDirectory/Stacks.exe — works because
        //    we always publish with -p:PublishSingleFile=true pointing here
        if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "Stacks.exe");
            candidate = fallback;
        }

        _cachedExePath = candidate;
    }

    private static bool IsTempExtractionPath(string path)
    {
        try
        {
            var tempRoot = Path.GetTempPath();
            return !string.IsNullOrEmpty(tempRoot) &&
                   path.StartsWith(tempRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Full registry value, e.g. "\"C:\path\Stacks.exe\"".</summary>
    private static string TargetCommand
    {
        get
        {
            // Defensive: if CacheExePath wasn't called (e.g. Enable() called
            // from a unit test), try to resolve on-demand here.
            if (string.IsNullOrEmpty(_cachedExePath))
                CacheExePath();
            return $"\"{_cachedExePath}\"";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var val = key?.GetValue(ValueName);
            return val != null &&
                   string.Equals(val.ToString(), TargetCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(ValueName, TargetCommand, RegistryValueKind.String);
        }
        catch { /* no admin needed for HKCU, but ignore if blocked */ }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }

    public static void Toggle() => (IsEnabled() ? (Action)Disable : Enable)();

    /// <summary>
    /// Enable on first run if not already present. Safe to call every launch —
    /// only writes when the value is missing or wrong.
    /// </summary>
    public static void EnsureEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var existing = key?.GetValue(ValueName)?.ToString();
            if (string.IsNullOrEmpty(existing) || existing.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                Enable();
        }
        catch { }
    }
}
