using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Orbit;

/// <summary>
/// Copy / move / delete with a progress window, conflict prompts and the Trash. (On Windows the shell does this;
/// here Orbit does it itself: permissions, times and symlinks are kept, moves on the same drive are instant renames.)
/// </summary>
public static class FileOps
{
    /// <summary>Raised when an operation fails for a reason other than the user cancelling.</summary>
    public static event Action<string> Failed;

    enum Conflict { Ask, Replace, Skip, KeepBoth }

    public static void Copy(IEnumerable<string> src, string dest, Window owner, bool renameOnCollision, Action done = null) =>
        Run("Copying", src.ToList(), dest, false, renameOnCollision, owner, done);

    public static void Move(IEnumerable<string> src, string dest, Window owner, Action done = null) =>
        Run("Moving", src.ToList(), dest, true, false, owner, done);

    public static void Delete(IEnumerable<string> src, bool recycle, Window owner, Action done = null)
    {
        var list = src.ToList();
        _ = Task.Run(async () =>
        {
            var failed = new List<(string path, string error)>();
            foreach (var p in list)
            {
                try
                {
                    if (recycle) RecycleBin.Trash(p);
                    else DeletePermanently(p);
                }
                catch (Exception ex)
                {
                    if (recycle && RecycleBin.GioTrash(p)) continue;
                    failed.Add((p, ex.Message));
                }
            }
            if (failed.Count > 0 && recycle)
            {
                // like Windows' "too big for the Recycle Bin" warning: offer to delete for good instead
                var names = string.Join("\n", failed.Take(5).Select(f => "• " + Path.GetFileName(f.path)));
                bool nuke = await Dispatcher.UIThread.InvokeAsync(() => Dialog.Ask(owner, "Can't move to the Trash",
                    $"{(failed.Count == 1 ? "This item" : $"These {failed.Count} items")} can't be moved to the Trash:\n\n{names}\n\n{failed[0].error}\n\nDelete {(failed.Count == 1 ? "it" : "them")} permanently instead? This can't be undone.",
                    "Delete permanently", "Cancel", warning: true, yesIsDefault: false));
                var still = new List<(string, string)>();
                if (nuke)
                    foreach (var (p, _) in failed)
                        try { DeletePermanently(p); } catch (Exception ex) { still.Add((p, ex.Message)); }
                failed = nuke ? still : new List<(string, string)>();
            }
            if (failed.Count > 0)
                Failed?.Invoke(failed.Count == 1 ? $"Couldn't delete “{Path.GetFileName(failed[0].path)}”: {failed[0].error}" : $"Couldn't delete {failed.Count} items: {failed[0].error}");
            try { done?.Invoke(); } catch { }
        });
    }

