using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

    static readonly Dictionary<string, IBrush> brushes = new()
    {
        ["Red"] = Make("#F87171"),
        ["Orange"] = Make("#FB923C"),
        ["Yellow"] = Make("#FACC15"),
        ["Green"] = Make("#4ADE80"),
        ["Blue"] = Make("#60A5FA"),
        ["Purple"] = Make("#C084FC"),
        ["Gray"] = Make("#9CA3AF"),
    };

    static IBrush Make(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static IBrush Brush(string name) => brushes.TryGetValue(name, out var b) ? b : brushes["Gray"];
}

public sealed class FileItem : ObservableObject
{
    string name;
    IImage large, small;
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
    /// <summary>A symbolic link (shown with the type "Link to …").</summary>
    public bool IsLink { get; private set; }

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0 || (name?.StartsWith('.') ?? false);
    public bool IsCloud => false;
    public string SizeText => IsDir ? "" : Fmt.Size(Size);
    public string ModifiedText => Modified.Year < 1700 ? "" : Modified.ToString("g", CultureInfo.CurrentCulture);

    public override string ToString() => Name;

    public string Tip
    {
        get
        {
            if (IsRecycled) return Name + (IsDir ? "" : "\n" + SizeText) + "\nFrom " + TypeName + "\nDeleted " + ModifiedText;
            var s = Name + "\n" + TypeName;
            if (!IsDir) s += " · " + SizeText;
            if (ModifiedText.Length > 0) s += "\nModified " + ModifiedText;
            return s;
        }
    }

    public IReadOnlyList<string> Tags => tags;
    public IEnumerable<IBrush> TagBrushes => tags.Select(TagPalette.Brush).ToList();
    public bool HasTags => tags.Count > 0;

    public void SetTags(IEnumerable<string> t)
    {
        tags = t == null ? new List<string>() : t.ToList();
        OnChanged(nameof(Tags));
        OnChanged(nameof(TagBrushes));
        OnChanged(nameof(HasTags));
    }

    public bool IsRenaming
    {
        get => isRenaming;
        set { isRenaming = value; OnChanged(); OnChanged(nameof(NameVisible)); }
    }
    public bool NameVisible => !isRenaming;
    public string EditName { get => editName; set { editName = value; OnChanged(); } }
    public bool IsCut { get => isCut; set { if (isCut != value) { isCut = value; OnChanged(); OnChanged(nameof(ItemOpacity)); } } }
    public bool IsDropTarget { get => isDropTarget; set { if (isDropTarget != value) { isDropTarget = value; OnChanged(); } } }
    /// <summary>Hidden items are dimmed, cut items more so.</summary>
    public double ItemOpacity => isCut ? 0.45 : IsHidden ? 0.55 : 1;

    // Tree View state
    int depth;
    bool isExpanded;
    public int Depth { get => depth; set { if (depth != value) { depth = value; OnChanged(); OnChanged(nameof(TreeIndent)); } } }
    public double TreeIndent => depth * 18;
    public bool IsExpanded { get => isExpanded; set { if (isExpanded != value) { isExpanded = value; OnChanged(); OnChanged(nameof(ChevronGlyph)); } } }
    public string ChevronGlyph => isExpanded ? "" : "";

    public IImage LargeIcon
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

