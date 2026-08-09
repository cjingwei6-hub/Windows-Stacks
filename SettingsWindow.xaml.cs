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
using Stacks.Models;
using Stacks.Services;

namespace Stacks;

/// <summary>
/// Steam++-style settings window: left sidebar navigation + right content panels.
/// Solid background, custom dropdown buttons, modern toggle switches.
///
/// Panels:
///   1. 📁 分类源         — desktop toggle + folders list
///   2. 📊 分组与布局     — group mode / layout / sort
///   3. 👁 显示选项       — hide-apps + auto-start + refresh
///   4. 📝 自定义         — rename stacks + custom classification rules
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

    /// <summary>Custom-rule list edited by the user (full replacement).</summary>
    public Action<IReadOnlyList<CustomRule>>? OnCustomRulesChanged { get; set; }

    /// <summary>Custom-name map edited by the user (full replacement).</summary>
    public Action<IReadOnlyDictionary<string, string>>? OnCustomGroupNamesChanged { get; set; }

    // ── Internal state ──
    private readonly ObservableCollection<string> _folders = new();
    private readonly ObservableCollection<CustomRule> _customRules = new();
    private readonly ObservableCollection<GroupNameRow> _groupNameRows = new();
    private readonly Dictionary<string, string> _customGroupNames = new();
    private bool _classifyDesktop = true;
    private bool _hideApps;
    private bool _autoStart;

    /// <summary>Built-in groups whose display name can be customised.</summary>
    private static readonly List<string> RenameableKeys = new()
    {
        "folder", "image", "video", "audio", "document", "spreadsheet",
        "presentation", "archive", "code", "font", "3d", "executable", "other"
    };

    /// <summary>Currently open dropdown button.</summary>
    private Button? _openDropdown;

    // ContextMenus live in Window.Resources (they can't be in the visual tree),
    // so they're accessed via FindResource + cached after Loaded.
    private ContextMenu? _groupModeMenu;
    private ContextMenu? _layoutMenu;
    private ContextMenu? _sortModeMenu;

    /// <summary>Lightweight row model for the rename list (avoid full MVVM ceremony).</summary>
    private class GroupNameRow
    {
        public string GroupKey { get; set; } = "";
        public string DefaultName { get; set; } = "";
        public string CustomName { get; set; } = "";
    }

    public SettingsWindow()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
        CustomRuleList.ItemsSource = _customRules;
        GroupNameList.ItemsSource = _groupNameRows;
    }

    /// <summary>Initialize UI state from current settings.</summary>
    public void LoadState(
        bool classifyDesktop,
        List<string> folders,
        string groupMode,
        string layout,
        string sortBy,
        bool hideApps,
        bool autoStart,
        Dictionary<string, string> customGroupNames,
        List<CustomRule> customRules)
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

        // Dropdown buttons — set display text and current value in Tag
        SetDropdown(GroupModeBtn, groupMode switch
        {
            "kind" => "按类型", "date" => "按日期", "none" => "不分组",
            "custom" => "按自定义规则", _ => "按类型"
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

        // ── Custom panel: rename rows + rules list ──
        _customGroupNames.Clear();
        foreach (var kv in customGroupNames)
            _customGroupNames[kv.Key] = kv.Value;

        _groupNameRows.Clear();
        foreach (var key in RenameableKeys)
        {
            var defaultName = GroupEngine.GroupNames.GetValueOrDefault(key, key);
            var custom = _customGroupNames.GetValueOrDefault(key, "");
            _groupNameRows.Add(new GroupNameRow
            {
                GroupKey = key,
                DefaultName = defaultName,
                CustomName = custom
            });
        }

        _customRules.Clear();
        foreach (var r in customRules)
            _customRules.Add(r);
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
            PanelCustom.Visibility = Visibility.Collapsed;

            // Show selected panel
            switch (tag)
            {
                case "sources":  PanelSources.Visibility = Visibility.Visible; break;
                case "grouping": PanelGrouping.Visibility = Visibility.Visible; break;
                case "display":  PanelDisplay.Visibility = Visibility.Visible; break;
                case "custom":   PanelCustom.Visibility = Visibility.Visible; break;
            }
        }
    }

    // ═══ Drag move ═══

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═══ Toggle switches ═══

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

    // ═══ Custom dropdown buttons ═══

    private static void SetDropdown(Button btn, string text) => btn.Content = text;

    private void OpenDropdown(Button btn, ContextMenu? menu)
    {
        if (menu == null) return;
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
            if (_groupModeMenu != null) _groupModeMenu.IsOpen = false;
            if (_layoutMenu    != null) _layoutMenu.IsOpen = false;
            if (_sortModeMenu  != null) _sortModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    private void GroupModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, _groupModeMenu);
    }

    private void GroupModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(GroupModeBtn, mi.Header.ToString()!.Trim());
            GroupModeBtn.Tag = tag;
            OnGroupModeChanged?.Invoke(tag);
            if (_groupModeMenu != null) _groupModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    private void LayoutBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, _layoutMenu);
    }

    private void LayoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(LayoutBtn, mi.Header.ToString()!.Trim());
            LayoutBtn.Tag = tag;
            OnLayoutChanged?.Invoke(tag);
            if (_layoutMenu != null) _layoutMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    private void SortModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) OpenDropdown(btn, _sortModeMenu);
    }

    private void SortModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
        {
            SetDropdown(SortModeBtn, mi.Header.ToString()!.Trim());
            SortModeBtn.Tag = tag;
            OnSortModeChanged?.Invoke(tag);
            if (_sortModeMenu != null) _sortModeMenu.IsOpen = false;
            _openDropdown = null;
        }
    }

    // ═══ Refresh ═══

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDropdown();
        OnRefreshRequested?.Invoke();
    }

    // ═══ Custom panel: rename rows + rule list ═══

    /// <summary>
    /// Fired by TextBoxes in the rename list whenever the user edits a name.
    /// Uses the TextBox's Tag (set via DataTemplate on GroupKey) to find the row.
    /// </summary>
    private void GroupNameInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string groupKey) return;

        var newName = tb.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(newName))
        {
            _customGroupNames.Remove(groupKey);
        }
        else
        {
            _customGroupNames[groupKey] = newName;
        }

        OnCustomGroupNamesChanged?.Invoke(
            new Dictionary<string, string>(_customGroupNames));
    }

    /// <summary>
    /// Add a new rule from the inline form. Validates non-empty inputs and
    /// parses comma-separated extensions (prepends ".", lowercases, dedups).
    /// </summary>
    private void AddCustomRule_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenDropdown();

        var name = NewRuleName.Text?.Trim() ?? "";
        var extsRaw = NewRuleExts.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("请输入规则名称", "Stacks", MessageBoxButton.OK, MessageBoxImage.Warning);
            NewRuleName.Focus();
            return;
        }
        if (string.IsNullOrEmpty(extsRaw))
        {
            MessageBox.Show("请输入至少一个扩展名", "Stacks", MessageBoxButton.OK, MessageBoxImage.Warning);
            NewRuleExts.Focus();
            return;
        }

        var exts = extsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.StartsWith('.') ? s.ToLowerInvariant() : "." + s.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (exts.Count == 0)
        {
            MessageBox.Show("扩展名格式不正确", "Stacks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Idempotency: same name + same extension set → don't duplicate
        if (_customRules.Any(r => r.Name.Equals(name, StringComparison.Ordinal)
            && r.Extensions.SequenceEqual(exts)))
        {
            return;
        }

        _customRules.Add(new CustomRule { Name = name, Extensions = exts });

        // Clear inputs
        NewRuleName.Clear();
        NewRuleExts.Clear();

        OnCustomRulesChanged?.Invoke(_customRules.ToList());
    }

    private void RemoveCustomRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        var rule = _customRules.FirstOrDefault(r => r.Id == id);
        if (rule != null)
        {
            _customRules.Remove(rule);

            // Also remove any display-name override tied to this rule's group key
            var groupKey = GroupEngine.CustomKeyPrefix + id;
            if (_customGroupNames.Remove(groupKey))
                OnCustomGroupNamesChanged?.Invoke(
                    new Dictionary<string, string>(_customGroupNames));

            OnCustomRulesChanged?.Invoke(_customRules.ToList());
        }
    }

    // ═══ Window loaded ═══

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Resolve ContextMenu resources once (x:Name doesn't generate fields
        // for elements inside Window.Resources)
        _groupModeMenu = FindResource("GroupModeMenu") as ContextMenu;
        _layoutMenu    = FindResource("LayoutMenu") as ContextMenu;
        _sortModeMenu  = FindResource("SortModeMenu") as ContextMenu;

        // Show first panel by default
        PanelSources.Visibility = Visibility.Visible;
    }
}
