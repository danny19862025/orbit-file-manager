using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Orbit;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class TagPalette
{
    public static readonly string[] Names = { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray" };

    static readonly Dictionary<string, Brush> brushes = new()
    {
        ["Red"] = Make("#F87171"),
        ["Orange"] = Make("#FB923C"),
        ["Yellow"] = Make("#FACC15"),
        ["Green"] = Make("#4ADE80"),
        ["Blue"] = Make("#60A5FA"),
        ["Purple"] = Make("#C084FC"),
        ["Gray"] = Make("#9CA3AF"),
    };

    static Brush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Brush Brush(string name) => brushes.TryGetValue(name, out var b) ? b : brushes["Gray"];
}

public sealed class FileItem : ObservableObject
{
    string name;
    ImageSource large, small;
    bool largeRequested, smallRequested;
    bool isRenaming, isCut, isDropTarget;
    string editName;
    List<string> tags = new();

    public string Name { get => name; set { name = value; OnChanged(); OnChanged(nameof(Tip)); } }
    public string FullPath { get; set; }
    public bool IsDir { get; private set; }
    public long Size { get; private set; }
    public DateTime Modified { get; private set; }
    public FileAttributes Attributes { get; private set; }
    public string Ext { get; private set; } = "";
    public string TypeName { get; private set; } = "";

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsCloud => ((int)Attributes & (0x400000 | 0x40000)) != 0 || (Attributes & FileAttributes.Offline) != 0;
    public string SizeText => IsDir ? "" : Fmt.Size(Size);
    public string ModifiedText => Modified.Year < 1700 ? "" : Modified.ToString("g", CultureInfo.CurrentCulture);

    public string Tip
    {
        get
        {
            var s = Name + "\n" + TypeName;
            if (!IsDir) s += " · " + SizeText;
            if (ModifiedText.Length > 0) s += "\nModified " + ModifiedText;
            return s;
        }
    }

    public IReadOnlyList<string> Tags => tags;
    public IEnumerable<Brush> TagBrushes => tags.Select(TagPalette.Brush).ToList();

    public void SetTags(IEnumerable<string> t)
    {
        tags = t == null ? new List<string>() : t.ToList();
        OnChanged(nameof(Tags));
        OnChanged(nameof(TagBrushes));
    }

    public bool IsRenaming { get => isRenaming; set { isRenaming = value; OnChanged(); } }
    public string EditName { get => editName; set { editName = value; OnChanged(); } }
    public bool IsCut { get => isCut; set { if (isCut != value) { isCut = value; OnChanged(); } } }
    public bool IsDropTarget { get => isDropTarget; set { if (isDropTarget != value) { isDropTarget = value; OnChanged(); } } }

    // Tree View state
    int depth;
    bool isExpanded;
    public int Depth { get => depth; set { if (depth != value) { depth = value; OnChanged(); OnChanged(nameof(TreeIndent)); } } }
    public double TreeIndent => depth * 18;
    public bool IsExpanded { get => isExpanded; set { if (isExpanded != value) { isExpanded = value; OnChanged(); } } }

    public ImageSource LargeIcon
    {
        get
        {
            if (large == null && !largeRequested)
            {
                largeRequested = true;
                large = IconService.Get(this, true, img =>
                {
                    if (img == null) { largeRequested = false; OnChanged(nameof(LargeIcon)); return; }
                    large = img; OnChanged(nameof(LargeIcon));
                });
            }
            return large;
        }
    }

    public ImageSource SmallIcon
    {
        get
        {
            if (small == null && !smallRequested)
            {
                smallRequested = true;
                small = IconService.Get(this, false, img =>
                {
                    if (img == null) { smallRequested = false; OnChanged(nameof(SmallIcon)); return; }
                    small = img; OnChanged(nameof(SmallIcon));
                });
            }
            return small;
        }
    }

    public void ResetIcons()
    {
        large = small = null;
        largeRequested = smallRequested = false;
        OnChanged(nameof(LargeIcon));
        OnChanged(nameof(SmallIcon));
    }

    public static FileItem From(FileSystemInfo info)
    {
        var f = new FileItem
        {
            name = info.Name,
            FullPath = info.FullName,
            IsDir = (info.Attributes & FileAttributes.Directory) != 0
        };
        f.Fill(info);
        return f;
    }

    public bool IsRemote { get; private set; }

    public static FileItem Remote(string url, string name, bool dir, long size, DateTime modified)
    {
        var f = new FileItem { name = name, FullPath = url, IsDir = dir, IsRemote = true };
        f.Attributes = (dir ? FileAttributes.Directory : FileAttributes.Normal) | (name.StartsWith('.') ? FileAttributes.Hidden : 0);
        f.Modified = modified;
        f.Size = dir ? -1 : size;
        f.Ext = dir ? "" : Path.GetExtension(name).ToLowerInvariant();
        f.TypeName = IconService.TypeName(f.Ext, dir);
        return f;
    }

    /// <summary>True for items inside an archive (read-only, extracted on demand).</summary>
    public bool InArchive { get; private set; }
    public long Packed { get; private set; }
    public bool Encrypted { get; private set; }

    public static FileItem Archived(string url, string name, bool dir, long size, long packed, DateTime modified, bool encrypted)
    {
        var f = Remote(url, name, dir, size, modified);
        f.InArchive = true;
        f.Packed = packed;
        f.Encrypted = encrypted;
        return f;
    }

    public static FileItem TryFromPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) return From(new DirectoryInfo(path));
            if (File.Exists(path)) return From(new FileInfo(path));
        }
        catch { }
        return null;
    }

    void Fill(FileSystemInfo info)
    {
        Attributes = info.Attributes;
        Modified = info.LastWriteTime;
        Size = IsDir ? -1 : (info is FileInfo fi ? fi.Length : 0);
        Ext = IsDir ? "" : Path.GetExtension(info.Name).ToLowerInvariant();
        TypeName = IconService.TypeName(Ext, IsDir);
    }

    public void Rebase(string newName, string newPath)
    {
        FullPath = newPath;
        Name = newName;
        if (!IsDir)
        {
            Ext = Path.GetExtension(newName).ToLowerInvariant();
            TypeName = IconService.TypeName(Ext, false);
        }
        ResetIcons();
        OnChanged(string.Empty);
    }

    /// <summary>Copy metadata from a fresh snapshot of the same path. Returns true if the thumbnail may have changed.</summary>
    public bool UpdateFrom(FileItem o)
    {
        bool contentChanged = o.Modified != Modified || o.Size != Size;
        name = o.name;
        Attributes = o.Attributes;
        Modified = o.Modified;
        Size = o.Size;
        Ext = o.Ext;
        TypeName = o.TypeName;
        if (contentChanged && !IsDir) { large = null; largeRequested = false; }
        OnChanged(string.Empty);
        return contentChanged;
    }
}

