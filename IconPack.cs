using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Orbit;

/// <summary>
/// Vector icon packs. Every pack maps Orbit's logical icon names (folder-images, python, zip…) to SVG.
/// Material is embedded; the others are files in "icons\" next to Orbit.exe, each with its own licence:
/// Papirus, Adwaita, Colloid and Candy (freedesktop.org themes, see <see cref="Freedesktop"/>), Phosphor
/// (tinted with the current theme's colours) and Devicon (language logos on top of Material).
/// A custom freedesktop theme folder can be chosen too.
/// </summary>
public static class IconPack
{
    public const string System = "system";
    public const string Material = "material";
    public const string Custom = "custom";

    public sealed record Info(string Id, string Name, string Description, string File = null, string Fallback = null, bool Tint = false);

    public static readonly Info[] All =
    {
        new(System, "Windows", "Your system's own icons and thumbnails"),
        new(Material, "Material", "Material Icon Theme — 1,100+ colourful file and folder icons"),
        new("papirus", "Papirus", "The popular Linux icon theme — flat, colourful folders and file types", "papirus.json"),
        new("adwaita", "Adwaita", "GNOME's default icons — calm and simple", "adwaita.json"),
        new("colloid", "Colloid", "Soft, rounded icons from the Colloid theme", "colloid.json"),
        new("candy", "Candy", "Sweet gradient icons from candy-icons", "candy.json"),
        new("phosphor", "Phosphor", "Clean duotone line icons that follow your theme's colours", "phosphor.json", Tint: true),
        new("devicon", "Devicon", "Programming language and tool logos, with Material for everything else", "devicon.json", Fallback: Material),
        new(Custom, "Freedesktop theme…", "Use any Linux icon theme folder (with index.theme), e.g. one from gnome-look.org"),
    };

    public static Info Find(string id) => All.FirstOrDefault(p => p.Id == id);