    public static void DeletePermanently(string p)
    {
        var fi = new FileInfo(p);
        if (fi.LinkTarget != null || File.Exists(p)) { File.Delete(p); return; }
        if (!Directory.Exists(p)) return;
        try { Directory.Delete(p, true); }
        catch (UnauthorizedAccessException)
        {
            // read-only folders inside: make them writable and try again
            foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories).Prepend(p))
                try { File.SetUnixFileMode(d, File.GetUnixFileMode(d) | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.UserRead); } catch { }
            Directory.Delete(p, true);
        }
    }

    // ------------------------------------------------------------------
    // Copy / move
    // ------------------------------------------------------------------
    sealed class Job
    {
        public string Verb;
        public long TotalBytes, DoneBytes;
        public int TotalFiles, DoneFiles;
        public string Current;
        public CancellationTokenSource Cts = new();
        public Conflict Policy = Conflict.Ask;
        public Window Owner;
        public List<string> Errors = new();
    }

    static void Run(string verb, List<string> src, string dest, bool move, bool renameOnCollision, Window owner, Action done)
    {
        if (src.Count == 0 || dest == null) return;
        var job = new Job { Verb = verb, Owner = owner, Policy = renameOnCollision ? Conflict.KeepBoth : Conflict.Ask };
        ProgressWindow win = null;
        var showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        showTimer.Tick += (_, _) =>
        {
            showTimer.Stop();
            win = new ProgressWindow(job.Verb, src.Count, dest, () => job.Cts.Cancel());
            win.Show(owner);
            win.Update(job.DoneBytes, job.TotalBytes, job.Current);
        };
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        tick.Tick += (_, _) => win?.Update(job.DoneBytes, job.TotalBytes, job.Current);
        showTimer.Start();
        tick.Start();

        _ = Task.Run(() =>
        {
            try
            {
                // sizes first, so the bar means something
                foreach (var s in src) Measure(s, job);
                foreach (var s in src)
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(s.TrimEnd('/'));
                    var target = Path.Combine(dest, name);
                    if (!move && string.Equals(Path.GetDirectoryName(s), dest, StringComparison.Ordinal)) target = UniqueName(target);
                    if (move && RecycleBin.MountOf(s) == RecycleBin.MountOf(dest) && !Exists(target))
                    {
                        job.Current = name;
                        if (Directory.Exists(s) && new DirectoryInfo(s).LinkTarget == null) Directory.Move(s, target); else File.Move(s, target);
                        job.DoneBytes += SizeOf(s, target);
                        continue;
                    }
                    Transfer(s, target, job, move);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { job.Errors.Add(ex.Message); }
            Dispatcher.UIThread.Post(() =>
            {
                showTimer.Stop();
                tick.Stop();
                win?.Close();
            });
            if (job.Errors.Count > 0)
                Failed?.Invoke(job.Errors.Count == 1 ? $"Couldn't {(move ? "move" : "copy")}: {job.Errors[0]}" : $"{job.Errors.Count} items couldn't be {(move ? "moved" : "copied")}: {job.Errors[0]}");
            try { done?.Invoke(); } catch { }
        });
    }

    static long SizeOf(string src, string target)
    {
        try { return File.Exists(target) ? new FileInfo(target).Length : 0; } catch { return 0; }
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p) || new FileInfo(p).LinkTarget != null;

    static void Measure(string p, Job job)
    {
        try
        {
            var fi = new FileInfo(p);
            if (fi.LinkTarget != null || fi.Exists) { job.TotalBytes += fi.LinkTarget != null ? 0 : fi.Length; job.TotalFiles++; return; }
            foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                try { job.TotalBytes += f.LinkTarget != null ? 0 : f.Length; } catch { }
                job.TotalFiles++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    static void Transfer(string src, string target, Job job, bool move)
    {
        var ct = job.Cts.Token;
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(src);
        bool isLink = info.LinkTarget != null;
        bool isDir = !isLink && Directory.Exists(src);

        if (isDir)
        {
            if (target == src || target.StartsWith(src.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException($"“{Path.GetFileName(src)}” can't be put inside itself.");
            if (File.Exists(target))
            {
                var c = Resolve(job, target, true);
                if (c == Conflict.Skip) return;
                if (c == Conflict.KeepBoth) target = UniqueName(target);
                else File.Delete(target);
            }
            // folders merge into an existing folder of the same name, like on Windows
            bool created = !Directory.Exists(target);
            Directory.CreateDirectory(target);
            foreach (var e in Directory.EnumerateFileSystemEntries(src).ToList())
                Transfer(e, Path.Combine(target, Path.GetFileName(e)), job, move);
            try
            {
                if (created) File.SetUnixFileMode(target, File.GetUnixFileMode(src));
                Directory.SetLastWriteTime(target, Directory.GetLastWriteTime(src));
            }
            catch { }
            if (move) { try { Directory.Delete(src, false); } catch (Exception ex) { job.Errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); } }
            return;
        }

        job.Current = Path.GetFileName(src);
        if (Exists(target))
        {
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(src), StringComparison.Ordinal)) target = UniqueName(target);
            else
            {
                var c = Resolve(job, target, false);
                if (c == Conflict.Skip) { job.DoneBytes += isLink ? 0 : info.Length; return; }
                if (c == Conflict.KeepBoth) target = UniqueName(target);
                else if (Directory.Exists(target) && new FileInfo(target).LinkTarget == null) Directory.Delete(target, true);
                else File.Delete(target);
            }
        }
        try
        {
            if (isLink)
            {
                File.CreateSymbolicLink(target, info.LinkTarget!);
            }
            else
            {
                var tmp = target + ".orbit-part";
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = i.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        o.Write(buf, 0, n);
                        job.DoneBytes += n;
                    }
                }
                if (ct.IsCancellationRequested) { try { File.Delete(tmp); } catch { } ct.ThrowIfCancellationRequested(); }
                File.Move(tmp, target, true);
                try
                {
                    File.SetUnixFileMode(target, File.GetUnixFileMode(src));
                    File.SetLastWriteTime(target, info.LastWriteTime);
                }
                catch { }
            }
            job.DoneFiles++;
            if (move) File.Delete(src);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { job.Errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
    }

    static Conflict Resolve(Job job, string target, bool folderOverFile)
    {
        if (job.Policy != Conflict.Ask) return job.Policy;
        var name = Path.GetFileName(target);
        var where = Path.GetDirectoryName(target);
        var (choice, all) = Dialog.OnUi(() => Dialog.Show(job.Owner, "Replace or skip",
            $"“{name}” already exists in {MainWindow.Display(where)}." + (folderOverFile ? "\nA file there has the same name as the folder you're copying." : ""),
            new[] { "Cancel", "Skip", "Keep both", "Replace" }, primary: 3, checkText: "Do this for all conflicts", cancel: 0));
        var c = choice switch { 1 => Conflict.Skip, 2 => Conflict.KeepBoth, 3 => Conflict.Replace, _ => Conflict.Ask };
        if (c == Conflict.Ask) { job.Cts.Cancel(); job.Cts.Token.ThrowIfCancellationRequested(); }
        if (all) job.Policy = c;
        return c;
    }

    /// <summary>"name (2).ext" next to an existing item.</summary>
    public static string UniqueName(string path)
    {
        if (!Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var file = Path.GetFileName(path);
        var ext = Directory.Exists(path) ? "" : Path.GetExtension(file);
        var stem = file[..^ext.Length];
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Exists(p)) return p;
        }
    }

    /// <summary>Plain recursive copy without UI (used by restore across drives).</summary>
    public static void CopyTree(string src, string dest)
    {
        if (File.Exists(src)) { File.Copy(src, dest); return; }
        Directory.CreateDirectory(dest);
        foreach (var e in Directory.EnumerateFileSystemEntries(src))
        {
            var t = Path.Combine(dest, Path.GetFileName(e));
            var fi = new FileInfo(e);
            if (fi.LinkTarget != null) File.CreateSymbolicLink(t, fi.LinkTarget);
            else if (Directory.Exists(e)) CopyTree(e, t);
            else File.Copy(e, t);
        }
    }
}

