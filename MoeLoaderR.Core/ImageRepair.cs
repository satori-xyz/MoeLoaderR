using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoeLoaderR.Core.Sites;
using Newtonsoft.Json.Linq;

namespace MoeLoaderR.Core;

public sealed record ImageRepairSource(string Site, int Id);

public static class ImageRepair
{
    public static ImageRepairSource Identify(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path),
            @"^(yande(?:\.re)?|konachan-g|konachan\.net|konachan(?:\.com)?)[\s_-]+([1-9][0-9]*)(?=$|[\s_-])", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var id)) return null;
        var name = match.Groups[1].Value.ToLowerInvariant();
        return new(name.StartsWith("yande") ? "yande" : name is "konachan-g" or "konachan.net" ? "konachan-g" : "konachan", id);
    }

    public static async Task<string> RepairAsync(string path, Settings settings, IProgress<string> progress, CancellationToken token)
    {
        var source = Identify(path) ?? throw new InvalidOperationException("文件名无法识别站点和图片 ID。");
        var site = settings.SiteManager.Sites.FirstOrDefault(s => s.ShortName == source.Site && s is YandeSite or KonachanSite)
            ?? throw new InvalidOperationException("未找到对应站点配置。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        token = timeout.Token;
        progress?.Report("正在查询图片下载地址…");
        var query = new SearchPara { Site = site, Keyword = "id:" + source.Id, PageIndex = 1, CountLimit = 1 };
        // Site parsers construct MoeItems, which subscribe to their search session.
        // Keep this session private so repairs do not change browsing state or history.
        _ = new SearchSession(query);
        var page = await site.GetRealPageAsync(query, token);
        var item = page?.SingleOrDefault(p => p.Id == source.Id) ?? throw new InvalidOperationException("图片不存在或当前账号无法访问。");
        var json = JObject.Parse(item.OriginString);
        // Always restore the site's original, independent of the old file's format.
        var url = (string)json["file_url"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != "https")
            throw new InvalidOperationException("网站未提供有效的原图下载地址。");
        var target = Path.ChangeExtension(path, Path.GetExtension(downloadUri.AbsolutePath).ToLowerInvariant());
        var expectedSize = (long?)json["file_size"];
        var md5 = (string)json["md5"];
        var net = site.GetCloneNet(item.DetailUrl, 900);
        using var client = net.Client;
        net.ReceiveProgressHandler.ProgressChanged += value => progress?.Report($"正在重新下载：{value}%");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        return await SaveResponseAsync(path, response, expectedSize, md5, token, target);
    }

    public static async Task<string> SaveResponseAsync(string path, HttpResponseMessage response, long? expectedSize, string md5, CancellationToken token, string target = null)
    {
        path = Path.GetFullPath(path);
        target = Path.GetFullPath(target ?? path);
        if (!Path.GetDirectoryName(target).Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileNameWithoutExtension(target).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("修复仅允许调整原文件扩展名。");
        var rename = !target.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (Path.GetExtension(target).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("不支持服务器返回的图片格式。");
        if (rename && File.Exists(target)) throw new IOException("实际格式对应的同名文件已存在，未覆盖：" + target);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidOperationException("不支持此图片格式。");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理符号链接或重解析目录。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("网站返回了网页，旧文件已保留。");
        // Hold a read handle throughout the transfer to prevent concurrent writes.
        using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (ImageIntegrityScanner.CheckFile(path, token)?.Kind != "疑似不完整")
            throw new IOException("文件已完整或无法确认异常，请重新检查。");
        var oldHash = await SHA256.HashDataAsync(original, token);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".repair-" + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await response.Content.CopyToAsync(output, token);
            var size = new FileInfo(temporary).Length;
            if ((expectedSize > 0 && size != expectedSize) || (response.Content.Headers.ContentLength is long length && size != length))
                throw new InvalidDataException("下载大小不符，旧文件已保留。");
            if (ImageIntegrityScanner.CheckFile(temporary, token) != null) throw new InvalidDataException("新文件未通过完整性检查，旧文件已保留。");
            if (!string.IsNullOrWhiteSpace(md5))
            {
                await using var downloaded = File.OpenRead(temporary);
                if (!Convert.ToHexString(await MD5.HashDataAsync(downloaded, token)).Equals(md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("新文件 MD5 不符，旧文件已保留。");
            }
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!(await SHA256.HashDataAsync(current, token)).SequenceEqual(oldHash)) throw new IOException("原文件已变化，请重新检查。");
            token.ThrowIfCancellationRequested();
            // Atomic replacement: if the filesystem cannot replace safely, keep the original.
            if (rename)
            {
                File.Move(temporary, target, false);
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("新图片已校验并保存，但旧文件未能删除：" + target, ex);
                }
            }
            else File.Replace(temporary, path, null);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
