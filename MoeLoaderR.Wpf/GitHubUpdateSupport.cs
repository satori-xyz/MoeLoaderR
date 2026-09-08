using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public static class GitHubUpdateSupport
{
    public static HttpClient CreateClient(Settings settings)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (settings.ProxyMode == Settings.ProxyModeEnum.None) handler.UseProxy = false;
        else if (settings.ProxyMode == Settings.ProxyModeEnum.Custom)
        {
            var scheme = settings.ProxyConnectMode == Settings.ProxyConnectModeEnum.Socks ? "socks5" : "http";
            handler.Proxy = new WebProxy($"{scheme}://{settings.ProxySetting}");
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

public sealed record UpdateJob(int ParentId, long ParentStarted, string Target, string Payload, string Sha256);

public static class UpdateInstaller
{
    public static string JobsDirectory => Path.Combine(App.AppDataDir, "Updates");

    public static string CreateDownloadPath()
    {
        var folder = Path.Combine(JobsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "MoeLoaderR.download");
    }

    public static void Start(string payload, string hash)
    {
        var target = Environment.ProcessPath;
        if (!Path.GetFileName(target).Equals("MoeLoaderR.exe", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.ChangeExtension(target, ".deps.json")))
            throw new InvalidOperationException("请从发布的单 exe 程序中执行安装更新。");
        var directory = Path.GetDirectoryName(payload);
        var helper = Path.Combine(directory, "MoeLoaderR.Updater.exe");
        File.Copy(target, helper, true);
        using var current = Process.GetCurrentProcess();
        var job = new UpdateJob(current.Id, current.StartTime.ToUniversalTime().Ticks, target, payload, hash);
        var manifest = Path.Combine(directory, "update.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(job));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(manifest);
        using var process = Process.Start(start);
        if (process == null) throw new IOException("无法启动更新程序。");
    }

    public static async Task ApplyAsync(string manifest)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifest));
        var root = Path.GetFullPath(JobsDirectory) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(Environment.ProcessPath) != folder)
            throw new InvalidDataException("更新任务路径无效。");
        var job = JsonSerializer.Deserialize<UpdateJob>(await File.ReadAllTextAsync(manifest));
        if (Path.GetDirectoryName(Path.GetFullPath(job.Payload)) != folder || !Path.IsPathFullyQualified(job.Target)
            || !Path.GetFileName(job.Target).Equals("MoeLoaderR.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新目标路径无效。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process parent = null;
        try { parent = Process.GetProcessById(job.ParentId); } catch (ArgumentException) { }
        if (parent != null)
        {
            using (parent)
                if (parent.StartTime.ToUniversalTime().Ticks == job.ParentStarted) await parent.WaitForExitAsync(timeout.Token);
        }
        var incoming = job.Target + ".incoming";
        var backup = job.Target + ".previous";
        try
        {
            File.Copy(job.Payload, incoming, true);
            await VerifyAndReplaceAsync(incoming, job.Target, backup, job.Sha256);
            try
            {
                using var started = Process.Start(new ProcessStartInfo(job.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(job.Target) });
                if (started == null) throw new IOException("无法启动新版程序。");
            }
            catch
            {
                File.Copy(backup, job.Target, true);
                throw;
            }
            File.Delete(job.Payload);
        }
        finally { if (File.Exists(incoming)) File.Delete(incoming); }
    }

    public static async Task VerifyAndReplaceAsync(string incoming, string target, string backup, string expectedHash)
    {
        await using (var stream = File.OpenRead(incoming))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装前校验失败，原程序保持不变。");
        }
        // Incoming and backup are siblings of the executable, including when it is on another drive.
        File.Replace(incoming, target, backup, true);
    }
}