/// <summary>The small "Copying 3 items to Downloads" window with a progress bar and Cancel.</summary>
public sealed class ProgressWindow : OrbitWindow
{
    readonly ProgressBar bar;
    readonly TextBlock detail, title;
    readonly int count;
    readonly string verb, dest;

    public ProgressWindow(string verb, int count, string dest, Action cancel)
    {
        this.verb = verb;
        this.count = count;
        this.dest = dest;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Title = verb;
        var stack = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
        title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = $"{verb} {(count == 1 ? "1 item" : $"{count} items")} to “{MainWindow.Display(dest)}”" };
        DragArea(title);
        stack.Children.Add(title);
        bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(3) };
        stack.Children.Add(bar);
        detail = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable("MutedBrush"));
        stack.Children.Add(detail);
        var b = new Button { Content = "Cancel", Classes = { "dlg" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        b.Click += (_, _) => { cancel(); detail.Text = "Stopping…"; };
        stack.Children.Add(b);
        SetBody(stack);
    }

    public void Update(long done, long total, string current)
    {
        bar.Value = total > 0 ? Math.Min(1, (double)done / total) : 0;
        bar.IsIndeterminate = total == 0;
        detail.Text = (total > 0 ? $"{Fmt.Size(done)} of {Fmt.Size(total)}" : "Preparing…") + (string.IsNullOrEmpty(current) ? "" : "  ·  " + current);
    }
}
