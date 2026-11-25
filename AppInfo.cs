using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Orbit;

/// <summary>Project links and the update check shown on Settings › About.</summary>
public static class AppInfo
{
    // ── Fill these in ─────────────────────────────────────────────────────
    /// <summary>Your GitHub repository, e.g. "https://github.com/your-name/orbit". Used for the repo button, bug reports and update checks.</summary>
    public const string RepoUrl = "https://github.com/danny19862025/orbit-file-manager";
    /// <summary>Your Buy Me a Coffee page, e.g. "https://buymeacoffee.com/your-name".</summary>
    public const string CoffeeUrl = "https://buymeacoffee.com/danny1986";
    // ──────────────────────────────────────────────────────────────────────

    public static Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string VersionText => Version.ToString(3);

    public static bool HasRepo => TryGetRepo(out _, out _);

    static bool TryGetRepo(out string owner, out string repo)
    {
        owner = repo = null;
        if (!Uri.TryCreate(RepoUrl, UriKind.Absolute, out var u) || !u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) return false;
        owner = parts[0];
        repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return owner.Length > 0 && repo.Length > 0;
    }

    public sealed record UpdateResult(bool UpdateAvailable, string LatestVersion, string DownloadUrl, string Message);

    /// <summary>Compares this build with the newest GitHub release of <see cref="RepoUrl"/>.</summary>
    public static async Task<UpdateResult> CheckForUpdateAsync()
    {
        if (!TryGetRepo(out var owner, out var repo))
            return new UpdateResult(false, null, null, "Add your GitHub repository link in AppInfo.cs to enable update checks.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Orbit-File-Manager");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new UpdateResult(false, null, null, "No releases have been published yet.");
            if (!resp.IsSuccessStatusCode)
                return new UpdateResult(false, null, null, $"GitHub answered {(int)resp.StatusCode}. Try again later.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : RepoUrl;
            var clean = tag.TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(clean.Contains('.') ? clean : clean + ".0", out var latest))
                return new UpdateResult(false, tag, url, $"Latest release is “{tag}”.");
            var mine = new Version(Version.Major, Version.Minor, Math.Max(0, Version.Build));
            var theirs = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            return theirs > mine
                ? new UpdateResult(true, theirs.ToString(3), url, $"Orbit {theirs.ToString(3)} is available — you have {VersionText}.")
                : new UpdateResult(false, theirs.ToString(3), url, $"You're on the latest version ({VersionText}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateResult(false, null, null, "Couldn't reach GitHub. Check your internet connection.");
        }
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
