using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

/// <summary>
/// PreviewWindow.xaml 的交互逻辑
/// </summary>
public partial class PreviewWindow
{
    public MoeItem CurrentMoeItem { get; set; }
    public Settings Settings { get; set; }
    public BitmapImage PreviewBitmapImage { get; set; }
    private Point _previousDragOffset;
    private readonly System.Windows.Threading.DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    public CancellationTokenSource Cts { get; set; }
        
    public PreviewWindow()
    {
        InitializeComponent();
        MouseWheel += OnMouseWheel;
        LargeImageThumb.DragDelta += LargeImageThumbOnDragDelta;
        LargeImageThumb.DragStarted += (_, _) => _previousDragOffset = new Point();
        LargeImage.ClearValue(MarginProperty);
        ImageCanvas.SizeChanged += (_, _) => CenterImage();
        _qualityTimer.Tick += (_, _) =>
        {
            _qualityTimer.Stop();
            RenderOptions.SetBitmapScalingMode(LargeImage, BitmapScalingMode.HighQuality);
        };
        Closed += (_, _) => { _qualityTimer.Stop(); Cts?.Cancel(); };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    public static void Show(PreviewWindow thisWnd , Window wnd, MoeItem moeitem, ImageSource imgSource)
    {
        if (thisWnd == null)
        {
            thisWnd = new PreviewWindow();
            thisWnd.Closed += delegate { thisWnd = null; };
            thisWnd.Owner = wnd;
            thisWnd.Width = wnd.Width * 0.85d;
            thisWnd.Height = wnd.Height * 0.85d;
            thisWnd.Show();
        }
        else
        {
            thisWnd.Activate();
        }

        thisWnd.Init(moeitem, imgSource);
    }

    public async void Init(MoeItem moeitem, ImageSource imgSource)
    {
        CurrentMoeItem = moeitem;
        Settings = moeitem.Site.Settings;
        DataContext = Settings;
        var info = CurrentMoeItem.Urls.GetPreview();
        if(info == null )return;
        DisplayItemInfo();
        RootGrid.GoElementState(nameof(LoadingBarShowState));
        ImageLoadProgressBar.Value = 0;
        await LoadImageAsync();
        RootGrid.GoElementState(nameof(LoadingBarHideState));
        InitImagePosition();
    }

    public void DisplayItemInfo()
    {
        var i = CurrentMoeItem;
        InfoTitleTextBlock.Text = i.Title;
        InfoTitleTextBlock.Visibility = string.IsNullOrWhiteSpace(i.Title) ? Visibility.Collapsed : Visibility.Visible;
        InfoIdTextBlock.Text = $"{i.Id}";
        InfoUploaderTextBlock.Text = i.Uploader;
        InfoScoreTextBlock.Text = $"{i.Score}";
        InfoResolutionTextBlock.Text = $"{i.Width}x{i.Height}";
        InfoDateTextBlock.Text = i.DateString;
        PoolsWrapPanel.Children.Clear();
        foreach (var pool in i.Pools)
        {
            var poolGroup = new StackPanel { Orientation = Orientation.Horizontal };
            var button = new Button
            {
                Content = pool.DisplayText, ToolTip = pool.Url,
                Margin = new Thickness(12, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(79, 70, 217))
            };
            button.Click += (_, _) => pool.Url.GoUrl();
            poolGroup.Children.Add(button);
            var downloadButton = new Button
            {
                Content = "下载 Pool", ToolTip = "下载整个图集 ZIP，保留原始文件名；需登录 Yande",
                Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(79, 70, 217))
            };
            downloadButton.Click += (_, _) =>
            {
                if (Application.Current.MainWindow is not MainWindow main) return;
                main.MoeDownloaderControl.Downloader.AddPoolDownload(i, pool, null);
                main.DownloaderMenuCheckBox.IsChecked = true;
                main.MoeDownloaderControl.ScrollToBottom();
                downloadButton.Content = "已加入下载";
            };
            poolGroup.Children.Add(downloadButton);
            PoolsWrapPanel.Children.Add(poolGroup);
        }
        TagsWrapPanel.Children.Clear();
        foreach (var iTag in i.Tags)
        {
            var tagTb = new TextBlock();
            tagTb.Text = iTag;
            tagTb.FontSize = 12;
            tagTb.Foreground = (SolidColorBrush)FindResource("HightLightFontColorBrush");
            tagTb.Margin = new Thickness(0,3,12,3);
            TagsWrapPanel.Children.Add(tagTb);
        }

    }

