using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MoeLoaderR.Core;

public static class PoolArchiveDownload
{
    public static async Task DownloadAsync(MoeItem item, CancellationToken token)
    {
        item.DlStatus = DownloadStatus.Downloading;
        try
        {
            var net = item.Site.GetCloneNet(item.PoolArchive.Url, 1800);
            using var client = net.Client;
            net.ReceiveProgressHandler.ProgressChanged += progress =>
            {
                item.Progress = Math.Min(99, progress);
                item.StatusText = $"正在下载 Pool：{item.Progress}%";
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await client.GetAsync($"{item.Site.HomeUrl}/pool/zip/{item.PoolArchive.Id}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await SaveResponseAsync(item, response, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (item.DlStatus != DownloadStatus.Cancel)
            {
                item.DlStatus = token.IsCancellationRequested ? DownloadStatus.Stop : DownloadStatus.Failed;
                item.StatusText = token.IsCancellationRequested ? "已停止" : "下载超时，请重试";
            }
        }
        catch (Exception ex)
        {
            if (item.DlStatus != DownloadStatus.Cancel)
            {
                item.DlStatus = DownloadStatus.Failed;
                item.StatusText = ex.Message;
            }
            Ex.Log("Pool 下载失败", ex);
        }
    }

    public static async Task SaveResponseAsync(MoeItem item, HttpResponseMessage response, CancellationToken token)
    {
        if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/user/login") == true || (int)response.StatusCode is 401 or 403)
            throw new InvalidOperationException("请先登录 Yande，再重试 Pool 下载。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("网站未返回 ZIP，请确认 Yande 登录状态后重试。");
        var disposition = response.Content.Headers.ContentDisposition;
        var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("服务器未提供有效的 ZIP 原始文件名。");
        item.OriginFileName = name;
        item.OriginFileNameWithoutExtension = Path.GetFileNameWithoutExtension(name);
        item.LocalFileShortNameWithoutExt = item.OriginFileNameWithoutExtension;
        item.LocalFileFullPath = Path.Combine(item.Site.Settings.ImageSavePath, name);
        if (File.Exists(item.LocalFileFullPath)) { Skip(item); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalFileFullPath)!);
        var temporary = item.LocalFileFullPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            // CopyToAsync uses the progress handler's streaming path; never buffer the archive in RAM.
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await response.Content.CopyToAsync(file, token);
            token.ThrowIfCancellationRequested();
            using (var file = File.OpenRead(temporary))
            {
                var signature = new byte[4];
                if (file.Read(signature) != 4 || signature[0] != 'P' || signature[1] != 'K' ||
                    !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6)))
                    throw new InvalidDataException("服务器返回的文件不是 ZIP，已取消保存。");
                if (response.Content.Headers.ContentLength is long length && file.Length != length)
                    throw new InvalidDataException("ZIP 下载不完整，请重试。");
                file.Position = 0;
                using var archive = new ZipArchive(file, ZipArchiveMode.Read, true);
                _ = archive.Entries.Count;
            }
            try { File.Move(temporary, item.LocalFileFullPath, false); }
            catch (IOException) when (File.Exists(item.LocalFileFullPath)) { Skip(item); return; }
            item.Progress = 100;
            item.StatusText = "Pool 下载完成";
            item.DlStatus = DownloadStatus.Success;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Skip(MoeItem item)
    {
        item.DlStatus = DownloadStatus.Skip;
        item.StatusText = "同名文件已存在，跳过";
    }
}
