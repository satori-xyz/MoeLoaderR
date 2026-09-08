using MoeLoaderR.Core;
using MoeLoaderR.Core.Sites;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MoeLoaderR.Wpf.ControlParts;

/// <summary>
/// 图片列表浏览器
/// </summary>
public partial class MoeExplorerControl
{
    private bool _schedulingImages;
    private bool _resettingImages;
    public PreviewWindow PreviewWindowInstance { get; set; }
    public Settings Settings { get; set; }
    public event Action<MoeItem, ImageSource> ImageItemDownloadButtonClicked;
    public event Action<MoeItem, ImageSource> MoeItemPreviewButtonClicked; 

    public MoeItemControl MouseOnImageControl { get; set; }
    public ObservableCollection<ThumbnailEntry> SelectedImageControls => ImageItemsWrapPanel.SelectedEntries;
    public Storyboard SearchStartSb => this.Sb("SearchStartSb");
    public Storyboard SearchingSb => this.Sb("SearchingSb");
    public Storyboard ShowSb => this.Sb("ShowSb");
    public MoeExplorerControl()
    {
        InitializeComponent();            
    }

    public void Init(Settings settings)
    {
        Settings = settings;
        KeyDown += OnKeyDown;
        ImageItemsScrollViewer.MouseRightButtonUp += ImageItemsScrollViewerOnMouseRightButtonUp;
        
        MoeItemPreviewButtonClicked += OnMoeItemPreviewButtonClicked;
        DownloadSelectedImagesButton.Click += DownloadSelectedImagesButtonOnClick;
        ImageItemDownloadButtonClicked += OnImageItemDownloadButtonClicked;
        
        MoeContextMenu.InitContextMenu(ImageItemsWrapPanel,ContextMenuPopup,SelectedImageControls); 
        MoeContextMenu.SearchByAuthorIdAction += SearchByAuthorId;
        InitPaging();

        new ChooseBoxHelper().InitSelectBox(null, ChooseBox, ChooseCanvasRoot, ImageItemsScrollViewer, ImageItemsWrapPanel);

        SelectedImageControls.CollectionChanged += SelectedImageControlsOnCollectionChanged;

        Settings.PropertyChanged += SettingsOnPropertyChanged;
        DownloadTypeComboBox.SelectionChanged += DownloadTypeComboBoxOnSelectionChanged;
        
        DownloadOperationGrid.Visibility = Visibility.Collapsed;
        ImageLoadingPool.CollectionChanged += ImageLoadingPoolOnCollectionChanged;
        ImageItemsWrapPanel.Attach(ImageItemsScrollViewer);
        ImageItemsWrapPanel.SetBinding(VirtualThumbnailPanel.ItemSizeProperty,
            new System.Windows.Data.Binding(nameof(Settings.ImageItemControlSize)) { Source = Settings });
        ImageItemsWrapPanel.CreateControl = CreateThumbnailControl;
        ImageItemsWrapPanel.LoadControl = ctrl => ImageLoadingPool.Add(ctrl);
        ImageItemsWrapPanel.ReleaseControl = ctrl =>
        {
            if (MouseOnImageControl == ctrl) MouseOnImageControl = null;
            ImageLoadingPool.Remove(ctrl);
        };
    }

