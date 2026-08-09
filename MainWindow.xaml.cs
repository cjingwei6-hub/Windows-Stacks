using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Stacks.Controls;
using Stacks.Interop;
using Stacks.Models;
using Stacks.Services;
using Point = System.Windows.Point; // disambiguate from System.Drawing.Point

namespace Stacks;

/// <summary>
/// Grid used for snapping dragged stacks, mirroring Windows desktop icon behavior.
/// Coordinates are canvas-relative (canvas fills the overlay window at 0,0).
/// </summary>
internal static class DesktopGrid
{
    public const double OriginX = 40;
    public const double OriginY = 40;
    public const double CellW = 132;
    public const double CellH = 120;

    public static double SnapX(double v) => OriginX + Math.Round((v - OriginX) / CellW) * CellW;
    public static double SnapY(double v) => OriginY + Math.Round((v - OriginY) / CellH) * CellH;
}

public partial class MainWindow : Window
{
    private readonly FileManager _fileManager = new();
    private readonly PositionData _settings = SettingsStore.Load();
    private FileWatcherService? _watcher;
    private readonly Dictionary<string, StackControl> _stacks = new();
    private IntPtr _hwnd;
    private DispatcherTimer? _zOrderTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isDebug;
    private string _layoutMode = "grid";
    private bool _isInitialized;
    private bool _isAnyStackDragging;
    private bool _groupsChangedPending;
    private bool _renderPending;
    private bool _stacksHidden; // "隐藏叠放框" toggle
    private bool _hideApps; // "隐藏应用" toggle

    // Multi-source classification: desktop + multiple folders can coexist
    private bool _classifyDesktop = true;
    private readonly List<string> _classifyFolders = new();

    // Layout constants
    private const double MarginX = 40;
    private const double MarginY = 40;
    private const double StackSpacing = 24;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(bool debug) : this()
    {
        _isDebug = debug;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Lock in the real exe path BEFORE anything else — single-file publish
        // can return a temp path that disappears by next launch, breaking
        // auto-start silently.
        AutoStartManager.CacheExePath();

        InitializeOverlay();

        // Restore saved preferences BEFORE the first scan/render so the user
        // sees their last grouping + layout immediately (not the defaults).
        ApplySavedPreferences();

        InitializeFileManager();

        // Restore saved multi-source classification (if any) before the first render
        _classifyDesktop = _settings.ClassifyDesktop;
        _classifyFolders.Clear();
        _classifyFolders.AddRange(_settings.ClassifyFolders);
        if (!_classifyDesktop || _classifyFolders.Count > 0)
        {
            // User has custom sources — apply them before first scan
            _fileManager.SetSourcePaths(GetCurrentSourcePaths());
        }

        InitializeTrayIcon();
        StartZOrderMaintenance();
        RenderAllStacks();
        StartFileWatcher();

        // Enable "launch at login" on first run (user requested auto-start).
        AutoStartManager.EnsureEnabled();

        _isInitialized = true;
    }

    /// <summary>
    /// Apply persisted grouping mode + layout to the engine/layout state.
    /// </summary>
    private void ApplySavedPreferences()
    {
        if (_settings.Layout is "grid" or "fan")
            _layoutMode = _settings.Layout;

        if (Enum.TryParse<GroupEngine.GroupMode>(_settings.GroupMode, true, out var mode))
            _fileManager.SetGroupMode(mode);

        if (Enum.TryParse<GroupEngine.SortMode>(_settings.SortBy, true, out var sortMode))
            _fileManager.SetSortMode(sortMode);

        _hideApps = _settings.HideApps;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Cleanup();
    }

    // ---- Window initialization ----

    private void InitializeOverlay()
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Do NOT hide desktop icons — user wants them visible and clickable.
        // We use WM_NCHITTEST passthrough: clicks on empty canvas area return
        // HTTRANSPARENT (-1) so they fall through to native desktop icons;
        // clicks on StackControl children are intercepted normally.

