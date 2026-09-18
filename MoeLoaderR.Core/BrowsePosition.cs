using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoeLoaderR.Core.Sites;
using Newtonsoft.Json;

namespace MoeLoaderR.Core;

public sealed class BrowsePosition
{
    public int Page { get; set; }
    public int FirstImageId { get; set; }
    public string SearchJson { get; set; }
    public DateTime SavedAt { get; set; }
    public static string Key(MoeSite site) => site.HomeUrl.TrimEnd('/').ToLowerInvariant();

    public static BrowsePosition Capture(MoeItem first)
    {
        var p = first.Para;
        if (p.PageIndex == null || p.CountLimit < 1 || !string.IsNullOrEmpty(p.PageIndexCursor) || p.Config?.IsSupportSearchByImageLastId == true)
            throw new InvalidOperationException("当前搜索模式暂不支持记录页码位置。");
        return new BrowsePosition
        {
            Page = p.PageIndex.Value, FirstImageId = first.Id, SavedAt = DateTime.Now,
            SearchJson = JsonConvert.SerializeObject(new
            {
                p.Keyword, p.MultiKeywords, p.CountLimit, p.IsShowExplicit, p.IsShowExplicitOnly,
                p.IsFilterResolution, p.MinWidth, p.MinHeight, p.IsFileTypeShowSpecificOnly,
                p.Orientation, p.OrderBy, p.Date, p.Lv2MenuIndex, p.Lv3MenuIndex, p.Lv4MenuIndex
            })
        };
    }

    public SearchPara Restore(MoeSite site)
    {
        var p = JsonConvert.DeserializeObject<SearchPara>(SearchJson);
        p.Site = site;
        p.Config = site.Config;
        p.PageIndex = Page;
        if (p.CountLimit < 1 || Page < 1) throw new InvalidOperationException("保存的位置无效，请重新记录。");
        return p;
    }

    public async Task<(SearchSession Session, SearchedPage Page)> LocateAsync(MoeSite site, IProgress<int> progress, CancellationToken token)
    {
        var original = Restore(site);
        // Search forward for new uploads, and a few pages backward for deletions.
        var candidates = new[] { Page }.Concat(Enumerable.Range(1, 100)
            .SelectMany(offset => offset <= 10 && Page > offset ? new[] { Page + offset, Page - offset } : new[] { Page + offset }));
        foreach (var number in candidates)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(number);
            var p = original.Clone();
            p.PageIndex = number;
            // Each probe has its own session so rejected pages and image metadata can be collected.
            var session = new SearchSession(p);
            var result = await site.GetRealPageAsync(p, token);
            token.ThrowIfCancellationRequested();
            if (result == null) throw new InvalidOperationException("网络请求失败，位置记录已保留，请重试。");
            if (result.SearchException != null) throw result.SearchException;
            if (!result.Any(item => item.Id == FirstImageId)) continue;
            result.Para = p;
            result.CurrentPageNum = number;
            result.CurrentPageNumFromOne = number;
            result.GenNextPagePara();
            session.RealPageCount = number;
            session.RealPageImageCount = (number - 1) * p.CountLimit + result.Count;
            return (session, result);
        }
        throw new InvalidOperationException("附近页中未找到记录的图片，可能已删除或排序改变。原记录仍保留。");
    }
}
