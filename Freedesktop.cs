using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbit;

/// <summary>
/// Reads Linux / freedesktop.org icon themes (Papirus, Adwaita, Colloid, Candy, or any theme folder that follows the
/// Icon Naming Specification) and turns them into Orbit icon packs: Orbit's logical icon names → SVG (or PNG) data.
/// </summary>
public static class Freedesktop
{
    /// <summary>Orbit logical icon name → freedesktop icon names to try, best first.</summary>
    static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // folders
        ["folder-base"] = new[] { "folder", "inode-directory" },
        ["folder-desktop"] = new[] { "user-desktop", "folder-desktop" },
        ["folder-download"] = new[] { "folder-download", "folder-downloads" },
        ["folder-docs"] = new[] { "folder-documents", "folder-document" },
        ["folder-images"] = new[] { "folder-pictures", "folder-images", "folder-image" },
        ["folder-audio"] = new[] { "folder-music", "folder-sound" },
        ["folder-video"] = new[] { "folder-videos", "folder-video" },
        ["folder-home"] = new[] { "user-home", "folder-home" },
        ["folder-trash"] = new[] { "user-trash", "folder-trash" },
        ["folder-src"] = new[] { "folder-development", "folder-code", "folder-script" },
        ["folder-scripts"] = new[] { "folder-script", "folder-development" },
        ["folder-public"] = new[] { "folder-publicshare", "folder-public" },
        ["folder-template"] = new[] { "folder-templates", "folder-template" },
        ["folder-temp"] = new[] { "folder-temp", "folder-tmp" },
        ["folder-git"] = new[] { "folder-git" },
        ["folder-github"] = new[] { "folder-github", "folder-git" },
        ["folder-gitlab"] = new[] { "folder-gitlab", "folder-git" },
        ["folder-node"] = new[] { "folder-node", "folder-nodejs" },
        ["folder-vscode"] = new[] { "folder-vscode", "folder-visual-studio-code" },
        ["folder-config"] = new[] { "folder-config", "folder-configure" },
        ["folder-dist"] = new[] { "folder-build", "folder-dist" },
        ["folder-archive"] = new[] { "folder-archive", "folder-tar" },
        ["folder-backup"] = new[] { "folder-backup", "folder-timeshift" },
        ["folder-keys"] = new[] { "folder-keys", "folder-lock", "folder-locked" },
        ["folder-private"] = new[] { "folder-private", "folder-lock", "folder-locked" },
        ["folder-secure"] = new[] { "folder-lock", "folder-locked" },
        ["folder-font"] = new[] { "folder-fonts", "folder-font" },
        ["folder-link"] = new[] { "folder-link", "folder-remote" },
        ["folder-shared"] = new[] { "folder-favorites", "folder-bookmark", "user-bookmarks" },
        ["folder-project"] = new[] { "folder-projects", "folder-development" },
        ["folder-packages"] = new[] { "folder-packages", "folder-package" },
        ["folder-mail"] = new[] { "folder-mail" },
        ["folder-app"] = new[] { "folder-applications", "folder-apps" },
        ["folder-games"] = new[] { "folder-games" },
        ["folder-server"] = new[] { "folder-network", "folder-remote", "network-server" },
        ["folder-database"] = new[] { "folder-database" },
        ["folder-docker"] = new[] { "folder-docker" },
        ["folder-android"] = new[] { "folder-android" },
        ["folder-windows"] = new[] { "folder-windows", "folder-wine" },
        ["folder-test"] = new[] { "folder-test" },
        ["folder-log"] = new[] { "folder-log", "folder-logs" },
        ["folder-tools"] = new[] { "folder-tools", "folder-utilities" },
        ["folder-upload"] = new[] { "folder-upload", "folder-uploads" },
        ["folder-other"] = new[] { "folder" },