public sealed class TabState : ObservableObject
{
    string path;
    bool isActive;

    public string Path { get => path; set { path = value; OnChanged(); OnChanged(nameof(Title)); } }
    public string Title => MainWindow.Display(path);
    public bool IsActive { get => isActive; set { isActive = value; OnChanged(); } }
    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();
}

public sealed class SideItem : ObservableObject
{
    bool isActive;
    string detail;
    double usage;

    public string Name { get; set; }
    public string Path { get; set; }
    public string Glyph { get; set; }
    public string Kind { get; set; }
    public ServerEntry Server { get; set; }
    public string ToolTipText { get; set; }
    /// <summary>Small protocol chip shown on the right (SMB, SFTP, DRIVE…).</summary>
    public string Badge { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    /// <summary>Green "connected / mounted" dot after the name.</summary>
    public bool IsConnected { get; set; }
    public Brush TagBrush { get; set; }
    public bool HasGlyph => TagBrush == null;
    public bool IsTagItem => TagBrush != null;
    public bool ShowUsage { get; set; }
    public string Tip => ToolTipText ?? Path;

    public string Detail { get => detail; set { detail = value; OnChanged(); } }
    public double Usage { get => usage; set { usage = value; OnChanged(); } }
    public bool IsActive { get => isActive; set { if (isActive != value) { isActive = value; OnChanged(); } } }
}

public sealed class Crumb
{
    public string Name { get; set; }
    public string Path { get; set; }
    public bool IsLast { get; set; }
}

public sealed class ItemComparer : IComparer
{
    readonly string by;
    readonly bool desc;

    public ItemComparer(string by, bool desc) { this.by = by; this.desc = desc; }

    public int Compare(object a, object b)
    {
        var x = (FileItem)a;
        var y = (FileItem)b;
        if (x.IsDir != y.IsDir) return x.IsDir ? -1 : 1;
        int r = by switch
        {
            "date" => x.Modified.CompareTo(y.Modified),
            "size" => x.Size.CompareTo(y.Size),
            "type" => string.Compare(x.TypeName, y.TypeName, StringComparison.CurrentCultureIgnoreCase),
            _ => 0
        };
        if (r == 0) r = Native.StrCmpLogicalW(x.Name, y.Name);
        return desc ? -r : r;
    }
}

public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class CountToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int n = value is int i ? i : 0;
        bool show = p as string == "zero" ? n == 0 : n > 0;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
