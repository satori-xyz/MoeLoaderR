using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MoeLoaderR.Core.Sites;

/// <summary>
///     yande.re fixed 2021.1.1
/// </summary>
public class YandeSite : BooruSite
{
    public override string HomeUrl => "https://yande.re";
    public override string DisplayName => "Yande";
    public override string ShortName => "yande";

    public override NetOperator GetCloneNet(string referer = null, double timeout = 40)
    {
        // LoginWindow clears Net after updating LoginCookies. Downloads may be
        // the first request afterwards, before another search calls Login().
        if (Net == null) Login();
        return base.GetCloneNet(referer, timeout);
    }

    public YandeSite()
    {
        Config.IsSupportAccount = true;
        LoginPageUrl = "https://yande.re/user/login";
    }
    public override bool VerifyCookie(CookieCollection ccol)
    {
        return ccol.Any(cookie => cookie.Name.Equals("user_id", StringComparison.OrdinalIgnoreCase));
    }
    public override string GetHintQuery(SearchPara para)
    {
        var pairs = new Pairs
        {
            {"limit", "15"},
            {"order", "count"},
            {"name", $"{para.Keyword.ToEncodedUrl()}"}
        };
        return $"{HomeUrl}/tag.xml{pairs.ToPairsString()}";
    }

    public override string GetPageQuery(SearchPara para)
    {
        var r18 = "";
        if (!para.IsShowExplicit)
        {
            r18 = "%20rating%3As";

        }
        var pairs = new Pairs
        {
            {"page", $"{para.PageIndex}"},
            {"limit", $"{para.CountLimit}"},
            {"tags", $"{para.Keyword.ToEncodedUrl()}{r18}" }
        };

        pairs.Add("api_version", "2");
        pairs.Add("include_pools", "1");
        return $"{HomeUrl}/post.json{pairs.ToPairsString()}";
    }

    public override async Task<SearchedPage> GetRealPageAsync(SearchPara para, CancellationToken token)
    {
        if (Net == null) Login();
        var json = await Net.CloneWithCookie().GetStringAsync(GetPageQuery(para), token: token);
        token.ThrowIfCancellationRequested();
        return json == null ? null : ParsePage(json, para);
    }

    public SearchedPage ParsePage(string json, SearchPara para)
    {
        var root = JObject.Parse(json);
        // The API repeats pool metadata when several posts belong to the same pool.
        var pools = (root["pools"] as JArray ?? new JArray())
            .DistinctBy(p => (int)p["id"]).ToDictionary(p => (int)p["id"]);
        var memberships = (root["pool_posts"] as JArray ?? new JArray())
            .Where(p => (bool?)p["active"] != false).ToLookup(p => (int)p["post_id"]);
        var result = new SearchedPage();
        foreach (var post in (JArray)root["posts"])
        {
            var item = new MoeItem(this, para)
            {
                Id = (int)post["id"], Width = (int?)post["width"] ?? 0, Height = (int?)post["height"] ?? 0,
                Uploader = (string)post["author"], Source = (string)post["source"],
                IsNsfw = (string)post["rating"] != "s", Score = (double?)post["score"] ?? 0,
                Date = ((string)post["created_at"]).ToDateTime(), OriginString = post.ToString()
            };
            item.DetailUrl = GetDetailPageUrl(item);
            foreach (var tag in ((string)post["tags"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) item.Tags.Add(tag);
            item.Urls.Add(DownloadTypeEnum.Thumbnail, (string)post["preview_url"], HomeUrl);
            item.Urls.Add(DownloadTypeEnum.Medium, (string)post["sample_url"], HomeUrl);
            item.Urls.Add(DownloadTypeEnum.Large, (string)post["jpeg_url"], HomeUrl);
            item.Urls.Add(DownloadTypeEnum.Origin, (string)post["file_url"], item.DetailUrl, filesize: (ulong?)post["file_size"] ?? 0);
            item.Pools = memberships[item.Id].Where(m => pools.ContainsKey((int)m["pool_id"]))
                .DistinctBy(m => (int)m["pool_id"])
                .Select(m =>
                {
                    var pool = pools[(int)m["pool_id"]];
                    return new ImagePool((int)pool["id"], ((string)pool["name"] ?? "").Replace('_', ' '),
                        (string)m["sequence"], (int?)pool["post_count"] ?? 0, $"{HomeUrl}/pool/show/{pool["id"]}");
                }).ToArray();
            result.Add(item);
        }
        result.CurrentPageItemsStartNum = (para.PageIndex - 1) * para.CountLimit + 1;
        return result;
    }
}
