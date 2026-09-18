using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoeLoaderR.Core.Sites;

namespace MoeLoaderR.Core;

/// <summary>
///     一次搜索会话
/// </summary>
public class SearchSession : BindingObject
{
    public SearchSession(SearchPara para)
    {
        Settings = para.Site.Settings;
        FirstSearchPara = para;
        Name = $"{para.Site.DisplayName} - {para.Keyword}";
        
        foreach (var downloadType in para.Site.DownloadTypes)
        {
            ResultDownloadTypes.Add(downloadType);
        }
        ResultDownloadTypes.AddAuto();
        para.Session = this;
    }

    public string Name { get; set; }
    
    

    public Settings Settings { get; set; }

    public SearchPara FirstSearchPara { get; set; }
    
    public SearchedVisualPages VisualPages { get; set; } = new();

    public DownloadTypes ResultDownloadTypes { get; set; } = new ();

    public DownloadType CurrentDownloadType { get; set; }

    public bool IsSearching => SearchingTasksCtsList.Count > 0;
    public ObservableCollection<CancellationTokenSource> SearchingTasksCtsList { get; set; } = new();

    public void SaveKeywords()
    {
        var history = FirstSearchPara.Site.SiteSettings.History;
        var keyword = FirstSearchPara.Keyword;
        var keys = FirstSearchPara.MultiKeywords;
        if (!keyword.IsEmpty())
        {
            if (history.Count > Settings.HistoryKeywordsMaxCount)
            {
                history.RemoveAt(Settings.HistoryKeywordsMaxCount - 1);
            }

            var b = false;
            for (var i = 0; i < history.Count; i++)
            {
                if (history[i].Word != keyword) continue;
                history.Move(i, 0);
                b = true;
                break;
            }
            if(!b) history.Insert(0, new AutoHintItem {IsHistory = true, Word = FirstSearchPara.Keyword});
        }
        else if (keys?.Count > 0)
        {
            foreach (var key in keys)
            {
                if (history.Count > Settings.HistoryKeywordsMaxCount)
                    history.RemoveAt(Settings.HistoryKeywordsMaxCount - 1);
                history.Insert(0, new AutoHintItem {IsHistory = true, Word = key});
            }
        }
    }


    private int _loadingVisualPage;

    public async Task<SearchedVisualPage> SearchNextVisualPage()
    {
        if (Interlocked.CompareExchange(ref _loadingVisualPage, 1, 0) != 0) return null;
        SearchedVisualPage vp = null;
        try
        {
            var last = VisualPages.LastOrDefault();
            if (last?.IsSearchComplete == true) return null;
            var lastReal = last?.RealPages.LastOrDefault();
            var retry = last != null && (lastReal == null || lastReal.SearchException != null);
            SearchPara para;
            if (retry)
            {
                vp = last;
                para = lastReal?.Para;
                if (lastReal != null) vp.RealPages.Remove(lastReal);
                para ??= VisualPages.SelectMany(page => page.RealPages).LastOrDefault()?.NextPagePara;
                if (para == null)
                {
                    para = FirstSearchPara.Clone();
                    para.PageIndex = vp.FirstRealPageIndex > 0 ? vp.FirstRealPageIndex : FirstSearchPara.PageIndex;
                }
            }
            else
            {
                para = lastReal?.NextPagePara ?? FirstSearchPara.Clone();
                vp = new SearchedVisualPage { FirstRealPageIndex = para.PageIndex ?? (lastReal?.CurrentPageNumFromOne ?? 0) + 1 };
                VisualPages.Add(vp);
            }
            vp.LoadStart();
            while (true)
            {
                var rp = await TryGetRealPage(para);
                vp.RealPages.Add(rp);
                if (rp.SearchException != null)
                {
                    Ex.ShowMessage($"搜索中断:{rp.SearchException.Message}，可点击下一页重试", rp.SearchException.StackTrace);
                    break;
                }
                if (rp.HasNextPage == false)
                {
                    vp.IsSearchComplete = true;
                    break;
                }
                if (vp.RealPages.Sum(page => page.Count - page.FilterCount) >= para.CountLimit) break;
                para = rp.NextPagePara;
            }
            return vp;
        }
        finally
        {
            try { vp?.GetEnd(); }
            finally { Interlocked.Exchange(ref _loadingVisualPage, 0); }
        }
    }
    public async Task<SearchedPage> TryGetRealPage(SearchPara para)
    {
        using var cts = new CancellationTokenSource();
        SearchingTasksCtsList.Add(cts);
        SearchedPage rp;

        try
        {
            rp = await para.Site.GetRealPageAsync(para, cts.Token);
            if (rp is null)
            {
                rp = new SearchedPage();
                rp.SearchException = new Exception("搜索不到图片");
                rp.HasNextPage = false;
            }
        }
        catch (Exception e)
        {

            rp = new SearchedPage();
            rp.SearchException = e;
            rp.HasNextPage = false;
        }

        finally { SearchingTasksCtsList.Remove(cts); }

        rp.Para = para;
        if (rp.Count == 0) rp.HasNextPage = false;

        if (rp.HasNextPage != false) rp.GenNextPagePara();

        if (rp.SearchException == null) RealPageCount++;
        // page info
        rp.CurrentPageNumFromOne ??= RealPageCount;
        //if(para.Config.IsSupportSearchByImageLastId)
        rp.CurrentPageNum = rp.Para.PageIndex;
        rp.CurrentPageItemsStartNum ??= RealPageImageCount + 1;
        
        RealPageImageCount += rp.Count;
        rp.CurrentPageItemsEndNum = rp.CurrentPageItemsStartNum + rp.Count - 1;
        rp.CurrentPageItemsOutputCount = rp.Count(item=> item.IsLocalFilter == false);
        var mes =
            $"第{rp.CurrentPageNum}页获取到图片{rp.CurrentPageItemsOriginCount}张，条件过滤{rp.CurrentPageItemsOriginCount - rp.CurrentPageItemsOutputCount}张";
        Ex.LogListOriginalString = rp.OriginString?.ToString();
        Ex.ShowMessage(mes, pos: Ex.MessagePos.Searching);
        return rp;
    }
    public int RealPageCount { get; set; }
    public int RealPageImageCount { get; set; }
    public async Task StopSearch()
    {
        foreach (var cts in SearchingTasksCtsList.ToArray()) await cts.CancelAsync();

        while (true)
        {
            if (SearchingTasksCtsList.Count == 0) break;
            await Task.Delay(500);
        }
    }

    public string GetCurrentSearchStateText()
    {
        var para = FirstSearchPara;
        var site = FirstSearchPara.Site;
        var sb = $"当前搜索：{site.DisplayName}";
        if (site.Lv2Cat?.Count > 0 && para.Lv2MenuIndex > -1)
        {
            var lv2 = site.Lv2Cat?[para.Lv2MenuIndex];
            if (lv2 != null)
            {
                sb += $"→{lv2.Name}";

                if (lv2.SubCategories?.Count > 0 && para.Lv3MenuIndex > -1)
                {
                    var lv3 = lv2.SubCategories?[para.Lv3MenuIndex];
                    if (lv3 != null)
                    {
                        sb += $"→{lv3.Name}";
                        if (lv3.SubCategories?.Count > 0 && para.Lv4MenuIndex > -1)
                        {
                            var lv4 = lv3.SubCategories?[para.Lv4MenuIndex];
                            if (lv4 != null) sb += $"→{lv4.Name}";
                        }
                    }
                }
            }
        }

        if (!para.Keyword.IsEmpty()) sb += $"→\"{para.Keyword}\"";
        return sb;
    }
}