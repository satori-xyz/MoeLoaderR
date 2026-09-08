using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace MoeLoaderR.Core.Sites;

/// <summary>
///     自定义站点
/// </summary>
public class CustomSite : MoeSite
{
    public CustomSite(CustomSiteConfig config)
    {
        CustomConfig = config;
        Config = config.Config;
        LoginPageUrl = config.LoginUrl;


        DownloadTypes.Add("原图", DownloadTypeEnum.Origin);
        
        Lv2Cat = new Categories(Config);
        if (CustomConfig.Config.IsSupportAccount)
        {
            if (CustomConfig.LoginUrl == null) LoginPageUrl = config.HomeUrl;
        }
    }


    public override bool VerifyCookie(CookieCollection ccol)
    {
        return CustomConfig.CookieLoginAuthKey == null || ccol.Any(cookie => cookie.Name.Equals(CustomConfig.CookieLoginAuthKey, StringComparison.OrdinalIgnoreCase));
    }

    public override void AfterInit()
    {
        Net ??= new NetOperator(Settings, this);
        _ = AddCat();
    }

    

    public void Login()
    {
        Net ??= new NetOperator(Settings, this);
        if (SiteSettings.LoginCookies != null)
        {
            Net.SetCookie(SiteSettings.GetCookieContainer());
        }

        IsUserLogin = true;
    }

    public async Task AddCat()
    {
        if (CustomConfig.CustomLv2MenuItems?.Count > 0)
        //if (false)
        {
            CustomConfig.Categories.Clear();
            //CustomConfig.Categories.Add("加载中..",null,null);
            //CatChange();
            //CustomConfig.Categories.Clear();
            //Lv2Cat.Add("加载中..");
            //CatChange();
            foreach (var item in CustomConfig.CustomLv2MenuItems)
            {
                var net = Net.CloneWithCookie();
                var url = item.PageUrl ?? CustomConfig.HomeUrl;
                var html = await net.GetHtmlAsync(url);
                var nodes = html.DocumentNode.SelectNodes(item.Menus.Path);
                if(nodes == null)break;
                foreach (var node in nodes)
                {
                    var cat = new CustomCategory();

                    try
                    {
                        var name = node.GetValue(item.MenuTitleFromMenus);
                        var url2 = node.GetValue(item.MenuUrlFromMenus);
                        if($"{url2}".Equals(HomeUrl, StringComparison.OrdinalIgnoreCase)) continue;
                        cat.Name = name;
                        if(cat.Name.Equals("首页")) continue;
                        cat.FirstPageApi = $"{url2}{item.FirstApi}";
                        var follow = $"{url2}{item.FollowApi}";
                        if (item.FollowApiReplaceFrom != null)
                        {
                            follow = follow.Replace(item.FollowApiReplaceFrom, item.FollowApiReplaceTo);
                        }
                        cat.FollowUpPageApi = $"{follow}";
                        if(item.OverridePagePara !=null) cat.OverridePagePara = item.OverridePagePara;
                        CustomConfig.Categories.Add(cat);
                    }
                    catch (Exception e)
                    {
                        Ex.Log(e);
                    }
                }

            }
        }
        //Lv2Cat.Clear();

        foreach (var cat in CustomConfig.Categories) Lv2Cat.Add(cat.Name);
        CatChange();
    }

    public override string HomeUrl => CustomConfig.HomeUrl;
    public override string DisplayName => CustomConfig.DisplayName;
    public override string ShortName => CustomConfig.ShortName;

    public override Uri Icon => CustomConfig.SiteIconUrl != null
        ? new Uri(CustomConfig.SiteIconUrl)
        : new Uri("/MoeLoaderR;component/Assets/SiteIcon/default.png", UriKind.RelativeOrAbsolute);

    public CustomSiteConfig CustomConfig { get; set; }

    public override async Task<SearchedPage> GetRealPageAsync(SearchPara para, CancellationToken token)
    {
        if (!IsUserLogin) Login();
        var net = Net.CloneWithCookie();
        var cat = CustomConfig.Categories[para.Lv2MenuIndex];
        var api = para.PageIndex <= 1 ? cat.FirstPageApi : cat.FollowUpPageApi;
        if (!para.Keyword.IsEmpty() && cat.OverrideSearchApi == null)
        {
            api = CustomConfig.SearchApi.Replace("{keyword}", para.Keyword.ToEncodedUrl());
        }
        var rapi = api.Replace("{pagenum}", $"{para.PageIndex}").Replace("{pagenum-1}", $"{para.PageIndex - 1}");
        var html = await net.GetHtmlAsync(rapi, token: token);
        if (html == null) return null;
        var moes = new SearchedPage
        {
            OriginString = new StringBuilder(html.Text)
        };
        var pa = cat.OverridePagePara ?? CustomConfig.PagePara;
        var list = (HtmlNodeCollection) html.DocumentNode.GetValue(pa.MainPageImagesNodes);
        
        if (!(list?.Count > 0)) return moes;
        foreach (var item in list)
        {
            var moe = new MoeItem(this, para);
            var url = item.GetValue(pa.ImageItemThumbnailUrlFromMainPageSingleImageNode);
            if (url == null) continue;
            var refer = pa.ImageItemThumbnailUrlFromMainPageSingleImageNode.Referer;
            moe.Urls.Add(DownloadTypeEnum.Thumbnail, url, refer);
            moe.Title = item.GetValue(pa.ImageItemTitleFromSingleMainPageSingleImageNode);
            var detail = item.GetValue(pa.ImageItemDetailUrlFromMainPageSingleImageNode);
            moe.DetailUrl = detail;
            moe.GetDetailTaskFunc += t => GetDetail(moe.DetailUrl, moe, pa, true, t);
            if (pa.ImageItemDateTimeFromMainPageSingleImageNode != null)
            {
                var date = item.GetValue(pa.ImageItemDateTimeFromMainPageSingleImageNode);
                moe.DateString = $"{date}";
            }

            moe.OriginString = item.InnerHtml;
            moes.Add(moe);
        }

        return moes;
    }

