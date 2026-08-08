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
        var paths = new List<string>();
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        var pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!paths.Contains(pub)) paths.Add(pub);
        _watcher.Start(paths);
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

        // ── Group by submenu ──
        var groupMenu = new System.Windows.Forms.ToolStripMenuItem("分组方式");
        groupMenu.DropDownItems.Add(CreateMenuItem("按类型", () => SetGroupMode(GroupEngine.GroupMode.Kind)));
        groupMenu.DropDownItems.Add(CreateMenuItem("按日期", () => SetGroupMode(GroupEngine.GroupMode.Date)));
        groupMenu.DropDownItems.Add(CreateMenuItem("不分组", () => SetGroupMode(GroupEngine.GroupMode.None)));
        menu.Items.Add(groupMenu);

        // ── Layout submenu ──
        var layoutMenu = new System.Windows.Forms.ToolStripMenuItem("展开布局");
        layoutMenu.DropDownItems.Add(CreateMenuItem("网格", () => SetLayout("grid")));
        layoutMenu.DropDownItems.Add(CreateMenuItem("扇形", () => SetLayout("fan")));
        menu.Items.Add(layoutMenu);

        // ── Sort submenu ──
        var sortMenu = new System.Windows.Forms.ToolStripMenuItem("排序方式");
        sortMenu.DropDownItems.Add(CreateMenuItem("按名称", () => SetSortMode(GroupEngine.SortMode.Name)));
        sortMenu.DropDownItems.Add(CreateMenuItem("按日期", () => SetSortMode(GroupEngine.SortMode.Date)));
        sortMenu.DropDownItems.Add(CreateMenuItem("按大小", () => SetSortMode(GroupEngine.SortMode.Size)));
        sortMenu.DropDownItems.Add(CreateMenuItem("按类型", () => SetSortMode(GroupEngine.SortMode.Type)));
        menu.Items.Add(sortMenu);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("刷新", () => _fileManager.FullScan()));

        // ── Hide all stacks toggle (checkbox) ──
        var hideItem = new System.Windows.Forms.ToolStripMenuItem("隐藏叠放框");
        hideItem.Checked = _stacksHidden;
        hideItem.Click += (s, e) =>
        {
            _stacksHidden = !_stacksHidden;
            hideItem.Checked = _stacksHidden;
            ToggleStacksVisibility();
        };
        menu.Items.Add(hideItem);

        // ── Hide apps toggle (checkbox) — hides the "executable"/应用 stack group ──
        var hideAppsItem = new System.Windows.Forms.ToolStripMenuItem("隐藏应用");
        hideAppsItem.Checked = _hideApps;
        hideAppsItem.Click += (s, e) =>
        {
            _hideApps = !_hideApps;
            hideAppsItem.Checked = _hideApps;
            _settings.HideApps = _hideApps;
            SettingsStore.Save(_settings);
            // Re-render to show/hide the 应用 group immediately
            DoRenderAllStacks();
        };
        menu.Items.Add(hideAppsItem);

        // ── Auto-start (launch at login) toggle ──
        var autostartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启动");
        autostartItem.Checked = AutoStartManager.IsEnabled();
        autostartItem.Click += (s, e) =>
        {
            AutoStartManager.Toggle();
            autostartItem.Checked = AutoStartManager.IsEnabled();
        };
        menu.Items.Add(autostartItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(CreateMenuItem("关于", ShowAbout));
        menu.Items.Add(CreateMenuItem("退出", CleanupAndExit));

        _trayIcon.ContextMenuStrip = menu;
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

    private void SetSortMode(GroupEngine.SortMode mode)
    {
        _fileManager.SetSortMode(mode);
        _settings.SortBy = mode.ToString().ToLowerInvariant();
        SettingsStore.Save(_settings);
    }

    private void ToggleStacksVisibility()
    {
        DesktopCanvas.Visibility = _stacksHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetGroupMode(GroupEngine.GroupMode mode)
    {
        // Reset manual positions so stacks reflow in new grouping
        foreach (var s in _stacks.Values)
            s.ResetManualPosition();
        _fileManager.SetGroupMode(mode);
        _settings.GroupMode = mode.ToString().ToLowerInvariant();
        SettingsStore.Save(_settings);
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
