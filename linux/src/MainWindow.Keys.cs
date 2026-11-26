using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Orbit;

/// <summary>Keyboard shortcuts as a table, so they can be reassigned in Settings › Shortcuts. (Same names and defaults as the Windows app.)</summary>
public partial class MainWindow
{
    public enum KeyScope { Anywhere, NotTyping, InFileList }

    public sealed record ShortcutDef(string Id, string Group, string Label, string Default, KeyScope Scope);

    /// <summary>Every reassignable command with its default key. Order = order shown in Settings.</summary>
    public static readonly ShortcutDef[] Shortcuts =
    {
        new("find", "General", "Find anything", "Ctrl+K", KeyScope.Anywhere),
        new("settings", "General", "Open Settings", "Ctrl+OemComma", KeyScope.Anywhere),
        new("preview", "General", "Preview selected item", "Space", KeyScope.InFileList),
        new("properties", "General", "Properties", "Alt+Enter", KeyScope.NotTyping),

        new("newTab", "Tabs", "New tab", "Ctrl+T", KeyScope.Anywhere),
        new("closeTab", "Tabs", "Close tab", "Ctrl+W", KeyScope.Anywhere),
        new("nextTab", "Tabs", "Next tab", "Ctrl+Tab", KeyScope.Anywhere),
        new("prevTab", "Tabs", "Previous tab", "Ctrl+Shift+Tab", KeyScope.Anywhere),

        new("back", "Navigation", "Back", "Alt+Left", KeyScope.Anywhere),
        new("forward", "Navigation", "Forward", "Alt+Right", KeyScope.Anywhere),
        new("up", "Navigation", "Up one folder", "Alt+Up", KeyScope.Anywhere),
        new("address", "Navigation", "Type a path", "Ctrl+L", KeyScope.Anywhere),
        new("refresh", "Navigation", "Refresh", "F5", KeyScope.Anywhere),
        new("bookmark", "Navigation", "Bookmark this folder", "Ctrl+D", KeyScope.Anywhere),

        new("filter", "View", "Filter this folder", "Ctrl+F", KeyScope.Anywhere),
        new("hidden", "View", "Show / hide hidden items", "Ctrl+H", KeyScope.Anywhere),
        new("gridView", "View", "Grid view", "Ctrl+Shift+D1", KeyScope.Anywhere),
        new("detailsView", "View", "Details view", "Ctrl+Shift+D2", KeyScope.Anywhere),
        new("columnsView", "View", "Columns view", "Ctrl+Shift+D3", KeyScope.Anywhere),
        new("treeView", "View", "Tree view", "Ctrl+Shift+D4", KeyScope.Anywhere),
        new("split", "View", "Split view", "Ctrl+Oem5", KeyScope.Anywhere),

        new("newFolder", "Files", "New folder", "Ctrl+Shift+N", KeyScope.Anywhere),
        new("rename", "Files", "Rename", "F2", KeyScope.InFileList),
        new("copy", "Files", "Copy", "Ctrl+C", KeyScope.InFileList),
        new("cut", "Files", "Cut", "Ctrl+X", KeyScope.InFileList),
        new("paste", "Files", "Paste", "Ctrl+V", KeyScope.NotTyping),
        new("copyPath", "Files", "Copy path", "Ctrl+Shift+C", KeyScope.NotTyping),
        new("delete", "Files", "Move to Trash", "Delete", KeyScope.InFileList),
        new("deletePermanent", "Files", "Delete permanently", "Shift+Delete", KeyScope.InFileList),
    };

    /// <summary>Fixed keys that aren't reassignable (shown for reference in Settings).</summary>
    public static readonly (string Label, string Keys)[] FixedShortcuts =
    {
        ("Open the selected item", "Enter"), ("Back", "Backspace / Mouse back"), ("Clear selection or filter", "Esc"),
        ("Context menu", "Menu key / Shift+F10"), ("Open folder in a new tab", "Middle-click"), ("Box-select", "Drag on empty space"),
        ("Copy / move while dragging", "Hold Ctrl / Shift"),
    };

    Dictionary<string, Action> shortcutActions;

    Dictionary<string, Action> ShortcutActions => shortcutActions ??= new()
    {
        ["find"] = OpenFind,
        ["settings"] = () => OpenSettings(null),
        ["preview"] = TogglePreview,
        ["properties"] = ShowProperties,
        ["newTab"] = () => AddTab(IsTag(current?.Path) ? Home : current?.Path ?? Home, true),
        ["closeTab"] = () => CloseTab(current),
        ["nextTab"] = () => CycleTab(1),
        ["prevTab"] = () => CycleTab(-1),
        ["back"] = GoBack,
        ["forward"] = GoForward,
        ["up"] = GoUp,
        ["address"] = BeginEditAddress,
        ["refresh"] = () => LoadCurrent(keepSelection: true),
        ["bookmark"] = () => ToggleBookmark(null),
        ["filter"] = OpenSearch,
        ["hidden"] = ToggleHidden,
        ["gridView"] = () => { SetView("grid"); UpdateCommandBar(); },
        ["detailsView"] = () => { SetView("details"); UpdateCommandBar(); },
        ["columnsView"] = () => { SetView("columns"); UpdateCommandBar(); },
        ["treeView"] = () => { SetView("tree"); UpdateCommandBar(); },
        ["split"] = ToggleSplit,
        ["newFolder"] = NewFolder,
        ["rename"] = BeginRenameSelected,
        ["copy"] = () => CopyToClipboard(false),
        ["cut"] = () => CopyToClipboard(true),
        ["paste"] = () => Paste(current?.Path),
        ["copyPath"] = () => CopyPaths(Selected().Select(f => f.FullPath).DefaultIfEmpty(current?.Path)),
        ["delete"] = () => DeleteSelected(false),
        ["deletePermanent"] = () => DeleteSelected(true),
    };