        // documents and text
        ["document"] = new[] { "text-x-generic", "text-plain", "unknown", "application-octet-stream" },
        ["readme"] = new[] { "text-x-readme", "text-x-generic" },
        ["license"] = new[] { "text-x-copying", "text-x-license" },
        ["changelog"] = new[] { "text-x-changelog" },
        ["markdown"] = new[] { "text-markdown", "text-x-markdown" },
        ["mdx"] = new[] { "text-markdown", "text-x-markdown" },
        ["log"] = new[] { "text-x-log", "text-x-generic" },
        ["todo"] = new[] { "text-x-generic" },
        ["pdf"] = new[] { "application-pdf", "x-office-document" },
        ["word"] = new[] { "x-office-document", "application-vnd.openxmlformats-officedocument.wordprocessingml.document", "application-msword" },
        ["table"] = new[] { "x-office-spreadsheet", "application-vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text-csv" },
        ["powerpoint"] = new[] { "x-office-presentation", "application-vnd.openxmlformats-officedocument.presentationml.presentation" },
        ["epub"] = new[] { "application-epub+zip", "x-office-document" },
        ["email"] = new[] { "message-rfc822", "application-mbox", "text-x-mail" },
        ["subtitles"] = new[] { "application-x-subrip", "text-x-ssa", "text-x-generic" },
        ["font"] = new[] { "font-x-generic", "application-x-font-ttf", "font-ttf" },

        // code
        ["javascript"] = new[] { "application-javascript", "text-javascript", "text-x-javascript", "text-x-script" },
        ["typescript"] = new[] { "application-typescript", "text-x-typescript", "text-typescript", "text-x-script" },
        ["typescript-def"] = new[] { "application-typescript", "text-x-typescript", "text-x-script" },
        ["react"] = new[] { "text-x-javascript", "application-javascript", "text-x-script" },
        ["react-ts"] = new[] { "text-x-typescript", "application-typescript", "text-x-script" },
        ["python"] = new[] { "text-x-python", "text-x-python3", "application-x-python", "text-x-script" },
        ["jupyter"] = new[] { "application-x-ipynb+json", "text-x-python", "text-x-script" },
        ["csharp"] = new[] { "text-x-csharp", "text-x-csrc", "text-x-script" },
        ["visualstudio"] = new[] { "text-x-csharp", "application-x-ms-dos-executable", "text-x-script" },
        ["cpp"] = new[] { "text-x-c++src", "text-x-c++", "text-x-csrc" },
        ["c"] = new[] { "text-x-csrc", "text-x-c" },
        ["h"] = new[] { "text-x-chdr", "text-x-csrc" },
        ["hpp"] = new[] { "text-x-c++hdr", "text-x-chdr" },
        ["java"] = new[] { "text-x-java", "application-x-java" },
        ["jar"] = new[] { "application-x-java-archive", "application-x-jar", "text-x-java" },
        ["javaclass"] = new[] { "application-x-java", "text-x-java" },
        ["go"] = new[] { "text-x-go", "text-go", "text-x-script" },
        ["go-mod"] = new[] { "text-x-go", "text-x-script" },
        ["rust"] = new[] { "text-rust", "text-x-rust", "text-x-script" },
        ["ruby"] = new[] { "application-x-ruby", "text-x-ruby", "text-x-script" },
        ["php"] = new[] { "application-x-php", "text-x-php", "text-x-script" },
        ["swift"] = new[] { "text-x-swift", "text-x-script" },
        ["kotlin"] = new[] { "text-x-kotlin", "text-x-script" },
        ["dart"] = new[] { "text-x-dart", "application-dart", "text-x-script" },
        ["lua"] = new[] { "text-x-lua", "text-x-script" },
        ["r"] = new[] { "text-x-r", "text-x-R", "text-x-script" },
        ["scala"] = new[] { "text-x-scala", "text-x-script" },
        ["zig"] = new[] { "text-x-zig", "text-x-script" },
        ["console"] = new[] { "application-x-shellscript", "text-x-script" },
        ["powershell"] = new[] { "application-x-powershell", "text-x-powershell", "application-x-shellscript", "text-x-script" },
        ["html"] = new[] { "text-html", "application-xhtml+xml" },
        ["css"] = new[] { "text-css" },
        ["sass"] = new[] { "text-x-scss", "text-x-sass", "text-css" },
        ["less"] = new[] { "text-x-less", "text-less", "text-css" },
        ["vue"] = new[] { "text-x-vue", "application-x-vue", "text-html" },
        ["svelte"] = new[] { "text-x-svelte", "text-html" },
        ["json"] = new[] { "application-json", "text-x-json", "text-x-script" },
        ["xml"] = new[] { "application-xml", "text-xml", "text-x-xml" },
        ["yaml"] = new[] { "application-x-yaml", "text-x-yaml", "application-yaml" },
        ["toml"] = new[] { "application-toml", "text-x-toml", "text-x-generic" },
        ["settings"] = new[] { "text-x-ini", "application-x-desktop", "text-x-generic" },
        ["tune"] = new[] { "text-x-ini", "text-x-generic" },
        ["editorconfig"] = new[] { "text-x-ini", "text-x-generic" },
        ["tsconfig"] = new[] { "application-json", "text-x-script" },
        ["jsconfig"] = new[] { "application-json", "text-x-script" },
        ["nodejs"] = new[] { "application-json", "text-x-script" },
        ["npm"] = new[] { "application-json" },
        ["makefile"] = new[] { "text-x-makefile", "text-x-script" },
        ["docker"] = new[] { "text-dockerfile", "text-x-dockerfile", "application-x-docker" },
        ["git"] = new[] { "text-x-git", "application-x-git", "text-x-generic" },
        ["diff"] = new[] { "text-x-patch", "text-x-diff" },
        ["sql"] = new[] { "application-sql", "text-x-sql" },
        ["database"] = new[] { "application-x-sqlite3", "application-vnd.sqlite3", "application-sql", "text-x-sql" },
        ["graphql"] = new[] { "application-graphql", "text-x-script" },
        ["proto"] = new[] { "text-x-protobuf", "text-x-script" },
        ["gradle"] = new[] { "text-x-gradle", "text-x-script" },
        ["terraform"] = new[] { "text-x-terraform", "text-x-script" },
        ["nix"] = new[] { "text-x-nix", "text-x-script" },
        ["http"] = new[] { "text-html" },
        ["url"] = new[] { "application-x-mswinurl", "text-html" },
        ["hosts"] = new[] { "text-x-generic" },
        ["claude"] = new[] { "text-markdown", "text-x-markdown" },
        ["contributing"] = new[] { "text-markdown", "text-x-markdown" },

