using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Orbit;

/// <summary>Mounted drives (the Linux counterpart of Windows' drive letters), plus eject and disk-image mounting through udisks.</summary>
public static class Drives
{
    public sealed record Drive(string Mount, string Name, string Label, string Device, string Fs, bool Removable, bool Optical, bool Network, long Free, long Total);

    static readonly HashSet<string> RealFs = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "vfat", "msdos", "exfat", "ntfs", "ntfs3", "fuseblk", "iso9660", "udf", "f2fs", "jfs",
        "reiserfs", "zfs", "bcachefs", "hfsplus", "apfs", "nilfs2"
    };
    static readonly HashSet<string> NetFs = new(StringComparer.Ordinal) { "nfs", "nfs4", "cifs", "smb3", "fuse.sshfs", "fuse.rclone", "9p", "virtiofs" };

    public static List<Drive> List()
    {
        var result = new List<Drive>();
        var byDevice = new Dictionary<string, Drive>(StringComparer.Ordinal);
        var labels = Labels();
        foreach (var (mount, source, fs) in RecycleBin.Mounts())
        {
            bool net = NetFs.Contains(fs);
            if (!RealFs.Contains(fs) && !net) continue;
            if (!net && !source.StartsWith("/dev/")) continue;
            if (Skip(mount)) continue;
            // btrfs subvolumes and bind mounts show the same device several times: keep the shortest mount point
            if (!net && byDevice.TryGetValue(source, out var prev) && prev.Mount.Length <= mount.Length) continue;
            long free = 0, total = 0;
            try { var di = new DriveInfo(mount); free = di.AvailableFreeSpace; total = di.TotalSize; } catch { }
            var dev = source;
            try { var real = new FileInfo(source).ResolveLinkTarget(true); if (real != null) dev = real.FullName; } catch { }
            labels.TryGetValue(dev, out var label);
            bool removable = IsRemovable(dev) || mount.StartsWith("/run/media/") || mount.StartsWith("/media/");
            bool optical = fs is "iso9660" or "udf" || Path.GetFileName(dev).StartsWith("sr");
            string name = mount == "/" ? (label ?? "File System") : !string.IsNullOrEmpty(label) ? label : Path.GetFileName(mount);
            if (mount == Xdg.Home) name = "Home";
            var d = new Drive(mount, name, label, dev, fs, removable, optical, net, free, total);
            if (!net)
            {
                if (byDevice.TryGetValue(source, out var old)) result.Remove(old);
                byDevice[source] = d;
            }
            result.Add(d);
        }
        return result.OrderBy(d => d.Mount == "/" ? 0 : d.Removable || d.Optical ? 2 : d.Network ? 3 : 1).ThenBy(d => d.Mount, StringComparer.Ordinal).ToList();
    }

    static bool Skip(string m) =>
        m.StartsWith("/boot") || m == "/efi" || m.StartsWith("/efi/") || m.StartsWith("/var/") || m.StartsWith("/snap") ||
        m.StartsWith("/proc") || m.StartsWith("/sys") || m.StartsWith("/usr") || m.StartsWith("/nix") || m.StartsWith("/tmp") ||
        m.StartsWith("/opt") || m == "/swap" || m.StartsWith("/.snapshots") || m.Contains("/.snapshots") ||
        (m.StartsWith("/run/") && !m.StartsWith("/run/media/")) || m.StartsWith("/var/lib/docker") || m.StartsWith("/recovery");

    /// <summary>/dev/sdX1 → "My Stick", from /dev/disk/by-label.</summary>
    static Dictionary<string, string> Labels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles("/dev/disk/by-label"))
            {
                try
                {
                    var target = new FileInfo(f).ResolveLinkTarget(true)?.FullName;
                    if (target != null) map[target] = Unescape(Path.GetFileName(f));
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    static string Unescape(string s)
    {
        // udev writes spaces and other bytes as \x20
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' && byte.TryParse(s.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            { bytes.Add(b); i += 3; }
            else bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    static bool IsRemovable(string dev)
    {
        try
        {
            var name = Path.GetFileName(dev);
            var sys = $"/sys/class/block/{name}";
            if (!Directory.Exists(sys)) return false;
            var real = new DirectoryInfo(sys).ResolveLinkTarget(true)?.FullName ?? sys;
            // a partition's parent folder is its disk
            foreach (var dir in new[] { real, Path.GetDirectoryName(real) })
            {
                var r = Path.Combine(dir!, "removable");
                if (File.Exists(r) && File.ReadAllText(r).Trim() == "1") return true;
            }
            return real.Contains("/usb");
        }
        catch { return false; }
    }

    static async Task<(int code, string output)> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, ((await o) + "\n" + (await e)).Trim());
    }

    /// <summary>Unmounts a drive (and powers off USB sticks / deletes loop devices for disk images). Returns an error or null.</summary>
    public static async Task<string> Eject(string device, string mount)
    {
        if (IconService.Which("udisksctl") == null)
        {
            var (c0, o0) = await Run("umount", mount);
            return c0 == 0 ? null : Last(o0);
        }
        var (code, output) = await Run("udisksctl", "unmount", "-b", device);
        if (code != 0 && !output.Contains("not mounted", StringComparison.OrdinalIgnoreCase)) return Last(output);
        var name = Path.GetFileName(device);
        if (name.StartsWith("loop")) await Run("udisksctl", "loop-delete", "-b", LoopParent(device));
        else if (IsRemovable(device)) await Run("udisksctl", "power-off", "-b", ParentDisk(device));
        return null;
    }

    static string LoopParent(string dev)
    {
        // /dev/loop0p1 → /dev/loop0
        var n = Path.GetFileName(dev);
        int p = n.IndexOf('p', 4);
        return p > 0 ? "/dev/" + n[..p] : dev;
    }

    static string ParentDisk(string dev)
    {
        try
        {
            var real = new DirectoryInfo($"/sys/class/block/{Path.GetFileName(dev)}").ResolveLinkTarget(true)?.FullName;
            if (real != null && File.Exists(Path.Combine(real, "partition"))) return "/dev/" + Path.GetFileName(Path.GetDirectoryName(real)!);
        }
        catch { }
        return dev;
    }

    static string Last(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    /// <summary>Mounts an ISO / disk image read-only through udisks. Returns (mount folder, null) or (null, error).</summary>
    public static async Task<(string path, string error)> MountImage(string file)
    {
        if (IconService.Which("udisksctl") == null) return (null, "Mounting disk images needs udisks2 (udisksctl).");
        var (code, output) = await Run("udisksctl", "loop-setup", "-r", "-f", file);
        if (code != 0) return (null, Last(output));
        var m = System.Text.RegularExpressions.Regex.Match(output, @"(/dev/loop\d+)");
        if (!m.Success) return (null, Last(output));
        var loop = m.Groups[1].Value;
        // udisks usually auto-mounts the new device; give it a moment, then mount it ourselves if needed
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            var mp = RecycleBin.Mounts().FirstOrDefault(x => x.Device == loop || x.Device.StartsWith(loop + "p"));
            if (mp.Mount != null) return (mp.Mount, null);
        }
        foreach (var dev in new[] { loop, loop + "p1" })
        {
            var (c2, o2) = await Run("udisksctl", "mount", "-b", dev);
            var mm = System.Text.RegularExpressions.Regex.Match(o2, @" at (.+?)\.?$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (c2 == 0 && mm.Success) return (mm.Groups[1].Value.Trim(), null);
        }
        return (null, "The image was attached but couldn't be mounted.");
    }
}
