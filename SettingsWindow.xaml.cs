using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Stacks.Services;

namespace Stacks;

/// <summary>
/// Modern Fluent-style settings window.
/// Manages classification sources (desktop + multiple folders), grouping,
/// layout, sort, display toggles, and auto-start — all in one place.
///
/// Communication with MainWindow is via callback delegates (no tight coupling).
/// </summary>
public partial class SettingsWindow : Window
{
    // ── Callbacks: MainWindow wires these up on creation ──
    public Action? OnRefreshRequested { get; set; }
    public Action<bool>? OnDesktopToggled { get; set; }
    public Action<string>? OnFolderAdded { get; set; }       // path added
    public Action<string>? OnFolderRemoved { get; set; }      // path removed
    public Action<string>? OnGroupModeChanged { get; set; }   // "kind"|"date"|"none"
    public Action<string>? OnLayoutChanged { get; set; }      // "grid"|"fan"
    public Action<string>? OnSortModeChanged { get; set; }    // "name"|"date"|"size"|"type"
    public Action<bool>? OnHideAppsToggled { get; set; }
    public Action<bool>? OnAutoStartToggled { get; set; }

    // ── Internal state ──
    private readonly ObservableCollection<string> _folders = new();
    private bool _classifyDesktop = true;
    private bool _hideApps;
    private bool _autoStart;

    // Toggle animation helpers
    private static readonly SolidColorBrush ToggleOnBrush = new((Color)ColorConverter.ConvertFromString("#FF0078D4"));
    private static readonly SolidColorBrush ToggleOffBrush = new((Color)ColorConverter.ConvertFromString("#FFCCCCCC"));

    public SettingsWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
    }

    /// <summary>
    /// Initialize UI state from current settings. Called by MainWindow before ShowDialog().
    /// </summary>
    public void LoadState(
        bool classifyDesktop,
        List<string> folders,
        string groupMode,
        string layout,
        string sortBy,
        bool hideApps,
        bool autoStart)
    {
        _classifyDesktop = classifyDesktop;
        _hideApps = hideApps;
        _autoStart = autoStart;

        // Desktop toggle
        SetToggleState(DesktopToggle, DesktopThumb, classifyDesktop);

        // Folder list
        _folders.Clear();
        foreach (var f in folders)
            _folders.Add(f);

        // Group mode combo
        SelectComboByTag(GroupModeCombo, groupMode);

        // Layout combo
        SelectComboByTag(LayoutCombo, layout);

        // Sort mode combo
        SelectComboByTag(SortModeCombo, sortBy);

        // Hide apps toggle
        SetToggleState(HideAppsToggle, HideAppsThumb, hideApps);

        // Auto-start toggle
        SetToggleState(AutoStartToggle, AutoStartThumb, autoStart);
    }

    // ── Drag move (title bar / card area) ──

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // ── Close ──

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Toggle switches ──

    private void DesktopToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _classifyDesktop = !_classifyDesktop;
        AnimateToggle(DesktopToggle, DesktopThumb, _classifyDesktop);
        OnDesktopToggled?.Invoke(_classifyDesktop);
    }

    private void HideAppsToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _hideApps = !_hideApps;
        AnimateToggle(HideAppsToggle, HideAppsThumb, _hideApps);
        OnHideAppsToggled?.Invoke(_hideApps);
    }

    private void AutoStartToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _autoStart = !_autoStart;
        AnimateToggle(AutoStartToggle, AutoStartThumb, _autoStart);
        OnAutoStartToggled?.Invoke(_autoStart);
    }

    // ── Folder management ──

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要参与分类的文件夹"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var path = dlg.SelectedPath;
            if (!_folders.Contains(path))
            {
                _folders.Add(path);
                OnFolderAdded?.Invoke(path);
            }
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            _folders.Remove(path);
            OnFolderRemoved?.Invoke(path);
        }
    }

    // ── ComboBox selections ──

    private void GroupModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            OnGroupModeChanged?.Invoke(tag);
    }

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            OnLayoutChanged?.Invoke(tag);
    }

    private void SortModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            OnSortModeChanged?.Invoke(tag);
    }

    // ── Refresh ──

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        OnRefreshRequested?.Invoke();
    }

    // ── Helpers ──

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag as string == tagValue)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static void SetToggleState(Border toggle, Ellipse thumb, bool isOn)
    {
        toggle.Background = isOn ? ToggleOnBrush : ToggleOffBrush;
        var transform = (TranslateTransform)thumb.RenderTransform;
        transform.X = isOn ? 20 : 0;
    }

    private static void AnimateToggle(Border toggle, Ellipse thumb, bool isOn)
    {
        toggle.Background = isOn ? ToggleOnBrush : ToggleOffBrush;

        var anim = new DoubleAnimation
        {
            To = isOn ? 20 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ((TranslateTransform)thumb.RenderTransform).BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure window is centered after sizing
        Left -= ActualWidth / 2;
        Top -= ActualHeight / 2;
    }
}
