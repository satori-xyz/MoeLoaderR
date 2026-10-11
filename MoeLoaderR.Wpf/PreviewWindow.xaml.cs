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
    private CancellationTokenSource _loadCts;
    private bool _closed;
        
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
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCts?.Cancel();
            _qualityTimer.Stop();
            LargeImageThumb.CancelDrag();
            LargeImage.Source = null;
            PreviewBitmapImage = null;
            CurrentMoeItem = null;
            Settings = null;
            DataContext = null;
            PoolsWrapPanel.Children.Clear();
            TagsWrapPanel.Children.Clear();
        };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    public async Task InitAsync(MoeItem item, ImageSource thumbnail)
    {
        if (_closed) return;
        if (ReferenceEquals(CurrentMoeItem, item) && (PreviewBitmapImage != null || _loadCts != null)) return;
        _loadCts?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _loadCts = request;
        bool IsCurrent() => !_closed && ReferenceEquals(_loadCts, request) && !request.IsCancellationRequested;
        try
        {
            CurrentMoeItem = item;
            Settings = item.Site.Settings;
            DataContext = Settings;
            _qualityTimer.Stop();
            LargeImageThumb.CancelDrag();
            RenderOptions.SetBitmapScalingMode(LargeImage, BitmapScalingMode.HighQuality);
            LargeImage.Source = null;
            PreviewBitmapImage = null;
            DisplayItemInfo();
            ImageLoadProgressBar.Value = 0;
            RootGrid.GoElementState(nameof(LoadingBarShowState));
            if (item.Urls.GetPreview() == null) return;
            var progress = new Progress<int>(value => { if (IsCurrent()) ImageLoadProgressBar.Value = value; });
            var image = await LoadImageAsync(item, request.Token, progress);
            if (!IsCurrent()) return;
            PreviewBitmapImage = image;
            SetImage(image);
            InitImagePosition();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrent()) Ex.Log($"预览加载失败：{item.DetailUrl}", ex);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, request))
            {
                _loadCts = null;
                if (!_closed) RootGrid.GoElementState(nameof(LoadingBarHideState));
            }
        }
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
    private async Task<BitmapImage> LoadImageAsync(MoeItem item, CancellationToken token, IProgress<int> progress)
    {
        var info = item.Urls.GetPreview();
        var net = item.Site.GetCloneNet(info.Referer, 30d);
        using var client = net.Client;
        Action<int> onProgress = value => progress.Report(value);
        net.ReceiveProgressHandler.ProgressChanged += onProgress;
        try
        {
            using var response = await client.GetAsync(info.Url, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await Task.Run(() => UiUtility.GetBitmapImageFromStream(stream), token);
        }
        finally { net.ReceiveProgressHandler.ProgressChanged -= onProgress; }
    }
}