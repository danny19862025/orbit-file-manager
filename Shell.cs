using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orbit;

/// <summary>Copy / move / delete through the Windows shell: native progress, conflict prompts, Undo-able, Recycle Bin.</summary>
public static class FileOps
{
    const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
                 FOF_NOCONFIRMMKDIR = 0x200, FOF_WANTNUKEWARNING = 0x4000;

    public static void Copy(IEnumerable<string> src, string dest, IntPtr owner, bool renameOnCollision, Action done = null) =>
        Run(FO_COPY, src, dest, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (renameOnCollision ? FOF_RENAMEONCOLLISION : 0)), owner, done);

    public static void Move(IEnumerable<string> src, string dest, IntPtr owner, Action done = null) =>
        Run(FO_MOVE, src, dest, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR), owner, done);

    public static void Delete(IEnumerable<string> src, bool recycle, IntPtr owner, Action done = null) =>
        Run(FO_DELETE, src, null,
            recycle ? (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING) : FOF_NOCONFIRMATION,
            owner, done);

    /// <summary>Raised (on a worker thread) when an operation fails for a reason other than the user cancelling.</summary>
    public static event Action<string> Failed;

    static void Run(uint func, IEnumerable<string> src, string dest, ushort flags, IntPtr owner, Action done)
    {
        var from = string.Join("\0", src) + "\0";
        var to = dest == null ? null : dest + "\0";
        var verb = func switch { FO_MOVE => "move", FO_COPY => "copy", _ => "delete" };
        var t = new Thread(() =>
        {
            var op = new Native.SHFILEOPSTRUCT { hwnd = owner, wFunc = func, pFrom = from, pTo = to, fFlags = flags };
            int rc;
            try { rc = Native.SHFileOperation(ref op); }
            catch (Exception ex) { rc = -1; Failed?.Invoke($"Couldn't {verb}: {ex.Message}"); }
            if (rc > 0 && rc != 1223 && !op.fAnyOperationsAborted)
                Failed?.Invoke($"Couldn't {verb} the items ({Describe(rc)}).");
            try { done?.Invoke(); } catch { }
        })
        { IsBackground = false, Name = "FileOp" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // SHFileOperation returns legacy DE_* codes for some failures rather than Win32 errors.
    static string Describe(int rc) => rc switch
    {
        0x71 => "source and destination are the same file",
        0x72 => "multiple sources and destinations don't match",
        0x73 or 0x74 => "the destination is inside the source folder",
        0x75 => "operation cancelled",
        0x78 => "access denied",
        0x79 => "path too long",
        0x7C => "invalid path",
        0x7E => "destination is a subfolder of the source",
        0x80 => "the destination already exists",
        0x81 => "the destination path is too long",
        0x10000 => "unknown error at the destination",
        _ => new Win32Exception(rc).Message
    };
}

public static class Shell
{
    public static void Open(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            var dir = SafeDir(path);
            if (dir != null) psi.WorkingDirectory = dir;
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // no association
        {
            OpenWith(path);
        }
    }

    static string SafeDir(string path)
    {
        try { return File.Exists(path) ? Path.GetDirectoryName(path) : null; } catch { return null; }
    }

    public static void OpenWith(string path) => Verb(path, "openas", IntPtr.Zero);

    public static void Properties(string path, IntPtr owner) => Verb(path, "properties", owner);

    static void Verb(string path, string verb, IntPtr owner)
    {
        var sei = new Native.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = owner,
            lpVerb = verb,
            lpFile = path,
            nShow = 1
        };
        Native.ShellExecuteEx(ref sei);
    }

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static void OpenInExplorer(string dir) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });

    public static void OpenTerminal(string dir)
    {
        var arg = dir.EndsWith('\\') ? dir + "." : dir;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{arg}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = dir });
        }
    }
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
