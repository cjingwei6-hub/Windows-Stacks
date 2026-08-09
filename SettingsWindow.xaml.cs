using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Stacks.Services;

namespace Stacks;

/// <summary>
/// Steam++-style settings window: left sidebar navigation + right content panels.
/// Solid background, custom dropdown buttons, modern toggle switches.
/// </summary>
public partial class SettingsWindow : Window
{
    // ── Callbacks: MainWindow wires these up on creation ──
    public Action? OnRefreshRequested { get; set; }
    public Action<bool>? OnDesktopToggled { get; set; }
    public Action<string>? OnFolderAdded { get; set; }
    public Action<string>? OnFolderRemoved { get; set; }
    public Action<string>? OnGroupModeChanged { get; set; }
    public Action<string>? OnLayoutChanged { get; set; }
    public Action<string>? OnSortModeChanged { get; set; }
    public Action<bool>? OnHideAppsToggled { get; set; }
    public Action<bool>? OnAutoStartToggled { get; set; }

    // ── Internal state ──
    private readonly ObservableCollection<string> _folders = new();
    private bool _classifyDesktop = true;
    private bool _hideApps;
    private bool _autoStart;

    // Currently open dropdown button (to close when another opens or click outside)
    private Button? _openDropdown;

    public SettingsWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
    }

    /// <summary>Initialize UI state from current settings.</summary>
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
        SetToggleTag(DesktopToggle, classifyDesktop);

        // Folder list
        _folders.Clear();
        foreach (var f in folders)
            _folders.Add(f);

        // Dropdown buttons — set display text and store current value in Tag
        SetDropdown(GroupModeBtn, groupMode switch
        {
            "kind" => "按类型", "date" => "按日期", "none" => "不分组", _ => "按类型"
        });
        GroupModeBtn.Tag = groupMode;

        SetDropdown(LayoutBtn, layout == "fan" ? "扇形" : "网格");
        LayoutBtn.Tag = layout;

        SetDropdown(SortModeBtn, sortBy switch
        {
            "name" => "按名称", "date" => "按日期", "size" => "按大小",
            "type" => "按类型", _ => "按名称"
        });
        SortModeBtn.Tag = sortBy;

        // Display toggles
        SetToggleTag(HideAppsToggle, hideApps);
        SetToggleTag(AutoStartToggle, autoStart);
    }

    // ═══ Navigation ═══

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            // Hide all panels
            PanelSources.Visibility = Visibility.Collapsed;
            PanelGrouping.Visibility = Visibility.Collapsed;
            PanelDisplay.Visibility = Visibility.Collapsed;

            // Show selected panel
            switch (tag)
            {
                case "sources": PanelSources.Visibility = Visibility.Visible; break;
                case "grouping": PanelGrouping.Visibility = Visibility.Visible; break;
                case "display":  PanelDisplay.Visibility = Visibility.Visible; break;
            }
        }
    }

    // ═══ Drag move ═══

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═══ Toggle switches (Tag="On"/"Off" drives Style trigger; Ellipse position set in code) ═══

    private static void SetToggleTag(Border toggle, bool isOn)
    {
        toggle.Tag = isOn ? "On" : "Off";
        if (toggle.Child is Ellipse thumb)
        {
            thumb.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            thumb.Margin = isOn ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
        }
    }

    private void DesktopToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
        {
            _classifyDesktop = !_classifyDesktop;
            SetToggleTag(b, _classifyDesktop);
            OnDesktopToggled?.Invoke(_classifyDesktop);
        }
    }

    private void HideAppsToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
        {
            _hideApps = !_hideApps;
            SetToggleTag(b, _hideApps);
            OnHideAppsToggled?.Invoke(_hideApps);
        }
    }

    private void AutoStartToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
        {
            _autoStart = !_autoStart;
            SetToggleTag(b, _autoStart);
            OnAutoStartToggled?.Invoke(_autoStart);
        }
    }

    // ═══ Folder management ═══

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDropdown();
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

    // ═══ Custom dropdown buttons (replace retro ComboBox) ═══

    private static void SetDropdown(Button btn, string text)
    {
        btn.Content = text;
    }

    /// <summary>Open the context menu below the dropdown button.</summary>
    private void OpenDropdown(Button btn, ContextMenu menu)
    {
        // If same button clicked again, toggle close
        if (_openDropdown == btn && menu.IsOpen)
        {
            menu.IsOpen = false;
            _openDropdown = null;
            return;
        }

        CloseOpenDropdown();
        _openDropdown = btn;
        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void CloseOpenDropdown()
    {
        if (_openDropdown != null)
        {
            // Find any open context menu and close it
            GroupModeMenu.IsOpen = false;
            LayoutMenu.IsOpen = false;
            SortModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    // --- Group mode dropdown ---
    private void GroupModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, GroupModeMenu);
    }

    private void GroupModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(GroupModeBtn, mi.Header.ToString().Trim());
            GroupModeBtn.Tag = tag;
            OnGroupModeChanged?.Invoke(tag);
            GroupModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    // --- Layout dropdown ---
    private void LayoutBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, LayoutMenu);
    }

    private void LayoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(LayoutBtn, mi.Header.ToString().Trim());
            LayoutBtn.Tag = tag;
            OnLayoutChanged?.Invoke(tag);
            LayoutMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    // --- Sort mode dropdown ---
    private void SortModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, SortModeMenu);
    }

    private void SortModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(SortModeBtn, mi.Header.ToString().Trim());
            SortModeBtn.Tag = tag;
            OnSortModeChanged?.Invoke(tag);
            SortModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    // ═══ Refresh ═══

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDropdown();
        OnRefreshRequested?.Invoke();
    }

    // ═══ Window loaded ═══

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show first panel by default
        PanelSources.Visibility = Visibility.Visible;
    }
}
