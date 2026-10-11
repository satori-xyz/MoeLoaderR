using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MoeLoaderR.Core;

public sealed record ImageCheckIssue(string Path, long? Size, string Kind, string Detail, int? GroupId = null, string Md5 = "")
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Size is long size ? $"{size / 1048576d:F2} MiB" : "—";
    public string GroupText => GroupId is int group ? $"第 {group} 组" : "—";
}

public sealed record ImageCheckProgress(int Checked, int Suspected, int Unreadable, string CurrentPath);

/// <summary>Read-only structure screening. Does not decode pixels or prove file integrity.</summary>
public static class ImageIntegrityScanner
{
    public static ImageCheckProgress Scan(string root, bool recursive, Action<ImageCheckIssue> report,
        IProgress<ImageCheckProgress> progress, CancellationToken token)
    {
        root = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在或无法访问，请检查网络共享连接。");
        var directories = new Stack<string>();
        directories.Push(root);
        var checkedCount = 0;
        var suspected = 0;
        var unreadable = 0;
        var timer = Stopwatch.StartNew();
        var current = root;
        var gate = new object();
        void Report(ImageCheckIssue issue)
        {
            if (issue.Kind == "疑似不完整") suspected++;
            else unreadable++;
            report(issue);
        }
        try
        {
            while (directories.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = directories.Pop();
                try
                {
                    var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint, BufferSize = 65536 };
                    Parallel.ForEach(new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options),
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, entry =>
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                if (recursive) lock (directories) directories.Push(entry.FullName);
                                return;
                            }
                            var extension = entry.Extension.ToLowerInvariant();
                            if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) return;
                            var issue = CheckFile(entry.FullName, token);
                            lock (gate)
                            {
                                current = entry.FullName;
                                checkedCount++;
                                if (issue != null) Report(issue);
                                if (timer.ElapsedMilliseconds >= 150)
                                {
                                    progress?.Report(new(checkedCount, suspected, unreadable, current));
                                    timer.Restart();
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            lock (gate) Report(new(entry.FullName, null, "无法读取", ex.Message));
                        }
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(new(directory, null, "无法读取", "目录未能完整扫描：" + ex.Message));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(error => error is IOException or UnauthorizedAccessException))
                {
                    Report(new(directory, null, "无法读取", "目录未能完整扫描：" + ex.InnerExceptions[0].Message));
                }
            }
            return new(checkedCount, suspected, unreadable, current);
        }
        finally { progress?.Report(new(checkedCount, suspected, unreadable, current)); }
    }

    public static ImageCheckIssue CheckFile(string path, CancellationToken token = default)
    {
        long? size = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var modified = File.GetLastWriteTimeUtc(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            size = file.Length;
            string problem;
            if (size == 0) problem = "空文件（0 字节）";
            else
            {
                problem = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => CheckJpeg(file, token),
                    ".png" => CheckPng(file, token),
                    ".webp" => CheckWebP(file),
                    _ => null
                };
            }
            if (new FileInfo(path).Length != size || File.GetLastWriteTimeUtc(path) != modified)
                return new(path, size, "需重查", "扫描期间文件发生变化，请等待下载或写入完成后重查。");
            return problem == null ? null : new(path, size, "疑似不完整", problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(path, size, "无法读取", ex.Message);
        }
    }

    private static string CheckPng(Stream file, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        Span<byte> tail = stackalloc byte[12];
        if (!Read(file, header) || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return "PNG 文件头不正确或不完整。";
        var first = true;
        var hasImageData = false;
        while (file.Position < file.Length)
        {
            token.ThrowIfCancellationRequested();
            if (!Read(file, header)) return "PNG 块头被截断，缺少 IEND 结束块。";
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (length > int.MaxValue || file.Length - file.Position < (long)length + 4)
                return "PNG 数据块长度超过文件剩余大小，文件可能被截断。";
            if (first && (type != 0x49484452 || length != 13)) return "PNG 缺少有效 IHDR 文件头块。";
            first = false;
            if (type == 0x49444154)
            {
                hasImageData = true; // IDAT
                // Normal PNGs end here. Avoid seeking through hundreds of IDAT blocks over SMB.
                var position = file.Position;
                file.Position = file.Length - tail.Length;
                if (Read(file, tail) && tail.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
                    return null;
                file.Position = position;
            }
            if (type == 0x49454E44) // IEND; accept trailing metadata after this structural terminator.
            {
                if (length != 0 || !hasImageData) return "PNG 结束块或图像数据块不完整。";
                if (!Read(file, header[..4]) || BinaryPrimitives.ReadUInt32BigEndian(header[..4]) != 0xAE426082)
                    return "PNG IEND 结束块校验值异常。";
                return null;
            }
            file.Seek((long)length + 4, SeekOrigin.Current);
        }
        return "PNG 缺少 IEND 结束块。";
    }

    private static string CheckJpeg(Stream file, CancellationToken token)
    {
        if (file.ReadByte() != 0xFF || file.ReadByte() != 0xD8) return "JPEG 文件头不正确或不完整。";
        // Skip metadata segments before looking for EOI, so embedded EXIF thumbnails cannot pass a truncated image.
        using var buffered = new BufferedStream(file, 65536);
        var pendingMarker = -1;
        var sawScan = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int marker;
            if (pendingMarker >= 0) { marker = pendingMarker; pendingMarker = -1; }
            else
            {
                if (buffered.ReadByte() != 0xFF) return "JPEG 标记结构不完整或文件被截断。";
                do { marker = buffered.ReadByte(); } while (marker == 0xFF);
            }
            if (marker < 0) return "JPEG 缺少 FF D9 结束标记。";
            if (marker == 0xD9) return sawScan ? null : "JPEG 缺少图像扫描数据。";
            if (marker is 0x00 or 0xD8) return "JPEG 标记结构异常。";
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            var high = buffered.ReadByte();
            var low = buffered.ReadByte();
            var length = (high << 8) | low;
            if (high < 0 || low < 0 || length < 2 || buffered.Length - buffered.Position < length - 2)
                return "JPEG 数据段被截断。";
            buffered.Seek(length - 2, SeekOrigin.Current);
            if (marker != 0xDA) continue;
            sawScan = true;
            var scanStart = buffered.Position;
            // Common case: only read the last 64 KiB, never decode image pixels.
            var tailStart = Math.Max(scanStart, buffered.Length - 65536);
            buffered.Position = tailStart;
            var tail = new byte[(int)(buffered.Length - tailStart)];
            if (Read(buffered, tail))
                for (var i = tail.Length - 2; i >= 0; i--)
                    if (tail[i] == 0xFF && tail[i + 1] == 0xD9) return null;
            // Unusual files may have a large trailer. Walk entropy markers with bounded buffering.
            buffered.Position = scanStart;
            var bytes = 0;
            while (true)
            {
                if ((bytes++ & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                var value = buffered.ReadByte();
                if (value < 0) return "JPEG 缺少 FF D9 结束标记，疑似下载被截断。";
                if (value != 0xFF) continue;
                do { value = buffered.ReadByte(); } while (value == 0xFF);
                if (value == 0 || value is >= 0xD0 and <= 0xD7) continue;
                if (value == 0xD9) return null;
                if (value < 0) return "JPEG 缺少 FF D9 结束标记。";
                pendingMarker = value;
                break;
            }
        }
    }

    private static string CheckWebP(Stream file)
    {
        Span<byte> header = stackalloc byte[12];
        if (!Read(file, header) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
            return "WebP 文件头不正确或不完整。";
        return (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8 == file.Length
            ? null : "WebP 声明的文件长度与实际大小不符。";
    }

    private static bool Read(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = stream.Read(buffer[offset..]);
            if (count == 0) return false;
            offset += count;
        }
        return true;
    }
}