        // Install Win32 message hook for WM_NCHITTEST click-through logic
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        // Apply WS_EX_NOACTIVATE for desktop-level behavior
        NativeMethods.ApplyNoActivate(_hwnd);

        // Native Windows acrylic / frosted-glass backdrop behind the overlay
        NativeMethods.EnableAcrylic(_hwnd);

        // Position at bottom of z-order
        NativeMethods.SetWindowBottom(_hwnd);

        // Cover desktop working area — NEVER cover the taskbar
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            // Safety: ensure we stay within working area (excludes taskbar)
            Left = wa.Left;
            Top = wa.Top;
            Width = Math.Min(wa.Width, screen.Bounds.Width);
            Height = Math.Min(wa.Height, screen.Bounds.Height - (screen.Bounds.Height - wa.Height));
        }
    }

    private void StartZOrderMaintenance()
    {
        _zOrderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _zOrderTimer.Tick += (s, e) => NativeMethods.SetWindowBottom(_hwnd);
        _zOrderTimer.Start();
    }

    // ---- File management ----

    private void InitializeFileManager()
    {
        _fileManager.GroupsChanged += OnGroupsChanged;
        _fileManager.Initialize();
    }

    private void StartFileWatcher()
    {
        _watcher = new FileWatcherService(_fileManager);
        _watcher.Start(GetCurrentSourcePaths());
    }

    /// <summary>
    /// Build the current list of classification sources from the multi-source model:
    /// desktop (if enabled) + all selected folders.
    /// </summary>
    private List<string> GetCurrentSourcePaths()
    {
        var paths = new List<string>();

        if (_classifyDesktop)
        {
            paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            var pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!paths.Contains(pub)) paths.Add(pub);
        }

        foreach (var f in _classifyFolders)
        {
            if (!paths.Contains(f) && System.IO.Directory.Exists(f))
                paths.Add(f);
        }

        return paths;
    }

    private void OnGroupsChanged()
    {
        // Defer layout update while user is dragging — prevents flicker & duplication
        if (_isAnyStackDragging)
        {
            _groupsChangedPending = true;
            return;
        }
        // Deduplicate: only one render queued at a time
        if (_renderPending) return;
        _renderPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            try { RenderAllStacks(); }
            finally { _renderPending = false; }
        }, DispatcherPriority.Background);
    }

    private bool _isRendering;

    // ---- Stack rendering and layout ----

    /// <summary>
    /// Full rebuild from file-manager groups. Thread-safe via _isRendering guard.
    /// </summary>
    private void RenderAllStacks()
    {
        if (_isRendering) return; // skip re-entrant calls
        _isRendering = true;
        try
        {
            DoRenderAllStacks();
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void DoRenderAllStacks()
    {
        var groups = _fileManager.GetSortedGroups();
        if (groups.Count == 0) return;

        // Filter out the "executable" (应用) group when hide-apps is toggled on
        if (_hideApps)
            groups = groups.Where(g => g.Key != "executable").ToList();

        // ── Phase 0: Save state from existing stacks ──
        var expandedKeys = new HashSet<string>();
        var manualPositions = new Dictionary<string, Point>();
        foreach (var (key, stack) in _stacks)
        {
            if (stack.IsExpanded) expandedKeys.Add(key);
            if (stack.IsManuallyPositioned)
            {
                manualPositions[key] = new Point(
                    Canvas.GetLeft(stack),
                    Canvas.GetTop(stack));
            }
            // Unsubscribe events before disposal to prevent leaks
            stack.StackClicked -= OnStackClicked;
            stack.FileDoubleClicked -= OnFileDoubleClicked;
            stack.DragStarted -= OnStackDragStarted;
            stack.DragEnded -= OnStackDragEnded;
        }

        // ── Phase 1: Nuclear cleanup — remove EVERYTHING from canvas and dictionary ──
        _stacks.Clear();
        DesktopCanvas.Children.Clear();

        // ── Phase 2: Rebuild all stacks from scratch ──
        double x = MarginX, y = MarginY;
        double rowMaxH = 0;

        foreach (var (key, items) in groups)
        {
            var name = _fileManager.Engine.GetDisplayName(key);

            var stack = new StackControl();
            stack.Initialize(key, name, items, _fileManager.Engine.Mode.ToString());
            stack.StackClicked += OnStackClicked;
            stack.FileDoubleClicked += OnFileDoubleClicked;
            stack.DragStarted += OnStackDragStarted;
            stack.DragEnded += OnStackDragEnded;
            stack.LayoutMode = _layoutMode;

            _stacks[key] = stack;
            DesktopCanvas.Children.Add(stack);

            // Restore expanded state
            if (expandedKeys.Contains(key))
                stack.Toggle();

            // Restore manual position — prefer this-session transient position,
            // then fall back to the persisted position from disk.
            Point? resolvedPos = null;
            if (manualPositions.TryGetValue(key, out var mp))
                resolvedPos = mp;
            else if (_settings.Positions.TryGetValue(key, out var dp))
                resolvedPos = new Point(dp.X, dp.Y);

            if (resolvedPos.HasValue)
            {
                Canvas.SetLeft(stack, resolvedPos.Value.X);
                Canvas.SetTop(stack, resolvedPos.Value.Y);
                stack.IsManuallyPositioned = true;
                continue; // Skip flow layout for manually positioned stacks
            }

            // Flow layout for auto-positioned stacks
            stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = stack.DesiredSize;

            double maxWidth = Width - MarginX * 2;
            if (x + size.Width > maxWidth && x > MarginX)
            {
                x = MarginX;
                y += rowMaxH + StackSpacing;
                rowMaxH = 0;
            }

            Canvas.SetLeft(stack, x);
            Canvas.SetTop(stack, y);

            x += size.Width + StackSpacing;
            rowMaxH = Math.Max(rowMaxH, size.Height);
        }
    }

    private void OnStackClicked(string groupKey)
    {
        // Collapse all other stacks
        foreach (var (key, stack) in _stacks)
        {
            if (key != groupKey && stack.IsExpanded)
                stack.Collapse();
        }

        // Toggle clicked stack
        if (_stacks.TryGetValue(groupKey, out var clicked))
            clicked.Toggle();

        // Fire-and-forget lightweight reflow after animation (no full rebuild)
        _ = DelayedReflow(350).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task DelayedReflow(int ms)
    {
        await Task.Delay(ms);
        Dispatcher.InvokeAsync(ReflowLayout, DispatcherPriority.Background);
    }

    /// <summary>
    /// Win32 message hook: intercept WM_NCHITTEST to make the overlay window
    /// "click-through" on empty desktop areas while keeping StackControl children
    /// fully interactive. When the cursor is over a StackControl we return
    /// normally (WPF handles the click); when over empty desktop area we return
    /// HTTRANSPARENT (-1) so the click falls through to the native desktop icons.
    ///
    /// WM_LBUTTONDOWN is intentionally NOT handled here — the click falls through
    /// to the desktop when the cursor is over an empty area (because WM_NCHITTEST
    /// returns HTTRANSPARENT), and StackControl handles left-clicks on its own
    /// background (see OnMouseLeftButtonDown in StackControl.xaml.cs).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // Convert screen coords (lParam) to WPF client coords
        int screenX = (short)(lParam.ToInt32() & 0xFFFF);
        int screenY = (short)(lParam.ToInt32() >> 16);
        var pt = new NativePoint { x = screenX, y = screenY };
        ScreenToClient(_hwnd, ref pt);
        bool overStack = IsPointOverAnyStackControl(new Point(pt.x, pt.y));

        // Cursor over a StackControl → let WPF handle the hit-test
        if (overStack)
            return IntPtr.Zero;

        // Empty desktop area → click-through to native desktop icons
        handled = true;
        return (IntPtr)(-1); // HTTRANSPARENT
    }

    // Simple POINT struct for ScreenToClient (matches Win32 POINT layout)
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

    private bool IsPointOverAnyStackControl(Point wpfPt)
    {
        foreach (var stack in _stacks.Values)
        {
            double l = Canvas.GetLeft(stack);
            double t = Canvas.GetTop(stack);
            if (double.IsNaN(l) || double.IsNaN(t)) continue;
            double r = l + stack.ActualWidth;
            double b = t + stack.ActualHeight;
            if (wpfPt.X >= l && wpfPt.X <= r && wpfPt.Y >= t && wpfPt.Y <= b)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Lightweight reflow: just remeasure and reposition existing stacks without recreating them.
    /// </summary>
    private void ReflowLayout()
    {
        double x = MarginX, y = MarginY;
        double rowMaxH = 0;

        foreach (var (key, stack) in _stacks)
        {
            // Skip repositioning for dragged or manually positioned stacks
            if (stack.IsDragging || stack.IsManuallyPositioned) continue;

            stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = stack.DesiredSize;

            double maxWidth = Width - MarginX * 2;
            if (x + size.Width > maxWidth && x > MarginX)
            {
                x = MarginX;
                y += rowMaxH + StackSpacing;
                rowMaxH = 0;
            }

            Canvas.SetLeft(stack, x);
            Canvas.SetTop(stack, y);

            x += size.Width + StackSpacing;
            rowMaxH = Math.Max(rowMaxH, size.Height);
        }
    }

    private void OnFileDoubleClicked(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnStackDragStarted(StackControl stack)
    {
        _isAnyStackDragging = true;
    }

    private void OnStackDragEnded(StackControl stack)
    {
        _isAnyStackDragging = false;

        // Persist the new manual position so it survives a reboot.
        double lx = Canvas.GetLeft(stack);
        double ly = Canvas.GetTop(stack);
        if (!double.IsNaN(lx) && !double.IsNaN(ly))
        {
            _settings.Positions[stack.GroupKey] = new PointData { X = lx, Y = ly };
            SettingsStore.Save(_settings);
        }

        // Refresh content if updates were skipped during drag
        stack.RefreshAfterDrag();

        // Process any pending group changes through the deduplicated render path
        if (_groupsChangedPending)
        {
            _groupsChangedPending = false;
            OnGroupsChanged();
        }
    }

    // ---- System tray ----

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Stacks - 桌面叠放",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();

        // ── Slim modern menu: only 3 items ──
        menu.Items.Add(CreateMenuItem("⚙ 设置", OpenSettings));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("ℹ 关于", ShowAbout));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("✕ 退出", CleanupAndExit));

        _trayIcon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Open the modern settings window. All changes are applied in real-time via callbacks.
    /// </summary>
    private void OpenSettings()
    {
        var wnd = new SettingsWindow();

        // Wire up real-time callbacks — each setting change takes effect immediately
        wnd.OnRefreshRequested = () => _fileManager.FullScan();

        wnd.OnDesktopToggled = (enabled) =>
        {
            _classifyDesktop = enabled;
            _settings.ClassifyDesktop = enabled;
            ApplySourceChange();
        };

       wnd.OnFolderAdded = (path) =>
        {
            if (!_classifyFolders.Contains(path))
                _classifyFolders.Add(path);
            if (!_settings.ClassifyFolders.Contains(path))
                _settings.ClassifyFolders.Add(path);
            SettingsStore.Save(_settings);
            ApplySourceChange();
        };

        wnd.OnFolderRemoved = (path) =>
        {
            _classifyFolders.Remove(path);
            _settings.ClassifyFolders.Remove(path);
            SettingsStore.Save(_settings);
            ApplySourceChange();
        };

        wnd.OnGroupModeChanged = (mode) =>
        {
            foreach (var s in _stacks.Values)
                s.ResetManualPosition();
            _fileManager.SetGroupMode(Enum.Parse<GroupEngine.GroupMode>(mode, true));
            _settings.GroupMode = mode;
            SettingsStore.Save(_settings);
        };

        wnd.OnLayoutChanged = (layout) =>
        {
            _layoutMode = layout;
            foreach (var s in _stacks.Values)
            {
                s.LayoutMode = layout;
                s.RefreshLayout();
            }
            _settings.Layout = layout;
            SettingsStore.Save(_settings);
            _ = DelayedReflow(100);
        };

        wnd.OnSortModeChanged = (sort) =>
        {
            _fileManager.SetSortMode(Enum.Parse<GroupEngine.SortMode>(sort, true));
            _settings.SortBy = sort;
            SettingsStore.Save(_settings);
        };

        wnd.OnHideAppsToggled = (hide) =>
        {
            _hideApps = hide;
            _settings.HideApps = hide;
            SettingsStore.Save(_settings);
            DoRenderAllStacks();
        };

        wnd.OnAutoStartToggled = (enabled) =>
        {
            AutoStartManager.Toggle();
            // Toggle() flips, so the new state is whatever IsEnabled returns now
        };

        // Push current state into the window
        wnd.LoadState(
            classifyDesktop: _classifyDesktop,
            folders: new List<string>(_classifyFolders),
            groupMode: _settings.GroupMode,
            layout: _settings.Layout,
            sortBy: _settings.SortBy,
            hideApps: _hideApps,
            autoStart: AutoStartManager.IsEnabled());

        // Show as a dialog centered on this window (but we're transparent/fullscreen,
        // so CenterOwner will center on screen which is fine)
        try { wnd.ShowDialog(); }
        catch { /* window may already be closing */ }
    }

    /// <summary>
    /// Called when classification sources change (desktop toggled, folder added/removed).
    /// Rebuilds source paths, restarts file watcher, and re-scans.
    /// </summary>
    private void ApplySourceChange()
    {
        SettingsStore.Save(_settings);
        RestartWatcher();
        _fileManager.SetSourcePaths(GetCurrentSourcePaths());
    }

    /// <summary>
    /// Create a tray menu item whose Click handler marshals to the WPF dispatcher.
    /// WinForms ContextMenuStrip fires on the WinForms message loop; without this,
    /// actions that touch WPF UI elements (stacks, canvas, etc.) either silently fail
    /// or throw cross-thread exceptions.
    /// </summary>
    private System.Windows.Forms.ToolStripMenuItem CreateMenuItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (s, e) => Dispatcher.BeginInvoke(action);
        return item;
    }

    // ── Classification source management (now in SettingsWindow) ──

    private void RestartWatcher()
    {
        _watcher?.Restart(GetCurrentSourcePaths());
    }

    private void ToggleStacksVisibility()
    {
        DesktopCanvas.Visibility = _stacksHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLayout(string mode)
    {
        _layoutMode = mode;
        foreach (var s in _stacks.Values)
        {
            s.LayoutMode = mode;
            s.RefreshLayout();
        }
        _settings.Layout = mode;
        SettingsStore.Save(_settings);
        _ = DelayedReflow(100);
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "Stacks - Windows 桌面叠放\n\n" +
            "在 Windows 上复刻 macOS 桌面「使用叠放」功能\n" +
            "版本: 1.0 (C# .NET 6 + WPF)\n\n" +
            "✓ 虚拟分组，不移动文件\n" +
            "✓ 按类型/日期自动分组\n" +
            "✓ 网格/扇形两种展开布局\n" +
            "✓ 拖拽归组 / 长按预览\n" +
            "✓ 实时文件监控",
            "关于 Stacks", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Drag & Drop onto desktop overlay ----

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        // Files dropped on desktop will be picked up by the file watcher
        _ = Task.Delay(500).ContinueWith(_ =>
            Dispatcher.InvokeAsync(() => _fileManager.FullScan()));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInitialized)
            ReflowLayout();
    }

    // ---- Cleanup ----

    private void CleanupAndExit()
    {
        Cleanup();
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        _zOrderTimer?.Stop();
        _watcher?.Dispose();
        _trayIcon?.Dispose();

        // Desktop icons are no longer hidden; nothing to restore.
    }
}
