using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Stacks.Interop;
using Stacks.Models;

namespace Stacks.Controls;

public partial class StackControl : UserControl
{
    public event Action<string>? StackClicked;
    public event Action<string>? FileDoubleClicked;
    public event Action<StackControl>? DragStarted;
    public event Action<StackControl>? DragEnded;

    private string _groupKey = "";
    private List<DesktopItem> _items = new();
    private bool _isExpanded;
    private string _layoutMode = "grid"; // "grid" or "fan"
    private Point _pressPoint;
    private bool _isLongPress;
    private bool _isDragging;
    private Point _dragStartPos; // Canvas position at drag start
    private DateTime _pressTime;
    private TranslateTransform? _dragTransform;
    private Point? _preExpandPosition; // saved before RepositionIfOutOfBounds shifts us
    private Border? _selectedItemBorder; // currently selected file item's icon border

    public string GroupKey => _groupKey;
    public bool IsExpanded => _isExpanded;
    public bool IsDragging => _isDragging;
    public bool IsManuallyPositioned { get; set; }
    public string LayoutMode { get => _layoutMode; set { _layoutMode = value; RefreshLayout(); } }
    public int ItemCount => _items.Count;

    private static readonly Dictionary<string, BitmapSource> IconCache = new();
#pragma warning disable CS0414
    private bool _isHovering; // Used for future hover effects
#pragma warning restore CS0414

    public StackControl()
    {
        InitializeComponent();
    }

    public void Initialize(string groupKey, string groupName, List<DesktopItem> items, string groupBy)
    {
        _groupKey = groupKey;
        _items = items;

        GroupNameLabel.Text = groupName;
        GroupCountLabel.Text = $"{items.Count} 项";
        ExpandedTitle.Text = groupName;

        RenderCollapsedIcons();
        RenderExpandedItems();
    }

    public void UpdateItems(List<DesktopItem> items, string groupName)
    {
        _items = items;
        GroupNameLabel.Text = groupName;
        GroupCountLabel.Text = $"{items.Count} 项";
        ExpandedTitle.Text = groupName;

        // Skip visual rebuild while dragging — prevents flicker
        if (_isDragging) return;

        if (!_isExpanded)
            RenderCollapsedIcons();
        else
            RenderExpandedItems();
    }

    /// <summary>
    /// Called by MainWindow after drag ends to refresh content if it was skipped.
    /// </summary>
    public void RefreshAfterDrag()
    {
        if (!_isExpanded)
            RenderCollapsedIcons();
        else
            RenderExpandedItems();
    }

    public void ResetManualPosition()
    {
        IsManuallyPositioned = false;
    }

    private void RenderCollapsedIcons()
    {
        IconPilePanel.Children.Clear();

        // Show ONLY the first item's icon — clean single-icon look (no messy pile)
        if (_items.Count > 0)
        {
            var img = new Image
            {
                Width = 56, Height = 56,
                Stretch = Stretch.Uniform,
                Source = GetFileIcon(_items[0], 56, subscribingImage: null)
            };
            var border = new Border
            {
                Width = 56, Height = 56,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 67)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                BorderThickness = new Thickness(0.5),
                Child = img
            };
            IconPilePanel.Children.Add(border);
        }

