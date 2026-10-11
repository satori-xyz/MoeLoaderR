using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MoeLoaderR.Core;

public sealed record DuplicateCheckProgress(string Phase, int Files, int Candidates, int Hashed,
    int Groups, int DuplicateFiles, int Unreadable, string CurrentPath);

/// <summary>Size -> MD5 -> byte comparison. Only reports matches observed during this scan.</summary>
public static class DuplicateImageScanner
{
    private sealed record FileStamp(string Path, long Size, DateTime Modified);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".avif", ".heic", ".heif", ".jxl" };

    public static DuplicateCheckProgress Scan(string root, bool recursive, Action<ImageCheckIssue> report,
        IProgress<DuplicateCheckProgress> progress, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在或无法访问，请检查网络共享连接。");
        var sizes = new Dictionary<long, List<FileStamp>>();
        var directories = new Stack<string>();
        directories.Push(root);
        int files = 0, candidates = 0, hashed = 0, groups = 0, duplicateFiles = 0, unreadable = 0;
        var phase = "统计文件大小";
        var current = root;
        var timer = Stopwatch.StartNew();
        DuplicateCheckProgress Snapshot() => new(phase, files, candidates, hashed, groups, duplicateFiles, unreadable, current);
        void Update(bool force = false)
        {
            if (!force && timer.ElapsedMilliseconds < 150) return;
            progress?.Report(Snapshot());
            timer.Restart();
        }
        void Error(string path, long? size, Exception error)
        {
            unreadable++;
            report(new(path, size, error is FileChangedException ? "需重查" : "无法读取", error.Message));
        }
        var buffer = new byte[65536];
        var compareBuffer = new byte[65536];
        try
        {
            while (directories.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = directories.Pop();
                current = directory;
                try
                {
                    var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, BufferSize = 65536 };
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                    {
                        token.ThrowIfCancellationRequested();
                        current = entry.FullName;
                        try
                        {
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                if (recursive) directories.Push(entry.FullName);
                            }
                            else if (Extensions.Contains(entry.Extension))
                            {
                                var info = (FileInfo)entry;
                                var stamp = new FileStamp(info.FullName, info.Length, info.LastWriteTimeUtc);
                                if (!sizes.TryGetValue(stamp.Size, out var bucket)) sizes.Add(stamp.Size, bucket = new());
                                bucket.Add(stamp);
                                files++;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(current, null, ex); }
                        Update();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(directory, null, ex); }
            }
            candidates = sizes.Values.Where(bucket => bucket.Count > 1).Sum(bucket => bucket.Count);
            phase = "计算 MD5";
            Update(true);
            foreach (var bucket in sizes.Values.Where(bucket => bucket.Count > 1))
            {
                token.ThrowIfCancellationRequested();
                var hashes = new Dictionary<string, List<FileStamp>>(StringComparer.Ordinal);
                phase = "计算 MD5";
                foreach (var stamp in bucket)
                {
                    token.ThrowIfCancellationRequested();
                    current = stamp.Path;
                    try
                    {
                        using var file = OpenUnchanged(stamp);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                        int count;
                        while ((count = ReadBlock(file, buffer, token)) > 0)
                        {
                            hash.AppendData(buffer, 0, count);
                            Update();
                        }
                        EnsureUnchanged(stamp);
                        var digest = Convert.ToHexString(hash.GetHashAndReset());
                        if (!hashes.TryGetValue(digest, out var matches)) hashes.Add(digest, matches = new());
                        matches.Add(stamp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(stamp.Path, stamp.Size, ex); }
                    hashed++;
                    Update();
                }
                phase = "逐字节核对";
                foreach (var hash in hashes.Where(pair => pair.Value.Count > 1))
                {
                    token.ThrowIfCancellationRequested();
                    // Partition same-MD5 files by actual content, even in the event of a collision.
                    var verified = new List<List<FileStamp>>();
                    try
                    {
                        foreach (var stamp in hash.Value)
                        {
                            token.ThrowIfCancellationRequested();
                            current = stamp.Path;
                            List<FileStamp> match = null;
                            foreach (var group in verified)
                            {
                                using var reference = OpenUnchanged(group[0]);
                                using var candidate = OpenUnchanged(stamp);
                                var equal = true;
                                while (true)
                                {
                                    var a = ReadBlock(reference, buffer, token);
                                    var b = ReadBlock(candidate, compareBuffer, token);
                                    Update();
                                    if (a != b || !buffer.AsSpan(0, a).SequenceEqual(compareBuffer.AsSpan(0, b))) { equal = false; break; }
                                    if (a == 0) break;
                                }
                                EnsureUnchanged(group[0]);
                                EnsureUnchanged(stamp);
                                if (equal) { match = group; break; }
                            }
                            if (match == null) verified.Add(new() { stamp });
                            else match.Add(stamp);
                        }
                        // Files that changed or disappeared must not be emitted as verified duplicates.
                        foreach (var stamp in hash.Value) EnsureUnchanged(stamp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        foreach (var stamp in hash.Value)
                        {
                            unreadable++;
                            report(new(stamp.Path, stamp.Size, "需重查", "同组文件变化或无法读取，本组未确认重复：" + ex.Message));
                        }
                        continue;
                    }
                    foreach (var group in verified.Where(group => group.Count > 1))
                    {
                        token.ThrowIfCancellationRequested();
                        groups++;
                        duplicateFiles += group.Count;
                        foreach (var stamp in group.OrderBy(stamp => stamp.Path, StringComparer.OrdinalIgnoreCase))
                            report(new(stamp.Path, stamp.Size, "重复文件", stamp.Path, groups, hash.Key));
                    }
                    Update();
                }
            }
            phase = "检查完成";
            return Snapshot();
        }
        finally { Update(true); }
    }

    private sealed class FileChangedException(string path) : IOException("文件自统计后发生变化，请等待写入完成后重查：" + path);

    private static void EnsureUnchanged(FileStamp stamp)
    {
        var info = new FileInfo(stamp.Path);
        if (!info.Exists || info.Length != stamp.Size || info.LastWriteTimeUtc != stamp.Modified) throw new FileChangedException(stamp.Path);
    }

    private static FileStream OpenUnchanged(FileStamp stamp)
    {
        EnsureUnchanged(stamp);
        var file = new FileStream(stamp.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { EnsureUnchanged(stamp); return file; }
        catch { file.Dispose(); throw; }
    }

    private static int ReadBlock(Stream file, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = file.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