    // ---------- gesture strings ----------
    static readonly HashSet<Key> ModifierKeysOnly = new()
    {
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin, Key.System
    };

    /// <summary>Keys that share a value under several names: the name used in settings (the Windows one).</summary>
    static readonly string[] PreferredNames =
    {
        "OemComma", "OemPeriod", "OemMinus", "OemPlus", "OemQuestion", "Oem1", "OemQuotes", "OemOpenBrackets", "Oem6", "Oem5", "Oem3",
        "Enter", "Back", "PageUp", "PageDown", "CapsLock", "Escape"
    };

    static string KeyName(Key key)
    {
        foreach (var n in PreferredNames)
            if (Enum.TryParse<Key>(n, out var k) && k == key) return n == "Back" ? "Backspace" : n;
        return key.ToString();
    }

    /// <summary>"Ctrl+Shift+N"-style gesture, or null for a lone modifier key.</summary>
    public static string GestureFrom(Key key, KeyModifiers m)
    {
        if (ModifierKeysOnly.Contains(key) || key == Key.None) return null;
        var parts = new List<string>();
        if ((m & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((m & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((m & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((m & KeyModifiers.Meta) != 0) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>Human-friendly form: "Ctrl+," instead of "Ctrl+OemComma".</summary>
    public static string PrettyGesture(string g)
    {
        if (string.IsNullOrEmpty(g)) return "Not set";
        return string.Join("+", g.Split('+').Select(p => p switch
        {
            "OemComma" => ",", "OemPeriod" => ".", "OemMinus" => "-", "OemPlus" => "=", "OemQuestion" => "/", "Oem1" => ";",
            "OemQuotes" => "'", "OemOpenBrackets" => "[", "Oem6" => "]", "Oem5" => "\\", "Oem3" => "`",
            "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "Delete" => "Del", "Escape" => "Esc",
            "PageUp" => "PgUp", "Next" => "PgDn", "PageDown" => "PgDn", "Win" => "Super",
            _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
            _ when p.StartsWith("NumPad") => "Num " + p[6..],
            _ => p
        }));
    }

    public static string GestureFor(string id)
    {
        if (AppSettings.Current.Keymap.TryGetValue(id, out var g)) return g;
        return Shortcuts.FirstOrDefault(s => s.Id == id)?.Default;
    }

    /// <summary>Compares gestures by their keys, so "Ctrl+Oem5" and "Ctrl+OemPipe" are the same.</summary>
    static bool SameGesture(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var pa = a.Split('+'); var pb = b.Split('+');
        if (pa.Length != pb.Length) return false;
        if (!pa[..^1].OrderBy(x => x).SequenceEqual(pb[..^1].OrderBy(x => x), StringComparer.OrdinalIgnoreCase)) return false;
        return Enum.TryParse<Key>(pa[^1] == "Backspace" ? "Back" : pa[^1], true, out var ka) &&
               Enum.TryParse<Key>(pb[^1] == "Backspace" ? "Back" : pb[^1], true, out var kb) && ka == kb;
    }

    /// <summary>Assign a gesture; any other command using it loses it. Returns the label of the command that lost it.</summary>
    public static string SetGesture(string id, string gesture)
    {
        string displaced = null;
        if (!string.IsNullOrEmpty(gesture))
            foreach (var s in Shortcuts)
                if (s.Id != id && SameGesture(GestureFor(s.Id), gesture))
                {
                    AppSettings.Current.Keymap[s.Id] = "";
                    displaced = s.Label;
                }
        var def = Shortcuts.First(s => s.Id == id).Default;
        if (SameGesture(gesture, def)) AppSettings.Current.Keymap.Remove(id);
        else AppSettings.Current.Keymap[id] = gesture ?? "";
        return displaced;
    }

    public static void ResetShortcuts() => AppSettings.Current.Keymap.Clear();

    /// <summary>Runs the command bound to this key press, if any. Returns true when handled.</summary>
    bool RunShortcut(Key key, KeyModifiers m, bool inText, bool inList)
    {
        var g = GestureFrom(key, m);
        if (g == null) return false;
        foreach (var s in Shortcuts)
        {
            if (!SameGesture(GestureFor(s.Id), g)) continue;
            bool ok = s.Scope switch
            {
                KeyScope.Anywhere => true,
                KeyScope.NotTyping => !inText,
                _ => inList
            };
            // Ctrl+C / Ctrl+V / Ctrl+X inside a text box belong to the text box
            if (inText && s.Scope != KeyScope.Anywhere) continue;
            if (!ok) continue;
            if (ShortcutActions.TryGetValue(s.Id, out var run)) { run(); return true; }
        }
        return false;
    }
}