    private void LargeImageThumbOnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (PreviewBitmapImage == null) return;
        BeginImageInteraction();
        // The stationary Thumb reports displacement from the start, not the last event.
        var offset = new Point(e.HorizontalChange, e.VerticalChange);
        var delta = offset - _previousDragOffset;
        _previousDragOffset = offset;
        SetImageOffset(ImageTranslateTransform.X + delta.X, ImageTranslateTransform.Y + delta.Y);
        e.Handled = true;
    }

    private void BeginImageInteraction()
    {
        RenderOptions.SetBitmapScalingMode(LargeImage, BitmapScalingMode.LowQuality);
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    private void SetImageOffset(double x, double y)
    {
        var width = LargeImage.Width * ImageScaleTransform.ScaleX;
        var height = LargeImage.Height * ImageScaleTransform.ScaleY;
        ImageTranslateTransform.X = Math.Clamp(x, Math.Min(0, ImageCanvas.ActualWidth - width), Math.Max(0, ImageCanvas.ActualWidth - width));
        ImageTranslateTransform.Y = Math.Clamp(y, Math.Min(0, ImageCanvas.ActualHeight - height), Math.Max(0, ImageCanvas.ActualHeight - height));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!LargeImageThumb.IsMouseOver || PreviewBitmapImage == null) return;
        ZoomImage(e.GetPosition(ImageCanvas), e.Delta);
        e.Handled = true;
    }

    internal void ZoomImage(Point anchor, int delta)
    {
        if (PreviewBitmapImage == null || ImageCanvas.ActualWidth <= 0 || ImageCanvas.ActualHeight <= 0) return;
        var previous = ImageScaleTransform.ScaleX;
        var fit = Math.Min(1, Math.Min(ImageCanvas.ActualWidth / LargeImage.Width, ImageCanvas.ActualHeight / LargeImage.Height));
        var scale = Math.Clamp(previous * Math.Pow(1.12, delta / 120d), fit / 4, Math.Max(8, fit));
        var ratio = scale / previous;
        var x = anchor.X - (anchor.X - ImageTranslateTransform.X) * ratio;
        var y = anchor.Y - (anchor.Y - ImageTranslateTransform.Y) * ratio;
        BeginImageInteraction();
        ImageScaleTransform.ScaleX = ImageScaleTransform.ScaleY = scale;
        SetImageOffset(x, y);
    }

    public void InitImagePosition()
    {
        if (PreviewBitmapImage == null || ImageCanvas.ActualWidth <= 0 || ImageCanvas.ActualHeight <= 0) return;
        LargeImage.Width = PreviewBitmapImage.PixelWidth;
        LargeImage.Height = PreviewBitmapImage.PixelHeight;
        var scale = Math.Min(1, Math.Min(ImageCanvas.ActualWidth / LargeImage.Width, ImageCanvas.ActualHeight / LargeImage.Height));
        ImageScaleTransform.ScaleX = ImageScaleTransform.ScaleY = scale;
        CenterImage();
    }

    private void CenterImage()
    {
        if (PreviewBitmapImage == null || !double.IsFinite(LargeImage.Width) || !double.IsFinite(LargeImage.Height)) return;
        ImageTranslateTransform.X = (ImageCanvas.ActualWidth - LargeImage.Width * ImageScaleTransform.ScaleX) / 2;
        ImageTranslateTransform.Y = (ImageCanvas.ActualHeight - LargeImage.Height * ImageScaleTransform.ScaleY) / 2;
    }

    public void SetImage(BitmapImage img)
    {
        LargeImage.Source = img;
    }
    /// <summary>
    /// 异步加载图片
    /// </summary>
    public async Task<Exception> LoadImageAsync()
    {
        // client

        var net = CurrentMoeItem.Site.GetCloneNet(CurrentMoeItem.ThumbnailUrlInfo.Referer, 30d);
        net.ReceiveProgressHandler.ProgressChanged += OnReceiveProgress;
        Exception loadEx = null;
        try
        {
            Cts?.Cancel();
            Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await net.Client.GetAsync(CurrentMoeItem.Urls.GetPreview().Url, Cts.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync();
            BitmapImage Func()
            {
                return UiUtility.GetBitmapImageFromStream(stream);
            }
            var source = await Task.Run(Func, Cts.Token);

            if (source != null)
            {
                PreviewBitmapImage = source;
                SetImage(source);
            }
        }
        catch (Exception ex)
        {
            loadEx = ex;
        }

        if (loadEx == null)
        {

            //this.Sb("LoadedImageSb").Begin();
        }
        else
        {
            //this.Sb("LoadFailSb").Begin();
            Ex.Log(loadEx.Message, loadEx.StackTrace);
            Ex.Log($"{CurrentMoeItem.ThumbnailUrlInfo.Url} 图片加载失败");
        }

        return loadEx;
    }

    private void OnReceiveProgress(int percentage)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ImageLoadProgressBar.Value = percentage;
        }));

    }
}