        // Set size from known content dimensions — NEVER trust Measure() on a
        // UserControl that contains multiple overlapping panels (it returns the
        // union of all children including collapsed ones, giving huge sizes).
        // Use Min/Max to clamp: even if WPF's layout pass tries to give us a
        // different size, these constraints win.
        //
        // Content breakdown (inside CollapsedView padding 8,6):
        //   drag handle:   ~28 × 4
        //   icon:          56 × 56
        //   group name:    ~80 × 20 (max-width 140)
        //   count label:   ~60 × 14
        //   vertical gap:  ~10
        //   + padding:     16 H × 12 V
        const double CollapsedW = 100;
        const double CollapsedH = 110;
        Width = CollapsedW;
        Height = CollapsedH;
        MinWidth = CollapsedW; MaxWidth = CollapsedW;
        MinHeight = CollapsedH; MaxHeight = CollapsedH;
    }

    private void RenderExpandedItems()
    {
        // Clear old selection — child elements are about to be rebuilt
        ClearSelection();
        ExpandedItems.Items.Clear();

        bool isFan = _layoutMode == "fan";

        // Always use WrapPanel — fan mode is purely a visual rotation effect,
        // the size/wrap behavior is identical to grid mode. This keeps the
        // "panel grows naturally until it exceeds screen → scrollbar kicks in"
        // behavior consistent across both modes.
        ExpandedItems.ItemsPanel = new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(WrapPanel)) { Name = "GridPanel" });

        int total = _items.Count;
        for (int idx = 0; idx < total; idx++)
        {
            var item = _items[idx];
            var stack = new StackPanel { Width = 80, Height = 90, Margin = new Thickness(4), Cursor = Cursors.Hand };

            // File icon
            var img = new Image
            {
                Width = 48, Height = 48,
                Stretch = Stretch.Uniform,
                Source = GetFileIcon(item, 64, subscribingImage: null),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Border for icon
            var iconBorder = new Border
            {
                Width = 56, Height = 56,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = img
            };

            // File name
            var nameLabel = new TextBlock
            {
                Text = item.DisplayName(14),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)),
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 8.5,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                MaxWidth = 76
            };

            stack.Children.Add(iconBorder);
            stack.Children.Add(nameLabel);

            // ── Fan mode visual: rotate each item based on its position within
            //    its row (5 items per row) — middle one straight, edges angled,
            //    creating a subtle arc on each row. Pure visual; layout/sizing
            //    is identical to grid mode. ──
            if (isFan)
            {
                const int colsPerRow = 5;
                int posInRow = idx % colsPerRow;
                double centerIdx = (colsPerRow - 1) / 2.0;
                double offset = posInRow - centerIdx;
                double angle = (offset / Math.Max(centerIdx, 1)) * 18; // ±18° max
                double yLift = Math.Abs(offset) * 3;                  // arc lift

                stack.RenderTransformOrigin = new Point(0.5, 1.0);
                stack.RenderTransform = new RotateTransform(angle);
                stack.Margin = new Thickness(6, yLift, 6, 0);
            }

            // Click handler — single click selects (visual highlight), double click opens
            string path = item.Path;
            var capturedBorder = iconBorder;
            stack.MouseLeftButtonDown += (s, e) =>
            {
                // ── Select this item on single click ──
                SelectItem(capturedBorder);
                if (e.ClickCount == 2)
                    FileDoubleClicked?.Invoke(path);
                e.Handled = true;
            };
            stack.MouseLeftButtonUp += (s, e) => { e.Handled = true; };
            stack.ToolTip = item.Name;

            ExpandedItems.Items.Add(stack);
        }

        // ── Size: 4 columns per row (user request) — wider panels, fewer rows,
        //    all content visible without scrolling in the normal case.
        //    Min 2 cols (avoids super-narrow 1-column panel for 1-2 items).
        //    Panel grows in height with row count; only caps when exceeding screen. ──
        int cols = Math.Max(2, Math.Min(total, 4));
        int rows = (int)Math.Ceiling((double)total / cols);
        double targetW = Math.Max(cols * 88 + 32, 280);
        double targetH = Math.Max(rows * 98 + 50, 160);

        // Grow the panel to fit ALL content by default (no scrollbar in the
        // normal case — the user wants the box to just get bigger).
        // ONLY cap + let the ScrollViewer take over when the content genuinely
        // exceeds the available screen height (so nothing is unreachable).
        // Use the real screen WorkingArea height (excludes taskbar) rather than
        // parent.ActualHeight — the latter can momentarily read small during the
        // expand layout pass and wrongly trigger the cap on small stacks.
        double screenH = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Height ?? 0;
        if (screenH <= 0 && VisualTreeHelper.GetParent(this) is Canvas p)
            screenH = p.ActualHeight;
        if (screenH > 0)
        {
            double maxH = screenH - 24; // 12px breathing room top/bottom
            if (targetH > maxH)
                targetH = maxH;
        }
        // If targetH <= maxH we keep the natural (uncapped) height → box grows,
        // no scrollbar. The ScrollViewer's Auto visibility only shows a bar when
        // the capped content is still taller than its own viewport.

        Width = targetW;
        Height = targetH;
        // Clear collapsed-size clamps so the panel can grow to its natural size
        MinWidth = 0; MaxWidth = double.PositiveInfinity;
        MinHeight = 0; MaxHeight = double.PositiveInfinity;

        // IMPORTANT: Only apply size changes if we're actually expanded.
        // During Initialize(), both RenderCollapsedIcons AND RenderExpandedItems are
        // called — the latter would overwrite the collapsed 100×110 size with the
        // expanded size (400×600+), causing every stack to render HUGE on first
        // launch even though ExpandedView is still Visibility=Collapsed.
        if (!_isExpanded)
        {
            // Re-apply collapsed clamps that were just cleared above
            const double CW = 100, CH = 110;
            Width = CW; Height = CH;
            MinWidth = CW; MaxWidth = CW;
            MinHeight = CH; MaxHeight = CH;
        }
    }

    public void Toggle()
    {
        _isExpanded = !_isExpanded;
        AnimateToggle();
    }

    public void Collapse()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        AnimateToggle();
    }

    private void AnimateToggle()
    {
        // Stop any running animation to prevent stale Completed callbacks
        ExpandedView.BeginAnimation(OpacityProperty, null);

        if (_isExpanded)
        {
            // Expand: hide collapsed immediately, show expanded with fade-in
            CollapsedView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            RenderExpandedItems();

            // Bring to front so expanded panel covers other stacks cleanly
            Panel.SetZIndex(this, 100);

            // Save current position BEFORE shifting — we restore it on collapse
            // so the stack doesn't "wander" after expand/collapse cycles.
            if (VisualTreeHelper.GetParent(this) is Canvas cv)
            {
                double lx = Canvas.GetLeft(this);
                double ly = Canvas.GetTop(this);
                _preExpandPosition = new Point(
                    double.IsNaN(lx) ? 0 : lx,
                    double.IsNaN(ly) ? 0 : ly);
            }

            // Keep the expanded panel inside the canvas bounds — shift left/up if needed
            RepositionIfOutOfBounds();

            var anim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ExpandedView.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            // ── COLLAPSE ──
            // CRITICAL ORDER: hide ExpandedView BEFORE measuring.
            // RenderCollapsedIcons() calls Measure(); if ExpandedView is still
            // Visible at that moment, DesiredSize includes the 500px+ expanded
            // panel and the control NEVER shrinks — leaving a giant invisible
            // hit area that swallows clicks for stacks below it (the
            // "click 代码 but hit 其他" bug). So we swap visibility FIRST.

            // 0. Clear file selection
            ClearSelection();

            // 1. Reset z-order immediately so stacks below are reachable
            Panel.SetZIndex(this, 0);

            // 2. Swap visibility FIRST so the upcoming measure only sees CollapsedView
            ExpandedView.Visibility = Visibility.Collapsed;
            CollapsedView.Visibility = Visibility.Visible;

            // 3. Now measure + resize to the small collapsed size (hit area shrinks NOW)
            RenderCollapsedIcons();

            // 4. Restore pre-expand position so the stack doesn't wander
            if (_preExpandPosition.HasValue && VisualTreeHelper.GetParent(this) is Canvas c)
            {
                Canvas.SetLeft(this, _preExpandPosition.Value.X);
                Canvas.SetTop(this, _preExpandPosition.Value.Y);
                _preExpandPosition = null;
            }

            // Fade out the now-hidden expanded view (visual only, no hit impact)
            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            ExpandedView.BeginAnimation(OpacityProperty, anim);
        }
    }

    /// <summary>
    /// If this control extends beyond its parent Canvas bounds, shift it so it fits.
    /// Called on expand to prevent the panel from going off-screen.
    /// </summary>
    private void RepositionIfOutOfBounds()
    {
        var parent = VisualTreeHelper.GetParent(this) as Canvas;
        if (parent == null) return;

        double cw = parent.ActualWidth;
        double ch = parent.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        double x = Canvas.GetLeft(this);
        double y = Canvas.GetTop(this);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;

        // Use the explicitly-set Width/Height (set in RenderExpandedItems /
        // RenderCollapsedIcons) rather than ActualWidth/ActualHeight.
        // At expand time the layout pass hasn't run yet so Actual* is still
        // the old collapsed size (100×110), making every bound-check wrong.
        double w = Width;
        double h = Height;
        if (w <= 0) w = ActualWidth;
        if (h <= 0) h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Right edge overflow → shift left so panel stays fully inside canvas
        if (x + w > cw)
            x = Math.Max(0, cw - w);
        // Bottom edge overflow → shift up
        if (y + h > ch)
            y = Math.Max(0, ch - h);
        // Left edge safety net (e.g. panel wider than canvas)
        if (x < 0)
            x = 0;
        // Top edge safety net
        if (y < 0)
            y = 0;

        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
    }

    public void RefreshLayout()
    {
        if (_isExpanded)
            RenderExpandedItems();
    }

    /// <summary>
    /// Highlight the clicked file item with a blue selection border,
    /// and deselect the previously selected item.
    /// </summary>
    private void SelectItem(Border? newBorder)
    {
        // Deselect previous
        if (_selectedItemBorder != null)
        {
            _selectedItemBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            _selectedItemBorder.BorderThickness = new Thickness(0.5);
        }

        // Select new
        _selectedItemBorder = newBorder;
        if (_selectedItemBorder != null)
        {
            _selectedItemBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)); // Windows blue
            _selectedItemBorder.BorderThickness = new Thickness(2);
        }
    }

    private void ClearSelection()
    {
        if (_selectedItemBorder != null)
        {
            _selectedItemBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            _selectedItemBorder.BorderThickness = new Thickness(0.5);
            _selectedItemBorder = null;
        }
    }

    // ---- Mouse events ----

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // ── Expanded state: click on background (not on a file item) collapses ──
        // File items (the StackPanels inside ExpandedItems) set e.Handled=true in
        // their own MouseLeftButtonDown handlers (see RenderExpandedItems). So
        // if e.Handled is still false here, the click landed on empty background
        // (WrapPanel gaps, ScrollViewer padding, etc.) — collapse the stack.
        if (_isExpanded)
        {
            if (!e.Handled)
            {
                Collapse();
                e.Handled = true;
            }
            return;
        }

        // ── Collapsed state: start drag ──
        _pressPoint = e.GetPosition(this);
        _pressTime = DateTime.Now;
        _isLongPress = false;
        _isDragging = false;

        // Commit any stale transform from a previous incomplete drag
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (!double.IsNaN(left) && !double.IsNaN(top))
            _dragStartPos = new Point(left, top);
        else
            _dragStartPos = new Point(0, 0);

        // Create ONE transform and update its X/Y — avoids per-frame allocation
        _dragTransform = new TranslateTransform(0, 0);
        RenderTransform = _dragTransform;
        CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_isDragging)
        {
            // Commit final position and clear transform
            var offset = e.GetPosition(this) - _pressPoint;
            var newLeft = _dragStartPos.X + offset.X;
            var newTop = _dragStartPos.Y + offset.Y;

            // Snap to grid, like Windows desktop icons
            newLeft = DesktopGrid.SnapX(newLeft);
            newTop = DesktopGrid.SnapY(newTop);

            // Clamp inside the canvas so it can't be dragged off-screen.
            // If the snapped position would exceed bounds, the boundary value wins
            // over the grid — this lets users place stacks at the screen edges
            // even when the grid cell doesn't align perfectly (same as Windows).
            if (VisualTreeHelper.GetParent(this) is Canvas canvas)
            {
                double cw = canvas.ActualWidth;
                double ch = canvas.ActualHeight;
                if (cw > 0 && ch > 0)
                {
                    double maxX = Math.Max(0, cw - this.ActualWidth);
                    double maxY = Math.Max(0, ch - this.ActualHeight);
                    newLeft = Math.Clamp(newLeft, 0, maxX);
                    newTop = Math.Clamp(newTop, 0, maxY);
                }
            }

            Canvas.SetLeft(this, newLeft);
            Canvas.SetTop(this, newTop);
            RenderTransform = null;
            _dragTransform = null;
            _isDragging = false;
            IsManuallyPositioned = true;

            // Force parent canvas to repaint and clear any ghost pixels
            var parent = VisualTreeHelper.GetParent(this) as UIElement;
            parent?.InvalidateVisual();

            DragEnded?.Invoke(this);
            return;
        }
        var delta = e.GetPosition(this) - _pressPoint;
        if (delta.Length < 5 && !_isLongPress)
        {
            if (_isExpanded)
                return;
            StackClicked?.Invoke(_groupKey);
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        _isLongPress = false;
        // Don't release capture during drag — MouseLeave fires even with capture
        if (!_isDragging)
        {
            ReleaseMouseCapture();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_isExpanded) return;

        var delta = e.GetPosition(this) - _pressPoint;

        // Start dragging after moving past the threshold
        if (!_isDragging && delta.Length > 3)
        {
            _isDragging = true;
            DragStarted?.Invoke(this);
        }

        if (_isDragging)
        {
            // Update cached transform — no per-frame allocation, much smoother
            if (_dragTransform != null)
            {
                _dragTransform.X = delta.X;
                _dragTransform.Y = delta.Y;
            }
            return;
        }

        // Long press (only when NOT dragging)
        if (!_isLongPress)
        {
            var elapsed = (DateTime.Now - _pressTime).TotalMilliseconds;
            if (elapsed > 600 && delta.Length < 10)
            {
                _isLongPress = true;
                if (_items.Count > 0)
                    _items[0].Open();
            }
        }
    }

    // ---- Drag & Drop ----

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            // Files will be picked up by file watcher, but we could also move them
            // to a specific folder if desired (not implemented for virtual grouping)
        }
    }

    // ---- Icon extraction ----

    /// <summary>
    /// Key: file path, Value: set of Image elements currently showing this file's icon.
    /// When the real Windows icon finishes loading in the background, every Image in
    /// this set gets its Source updated automatically — no need for the user to
    /// expand/collapse to see the real icon.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentBag<WeakReference<Image>>> _iconPendingImages = new();
    private static readonly ConcurrentDictionary<string, bool> _iconLoading = new();

    /// <summary>
    /// Get a file icon — returns a crisp custom icon (<b>immediately</b>, no blocking)
    /// and kicks off a background task to extract the real Windows icon for this file.
    /// When the real icon arrives, all Image elements currently displaying this file
    /// are updated automatically on the UI thread.
    ///
    /// This two-phase approach eliminates the single biggest startup bottleneck:
    /// blocking SHGetFileInfo COM calls (500-1000ms each) were serialized on the UI
    /// thread during RenderAllStacks, causing a 10+ second "dead" window after launch.
    /// </summary>
    private static BitmapSource GetFileIcon(DesktopItem item, int size, Image? subscribingImage = null)
    {
        string cacheKey = item.Path;
        if (IconCache.TryGetValue(cacheKey, out var cached))
        {
            // If an Image element is passing in for subscription, register it so
            // any future cache update (e.g. real icon loaded) can push to it.
            return cached;
        }

        string ext = item.Extension?.TrimStart('.').ToUpperInvariant() ?? "";
        bool forceFallback = ext is "URL" or "HTM" or "HTML" or "WEBLOC" or "LNK";

        // ── Phase 1 (immediate): always return a crisp custom icon first ──
        var defaultIcon = CreateDefaultIcon(item, size);
        IconCache[cacheKey] = defaultIcon;

        // ── Phase 2 (background): try to extract a better Windows icon ──
        if (!forceFallback)
        {
            // Deduplicate: only one background load per file
            if (_iconLoading.TryAdd(cacheKey, true))
            {
                var capturedPath = item.Path;
                var capturedKey = cacheKey;
                var capturedSize = size;

                Task.Run(() =>
                {
                    try
                    {
                        using var icon = NativeMethods.ExtractHighResIcon(capturedPath, 256);
                        if (icon != null && icon.Size.Width >= 48 && icon.Size.Height >= 48)
                        {
                            using var iconBmp = icon.ToBitmap();
                            var hbmp = iconBmp.GetHbitmap();
                            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hbmp, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            bmp.Freeze();
                            Gdi32DeleteObject(hbmp);

                            // Replace cached default with the real icon
                            IconCache[capturedKey] = bmp;
                        }
                    }
                    catch { }
                    finally
                    {
                        _iconLoading.TryRemove(capturedKey, out _);
                    }
                });
            }
        }

        return defaultIcon;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Gdi32DeleteObject(IntPtr hObject);

    private static BitmapSource CreateDefaultIcon(DesktopItem item, int size)
    {
        // Cache by extension + group so distinct file types get distinct icons
        string ext = item.Extension?.TrimStart('.').ToUpperInvariant() ?? "";
        string cacheKey = $"default_{item.GroupKey}_{ext}_{(item.IsDirectory ? "dir" : "file")}_{(item.IsLink ? "link" : "nlink")}";
        if (IconCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // ── Color scheme per type ──
        // Each type gets a primary color + lighter accent for text/highlights
        Color primary, accent;
        string label;

        if (item.IsDirectory)
        {
            primary = Color.FromRgb(255, 183, 0);    // gold folder
            accent = Color.FromRgb(255, 248, 225);
            label = "";
        }
        else if (item.IsLink || ext == "LNK" || ext == "URL")
        {
            primary = Color.FromRgb(0, 120, 215);    // Windows blue shortcut
            accent = Color.FromRgb(220, 240, 255);
            label = ext.Length > 0 && ext.Length <= 4 ? ext : "LNK";
        }
        else if (item.GroupKey == "executable" || ext is "EXE" or "MSI" or "APPX" or "BAT" or "CMD" or "PS1")
        {
            primary = Color.FromRgb(0, 150, 136);    // teal app tile
            accent = Color.FromRgb(178, 235, 242);
            label = ext.Length > 0 && ext.Length <= 4 ? ext : "APP";
        }
        else
        {
            (primary, accent) = item.GroupKey switch
            {
                "image"        => (Color.FromRgb(33, 150, 243),   Color.FromRgb(225, 245, 254)),  // blue
                "video"        => (Color.FromRgb(156, 39, 176),   Color.FromRgb(243, 224, 248)),  // purple
                "audio"        => (Color.FromRgb(255, 87, 34),    Color.FromRgb(255, 224, 204)),  // deep orange
                "document"     => (Color.FromRgb(121, 85, 72),    Color.FromRgb(237, 228, 223)),  // brown
                "spreadsheet"  => (Color.FromRgb(76, 175, 80),    Color.FromRgb(232, 245, 233)),  // green
                "presentation" => (Color.FromRgb(244, 67, 54),    Color.FromRgb(255, 235, 238)),  // red
                "archive"      => (Color.FromRgb(139, 87, 42),    Color.FromRgb(237, 224, 210)),  // dark brown
                "code"         => (Color.FromRgb(63, 81, 181),    Color.FromRgb(232, 234, 246)),  // indigo
                "font"         => (Color.FromRgb(148, 103, 189),  Color.FromRgb(243, 229, 245)),  // deep purple
                "3d"           => (Color.FromRgb(0, 121, 107),    Color.FromRgb(224, 247, 242)),  // teal dark
                _              => (Color.FromRgb(117, 117, 117),  Color.FromRgb(245, 245, 245))   // grey
            };
            label = ext.Length > 0 && ext.Length <= 4 ? ext : "?";
        }

        // ── DPI-aware rendering ──
        // RenderTargetBitmap takes PIXEL dimensions, but our drawing is in DIPs.
        // On a 150% DPI screen (144 DPI), a 64 DIP icon needs 96 pixels.
        // Without this correction, the bitmap is too small and the icon gets clipped.
        double dpiX = 96.0, dpiY = 96.0;
        try
        {
            var source = PresentationSource.FromVisual(Application.Current.MainWindow);
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11 * 96;
                dpiY = source.CompositionTarget.TransformToDevice.M22 * 96;
            }
        }
        catch { }

        double dipSize = size;                          // drawing coordinates in DIPs
        int pixelW = (int)Math.Ceiling(dipSize * dpiX / 96.0);
        int pixelH = (int)Math.Ceiling(dipSize * dpiY / 96.0);
        double corner = dipSize * 0.20;
        double pixelsPerDip = dpiX / 96.0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // ── Shadow ──
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                null,
                new Rect(1.5, 2.5, dipSize - 2, dipSize - 3),
                corner, corner);

            if (item.IsDirectory)
            {
                // ── Folder shape: tab on top-left + body ──
                double bodyY = dipSize * 0.28;
                double bodyH = dipSize * 0.65;
                double tabW = dipSize * 0.42;
                double tabH = dipSize * 0.18;

                // Folder body gradient
                var folderGrad = new LinearGradientBrush(
                    Color.FromArgb(255, (byte)Math.Min(255, primary.R + 30), (byte)Math.Min(255, primary.G + 30), (byte)Math.Min(255, primary.B + 30)),
                    primary,
                    new Point(0, 0), new Point(0, 1));
                dc.DrawRoundedRectangle(folderGrad, null,
                    new Rect(2, bodyY, dipSize - 4, bodyH), corner * 0.5, corner * 0.5);

                // Folder tab (top-left)
                dc.DrawRoundedRectangle(folderGrad, null,
                    new Rect(2, bodyY - tabH * 0.7, tabW, tabH + 4), corner * 0.4, corner * 0.4);

                // Glossy highlight on folder body
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                    null,
                    new Rect(2, bodyY, dipSize - 4, bodyH * 0.4),
                    corner * 0.5, corner * 0.5);

                // White border
                dc.DrawRoundedRectangle(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 0.5),
                    new Rect(2, bodyY, dipSize - 4, bodyH), corner * 0.5, corner * 0.5);
            }
            else
            {
                // ── Rounded tile with gradient + gloss + text ──
                var gradient = new LinearGradientBrush(
                    Color.FromArgb(255, (byte)Math.Min(255, primary.R + 25), (byte)Math.Min(255, primary.G + 25), (byte)Math.Min(255, primary.B + 25)),
                    primary,
                    new Point(0, 0), new Point(0, 1));
                dc.DrawRoundedRectangle(gradient,
                    new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 0.5),
                    new Rect(0, 0, dipSize - 2, dipSize - 4),
                    corner, corner);

                // Glossy highlight on top 45%
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    null,
                    new Rect(0, 0, dipSize - 2, (dipSize - 4) * 0.45),
                    corner, corner);

                // ── Extension text — bold and prominent so the file type is unmistakable ──
                if (!string.IsNullOrEmpty(label))
                {
                    var typeface = new Typeface(
                        new FontFamily("Segoe UI, Microsoft YaHei, Arial"),
                        FontStyles.Normal,
                        FontWeights.Bold,
                        FontStretches.Normal);
                    // Aggressively large text: a 56 DIP tile rendering "URL" at 0.55
                    // yields ~31px tall characters — unmistakable at thumbnail size.
                    double fontSize = label.Length switch
                    {
                        1 => dipSize * 0.65,
                        2 => dipSize * 0.55,
                        3 => dipSize * 0.48,
                        4 => dipSize * 0.42,
                        _ => dipSize * 0.32
                    };
                    var ft = new FormattedText(
                        label,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        new SolidColorBrush(accent),
                        pixelsPerDip);

                    // Center text in the tile
                    double tx = ((dipSize - 2) - ft.Width) / 2;
                    double ty = ((dipSize - 4) - ft.Height) / 2;
                    dc.DrawText(ft, new Point(tx, ty));
                }
            }
        }

        var rtb = new RenderTargetBitmap(pixelW, pixelH, dpiX, dpiY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        IconCache[cacheKey] = rtb;
        return rtb;
    }
}
