using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoeLoaderR.Core;

public sealed record UpdateRelease(string Tag, Version Version, Uri AssetUrl, long Size, string Sha256);

public sealed class GitHubUpdateClient(HttpClient client)
{
    public const string Repository = "satori-xyz/MoeLoaderR";
    public const string AssetName = "MoeLoaderR.exe";
    public const long MaximumAssetSize = 1024L * 1024 * 1024;

    public static Version ParseVersion(string tag)
    {
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('+')[0], out var version))
            throw new InvalidDataException("发布版本号无效，请使用 v0.1.0 这样的正式版本号。");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private static HttpRequestMessage Request(Uri url, string token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("MoeLoaderR-Updater/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (url.Host == "api.github.com")
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new("Bearer", token.Trim());
        }
        return request;
    }

    public async Task<UpdateRelease> CheckAsync(Version current, string token, CancellationToken cancellationToken)
    {
        using var request = Request(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), token, "application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("暂无正式发布，或当前账号无权访问此私有仓库。");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("GitHub 授权无效、权限不足或请求受限，请检查登录授权后重试。");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("更新源不是正式发布版本。");
        var tag = root.GetProperty("tag_name").GetString();
        var version = ParseVersion(tag);
        if (version <= current) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName || asset.GetProperty("state").GetString() != "uploaded") continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (size <= 0 || size > MaximumAssetSize || digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || digest.Length != 71 || !IsHex(digest[7..]))
                throw new InvalidDataException("更新文件缺少有效大小或 SHA-256 校验信息。");
            var id = asset.GetProperty("id").GetInt64();
            return new UpdateRelease(tag, version, new Uri($"https://api.github.com/repos/{Repository}/releases/assets/{id}"), size, digest[7..]);
        }
        throw new InvalidDataException($"此版本尚未上传 {AssetName}。");
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    public async Task DownloadAsync(UpdateRelease release, string token, string destination, IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) throw new IOException("更新缓存文件已存在，请重试。");
        using var request = Request(release.AssetUrl, token, "application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        HttpResponseMessage redirected = null;
        try
        {
            var payload = response;
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location == null || !location.IsAbsoluteUri || location.Scheme != "https" ||
                    !(location.Host == "github.com" || location.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("GitHub 下载重定向地址无效。");
                using var follow = Request(location, null, "application/octet-stream");
                redirected = await client.SendAsync(follow, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                payload = redirected;
            }
            payload.EnsureSuccessStatusCode();
            await using var input = await payload.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long total = 0;
                var last = -1;
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("更新文件大小与发布信息不一致。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    var percent = (int)(total * 100 / release.Size);
                    if (percent != last) { progress?.Report(percent); last = percent; }
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新文件校验失败，请重新下载。");
            }
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally { redirected?.Dispose(); }
    }
}
