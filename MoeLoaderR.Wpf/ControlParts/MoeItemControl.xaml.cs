using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MoeLoaderR.Core;
using MoeLoaderR.Core.Sites;


namespace MoeLoaderR.Wpf.ControlParts;

/// <summary>
/// 缩略图面板中的图片用户控件
/// </summary>
public partial class MoeItemControl : IDisposable
{
    private LoadingStateEnum _loadingState = LoadingStateEnum.Waiting;
    private bool _disposed;
    private Popup _poolPopup;

    public enum LoadingStateEnum
    {
        Waiting, Loading,  Loaded
    }

    public LoadingStateEnum LoadingState
    {
        get => _loadingState;
        set
        {
            if(value == _loadingState) return;
            _loadingState = value;
            ImageLoadingStateChangedEvent?.Invoke(this);
        }
    }

    public MoeItem MoeItem { get; set; }

    public Settings Settings { get; set; }

    public event Action<MoeItemControl> ImageLoadingStateChangedEvent;

    public MoeItemControl(Settings settings, MoeItem item)
    {
        Settings = settings;
        MoeItem = item;
        DataContext = this;

        InitializeComponent();

        MouseEnter += delegate { VisualStateManager.GoToState(this, nameof(MouseOverState), true); };
        MouseLeave += delegate { VisualStateManager.GoToState(this, nameof(NormalState), true); };
        DetailPageLinkButton.Click += delegate { MoeItem.DetailUrl.GoUrl(); };
        RefreshButton.Click += RefreshButtonOnClick;
        PoolButton.Click += (_, _) => ShowPools();
        Unloaded += (_, _) => ClosePools();
        ImageCheckBox.Click += ImageCheckBoxOnClick;
        StarButton.Click += StarButtonOnClick;
        MoeItem.PropertyChanged += MoeItemOnPropertyChanged;
        MoeItem.Site.PropertyChanged += SiteOnPropertyChanged;
        InitVisual();
    }
        
    private void ClosePools()
    {
        if (_poolPopup == null) return;
        _poolPopup.IsOpen = false;
        _poolPopup.Child = null;
        _poolPopup = null;
    }

