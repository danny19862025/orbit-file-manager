using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orbit;

/// <summary>All settings, in the same JSON format as the Windows app: ~/.config/Orbit/settings.json.</summary>
public sealed class AppSettings
{
    public static AppSettings Current { get; private set; } = new();

    public static string Dir => Path.Combine(Xdg.Config, "Orbit");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public List<string> Bookmarks { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.Ordinal);
    public List<string> Dropzone { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public bool ShowHidden { get; set; }
    public string ViewMode { get; set; } = "grid";
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; }
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 800;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 250;
    public bool SidebarCollapsed { get; set; }
    public Dictionary<string, bool> Sections { get; set; } = new();
    public string Theme { get; set; } = "Catppuccin Mocha";
    public bool Translucent { get; set; }
    /// <summary>How see-through the window is when Translucent is on, 5–90 %.</summary>
    public int Transparency { get; set; } = 15;
    public bool RestoreTabs { get; set; } = true;
    public string SplitPath { get; set; }
    public double SplitWidth { get; set; } = 460;
    public List<string> CustomPlaces { get; set; } = new();
    public List<string> HiddenSections { get; set; } = new();
    public Dictionary<string, string> Keymap { get; set; } = new();
    public string IconPack { get; set; } = Orbit.IconPack.Material;
    public string CustomIconTheme { get; set; }
    public bool PackThumbnails { get; set; } = true;
    public List<ServerEntry> Servers { get; set; } = new();
    /// <summary>SSH host keys the user has accepted: "[host]:port" → "type SHA256:fingerprint".</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();
    /// <summary>Details view column widths: name, date, type, size.</summary>
    public double[] Columns { get; set; }

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
        Current.Normalize();
    }

    void Normalize()
    {
        Bookmarks ??= new();
        Recent ??= new();
        Dropzone ??= new();
        OpenTabs ??= new();
        Sections ??= new();
        Servers ??= new();
        KnownHosts ??= new();
        CustomPlaces ??= new();
        HiddenSections ??= new();
        Keymap ??= new();
        Servers.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Host));
        Theme = Orbit.Themes.Find(Theme).Name;
        Transparency = Math.Clamp(Transparency, 5, 90);
        if (Orbit.IconPack.Find(IconPack) == null) IconPack = Orbit.IconPack.Material;
        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (Tags != null)
            foreach (var kv in Tags)
                if (kv.Value != null && kv.Value.Count > 0) tags[kv.Key] = kv.Value;
        Tags = tags;
        if (ViewMode is not ("details" or "tree" or "columns")) ViewMode = "grid";
        if (SortBy is not ("name" or "date" or "size" or "type")) SortBy = "name";
        if (Width < 600 || double.IsNaN(Width)) Width = 1240;
        if (Height < 400 || double.IsNaN(Height)) Height = 800;
        if (SidebarWidth < 180 || SidebarWidth > 480 || double.IsNaN(SidebarWidth)) SidebarWidth = 250;
        if (Columns == null || Columns.Length != 4) Columns = new double[] { 400, 170, 170, 100 };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}

/// <summary>XDG base directories.</summary>
public static class Xdg
{
    static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) || !Path.IsPathRooted(v) ? fallback : v;
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Config => Env("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
    public static string Data => Env("XDG_DATA_HOME", Path.Combine(Home, ".local", "share"));
    public static string Cache => Env("XDG_CACHE_HOME", Path.Combine(Home, ".cache"));
    public static string Runtime => Env("XDG_RUNTIME_DIR", Path.Combine(Path.GetTempPath(), "orbit-" + Environment.UserName));
    public static string[] DataDirs => (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share")
        .Split(':', StringSplitOptions.RemoveEmptyEntries);

    static Dictionary<string, string> userDirs;

    /// <summary>A folder from ~/.config/user-dirs.dirs (DESKTOP, DOWNLOAD, DOCUMENTS, MUSIC, PICTURES, VIDEOS…).</summary>
    public static string UserDir(string key, string fallbackName)
    {
        if (userDirs == null)
        {
            userDirs = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var f = Path.Combine(Config, "user-dirs.dirs");
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var l = line.Trim();
                        if (!l.StartsWith("XDG_") || !l.Contains('=')) continue;
                        var k = l[4..l.IndexOf('=')].Replace("_DIR", "");
                        var v = l[(l.IndexOf('=') + 1)..].Trim().Trim('"').Replace("$HOME", Home);
                        userDirs[k] = v;
                    }
            }
            catch { }
        }
        if (userDirs.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p) && p.TrimEnd('/') != Home) return p.TrimEnd('/');
        return Path.Combine(Home, fallbackName);
    }
}
