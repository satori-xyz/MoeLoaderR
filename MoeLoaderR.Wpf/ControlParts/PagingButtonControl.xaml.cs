using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf.ControlParts;

/// <summary>
/// PagingButtonControl.xaml 的交互逻辑
/// </summary>
public partial class PagingButtonControl
{
    private static readonly DoubleAnimation LoadingRotation = CreateLoadingRotation();

    private static DoubleAnimation CreateLoadingRotation()
    {
        var animation = new DoubleAnimation(0, 360, System.TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.Freeze();
        return animation;
    }

    public PagingButtonControl()
    {
        InitializeComponent();
    }
        
    public SearchedVisualPage VisualPage { get; set; }

    public void Init(SearchedVisualPage page, double size, int? startPageNum = null)
    {
        VisualPage = page;
        VisualPage.LoadStartEvent += VisualPageOnLoadStartEvent;
        VisualPage.LoadEndEvent += VisualPageOnLoadEndEvent;
        VisualPage.GetEndEvent += VisualPageOnGetEndEvent;
        PageNumTextBlock.Text = startPageNum == null ? page.VisualIndex.ToString() : startPageNum.ToString();

        MultiPageNumTextGrid.Visibility = Visibility.Collapsed;
        NextIconTextBlock.Visibility = Visibility.Collapsed;
        Width = size;
        Height = size;
        VisualPage.PropertyChanged += VisualPageOnPropertyChanged;
        UpdateSelection();
        PageButton.Click += PageButtonOnClick;
    }

    private void PageButtonOnClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftAlt))
        {
            ShowOriginString();
        }
    }

    public void ShowOriginString()
    {
        MessageWindow.ShowDialog(VisualPage);
    }
    private void VisualPageOnGetEndEvent(SearchedVisualPage page)
    {
        if (page.RealPages.Count > 1)
        {
            PageNumTextBlock.Visibility = Visibility.Collapsed;
            MultiPageNumTextGrid.Visibility = Visibility.Visible;
            PageStartNumTextBlock.Text = (page.RealPages[0].CurrentPageNum?? page.RealPages[0].CurrentPageNumFromOne).ToString();
            PageEndNumTextBlock.Text = (page.RealPages[^1].CurrentPageNum?? page.RealPages[^1].CurrentPageNumFromOne).ToString();
        }
    }

    private void VisualPageOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VisualPage.IsCurrentPage))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var selected = VisualPage.IsCurrentPage;
        PageButton.Background = selected ? SelectionBrush : Brushes.Transparent;
        var foreground = selected ? Brushes.White : Brushes.DimGray;
        PageNumTextBlock.Foreground = foreground;
        MultiPageNumTextGrid.SetValue(TextElement.ForegroundProperty, foreground);
    }

    private static readonly Brush SelectionBrush = CreateSelectionBrush();
    private static Brush CreateSelectionBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(79, 70, 217));
        brush.Freeze();
        return brush;
    }

    public void SetNextPageButton()
    {
        PageNumTextBlock.Visibility = Visibility.Collapsed;
        MultiPageNumTextGrid.Visibility = Visibility.Collapsed;
    }

    private void VisualPageOnLoadEndEvent(SearchedVisualPage obj)
    {
            
        StopLoading();
    }

    private void VisualPageOnLoadStartEvent(SearchedVisualPage obj)
    {
            
        StartLoading();
    }

    public void StartLoading()
    {
        LoadingIcon.Opacity = 1;
        LoadingIcon.Visibility = Visibility.Visible;
        var rotation = (RotateTransform)((TransformGroup)LoadingIcon.RenderTransform).Children[2];
        rotation.BeginAnimation(RotateTransform.AngleProperty, LoadingRotation);

    }

    public void StopLoading()
    {
        var rotation = (RotateTransform)((TransformGroup)LoadingIcon.RenderTransform).Children[2];
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        LoadingIcon.Visibility = Visibility.Hidden;
        LoadingIcon.Opacity = 0;
    }
}