        // media
        ["image"] = new[] { "image-x-generic", "image-png", "image-jpeg" },
        ["svg"] = new[] { "image-svg+xml", "image-x-svg+xml", "image-x-generic" },
        ["adobe-photoshop"] = new[] { "image-vnd.adobe.photoshop", "image-x-psd", "image-x-generic" },
        ["adobe-illustrator"] = new[] { "application-illustrator", "application-postscript", "image-x-generic" },
        ["figma"] = new[] { "image-x-generic" },
        ["blender"] = new[] { "application-x-blender", "model" },
        ["3d"] = new[] { "model", "application-x-blender", "image-x-generic" },
        ["video"] = new[] { "video-x-generic", "video-mp4" },
        ["audio"] = new[] { "audio-x-generic", "audio-mpeg" },

        // system, archives, security
        ["zip"] = new[] { "application-zip", "application-x-zip", "application-x-zip-compressed", "application-x-7zip", "application-x-7z-compressed", "package-x-generic", "application-x-archive", "application-x-compressed-tar" },
        ["disc"] = new[] { "application-x-cd-image", "media-optical", "application-x-raw-disk-image" },
        ["exe"] = new[] { "application-x-ms-dos-executable", "application-x-msdownload", "application-x-executable" },
        ["dll"] = new[] { "application-x-sharedlib", "application-x-executable" },
        ["regedit"] = new[] { "text-x-ini", "text-x-generic" },
        ["key"] = new[] { "application-pgp-keys", "application-x-pem-key", "application-certificate" },
        ["certificate"] = new[] { "application-certificate", "application-pkix-cert", "application-x-x509-ca-cert" },
        ["lock"] = new[] { "application-pgp-encrypted", "application-pgp-keys", "text-x-generic" },
        ["download"] = new[] { "application-x-bittorrent", "text-x-generic" },
        ["unity"] = new[] { "application-x-executable" },
    };

    /// <summary>Colour variants (folder-blue-…, folder-red, …) that themes ship many copies of. Only the default colour is kept.</summary>
    static readonly Regex ColourVariant = new(
        @"^folder-(adwaita|black|blue|bluegrey|breeze|brown|carmine|cyan|darkcyan|deeporange|green|grey|indigo|magenta|nordic|orange|palebrown|paleorange|pink|purple|red|teal|violet|white|yaru|yellow|catppuccin|everforest|gruvbox|dracula|nord|dark|light|sky)(-|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Loads a theme folder into Orbit logical names → icon data ("&lt;svg…" text or "data:image/png;base64,…").</summary>
    /// <summary>Icons bigger than this are usually elaborate artwork the renderer can't draw well; the next candidate is used.</summary>
    const int MaxIcon = 64_000;

    public static Dictionary<string, string> Load(string themeDir)
    {
        var files = Index(themeDir);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (logical, names) in Map)
            foreach (var n in names)
                if (files.TryGetValue(n, out var file) && Read(file) is { Length: < MaxIcon } data) { result[logical] = data; break; }
        // every folder-* the theme has (folder-dropbox, folder-steam…) so named folders find their own icon
        foreach (var (name, file) in files)
            if (name.StartsWith("folder-", StringComparison.OrdinalIgnoreCase) && !result.ContainsKey(name) && !ColourVariant.IsMatch(name)
                && !name.Contains("-open") && !name.Contains("drag-accept") && Read(file) is { Length: < MaxIcon } data)
                result[name] = data;
        if (!result.ContainsKey("folder-base") || !result.ContainsKey("document"))
            throw new InvalidDataException("This folder doesn't look like an icon theme — it has no folder or text-x-generic icon.");
        return result;
    }

    /// <summary>Writes a theme as an Orbit pack file (used to prepare the bundled packs).</summary>
    public static void Bake(string themeDir, string outFile, string name, string credit)
    {
        var icons = Load(themeDir);
        using var fs = File.Create(outFile);
        using var w = new Utf8JsonWriter(fs);
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("credit", credit);
        w.WriteStartObject("icons");
        foreach (var (k, v) in icons.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteString(k, v);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>Icon name → best file: scalable SVG first, then the largest PNG. Symbolic (monochrome) icons are skipped.</summary>
    static Dictionary<string, string> Index(string root)
    {
        var best = new Dictionary<string, (string Path, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".svg" or ".png")) continue;
            var rel = Path.GetRelativePath(root, f).ToLowerInvariant();
            if (rel.Contains("symbolic") || rel.StartsWith(".git")) continue;
            var name = Path.GetFileNameWithoutExtension(f);
            int score = ext == ".svg" ? 10_000 : 0;
            var m = Regex.Match(rel, @"(\d+)(x\d+)?(@\d+x)?[\\/]");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var size)) score += Math.Min(size, 512) * (ext == ".svg" ? 1 : 10);
            else if (rel.Contains("scalable")) score += 600;
            if (!best.TryGetValue(name, out var cur) || score > cur.Score) best[name] = (f, score);
        }
        return best.ToDictionary(k => k.Key, v => v.Value.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads an icon file. Symlinks checked out on Windows become small text files holding the target path; those are followed.</summary>
    static string Read(string file, int depth = 0)
    {
        try
        {
            if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
                return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(file));
            var text = File.ReadAllText(file);
            if (text.Contains("<svg")) return Clean(text);
            var target = text.Trim();
            if (depth < 8 && target.Length < 260 && !target.Contains('\n'))
            {
                var next = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target));
                if (File.Exists(next)) return Read(next, depth + 1);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Strips editor metadata so the packs stay small.</summary>
    static string Clean(string svg)
    {
        svg = Regex.Replace(svg, @"<\?xml.*?\?>|<!--.*?-->|<metadata\b.*?</metadata>|<sodipodi:namedview\b[^>]*/>|<sodipodi:namedview\b[^>]*[^/]>.*?</sodipodi:namedview>", "", RegexOptions.Singleline);
        svg = Regex.Replace(svg, @"\s+(inkscape|sodipodi):[\w-]+=""[^""]*""", "");
        svg = Regex.Replace(svg, @">\s+<", "><");
        return svg.Trim();
    }
}