    private void ImageLoadingPoolOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (_schedulingImages || _resettingImages) return;
        _schedulingImages = true;
        try
        {
            foreach (var image in ImageLoadingPool.ToArray())
            {
                if (image.LoadingState != MoeItemControl.LoadingStateEnum.Waiting || !ImageLoadingPool.Contains(image)) continue;
                if (ImageLoadingPool.Count(img => img.LoadingState == MoeItemControl.LoadingStateEnum.Loading) >= Settings.MaxOnLoadingImageCount) break;
                _ = image.TryLoad();
            }
        }
        finally { _schedulingImages = false; }
    }

    public void DownloadTypeComboBoxOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Settings.CurrentSession.CurrentDownloadType = DownloadTypeComboBox.SelectedItem as DownloadType;
        Settings.CurrentSession.OnPropertyChanged(nameof(SearchSession.CurrentDownloadType));
    }

    private void SettingsOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.CurrentSession))
        {
            InitSession(Settings.CurrentSession);
            ResetVisualPageDisplay();
        }
    }

    public void InitSession(SearchSession session)
    {
        PagingStackPanel.Children.Clear();
        CurrentDisplayIndex = 0;
        Settings.CurrentSession = session;
        session.VisualPages.AddEvent += AddPageButton;
    }
    
    public string GetCountString(int? i)
    {
        if (i == null) return "?";
        return i.ToString();
    }

    private void OnImageItemDownloadButtonClicked(MoeItem item, ImageSource imgSource)
    {
        if(Application.Current.MainWindow is not MainWindow mw) return;
        mw.MoeDownloaderControl.Downloader.AddDownload(item, imgSource);
        var lb = mw.MoeDownloaderControl.DownloadItemsListBox;
        var ctrl = lb.Items[^1];
        lb.ScrollIntoView(ctrl);
        if (mw.DownloaderMenuCheckBox.IsChecked != false) return;
        mw.DownloaderMenuCheckBox.IsChecked = true;
    }

    private void SearchByAuthorId(MoeSite site, string arg2)
    {
        if (Application.Current.MainWindow is not MainWindow mw ) return;
            
        Settings.SiteManager.CurrentSelectedSite = site;
        mw.SearchControl.MoeSitesLv2ComboBox.SelectedIndex = 1;
        mw.SearchControl.KeywordTextBox.Text = arg2;
        mw.SearchControl.SearchButtonOnClick(null, null);
    }

    private async void DownloadSelectedImagesButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;
        var selected = SelectedImageControls.ToArray();
        DownloadSelectedImagesButton.IsEnabled = false;
        try
        {
            var count = 0;
            foreach (var ctrl in selected)
            {
                var img = ctrl.MoeItem;
                if (!await EnsureDetailAsync(img) || img.DownloadUrlInfo?.Url == null) continue;
                mw.MoeDownloaderControl.Downloader.AddDownload(img, ctrl.Thumbnail);
                count++;
            }
            if (mw.DownloaderMenuCheckBox.IsChecked == false && count > 0) mw.DownloaderMenuCheckBox.IsChecked = true;
                
            foreach (var ct in selected)
            {
                ct.IsSelected = false;
            }

            var lb = mw.MoeDownloaderControl.DownloadItemsListBox;
            if (lb.Items.Count != 0)
            {
                lb.ScrollIntoView(lb.Items[^1]);
            }
        }
        finally { DownloadSelectedImagesButton.IsEnabled = true; }
    }

    private void OnMoeItemPreviewButtonClicked(MoeItem item, ImageSource imgSource)
    {
        PreviewWindow.Show(PreviewWindowInstance, Application.Current.MainWindow, item, imgSource);
    }
    
        
    private void SelectedImageControlsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        DownloadOperationGrid.Visibility = SelectedImageControls.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ImageCountTextBlock.Text = $"已选择{SelectedImageControls.Count}张（组）图片";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.IsKeyDown(Key.LeftCtrl))
        {
            MoeContextMenu.ContextSelectAllButtonOnClick(null, null);
        }
    }
    private void ImageItemsScrollViewerOnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        MoeContextMenu.SpPanel.Children.Clear();
        MoeContextMenu.ContextMenuImageInfoStackPanel.Children.Clear();
        if (ImageItemsWrapPanel.Entries.Count != 0) ContextMenuPopup.IsOpen = true;
    }


    private void ItemCtrlOnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ContextMenuPopup.IsOpen = true;
        if (sender is not MoeItemControl ctrl) return;
        MoeContextMenu.LoadExtFunc(ctrl.MoeItem);
        MoeContextMenu.LoadImgInfo(ctrl.MoeItem);
        e.Handled = true;
    }


    public void ResetVisualPageDisplay()
    {
        WelcomeBorder.Visibility = Visibility.Visible;
        MouseOnImageControl = null;
        _resettingImages = true;
        try
        {
            ImageItemsWrapPanel.ClearItems();
            ImageLoadingPool.Clear();
        }
        finally { _resettingImages = false; }
        ImageItemsScrollViewer.ScrollToTop();
    }


    public void SearchStartedVisual()
    {
        WelcomeBorder.Visibility = Visibility.Collapsed;
        SearchStartSb.Begin();
        SearchingSb.Begin();


    }

    public async void SearchStopVisual()
    {

        ShowSb.Begin();
        await Task.Delay(TimeSpan.FromSeconds(2));
        SearchingSb.Stop();

    }


    public ObservableCollection<MoeItemControl> ImageLoadingPool { get; set; } = new();
    

    public Task ShowVisualPage(SearchedVisualPage page)
    {
        DownloadOperationGrid.Visibility = Visibility.Collapsed;
        NextButtonControl.Visibility = Visibility.Visible;
        CurrentDisplayIndex = page.VisualIndex;
        foreach (PagingButtonControl pageButton in PagingStackPanel.Children)
            pageButton.VisualPage.IsCurrentPage = pageButton.VisualPage == page;
        JumpPageTextBox.Text = page.FirstRealPageIndex.ToString();
        if (PagingStackPanel.Children[page.VisualIndex - 1] is PagingButtonControl button)
        {
            button.VisualPage.IsCurrentPage = true;
            button.BringIntoView();
        }
        page.LoadStart();
        ResetVisualPageDisplay();
        ImageItemsWrapPanel.SetItems(page.RealPages.SelectMany(p => p).Where(img => !img.IsLocalFilter));
        WelcomeBorder.Visibility = Visibility.Collapsed;
        page.LoadEnd();
        return Task.CompletedTask;
    }

    private MoeItemControl CreateThumbnailControl(ThumbnailEntry entry)
    {
        var ctrl = new MoeItemControl(Settings, entry.MoeItem);
        ctrl.DownloadButton.Click += delegate { ImageItemDownloadButtonClicked?.Invoke(ctrl.MoeItem, ctrl.PreviewImage.Source); };
        ctrl.PreviewButton.Click += delegate { MoeItemPreviewButtonClicked?.Invoke(ctrl.MoeItem, ctrl.PreviewImage.Source); };
        ctrl.MouseEnter += delegate { MouseOnImageControl = ctrl; };
        ctrl.MouseRightButtonUp += ItemCtrlOnMouseRightButtonUp;
        ctrl.ImageLoadingStateChangedEvent += control =>
        {
            if (control.LoadingState == MoeItemControl.LoadingStateEnum.Loaded)
            {
                entry.NeedsRefresh = control.RefreshButton.Visibility == Visibility.Visible;
                ImageLoadingPool.Remove(control);
            }
        };
        return ctrl;
    }

    private static async Task<bool> EnsureDetailAsync(MoeItem item)
    {
        try
        {
            if (item.CanDownload) return true;
            item.ErrorMessage = null;
            await item.TryGetDetail(default);
            if (string.IsNullOrEmpty(item.ErrorMessage)) return true;
            Ex.ShowMessage("图片详情加载失败，请重试");
            return false;
        }
        catch (Exception e) { Ex.Log(e); Ex.ShowMessage("图片详情加载失败，请重试"); return false; }
    }
    #region 页码相关
    public int CurrentDisplayIndex { get; set; }
    public PageButtonTipDataList DataGridTipSource { get; set; } = new();
    private void InitPaging()
    {
        JumpPageButton.Click += async (_, _) => await JumpToPageAsync();
        JumpPageTextBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await JumpToPageAsync();
        };
        NextButtonControl.PageButton.Click += NextPageButtonOnClick;
        NextButtonControl.SetNextPageButton();
        NextButtonControl.Visibility = Visibility.Collapsed;
        PageTipDataGrid.ItemsSource = DataGridTipSource;
        this.GoState(nameof(NoNextPageState), nameof(NoSelectedItemState));
    }

    private async Task JumpToPageAsync()
    {
        if (!JumpPageButton.IsEnabled) return;
        if (!int.TryParse(JumpPageTextBox.Text, out var target) || target < 1 || target > 99999)
        {
            Ex.ShowMessage("请输入 1～99999 的页码");
            return;
        }
        var session = Settings.CurrentSession;
        if (session == null) { Ex.ShowMessage("请先搜索图片"); return; }
        if (session.IsSearching) { Ex.ShowMessage("请等待当前搜索完成"); return; }
        var cached = session.VisualPages.FirstOrDefault(p => p.FirstRealPageIndex == target ||
            p.RealPages.Any(real => real.CurrentPageNum == target));
        if (cached != null) { await ShowVisualPage(cached); return; }
        var para = session.FirstSearchPara.Clone();
        if (para.Config.IsSupportSearchByImageLastId || !string.IsNullOrEmpty(para.PageIndexCursor))
        {
            Ex.ShowMessage("当前搜索模式不支持直接跳页，请使用下一页");
            return;
        }
        if (Application.Current.MainWindow is not MainWindow window) return;
        para.PageIndex = target;
        para.PageIndexCursor = null;
        JumpPageButton.IsEnabled = false;
        try { await window.SearchControl.StartSearch(para); }
        catch (Exception e) { window.SearchControl.SetSearchVisual(false); Ex.Log(e); Ex.ShowMessage("跳转失败，请重试"); }
        finally { JumpPageButton.IsEnabled = true; }
    }

    private async void NextPageButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (Settings.CurrentSession.VisualPages.LastOrDefault()?.IsSearchComplete == true)
        {
            return;
        }
        foreach (PagingButtonControl control in PagingStackPanel.Children)
        {
            control.VisualPage.IsCurrentPage = false;
        }
        var vp = await Settings.CurrentSession.SearchNextVisualPage();
        var offset = PagingStackPanel.Children[^1].TranslatePoint(new Point(0, 0), PagingStackPanel).X;
        PagingScrollViewer.ScrollToHorizontalOffset(offset);
        _ = ShowVisualPage(vp);
    }

    public void AddPageButton(SearchedVisualPage page)
    {
        var button = new PagingButtonControl();
        button.Init(page, 44, page.FirstRealPageIndex);
        button.PageButton.Click += delegate
        {
            if (button.VisualPage.VisualIndex == CurrentDisplayIndex) return;
            foreach (PagingButtonControl c in PagingStackPanel.Children)
            {
                c.VisualPage.IsCurrentPage = false;
            }
            _ = ShowVisualPage(button.VisualPage);
        };
        button.MouseEnter += delegate
        {
            PageTipPopup.IsOpen = true;
            foreach (var p in button.VisualPage.RealPages)
            {
                var tb = new TextBlock
                {
                    Foreground = Brushes.Black,
                    Text = p.GetTipString()
                };
                PageTipRootStackPanel.Children.Add(tb);
                var idmax = 0;
                var idmin = 0;
                if (p.Count > 1)
                {
                    idmax = p.Max(img => img.Id);
                    idmin = p.Min(img => img.Id);
                }
                DataGridTipSource.Add(new PageButtonTipData
                {
                    CurrentPageNum = $"{GetCountString(p.CurrentPageNum)}/{GetCountString(p.TotalPageCount)}",
                    CurrentPagePicCount = $"{GetCountString(p.CurrentPageItemsOutputCount)}/{GetCountString(p.CurrentPageItemsOriginCount)}",
                    CurrentPagePicNumRange = $"{GetCountString(p.CurrentPageItemsStartNum)}~{GetCountString(p.CurrentPageItemsEndNum)}",
                    CurrentPagePicIdRange = $"{idmin}~{idmax}",
                });
            }

        };
        button.MouseLeave += delegate
        {
            PageTipPopup.IsOpen = false;
            PageTipRootStackPanel.Children.Clear();
            DataGridTipSource.Clear();
        };

        PagingStackPanel.Children.Add(button);
    }

    #endregion
    
}
