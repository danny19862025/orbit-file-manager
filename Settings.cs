using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orbit;

public sealed class AppSettings
{
    public static AppSettings Current { get; private set; } = new();

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public List<string> Bookmarks { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public bool RestoreTabs { get; set; } = true;
    public List<string> CustomPlaces { get; set; } = new();
    public List<string> HiddenSections { get; set; } = new();
    public Dictionary<string, string> Keymap { get; set; } = new();
    public string IconPack { get; set; } = Orbit.IconPack.Material;
    public bool PackThumbnails { get; set; } = true;
    public List<ServerEntry> Servers { get; set; } = new();
    /// <summary>SSH host keys the user has accepted: "[host]:port" → "type SHA256:fingerprint".</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new();

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
        if (IconPack != Orbit.IconPack.System && IconPack != Orbit.IconPack.Material) IconPack = Orbit.IconPack.Material;
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (Tags != null)
            foreach (var kv in Tags)
                if (kv.Value != null && kv.Value.Count > 0) tags[kv.Key] = kv.Value;
        Tags = tags;
        if (ViewMode is not ("details" or "tree" or "columns")) ViewMode = "grid";
        if (SortBy is not ("name" or "date" or "size" or "type")) SortBy = "name";
        if (Width < 600 || double.IsNaN(Width)) Width = 1240;
        if (Height < 400 || double.IsNaN(Height)) Height = 800;
        if (SidebarWidth < 180 || SidebarWidth > 480 || double.IsNaN(SidebarWidth)) SidebarWidth = 250;
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
            File.AppendAllText(Path.Combine(Dir, "error.log"), $"[{DateTime.Now:u}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