    private void ShowPools()
    {
        if (_disposed || MoeItem.Pools.Length == 0) return;
        if (_poolPopup?.IsOpen == true) { ClosePools(); return; }
        ClosePools();
        var content = new StackPanel();
        var accent = new SolidColorBrush(Color.FromRgb(79, 70, 217));
        foreach (var pool in MoeItem.Pools)
        {
            var group = new StackPanel { Margin = new Thickness(0, content.Children.Count == 0 ? 0 : 14, 0, 0) };
            group.Children.Add(new TextBlock { Text = pool.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(48, 53, 70)) });
            group.Children.Add(new TextBlock { Text = $"共 {pool.PostCount} 张 · 当前 #{pool.Sequence} · ID {pool.Id}", FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 5, 0, 8), TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var view = new Button { Content = "查看图集", Padding = new Thickness(10, 5, 10, 5), Foreground = accent };
            view.Click += (_, _) => { pool.Url.GoUrl(); ClosePools(); };
            var download = new Button { Content = "下载 ZIP", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0), Foreground = accent };
            download.Click += (_, _) =>
            {
                if (Application.Current.MainWindow is not MainWindow main) return;
                var task = main.MoeDownloaderControl.Downloader.AddPoolDownload(MoeItem, pool, null);
                main.DownloaderMenuCheckBox.IsChecked = true;
                main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => main.MoeDownloaderControl.DownloadItemsListBox.ScrollIntoView(task)));
                download.Content = "已加入下载";
            };
            actions.Children.Add(view);
            actions.Children.Add(download);
            group.Children.Add(actions);
            content.Children.Add(group);
        }
        _poolPopup = new Popup
        {
            PlacementTarget = PoolButton, Placement = PlacementMode.Right, HorizontalOffset = 6,
            StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Width = 310, Padding = new Thickness(14), Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(225, 229, 238)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Child = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content }
            }
        };
        _poolPopup.IsOpen = true;
    }

    private void SiteOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MoeSite.IsUserLogin))
        {
            if (MoeItem.Site.IsUserLogin)
            {
                StarButton.Visibility = MoeItem.Site.Config.IsSupportStarButton ? Visibility.Visible : Visibility.Collapsed;
                ThumbButton.Visibility = MoeItem.Site.Config.IsSupportThumbButton ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                StarButton.Visibility = Visibility.Collapsed;
                ThumbButton.Visibility = Visibility.Collapsed;
            }
                
        }
    }

    public void InitVisual()
    {
        OperationBorder.Opacity = 0;
        MoeItemOnPropertyChanged(this, new PropertyChangedEventArgs(nameof(MoeItem.IsFav)));
        SiteOnPropertyChanged(this, new PropertyChangedEventArgs(nameof(MoeSite.IsUserLogin)));
        if (MoeItem.ChildrenItemsCount > 1)
        {
            SetMultiPicVisual();
        }
    }

    public void SetMultiPicVisual()
    {
        ImageCheckBox.Margin = new Thickness(0, 0, 8, 8);
        MultiPicBgGrid.Visibility = Visibility.Visible;
    }

    private void MoeItemOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MoeItem.IsFav))
        {
            FavTextBlock.Foreground = MoeItem.IsFav ? Brushes.DeepPink : Brushes.White;
        }
        if(e.PropertyName ==  nameof(MoeItem.ChildrenItemsCount))
        {
            if (MoeItem.ChildrenItemsCount > 1)
            {
                SetMultiPicVisual();
            }
        }
    }

    private async void StarButtonOnClick(object sender, RoutedEventArgs e)
    {
        var b = await MoeItem.Site.StarAsync(MoeItem, default);
        if (b)
        {
            FavTextBlock.Foreground = Brushes.DeepPink;
            Ex.ShowMessage("收藏成功");
        }
        else
        {
            Ex.ShowMessage("收藏失败");
        }
    }


    private void ImageCheckBoxOnClick(object sender, RoutedEventArgs e)
    {
        var wnd = Application.Current.MainWindow;
        Ex.LogItemString = MoeItem.OriginString;
        if (!Keyboard.IsKeyDown(Key.LeftAlt))return;
        MessageWindow.ShowDialog("原始内容",MoeItem.OriginString,wnd,true);
    }

    private async void RefreshButtonOnClick(object sender, RoutedEventArgs e)
    {
        await TryLoad();
    }
        

    public CancellationTokenSource DetailAndImgLoadCts { get; set; }



    public void Show()
    {
        if (!_disposed) FadeIn(LayoutRoot);
    }

    // Share immutable timelines. Only active effects allocate animation clocks.
    private static readonly DoubleAnimation FadeAnimation = CreateAnimation(0, 1, 0.2, false);
    private static readonly DoubleAnimation SpinAnimation = CreateAnimation(0, 360, 1.8, true);

    private static DoubleAnimation CreateAnimation(double from, double to, double seconds, bool repeat)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            FillBehavior = FillBehavior.Stop,
            RepeatBehavior = repeat ? RepeatBehavior.Forever : new RepeatBehavior(1)
        };
        animation.Freeze();
        return animation;
    }

    private RotateTransform LoadingRotation => (RotateTransform)((TransformGroup)LoadingIcon.RenderTransform).Children[2];

    private void FadeIn(UIElement target)
    {
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = 1;
        var clock = FadeAnimation.CreateClock();
        clock.Completed += (_, _) => target.ApplyAnimationClock(OpacityProperty, null);
        target.ApplyAnimationClock(OpacityProperty, clock);
    }

    private void StartLoadingVisual()
    {
        LoadingAnimeGrid.Visibility = Visibility.Visible;
        LoadFailIcon.Opacity = 0;
        RefreshButton.Visibility = Visibility.Collapsed;
        LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, SpinAnimation);
    }

    private void StopLoadingVisual()
    {
        LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingAnimeGrid.Visibility = Visibility.Collapsed;
    }

    public async Task TryLoad()
    {
        if (_disposed) return;
        var useCachedImage = LoadingState == LoadingStateEnum.Waiting && PreviewImage.Source != null;
        LoadingState = LoadingStateEnum.Loading;
        DetailAndImgLoadCts?.Cancel();
        using var loadCts = new CancellationTokenSource();
        DetailAndImgLoadCts = loadCts;
        try
        {
            StartLoadingVisual();
            var imgTask = useCachedImage ? Task.FromResult(true) : LoadDisplayImageAsync(DetailAndImgLoadCts.Token);
            var detailTask = useCachedImage && MoeItem.CanDownload
                ? Task.FromResult(true) : LoadDetailTask(DetailAndImgLoadCts.Token);
            
            bool imgB = false, detailB = false;

            try
            {
                imgB = await imgTask;
            }
            catch
            {
                // ignored
            }

            // Observe both tasks even when this page has been removed.
            if (_disposed || loadCts.IsCancellationRequested)
            {
                try { await detailTask; } catch { }
                return;
            }

            if (imgB)
            {
                ImageGrid.Visibility = Visibility.Visible;
                ImageBgBorder.Opacity = 0.8;
                if (!useCachedImage) FadeIn(PreviewImage);
            }
            
            else
            {
                LoadFailIcon.Opacity = 0.5;
                RefreshButton.Visibility = Visibility.Visible;
            }
            try
            {
                detailB = await detailTask;
            }
            catch
            {
                // ignored
            }

            if (_disposed || loadCts.IsCancellationRequested) return;
            if (detailB) MoeItem.CanDownload = true;
            else RefreshButton.Visibility = Visibility.Visible;

            if (imgB && detailB)
            {
                RefreshButton.Visibility = Visibility.Collapsed;
            }

            StopLoadingVisual();
            LoadingState = LoadingStateEnum.Loaded;
        }
        finally
        {
            if (ReferenceEquals(DetailAndImgLoadCts, loadCts)) DetailAndImgLoadCts = null;
        }
    }

    public async Task<bool> LoadDetailTask(CancellationToken token)
    {
        try
        {
            await MoeItem.TryGetDetail(token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            Ex.Log($"{MoeItem.ThumbnailUrlInfo.Url} LoadDetailTask fail : {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 异步加载图片
    /// </summary>
    public async Task<bool> LoadDisplayImageAsync(CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested) return false;
        await using var stream = await MoeItem.TryLoadThumbnailStreamAsync(token);
        if (stream == null) return false;
        BitmapImage GetBitmapFunc()
        {
            return UiUtility.GetBitmapImageFromStream(stream);
        }

        var bitimg = await Task.Run(GetBitmapFunc, token);
        if (_disposed || token.IsCancellationRequested || bitimg == null) return false;
        PreviewImage.Source = bitimg;
        

        return true;
    }

    public void SetCachedThumbnail(ImageSource source)
    {
        if (_disposed) return;
        PreviewImage.Source = source;
        PreviewImage.Opacity = 1;
        ImageBgBorder.Opacity = 0.8;
        
    }
        

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClosePools();
        MoeItem.PropertyChanged -= MoeItemOnPropertyChanged;
        MoeItem.Site.PropertyChanged -= SiteOnPropertyChanged;
        ImageLoadingStateChangedEvent = null;
        // TryLoad owns and disposes the source after both tasks have settled.
        DetailAndImgLoadCts?.Cancel();
        StopLoadingVisual();
        LayoutRoot.BeginAnimation(OpacityProperty, null);
        PreviewImage.BeginAnimation(OpacityProperty, null);
        PreviewImage.Source = null;
        ImageBgBorder.Background = null;
        DataContext = null;
        GC.SuppressFinalize(this);
    }
        
}
