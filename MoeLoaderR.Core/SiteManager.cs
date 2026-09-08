using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MoeLoaderR.Core.Sites;
using Newtonsoft.Json;

namespace MoeLoaderR.Core;

/// <summary>
///     站点管理器
/// </summary>
public class SiteManager : BindingObject
{
    private MoeSite _currentSelectedSite;

    public SiteManager(Settings settings)
    {
        Settings = settings;
        Sites = new MoeSites(settings);
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        RefreshSiteList();
    }

    public Settings Settings { get; set; }
    public MoeSites Sites { get; set; }

    public MoeSite CurrentSelectedSite
    {
        get => _currentSelectedSite;
        set => SetField(ref _currentSelectedSite, value, nameof(CurrentSelectedSite));
    }



    public void SetDefaultSiteList()
    {

        Sites.Add(new PixivSite());
        Sites.Add(new PixivR18Site());

        Sites.Add(new KonachanSite());
        Sites.Add(new KonachanNetSite());
        Sites.Add(new YandeSite());
        Sites.Add(new GelbooruSite());
        Sites.Add(new SankakuChanSite());
        //Sites.Add(new SankakuIdolSite());
        Sites.Add(new DanbooruSite());
        Sites.Add(new DeviantartSite());
        
        Sites.Add(new BehoimiSite());
        Sites.Add(new SafebooruSite());
        Sites.Add(new LolibooruSite());
        Sites.Add(new AtfbooruSite());
        Sites.Add(new Rule34Site());

        //Sites.Add(new KawaiinyanSite());
        Sites.Add(new MiniTokyoSite());
        Sites.Add(new EshuuSite());
        Sites.Add(new ZeroChanSite());
        Sites.Add(new AnimePicturesSite());
        Sites.Add(new WorldCosplaySite());
    }

    public async void SetCustomSitesFormJson(string dir)
    {
        var files = dir.GetDirFiles().Where(i => i.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file.FullName);
                var set = JsonConvert.DeserializeObject<CustomSiteConfig>(json);
                if (set == null) continue;
                Sites.Add(new CustomSite(set));
            }
            catch (Exception e)
            {
                Ex.Log($"读取{file.Name}失败");
                Ex.Log(e);

                if (Debugger.IsAttached) throw;
            }
        }
    }

    private void SettingsOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        //if (e.PropertyName == nameof(Settings.IsCustomSiteMode))
        //{
        //    Sites.Clear();
        //    if (Settings.IsCustomSiteMode) SetCustomSitesFormJson(Settings.CustomSitesDir);
        //    else SetDefaultSiteList();
        //}
    }

    public void RefreshSiteList()
    {
        SetDefaultSiteList();
        SetCustomSitesFormJson(Settings.CustomSitesDir);
    }
}