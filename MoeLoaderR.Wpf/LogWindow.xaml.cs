using System.Windows;
using System.Windows.Controls;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

/// <summary>
/// LogWindow.xaml 的交互逻辑
/// </summary>
public partial class LogWindow
{
    public Settings Settings { get; set; }
    public LogWindow()
    {
        InitializeComponent();
        DataContext = Settings;
        Owner = Application.Current.MainWindow;
        CopyButton.Click+= CopyButtonOnClick;
        ClearButton.Click+= ClearButtonOnClick;
        LogListBox.SelectionChanged += LogListBoxOnSelectionChanged;
        Ex.LogItemStringAction += OnItemString;
        Ex.LogListOriginalStringAction += OnListString;
        Closed += delegate
        {
            Ex.LogItemStringAction -= OnItemString;
            Ex.LogListOriginalStringAction -= OnListString;
            Ex.LogCollection.CollectionChanged -= LogCollection_CollectionChanged;
            LogListBox.ItemsSource = null;
        };
        
    }

    private void OnItemString(string text) => ItemStringTextBox.Text = text;
    private void OnListString(string text) => ListOriginalStringTextBox.Text = text;

    private void LogListBoxOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CopyButton.IsEnabled = LogListBox.SelectedItems.Count > 0;

        CopyButton.Content = $"复制{LogListBox.SelectedItems.Count}项";
    }

    private void ClearButtonOnClick(object sender, RoutedEventArgs e)
    {
        Ex.LogCollection.Clear();
    }

    private void CopyButtonOnClick(object sender, RoutedEventArgs e)
    {
        var col = LogListBox.SelectedItems;
        var strs = "";
        foreach (var str in col)
        {
            strs += str + "\r\n";
        }
        strs.CopyToClipboard(false);
    }

    public void Init(Settings settings)
    {
        LogListBox.ItemsSource = Ex.LogCollection;
        Settings = settings;
        DataContext = Settings;
        Ex.LogCollection.CollectionChanged += LogCollection_CollectionChanged;
    }

    private void LogCollection_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var sv = LogListBox.Template.FindName("HostScrollViewer", LogListBox) as ScrollViewer;
        sv?.ScrollToEnd();
    }

    
}

public class LogWindowHelper
{
    public LogWindow LogWindow { get; set; }

    public void Init(Button button,Settings settings)
    {
        button.Click += delegate
        {
            if (LogWindow == null)
            {
                LogWindow = new LogWindow();
                LogWindow.Init(settings);
                LogWindow.Show();
                LogWindow.Closed += delegate { LogWindow = null; };
            }
            else
            {
                LogWindow.Activate();
            }

        };
    }
}