    public IImage SmallIcon
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
        f.TypeName = IconService.TypeName(f.Ext, dir, name);
        return f;
    }

    /// <summary>Where a Trash item was deleted from (null for everything else).</summary>
    public string OriginalPath { get; private set; }
    public bool IsRecycled => OriginalPath != null;
    /// <summary>The .trashinfo file that describes a Trash item.</summary>
    public string TrashInfo { get; private set; }

    /// <summary>An item in the Trash: shown under its original name, with the deletion time as its date.</summary>
    public static FileItem Recycled(string dataPath, string infoPath, string originalPath, DateTime deleted, long size, bool dir)
    {
        var name = Path.GetFileName(originalPath.TrimEnd('/'));
        if (string.IsNullOrEmpty(name)) name = originalPath;
        var f = new FileItem { name = name, FullPath = dataPath, IsDir = dir, OriginalPath = originalPath, TrashInfo = infoPath };
        f.Attributes = dir ? FileAttributes.Directory : FileAttributes.Normal;
        f.Modified = deleted;
        f.Size = dir ? -1 : size;
        f.Ext = dir ? "" : Path.GetExtension(name).ToLowerInvariant();
        f.TypeName = Path.GetDirectoryName(originalPath) ?? originalPath; // the Type column shows where it came from
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
            var fi = new FileInfo(path);
            if (fi.LinkTarget != null) return From(fi); // broken symlink
        }
        catch { }
        return null;
    }

    void Fill(FileSystemInfo info)
    {
        Attributes = info.Attributes;
        try { IsLink = info.LinkTarget != null; } catch { }
        Modified = info.LastWriteTime;
        long len = 0;
        if (!IsDir && info is FileInfo fi) { try { len = fi.Length; } catch { } }
        Size = IsDir ? -1 : len;
        Ext = IsDir ? "" : Path.GetExtension(info.Name).ToLowerInvariant();
        TypeName = IconService.TypeName(Ext, IsDir, info.Name);
        if (IsLink) TypeName = "Link to " + (IsDir ? "folder" : TypeName);
    }

    public void Rebase(string newName, string newPath)
    {
        FullPath = newPath;
        Name = newName;
        if (!IsDir)
        {
            Ext = Path.GetExtension(newName).ToLowerInvariant();
            TypeName = IconService.TypeName(Ext, false, newName);
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
    /// <summary>Block device behind a drive (for Eject), if known.</summary>
    public string Device { get; set; }
    public bool Removable { get; set; }
    /// <summary>Small protocol chip shown on the right (SMB, SFTP, DRIVE…).</summary>
    public string Badge { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    /// <summary>Green "connected / mounted" dot after the name.</summary>
    public bool IsConnected { get; set; }
    public IBrush TagBrush { get; set; }
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
    public bool HasChevron => !IsLast;
}

public sealed class ItemComparer : IComparer<FileItem>
{
    readonly string by;
    readonly bool desc;

    public ItemComparer(string by, bool desc) { this.by = by; this.desc = desc; }

    public int Compare(FileItem x, FileItem y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x.IsDir != y.IsDir) return x.IsDir ? -1 : 1;
        int r = by switch
        {
            "date" => x.Modified.CompareTo(y.Modified),
            "size" => x.Size.CompareTo(y.Size),
            "type" => string.Compare(x.TypeName, y.TypeName, StringComparison.CurrentCultureIgnoreCase),
            _ => 0
        };
        if (r == 0) r = Natural.Compare(x.Name, y.Name);
        if (r == 0) r = string.CompareOrdinal(x.Name, y.Name);
        return desc ? -r : r;
    }
}

/// <summary>"Natural" name order like Windows Explorer: file2 before file10, case-insensitive.</summary>
public static class Natural
{
    public static int Compare(string a, string b)
    {
        if (a == null || b == null) return string.CompareOrdinal(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int si = i, sj = j;
                while (si < a.Length && a[si] == '0') si++;
                while (sj < b.Length && b[sj] == '0') sj++;
                int ei = si, ej = sj;
                while (ei < a.Length && char.IsDigit(a[ei])) ei++;
                while (ej < b.Length && char.IsDigit(b[ej])) ej++;
                int la = ei - si, lb = ej - sj;
                if (la != lb) return la < lb ? -1 : 1;
                int c = string.CompareOrdinal(a, si, b, sj, la);
                if (c != 0) return c < 0 ? -1 : 1;
                i = ei; j = ej;
                continue;
            }
            int cmp = string.Compare(a, i, b, j, 1, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            if (cmp != 0) return cmp;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? 0.55 : 1.0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Fmt
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Size(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        string f = v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
        return v.ToString(f) + " " + Units[u];
    }
}