    sealed class Pack
    {
        public Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase);
        public string Error;
    }

    static readonly object gate = new();
    static readonly ConcurrentDictionary<string, Pack> packs = new();
    static readonly ConcurrentDictionary<string, ImageSource> rendered = new();

    public static string ActiveId => AppSettings.Current.IconPack ?? Material;
    public static bool IsActive => ActiveId != System;
    /// <summary>True when icons are drawn in the theme's colours, so they must be redrawn when the theme changes.</summary>
    public static bool Tinted => Find(ActiveId)?.Tint == true;

    public static string IconsDir => Path.Combine(AppContext.BaseDirectory, "icons");

    static Pack Get(string id) => packs.GetOrAdd(id ?? Material, Load);

    static Pack Load(string id)
    {
        var pack = new Pack();
        try
        {
            if (id == Material) LoadIconify(pack, typeof(IconPack).Assembly.GetManifestResourceStream("Orbit.material-icons.json")!);
            else if (id == Custom)
            {
                var dir = AppSettings.Current.CustomIconTheme;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new DirectoryNotFoundException("Choose an icon theme folder in Settings › Icons.");
                pack.Icons = Freedesktop.Load(dir);
            }
            else if (Find(id)?.File is { } file)
            {
                using var fs = File.OpenRead(Path.Combine(IconsDir, file));
                using var doc = JsonDocument.Parse(fs);
                foreach (var p in doc.RootElement.GetProperty("icons").EnumerateObject()) pack.Icons[p.Name] = p.Value.GetString();
            }
        }
        catch (Exception ex) { pack.Error = ex.Message; AppSettings.LogError(ex); }
        return pack;
    }

    /// <summary>Iconify JSON (bodies without the outer &lt;svg&gt;) → full SVG documents.</summary>
    static void LoadIconify(Pack pack, Stream s)
    {
        using (s)
        using (var doc = JsonDocument.Parse(s))
        {
            var root = doc.RootElement;
            double dw = root.TryGetProperty("width", out var w0) ? w0.GetDouble() : 16;
            double dh = root.TryGetProperty("height", out var h0) ? h0.GetDouble() : 16;
            foreach (var p in root.GetProperty("icons").EnumerateObject())
            {
                double w = p.Value.TryGetProperty("width", out var w1) ? w1.GetDouble() : dw;
                double h = p.Value.TryGetProperty("height", out var h1) ? h1.GetDouble() : dh;
                pack.Icons[p.Name] = Wrap(p.Value.GetProperty("body").GetString(), w, h);
            }
            if (root.TryGetProperty("aliases", out var aliases))
                foreach (var a in aliases.EnumerateObject())
                    if (a.Value.TryGetProperty("parent", out var parent) && pack.Icons.TryGetValue(parent.GetString()!, out var target))
                        pack.Icons[a.Name] = target;
        }
    }

    public static string Wrap(string body, double w, double h)
    {
        string W = w.ToString(CultureInfo.InvariantCulture), H = h.ToString(CultureInfo.InvariantCulture);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">{body}</svg>";
    }

    /// <summary>Why a pack couldn't load (missing files, bad theme folder), or null.</summary>
    public static string ErrorFor(string id) => Get(id).Error;

    /// <summary>Forget a loaded pack (after choosing a different custom theme folder).</summary>
    public static void Unload(string id) { packs.TryRemove(id, out _); rendered.Clear(); }

    static bool HasIn(string packId, string name)
    {
        if (Get(packId).Icons.ContainsKey(name)) return true;
        return Find(packId)?.Fallback is { } fb && Get(fb).Icons.ContainsKey(name);
    }

    public static bool Has(string name) => HasIn(ActiveId, name);

    /// <summary>Renders an icon from the active pack to a frozen image (safe to use from any thread).</summary>
    public static ImageSource Render(string name) => RenderFrom(ActiveId, name);

    public static ImageSource RenderFrom(string packId, string name)
    {
        if (name == null) return null;
        var info = Find(packId);
        bool folder = name.StartsWith("folder", StringComparison.OrdinalIgnoreCase);
        string tint = info?.Tint == true ? (folder ? Themes.Current?.Accent : Themes.Current?.Text) ?? "#CDD6F4" : null;
        var key = $"{packId}|{name}|{tint}";
        if (rendered.TryGetValue(key, out var img)) return img;
        if (!Get(packId).Icons.TryGetValue(name, out var data))
        {
            if (info?.Fallback is { } fb && Get(fb).Icons.TryGetValue(name, out var fbData)) data = fbData;
            else return null;
        }
        try
        {
            var image = data.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? Png(data) : Svg(data, tint);
            if (image == null) return Generic(packId, key, name, folder);
            image.Freeze();
            rendered[key] = image;
            return image;
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            return Generic(packId, key, name, folder);
        }
    }

    /// <summary>A pack's plain folder / document icon, used when a specific icon can't be drawn.</summary>
    static ImageSource Generic(string packId, string key, string name, bool folder)
    {
        var generic = folder ? "folder-base" : "document";
        var img = name.Equals(generic, StringComparison.OrdinalIgnoreCase) ? null : RenderFrom(packId, generic);
        if (img != null) rendered[key] = img;
        return img;
    }

    static ImageSource Png(string data)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(Convert.FromBase64String(data[22..]));
        bmp.EndInit();
        return bmp;
    }

    static ImageSource Svg(string svg, string tint)
    {
        if (tint != null) svg = svg.Replace("currentColor", tint);
        DrawingGroup drawing;
        lock (gate) // the SVG reader isn't documented as thread-safe
        {
            var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true, CultureInfo = CultureInfo.InvariantCulture });
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            drawing = reader.Read(ms);
        }
        if (drawing == null || drawing.Bounds.IsEmpty || drawing.Bounds.Width < 0.01) return null;
        // Pad to the full viewBox so every icon keeps the same visual size and position.
        var (w, h) = ViewBox(svg, drawing);
        var framed = new DrawingGroup();
        framed.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new global::System.Windows.Rect(0, 0, w, h))));
        framed.Children.Add(drawing);
        framed.ClipGeometry = new RectangleGeometry(new global::System.Windows.Rect(0, 0, w, h));
        return new DrawingImage(framed);
    }

    static (double, double) ViewBox(string svg, DrawingGroup drawing)
    {
        // The SVG reader draws at the width/height size when they're given (scaling the viewBox to fit), so frame to those first.
        var root = global::System.Text.RegularExpressions.Regex.Match(svg, @"<svg\b[^>]*>").Value;
        var mw = global::System.Text.RegularExpressions.Regex.Match(root, @"\swidth\s*=\s*[""']\s*([\d.]+)\s*(px|pt)?\s*[""']");
        var mh = global::System.Text.RegularExpressions.Regex.Match(root, @"\sheight\s*=\s*[""']\s*([\d.]+)\s*(px|pt)?\s*[""']");
        if (mw.Success && mh.Success && double.TryParse(mw.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var aw)
            && double.TryParse(mh.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) && aw > 0 && ah > 0)
            return mw.Groups[2].Value == "pt" ? (aw * 4 / 3, ah * 4 / 3) : (aw, ah);
        var m =global::System.Text.RegularExpressions.Regex.Match(svg, @"<svg[^>]*?viewBox\s*=\s*[""']\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                      && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0)
            return (w, h);
        var b = drawing.Bounds;
        return (Math.Max(1, b.Right), Math.Max(1, b.Bottom));
    }

    public static void ClearCache() => rendered.Clear();
    // ---------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------
    static readonly Dictionary<string, string> FolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desktop"] = "folder-desktop", ["downloads"] = "folder-download", ["download"] = "folder-download",
        ["documents"] = "folder-docs", ["document"] = "folder-docs", ["docs"] = "folder-docs", ["doc"] = "folder-docs",
        ["pictures"] = "folder-images", ["images"] = "folder-images", ["image"] = "folder-images", ["img"] = "folder-images",
        ["photos"] = "folder-images", ["screenshots"] = "folder-images", ["wallpapers"] = "folder-images", ["camera roll"] = "folder-images",
        ["music"] = "folder-audio", ["audio"] = "folder-audio", ["sounds"] = "folder-audio", ["podcasts"] = "folder-audio",
        ["videos"] = "folder-video", ["video"] = "folder-video", ["movies"] = "folder-video", ["captures"] = "folder-video",
        ["src"] = "folder-src", ["source"] = "folder-src", ["sources"] = "folder-src",
        ["node_modules"] = "folder-node", [".git"] = "folder-git", [".github"] = "folder-github", [".gitlab"] = "folder-gitlab",
        [".vscode"] = "folder-vscode", [".claude"] = "folder-claude", [".cursor"] = "folder-cursor",
        [".config"] = "folder-config", ["config"] = "folder-config", ["configs"] = "folder-config", ["settings"] = "folder-config", ["appdata"] = "folder-config",
        ["test"] = "folder-test", ["tests"] = "folder-test", ["__tests__"] = "folder-test", ["spec"] = "folder-test",
        ["dist"] = "folder-dist", ["build"] = "folder-dist", ["out"] = "folder-dist", ["bin"] = "folder-dist", ["release"] = "folder-dist",
        ["obj"] = "folder-dist", ["target"] = "folder-target",
        ["public"] = "folder-public", ["scripts"] = "folder-scripts", ["script"] = "folder-scripts", ["lib"] = "folder-lib", ["libs"] = "folder-lib",
        ["temp"] = "folder-temp", ["tmp"] = "folder-temp", ["cache"] = "folder-temp", [".cache"] = "folder-temp",
        ["logs"] = "folder-log", ["log"] = "folder-log", ["apps"] = "folder-app", ["applications"] = "folder-app", ["programs"] = "folder-app",
        ["archive"] = "folder-archive", ["archives"] = "folder-archive", ["backup"] = "folder-backup", ["backups"] = "folder-backup",
        [".ssh"] = "folder-keys", ["keys"] = "folder-keys", ["certs"] = "folder-keys", ["fonts"] = "folder-font",
        ["assets"] = "folder-resource", ["resources"] = "folder-resource", ["res"] = "folder-resource",
        ["components"] = "folder-components", ["utils"] = "folder-utils", ["helpers"] = "folder-helper", ["tools"] = "folder-tools",
        ["templates"] = "folder-template", ["themes"] = "folder-theme", ["uploads"] = "folder-upload", ["upload"] = "folder-upload",
        ["windows"] = "folder-windows", ["mail"] = "folder-mail", ["contacts"] = "folder-mail", ["favorites"] = "folder-shared",
        ["links"] = "folder-link", ["searches"] = "folder-filter", ["saved games"] = "folder-other", ["onedrive"] = "folder-other",
        ["projects"] = "folder-project", ["project"] = "folder-project", ["packages"] = "folder-packages", [".nuget"] = "folder-packages",
        [".npm"] = "folder-packages", [".cargo"] = "folder-rust", [".rustup"] = "folder-rust", [".dotnet"] = "folder-packages",
        ["home"] = "folder-home", ["users"] = "folder-home", ["trash"] = "folder-trash", ["recycle bin"] = "folder-trash",
        ["private"] = "folder-private", ["secure"] = "folder-secure", ["server"] = "folder-server", ["database"] = "folder-database",
        ["docker"] = "folder-docker", ["api"] = "folder-api", ["android"] = "folder-android", ["ios"] = "folder-ios",
    };

    static readonly Dictionary<string, string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["package.json"] = "nodejs", ["package-lock.json"] = "npm", [".npmrc"] = "npm", ["yarn.lock"] = "lock", ["pnpm-lock.yaml"] = "pnpm",
        ["dockerfile"] = "docker", ["docker-compose.yml"] = "docker", ["compose.yaml"] = "docker", [".dockerignore"] = "docker",
        ["readme.md"] = "readme", ["readme"] = "readme", ["readme.txt"] = "readme", ["license"] = "license", ["license.md"] = "license",
        ["license.txt"] = "license", ["changelog.md"] = "changelog", ["contributing.md"] = "contributing",
        [".gitignore"] = "git", [".gitattributes"] = "git", [".gitmodules"] = "git", [".gitconfig"] = "git", [".git-credentials"] = "git",
        ["makefile"] = "makefile", ["cargo.toml"] = "rust", ["cargo.lock"] = "lock", ["go.mod"] = "go-mod", ["go.sum"] = "go-mod",
        ["tsconfig.json"] = "tsconfig", ["jsconfig.json"] = "jsconfig", [".editorconfig"] = "editorconfig", [".env"] = "tune",
        [".prettierrc"] = "prettier", [".eslintrc"] = "eslint", ["vite.config.ts"] = "vite", ["vite.config.js"] = "vite",
        ["tailwind.config.js"] = "tailwindcss", ["tailwind.config.ts"] = "tailwindcss", [".bash_history"] = "console", [".bashrc"] = "console",
        [".profile"] = "console", ["hosts"] = "hosts", ["claude.md"] = "claude", [".claude.json"] = "claude", ["todo.md"] = "todo",
    };

    static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["ts"] = "typescript", ["mts"] = "typescript", ["cts"] = "typescript",
        ["tsx"] = "react-ts", ["jsx"] = "react", ["d.ts"] = "typescript-def", ["py"] = "python", ["pyw"] = "python", ["ipynb"] = "jupyter",
        ["cs"] = "csharp", ["csx"] = "csharp", ["csproj"] = "visualstudio", ["sln"] = "visualstudio", ["slnx"] = "visualstudio", ["vcxproj"] = "visualstudio",
        ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["c"] = "c", ["h"] = "h", ["hpp"] = "hpp", ["hh"] = "hpp", ["java"] = "java",
        ["jar"] = "jar", ["class"] = "javaclass", ["go"] = "go", ["rs"] = "rust", ["rb"] = "ruby", ["php"] = "php", ["swift"] = "swift",
        ["kt"] = "kotlin", ["kts"] = "kotlin", ["dart"] = "dart", ["lua"] = "lua", ["r"] = "r", ["scala"] = "scala", ["zig"] = "zig",
        ["sh"] = "console", ["bash"] = "console", ["zsh"] = "console", ["fish"] = "console", ["bat"] = "console", ["cmd"] = "console",
        ["ps1"] = "powershell", ["psm1"] = "powershell", ["psd1"] = "powershell",
        ["html"] = "html", ["htm"] = "html", ["css"] = "css", ["scss"] = "sass", ["sass"] = "sass", ["less"] = "less", ["vue"] = "vue", ["svelte"] = "svelte",
        ["json"] = "json", ["jsonc"] = "json", ["json5"] = "json", ["xml"] = "xml", ["xaml"] = "xml", ["yaml"] = "yaml", ["yml"] = "yaml", ["toml"] = "toml",
        ["md"] = "markdown", ["markdown"] = "markdown", ["mdx"] = "mdx", ["txt"] = "document", ["rtf"] = "word", ["log"] = "log", ["pdf"] = "pdf",
        ["doc"] = "word", ["docx"] = "word", ["odt"] = "word", ["xls"] = "table", ["xlsx"] = "table", ["csv"] = "table", ["tsv"] = "table", ["ods"] = "table",
        ["ppt"] = "powerpoint", ["pptx"] = "powerpoint", ["odp"] = "powerpoint",
        ["orbitlock"] = "lock", ["zip"] = "zip", ["rar"] = "zip", ["7z"] = "zip", ["tar"] = "zip", ["gz"] = "zip", ["tgz"] = "zip", ["bz2"] = "zip", ["xz"] = "zip", ["zst"] = "zip",
        ["iso"] = "disc", ["img"] = "disc", ["vhd"] = "disc", ["vhdx"] = "disc", ["exe"] = "exe", ["msi"] = "exe", ["dll"] = "dll", ["sys"] = "dll",
        ["png"] = "image", ["jpg"] = "image", ["jpeg"] = "image", ["jfif"] = "image", ["gif"] = "image", ["bmp"] = "image", ["webp"] = "image",
        ["heic"] = "image", ["heif"] = "image", ["avif"] = "image", ["tif"] = "image", ["tiff"] = "image", ["ico"] = "image", ["raw"] = "image",
        ["svg"] = "svg", ["psd"] = "adobe-photoshop", ["ai"] = "adobe-illustrator", ["fig"] = "figma", ["blend"] = "blender",
        ["obj"] = "3d", ["fbx"] = "3d", ["stl"] = "3d", ["gltf"] = "3d", ["glb"] = "3d",
        ["mp4"] = "video", ["mkv"] = "video", ["mov"] = "video", ["avi"] = "video", ["wmv"] = "video", ["webm"] = "video", ["m4v"] = "video",
        ["mpg"] = "video", ["mpeg"] = "video", ["mp3"] = "audio", ["wav"] = "audio", ["flac"] = "audio", ["m4a"] = "audio", ["aac"] = "audio",
        ["ogg"] = "audio", ["opus"] = "audio", ["wma"] = "audio", ["srt"] = "subtitles", ["vtt"] = "subtitles", ["ass"] = "subtitles",
        ["ttf"] = "font", ["otf"] = "font", ["woff"] = "font", ["woff2"] = "font",
        ["db"] = "database", ["sqlite"] = "database", ["sqlite3"] = "database", ["sql"] = "database", ["mdb"] = "database", ["accdb"] = "database",
        ["key"] = "key", ["pem"] = "key", ["pub"] = "key", ["ppk"] = "key", ["crt"] = "certificate", ["cer"] = "certificate", ["pfx"] = "certificate",
        ["lock"] = "lock", ["ini"] = "settings", ["cfg"] = "settings", ["conf"] = "settings", ["config"] = "settings", ["reg"] = "regedit",
        ["env"] = "tune", ["eml"] = "email", ["msg"] = "email", ["epub"] = "epub", ["torrent"] = "download", ["url"] = "url",
        ["diff"] = "diff", ["patch"] = "diff", ["http"] = "http", ["graphql"] = "graphql", ["gql"] = "graphql", ["proto"] = "proto",
        ["tf"] = "terraform", ["nix"] = "nix", ["dockerfile"] = "docker", ["gradle"] = "gradle", ["unity"] = "unity", ["uasset"] = "unity",
        ["backup"] = "document", ["bak"] = "document", ["tmp"] = "document", ["drawio"] = "drawio", ["excalidraw"] = "excalidraw",
    };

    /// <summary>Icon name for an item, or null to fall back to the system icon.</summary>
    public static string Resolve(string name, bool isDir)
    {
        if (isDir)
        {
            if (FolderNames.TryGetValue(name, out var f) && Has(f)) return f;
            var guess = "folder-" + name.TrimStart('.').ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
            if (Has(guess)) return guess;
            return "folder-base";
        }
        if (FileNames.TryGetValue(name, out var byName) && Has(byName)) return byName;
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".d.ts") && Has("typescript-def")) return "typescript-def";
        if (lower.StartsWith("dockerfile") && Has("docker")) return "docker";
        int dot = lower.LastIndexOf('.');
        if (dot >= 0 && dot < lower.Length - 1)
        {
            var ext = lower[(dot + 1)..];
            if (Extensions.TryGetValue(ext, out var byExt) && Has(byExt)) return byExt;
            if (Has(ext)) return ext;
        }
        return "document";
    }
}
