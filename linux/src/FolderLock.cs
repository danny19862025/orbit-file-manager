using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbit;

/// <summary>
/// Password-locked folders. A folder becomes a single "name.orbitlock" file:
///
///   header  = "ORBITLK1" | version(1) | iterations(int32) | salt(16) | noncePrefix(8) | chunkSize(int32)
///   chunks  = final(1) | length(int32) | ciphertext(length) | tag(16)   — repeated, the last has final=1
///
/// The key is PBKDF2-SHA256(password, salt). Every chunk is AES-256-GCM with nonce = noncePrefix | index,
/// and authenticated data = header | index | final, so a wrong password, a changed byte, reordered or
/// missing chunks, or a cut-off file are all detected. The plaintext is a simple stream of entries:
///   kind(1: 1=folder, 2=file, 0=end) | path(string) | modified(int64 ticks) | [file: length(int64) | bytes]
/// Nothing is ever written to disk unencrypted, and the password is never stored.
/// </summary>
public static class FolderLock
{
    public const string Ext = ".orbitlock";
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORBITLK1");
    const byte Version = 1;
    const int Iterations = 600_000;
    const int ChunkSize = 1 << 20;
    const int HeaderLen = 8 + 1 + 4 + 16 + 8 + 4;

    public static bool IsLockFile(string path) => path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase);

    public sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base("That password isn't right.") { }
    }

    public sealed record Progress(long Done, long Total, string Current);

    // ------------------------------------------------------------------
    // Lock
    // ------------------------------------------------------------------
    /// <summary>Encrypts <paramref name="folder"/> into "folder.orbitlock" next to it, then verifies the file.
    /// The original folder is left alone; the caller decides what to do with it.</summary>
    public static string Lock(string folder, string password, IProgress<Progress> progress, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder).TrimEnd('/');
        var dest = UniquePath(folder + Ext);
        var entries = Scan(folder);
        long total = entries.Where(e => !e.IsDir).Sum(e => e.Length);
        var tmp = dest + ".partial";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var header = NewHeader(out var salt, out var prefix);
                fs.Write(header);
                using var key = DeriveKey(password, salt, Iterations);
                using var enc = new EncryptStream(fs, key.Key, header, prefix);
                using var w = new BinaryWriter(enc, Encoding.UTF8, leaveOpen: true);
                long done = 0;
                var buf = new byte[1 << 16];
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    w.Write(e.IsDir ? (byte)1 : (byte)2);
                    w.Write(e.Rel);
                    w.Write(e.Modified);
                    if (e.IsDir) continue;
                    progress?.Report(new Progress(done, total, e.Rel));
                    using var src = new FileStream(e.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    long len = src.Length;
                    w.Write(len);
                    long left = len;
                    while (left > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) throw new IOException($"“{e.Rel}” changed while it was being locked.");
                        w.Write(buf, 0, n);
                        left -= n;
                        done += n;
                        progress?.Report(new Progress(done, total, e.Rel));
                    }
                }
                w.Write((byte)0);
            }
            // read everything back before anything else happens to the original
            progress?.Report(new Progress(total, total, "Checking the locked file…"));
            Verify(tmp, password, entries, ct);
            File.Move(tmp, dest);
            return dest;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    sealed record Entry(string Full, string Rel, bool IsDir, long Length, long Modified);

    static List<Entry> Scan(string root)
    {
        var list = new List<Entry>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var di = new DirectoryInfo(d);
                if (di.LinkTarget != null)
                    throw new IOException($"“{Path.GetRelativePath(root, d)}” is a link to another place. Orbit doesn't lock folders that contain links.");
                list.Add(new Entry(d, Path.GetRelativePath(root, d), true, 0, di.LastWriteTimeUtc.Ticks));
                stack.Push(d);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var fi = new FileInfo(f);
                if (fi.LinkTarget != null)
                    throw new IOException($"“{Path.GetRelativePath(root, f)}” is a link to another place. Orbit doesn't lock folders that contain links.");
                list.Add(new Entry(f, Path.GetRelativePath(root, f), false, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
        }
        return list;
    }

    static void Verify(string lockFile, string password, List<Entry> expected, CancellationToken ct)
    {
        int i = 0;
        ReadEntries(lockFile, password, ct, (kind, rel, modified, len, data) =>
        {
            if (i >= expected.Count) throw new IOException("The locked file has more entries than the folder.");
            var e = expected[i++];
            if (e.Rel != rel || e.IsDir != (kind == 1) || (!e.IsDir && e.Length != len))
                throw new IOException($"The locked copy of “{e.Rel}” doesn't match the original.");
            if (data != null) data.CopyTo(Stream.Null);
        });
        if (i != expected.Count) throw new IOException("The locked file is missing entries.");
    }

    // ------------------------------------------------------------------
    // Unlock
    // ------------------------------------------------------------------
    /// <summary>Decrypts a .orbitlock file into a folder next to it. Everything is written into a hidden
    /// staging folder first and only renamed into place once the whole file has been checked.</summary>
    public static string Unlock(string lockFile, string password, IProgress<Progress> progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(lockFile))!;
        var name = Path.GetFileName(lockFile);
        name = name.Substring(0, name.Length - Ext.Length);
        if (name.Length == 0) name = "Unlocked";
        var stage = Path.Combine(dir, $".{name}.unlocking-{Guid.NewGuid():N}");
        var stageInfo = Directory.CreateDirectory(stage); // a dot-folder, so it's hidden while it fills
        var stageRoot = stageInfo.FullName.TrimEnd('/') + "/";
        long total = new FileInfo(lockFile).Length, done = 0;
        var dirTimes = new List<(string, long)>();
        try
        {
            var buf = new byte[1 << 16];
            ReadEntries(lockFile, password, ct, (kind, rel, modified, len, data) =>
            {
                rel = rel.Replace('\\', '/'); // folders locked on Windows store \ separators
                var target = Path.GetFullPath(Path.Combine(stage, rel));
                if (Path.IsPathRooted(rel) || rel.Split('/').Contains("..") || !target.StartsWith(stageRoot, StringComparison.Ordinal))
                    throw new IOException("The locked file contains an unsafe path.");
                if (kind == 1)
                {
                    Directory.CreateDirectory(target);
                    dirTimes.Add((target, modified));
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report(new Progress(done, total, rel));
                using (var o = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n;
                    while ((n = data.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        o.Write(buf, 0, n);
                        done += n;
                        progress?.Report(new Progress(Math.Min(done, total), total, rel));
                    }
                }
                try { File.SetLastWriteTimeUtc(target, new DateTime(modified, DateTimeKind.Utc)); } catch { }
            });
            foreach (var (d, t) in dirTimes.OrderByDescending(x => x.Item1.Length))
                try { Directory.SetLastWriteTimeUtc(d, new DateTime(t, DateTimeKind.Utc)); } catch { }
            var dest = UniquePath(Path.Combine(dir, name));
            Directory.Move(stage, dest);
            return dest;
        }
        catch
        {
            try { Directory.Delete(stage, true); } catch { }
            throw;
        }
    }

    /// <summary>Checks a password against a lock file by decrypting its first chunk.</summary>
    public static bool CheckPassword(string lockFile, string password)
    {
        using var fs = File.OpenRead(lockFile);
        var (header, salt, prefix, iterations) = ReadHeader(fs);
        using var key = DeriveKey(password, salt, iterations);
        try { using var dec = new DecryptStream(fs, key.Key, header, prefix); dec.ReadByte(); return true; }
        catch (WrongPasswordException) { return false; }
    }

    delegate void EntryHandler(byte kind, string rel, long modified, long length, Stream data);

    static void ReadEntries(string lockFile, string password, CancellationToken ct, EntryHandler handle)
    {
        using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (header, salt, prefix, iterations) = ReadHeader(fs);
        using var key = DeriveKey(password, salt, iterations);
        using var dec = new DecryptStream(fs, key.Key, header, prefix);
        using var r = new BinaryReader(dec, Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            byte kind = r.ReadByte();
            if (kind == 0) break;
            if (kind != 1 && kind != 2) throw new InvalidDataException("The locked file is damaged.");
            var rel = r.ReadString();
            long modified = r.ReadInt64();
            if (kind == 1) { handle(kind, rel, modified, 0, null); continue; }
            long len = r.ReadInt64();
            if (len < 0) throw new InvalidDataException("The locked file is damaged.");
            var slice = new SliceStream(dec, len);
            handle(kind, rel, modified, len, slice);
            slice.Skip();
        }
        if (!dec.AtEnd()) throw new InvalidDataException("The locked file is damaged.");
    }

    // ------------------------------------------------------------------
    // Header and key
    // ------------------------------------------------------------------
    static byte[] NewHeader(out byte[] salt, out byte[] prefix)
    {
        salt = RandomNumberGenerator.GetBytes(16);
        prefix = RandomNumberGenerator.GetBytes(8);
        var h = new byte[HeaderLen];
        Magic.CopyTo(h, 0);
        h[8] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(9), Iterations);
        salt.CopyTo(h, 13);
        prefix.CopyTo(h, 29);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(37), ChunkSize);
        return h;
    }

    static (byte[] header, byte[] salt, byte[] prefix, int iterations) ReadHeader(Stream s)
    {
        var h = new byte[HeaderLen];
        if (s.ReadAtLeast(h, HeaderLen, false) != HeaderLen || !h.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("This isn't an Orbit locked folder.");
        if (h[8] != Version) throw new InvalidDataException("This locked folder was made by a newer version of Orbit.");
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(9));
        int chunk = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(37));
        if (iterations < 100_000 || iterations > 10_000_000 || chunk != ChunkSize) throw new InvalidDataException("The locked file is damaged.");
        return (h, h[13..29], h[29..37], iterations);
    }

    sealed class KeyBox : IDisposable
    {
        public byte[] Key { get; init; }
        public void Dispose() => CryptographicOperations.ZeroMemory(Key);
    }

    static KeyBox DeriveKey(string password, byte[] salt, int iterations) =>
        new() { Key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, 32) };

    static byte[] Nonce(byte[] prefix, long index)
    {
        var n = new byte[12];
        prefix.CopyTo(n, 0);
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(8), checked((uint)index));
        return n;
    }

    static byte[] Aad(byte[] header, long index, bool final)
    {
        var a = new byte[header.Length + 9];
        header.CopyTo(a, 0);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(header.Length), index);
        a[^1] = final ? (byte)1 : (byte)0;
        return a;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var ext = path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) ? Ext : "";
        var name = Path.GetFileName(path);
        name = name.Substring(0, name.Length - ext.Length);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    // ------------------------------------------------------------------
    // Chunked AES-GCM streams
    // ------------------------------------------------------------------
    sealed class EncryptStream : Stream
    {
        readonly Stream inner;
        readonly AesGcm gcm;
        readonly byte[] header, prefix, buf = new byte[ChunkSize];
        int fill;
        long index;
        bool closed;

        public EncryptStream(Stream inner, byte[] key, byte[] header, byte[] prefix)
        {
            this.inner = inner; this.header = header; this.prefix = prefix;
            gcm = new AesGcm(key, 16);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                if (fill == buf.Length) Emit(false);
                int n = Math.Min(buf.Length - fill, data.Length);
                data[..n].CopyTo(buf.AsSpan(fill));
                fill += n;
                data = data[n..];
            }
        }

        void Emit(bool final)
        {
            var cipher = new byte[fill];
            var tag = new byte[16];
            gcm.Encrypt(Nonce(prefix, index), buf.AsSpan(0, fill), cipher, tag, Aad(header, index, final));
            Span<byte> head = stackalloc byte[5];
            head[0] = final ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(head[1..], fill);
            inner.Write(head);
            inner.Write(cipher);
            inner.Write(tag);
            CryptographicOperations.ZeroMemory(buf.AsSpan(0, fill));
            fill = 0;
            index++;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closed)
            {
                closed = true;
                Emit(true);
                inner.Flush();
                gcm.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    sealed class DecryptStream : Stream
    {
        readonly Stream inner;
        readonly AesGcm gcm;
        readonly byte[] header, prefix;
        byte[] plain = Array.Empty<byte>();
        int pos;
        long index;
        bool sawFinal;

        public DecryptStream(Stream inner, byte[] key, byte[] header, byte[] prefix)
        {
            this.inner = inner; this.header = header; this.prefix = prefix;
            gcm = new AesGcm(key, 16);
        }

        bool Next()
        {
            if (sawFinal) return false;
            Span<byte> head = stackalloc byte[5];
            if (inner.ReadAtLeast(head, 5, false) != 5) throw new InvalidDataException("The locked file is cut off — part of it is missing.");
            bool final = head[0] == 1;
            if (head[0] > 1) throw new InvalidDataException("The locked file is damaged.");
            int len = BinaryPrimitives.ReadInt32LittleEndian(head[1..]);
            if (len < 0 || len > ChunkSize) throw new InvalidDataException("The locked file is damaged.");
            var cipher = new byte[len];
            var tag = new byte[16];
            if (inner.ReadAtLeast(cipher, len, false) != len || inner.ReadAtLeast(tag, 16, false) != 16)
                throw new InvalidDataException("The locked file is cut off — part of it is missing.");
            var p = new byte[len];
            try { gcm.Decrypt(Nonce(prefix, index), cipher, tag, p, Aad(header, index, final)); }
            catch (AuthenticationTagMismatchException)
            {
                if (index == 0) throw new WrongPasswordException();
                throw new InvalidDataException("The locked file has been changed or damaged.");
            }
            if (plain.Length > 0) CryptographicOperations.ZeroMemory(plain);
            plain = p;
            pos = 0;
            index++;
            sawFinal = final;
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> dest)
        {
            while (pos == plain.Length)
                if (!Next()) return 0;
            int n = Math.Min(dest.Length, plain.Length - pos);
            plain.AsSpan(pos, n).CopyTo(dest);
            pos += n;
            return n;
        }

        /// <summary>True when every byte has been read, the final chunk was seen and nothing follows it.</summary>
        public bool AtEnd()
        {
            while (pos == plain.Length && !sawFinal) Next();
            return pos == plain.Length && sawFinal && inner.ReadByte() == -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { gcm.Dispose(); if (plain.Length > 0) CryptographicOperations.ZeroMemory(plain); }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A read-only window of <c>length</c> bytes over the decrypted stream (one file's contents).</summary>
    sealed class SliceStream : Stream
    {
        readonly Stream inner;
        long left;
        public SliceStream(Stream inner, long length) { this.inner = inner; left = length; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> dest)
        {
            if (left == 0) return 0;
            int n = inner.Read(dest[..(int)Math.Min(dest.Length, left)]);
            if (n == 0) throw new InvalidDataException("The locked file is cut off — part of it is missing.");
            left -= n;
            return n;
        }

        public void Skip()
        {
            var b = new byte[1 << 16];
            while (left > 0) Read(b, 0, b.Length);
        }

        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
