using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Orbit;

/// <summary>
/// Vector icon pack (Material Icon Theme, MIT) loaded from an embedded Iconify JSON file.
/// Maps file names, extensions and folder names to icons, VS Code style.
/// </summary>
public static class IconPack
{
    public const string System = "system";
    public const string Material = "material";

    public static readonly (string Id, string Name, string Description)[] All =
    {
        (System, "Windows", "Your system's own icons and thumbnails"),
        (Material, "Material", "Material Icon Theme — 1,100+ colourful file and folder icons"),
    };

    static readonly object gate = new();
    static Dictionary<string, (string Body, double W, double H)> icons;
    static readonly ConcurrentDictionary<string, ImageSource> rendered = new();

    public static bool IsActive => AppSettings.Current.IconPack == Material;

    static void EnsureLoaded()
    {
        if (icons != null) return;
        lock (gate)
        {
            if (icons != null) return;
            var map = new Dictionary<string, (string, double, double)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var s = typeof(IconPack).Assembly.GetManifestResourceStream("Orbit.material-icons.json");
                using var doc = JsonDocument.Parse(s!);
                var root = doc.RootElement;
                double dw = root.TryGetProperty("width", out var w0) ? w0.GetDouble() : 16;
                double dh = root.TryGetProperty("height", out var h0) ? h0.GetDouble() : 16;
                foreach (var p in root.GetProperty("icons").EnumerateObject())
                {
                    double w = p.Value.TryGetProperty("width", out var w1) ? w1.GetDouble() : dw;
                    double h = p.Value.TryGetProperty("height", out var h1) ? h1.GetDouble() : dh;
                    map[p.Name] = (p.Value.GetProperty("body").GetString(), w, h);
                }
                if (root.TryGetProperty("aliases", out var aliases))
                    foreach (var a in aliases.EnumerateObject())
                        if (a.Value.TryGetProperty("parent", out var parent) && map.TryGetValue(parent.GetString()!, out var target))
                            map[a.Name] = target;
            }
            catch (Exception ex) { AppSettings.LogError(ex); }
            icons = map;
        }
    }

    public static bool Has(string name)
    {
        EnsureLoaded();
        return icons.ContainsKey(name);
    }

    /// <summary>Renders an icon to a frozen vector image (safe to use from any thread).</summary>
    public static ImageSource Render(string name)
    {
        if (rendered.TryGetValue(name, out var img)) return img;
        EnsureLoaded();
        if (!icons.TryGetValue(name, out var icon)) return null;
        try
        {
            var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                      $"width=\"{icon.W.ToString(CultureInfo.InvariantCulture)}\" height=\"{icon.H.ToString(CultureInfo.InvariantCulture)}\" viewBox=\"0 0 {icon.W.ToString(CultureInfo.InvariantCulture)} {icon.H.ToString(CultureInfo.InvariantCulture)}\">{icon.Body}</svg>";
            DrawingGroup drawing;
            lock (gate) // the SVG reader isn't documented as thread-safe
            {
                var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true, CultureInfo = CultureInfo.InvariantCulture });
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
                drawing = reader.Read(ms);
            }
            if (drawing == null) return null;
            // Pad to the full viewBox so every icon keeps the same visual size and position.
            var framed = new DrawingGroup();
            framed.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new global::System.Windows.Rect(0, 0, icon.W, icon.H))));
            framed.Children.Add(drawing);
            framed.ClipGeometry = new RectangleGeometry(new global::System.Windows.Rect(0, 0, icon.W, icon.H));
            var image = new DrawingImage(framed);
            image.Freeze();
            rendered[name] = image;
            return image;
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            return null;
        }
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
        EnsureLoaded();
        if (isDir)
        {
            if (FolderNames.TryGetValue(name, out var f) && Has(f)) return f;
            var guess = "folder-" + name.TrimStart('.').ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
            if (Has(guess)) return guess;
            return "folder-base";
        }
        if (FileNames.TryGetValue(name, out var byName) && Has(byName)) return byName;
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".d.ts")) return "typescript-def";
        if (lower.StartsWith("dockerfile")) return "docker";
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
