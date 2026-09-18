using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public partial class MainWindow
{
    private CancellationTokenSource _positionSearch;

    private void InitBrowsePositions()
    {
        Settings.BrowsePositions ??= new();
        RecordPositionButton.Click += (_, _) => RecordBrowsePosition();
        ReturnPositionButton.Click += async (_, _) => await ReturnToBrowsePosition();
        Settings.SiteManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SiteManager.CurrentSelectedSite)) UpdatePositionStatus();
        };
        Closed += (_, _) => _positionSearch?.Cancel();
        UpdatePositionStatus();
    }

    private void UpdatePositionStatus()
    {
        var site = Settings.SiteManager.CurrentSelectedSite;
        PositionStatusText.Text = site != null && Settings.BrowsePositions.TryGetValue(BrowsePosition.Key(site), out var position)
            ? $"已记录第 {position.Page} 页\n图片 ID：{position.FirstImageId}" : "当前站点暂无记录";
    }

    private void RecordBrowsePosition()
    {
        try
        {
            if (Settings.CurrentSession?.IsSearching == true) throw new InvalidOperationException("请等待当前搜索完成。");
            var first = MoeExplorer.ImageItemsWrapPanel.Entries.FirstOrDefault()?.MoeItem;
            if (first == null) throw new InvalidOperationException("请先浏览图片再记录位置。");
            var site = Settings.SiteManager.CurrentSelectedSite;
            if (site == null || BrowsePosition.Key(site) != BrowsePosition.Key(first.Site))
                throw new InvalidOperationException("当前显示的图片属于其他站点，请先搜索当前站点。");
            var position = BrowsePosition.Capture(first);
            var key = BrowsePosition.Key(first.Site);
            Settings.BrowsePositions.TryGetValue(key, out var previous);
            Settings.BrowsePositions[key] = position;
            try { Settings.Save(App.SettingJsonFilePath); }
            catch
            {
                if (previous == null) Settings.BrowsePositions.Remove(key);
                else Settings.BrowsePositions[key] = previous;
                throw;
            }
            UpdatePositionStatus();
        }
        catch (Exception ex) { PositionStatusText.Text = ex.Message; Ex.Log(ex); }
    }

    private async Task ReturnToBrowsePosition()
    {
        if (_positionSearch != null) { _positionSearch.Cancel(); return; }
        var site = Settings.SiteManager.CurrentSelectedSite;
        if (site == null || !Settings.BrowsePositions.TryGetValue(BrowsePosition.Key(site), out var position))
        { PositionStatusText.Text = "当前站点暂无记录"; return; }
        if (Settings.CurrentSession?.IsSearching == true) { PositionStatusText.Text = "请等待当前搜索完成"; return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _positionSearch = cts;
        ReturnPositionLabel.Text = "取消查找";
        RecordPositionButton.IsEnabled = SearchControl.IsEnabled = MoeExplorer.IsEnabled = false;
        try
        {
            var result = await position.LocateAsync(site, new Progress<int>(page =>
            {
                if (_positionSearch == cts && !cts.IsCancellationRequested) PositionStatusText.Text = $"正在查找第 {page} 页…";
            }), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Settings.CurrentSession = result.Session;
            MoeExplorer.DownloadTypeComboBox.ItemsSource = result.Session.ResultDownloadTypes;
            MoeExplorer.DownloadTypeComboBox.SelectedIndex = 0;
            var page = new SearchedVisualPage { FirstRealPageIndex = result.Page.CurrentPageNum.Value, IsSearchComplete = result.Page.HasNextPage == false };
            page.RealPages.Add(result.Page);
            result.Session.VisualPages.Add(page);
            await MoeExplorer.ShowVisualPage(page);
            MoeExplorer.ImageItemsWrapPanel.ScrollToImage(position.FirstImageId);
            PositionStatusText.Text = $"已找回第 {page.FirstRealPageIndex} 页\n图片 ID：{position.FirstImageId}";
        }
        catch (OperationCanceledException) { PositionStatusText.Text = "查找已取消或超时\n原记录已保留"; }
        catch (Exception ex) { PositionStatusText.Text = ex.Message; Ex.Log(ex); }
        finally
        {
            _positionSearch = null;
            ReturnPositionLabel.Text = "回到位置";
            RecordPositionButton.IsEnabled = SearchControl.IsEnabled = MoeExplorer.IsEnabled = true;
        }
    }
}
