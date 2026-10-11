using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MoeLoaderR.Core;

public sealed record DuplicateDeleteGroup(ImageCheckIssue Keep, IReadOnlyList<ImageCheckIssue> Remove);
public sealed record DuplicateDeletePlan(string Root, IReadOnlyList<DuplicateDeleteGroup> Groups)
{
    public int DeleteCount => Groups.Sum(group => group.Remove.Count);
}
public sealed record DuplicateDeleteResult(ImageCheckIssue File, bool Deleted, string Message);
public sealed record DuplicateDeleteSummary(int Deleted, int Skipped, bool Canceled);

public static class DuplicateImageDeletion
{
    public static DuplicateDeletePlan CreatePlan(string root, IEnumerable<ImageCheckIssue> rows)
    {
        var groups = rows.Where(row => row.GroupId != null && row.Kind == "重复文件")
            .GroupBy(row => row.GroupId).OrderBy(group => group.Key)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var sorted = group.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                return new DuplicateDeleteGroup(sorted[0], Array.AsReadOnly(sorted[1..]));
            }).ToArray();
        var plan = new DuplicateDeletePlan(Path.GetFullPath(root), Array.AsReadOnly(groups));
        ValidatePlan(plan);
        return plan;
    }

    private static void ValidatePlan(DuplicateDeletePlan plan)
    {
        var root = Path.GetFullPath(plan.Root);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Groups)
        {
            if (group.Keep.Size is not >= 0 || group.Keep.Md5.Length != 32 || !group.Keep.Md5.All(Uri.IsHexDigit))
                throw new InvalidDataException("查重记录缺少有效的大小或 MD5，请重新扫描。");
            foreach (var row in group.Remove.Prepend(group.Keep))
            {
                var path = Path.GetFullPath(row.Path);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !seen.Add(path)
                    || row.Size != group.Keep.Size || row.Md5 != group.Keep.Md5 || row.GroupId != group.Keep.GroupId)
                    throw new InvalidDataException("删除清单存在重复路径、越界路径或无效分组，请重新扫描。");
            }
        }
    }

    public static DuplicateDeleteSummary Delete(DuplicateDeletePlan plan, Action<DuplicateDeleteResult> report,
        IProgress<string> progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidatePlan(plan); // Validate every target before the first deletion.
        int deleted = 0, skipped = 0;
        var buffer = new byte[65536];
        var otherBuffer = new byte[65536];
        try
        {
            foreach (var group in plan.Groups)
                foreach (var row in group.Remove)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(row.Path);
                    try
                    {
                        DeleteVerifiedCopy(plan.Root, group.Keep, row, buffer, otherBuffer, token);
                        deleted++;
                        report(new(row, true, "已删除"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        skipped++;
                        report(new(row, false, "未删除：" + ex.Message));
                    }
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(deleted, skipped, true); }
        return new(deleted, skipped, false);
    }

    private static void DeleteVerifiedCopy(string root, ImageCheckIssue keep, ImageCheckIssue remove,
        byte[] buffer, byte[] otherBuffer, CancellationToken token)
    {
        RejectLinks(root, keep.Path);
        RejectLinks(root, remove.Path);
        // Holding these handles denies writes and renames throughout verification and deletion.
        using var retained = new FileStream(keep.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var handle = CreateFileW(ExtendedPath(remove.Path), 0x80010000, 1, IntPtr.Zero, 3, 0x08200080, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var duplicate = new FileStream(handle, FileAccess.Read, 65536);
        if (retained.Length != keep.Size || duplicate.Length != remove.Size) throw new IOException("文件大小已变化，请重新查重。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        while (true)
        {
            var a = ReadBlock(retained, buffer, token);
            var b = ReadBlock(duplicate, otherBuffer, token);
            if (a != b || !buffer.AsSpan(0, a).SequenceEqual(otherBuffer.AsSpan(0, b)))
                throw new IOException("文件内容已变化，与保留文件不一致。");
            if (a == 0) break;
            hash.AppendData(buffer, 0, a);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(keep.Md5, StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件 MD5 与扫描结果不一致，请重新查重。");
        token.ThrowIfCancellationRequested();
        var disposition = new FileDisposition { DeleteFile = 1 };
        // Delete the exact handle we compared, never reopen the path after verification.
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void RejectLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("文件或子目录是链接，已跳过，请重新检查实际目录。");
            current = Path.GetDirectoryName(current) ?? throw new IOException("文件不在扫描目录内。");
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static string ExtendedPath(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDisposition info, uint size);
}