    public async Task GetDetail(string url, MoeItem father, CustomPagePara pa, bool isFirst, CancellationToken token)
    {
        var items = await GetNewItems(url, father, pa, isFirst, token);
        

        foreach (var moeItem in items) father.ChildrenItems.Add(moeItem);
        if (isFirst)
        {
            var urlinfo = father.ChildrenItems[0]?.DownloadUrlInfo;
            if (father.ChildrenItems.Count > 0 && urlinfo != null) father.Urls.Add(urlinfo);
        }
    }

    public async Task<MoeItems> GetNewItems(string url, MoeItem father, CustomPagePara pa, bool isFirst,
        CancellationToken token)
    {
        var currentdir = url[..(url.LastIndexOf('/') + 1)];
        var newItems = new MoeItems();
        var net = Net.CloneWithCookie();
        var html = await net.GetHtmlAsync(url, token: token, showSearchMessage: false);
        if (html == null) return newItems;
        var root = html.DocumentNode;
        var imgOrImgs = root.GetValue(pa.DetailPageImagesNodes);
        if (pa.DetailPageImagesNodes.IsMultiValues)
        {
            var imgs = (HtmlNodeCollection) imgOrImgs;
            if (imgs?.Count > 0)
                foreach (var img in imgs)
                {
                    var newm = GetChildrenItem(img, pa, father);
                    newItems.Add(newm);
                }
            else return newItems;
        }
        else
        {
            var img = (HtmlNode) imgOrImgs;
            var newm = GetChildrenItem(img, pa, father);
            newItems.Add(newm);
        }


        var currentPageIndex = root.GetValue(pa.DetailCurrentPageIndex);
        var currentIndex = $"{currentPageIndex}".ToInt();
        var nextpageIndex = $"{root.GetValue(pa.DetailNextPageIndex)}".ToInt();
        //var maxPageIndex = $"{root.GetValue(pa.DetailMaxPageIndex)}".ToInt();
        if (isFirst)
        {
            var countStr = $"{root.GetValue(pa.DetailImagesCount)}";
            var imageCount = countStr.ToInt();
            if (imageCount != 0) father.ChildrenItemsCount = imageCount;
        }

        if (nextpageIndex == currentIndex + 1)
        {
            var nextUrl = root.GetValue(pa.DetailNextPageUrl);
            if (pa.DetailNextPageUrl.Pre == "currentDir")
            {
                nextUrl = $"{currentdir}{nextUrl}";
            }
            if (nextUrl != null)
            {
                var last = newItems.LastOrDefault();
                if (last != null)
                {
                    last.IsResolveAndDownloadNextItem = true;
                    last.GetNextItemsTaskFunc += async t => await GetNewItems(nextUrl, father, pa, false, t);
                }
            }
            
        }

        return newItems;
    }

    public MoeItem GetChildrenItem(HtmlNode img, CustomPagePara pa, MoeItem father)
    {
        var newMoeItem = new MoeItem(this, father.Para);
        if (pa.DetailImageItemOriginUrlFromDetailImagesList != null)
        {
            var imgurl = img.GetValue(pa.DetailImageItemOriginUrlFromDetailImagesList);
            if (imgurl != null)
                newMoeItem.Urls.Add(DownloadTypeEnum.Origin, imgurl, referer: pa.DetailImageItemOriginUrlFromDetailImagesList.Referer);
        }

        if (pa.DetailPageImageItemThumbnailUrlFromSingleDetailPageImageNodes != null)
        {
            var imgurl = img.GetValue(pa.DetailPageImageItemThumbnailUrlFromSingleDetailPageImageNodes);
            if (imgurl != null)
                newMoeItem.Urls.Add(DownloadTypeEnum.Thumbnail, imgurl,
                    referer: pa.DetailPageImageItemThumbnailUrlFromSingleDetailPageImageNodes.Referer);
        }

        if (pa.DetailImageItemDetailUrlFromDetailImagesList != null)
        {
            var imgurl = img.GetValue(pa.DetailImageItemDetailUrlFromDetailImagesList);
            if (imgurl != null)
            {
                newMoeItem.DetailUrl = $"{imgurl}";
                var url = new UrlInfo(DownloadTypeEnum.Origin, "",
                    resolveUrlFunc: (item, url, token) => GetDetailLv2(pa, item, url, token));

                newMoeItem.Urls.Add(url);
            }
        }

        return newMoeItem;
    }

    public async Task GetDetailLv2(CustomPagePara pa, MoeItem currentItem, UrlInfo urlinfo, CancellationToken token)
    {
        var url = currentItem.DetailUrl;
        var net = Net.CloneWithCookie();
        var html = await net.GetHtmlAsync(url, null, false, token);
        if (pa.DetailLv2ImageOriginUrl != null)
        {
            var originUrl = html.DocumentNode.GetValue(pa.DetailLv2ImageOriginUrl);
            urlinfo.Url = $"{originUrl}";
        }

        if (pa.DetailLv2ImagePreviewUrl != null)
        {
            var prevUrl = html.DocumentNode.GetValue(pa.DetailLv2ImagePreviewUrl);
            currentItem.Urls.Add(DownloadTypeEnum.Medium, $"{prevUrl}");
        }
    }
}