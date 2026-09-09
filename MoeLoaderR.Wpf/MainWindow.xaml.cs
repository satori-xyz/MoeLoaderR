using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MoeLoaderR.Core;
using MoeLoaderR.Wpf.ControlParts;

namespace MoeLoaderR.Wpf;

public partial class MainWindow
{
    private SettingsWindow _settingsWindow;
    public MainWindow()
    {
        InitializeComponent();
    }

    public Settings Settings { get; set; }
    
    public void Init(Settings settings)
    {
        // gen custom test 请删除后运行
        //if (Debugger.IsAttached)
        //{
        //    var cus = new CustomSiteFactory();
        //    cus.GenTestSites();
        //    cus.OutputJson(App.CustomSiteDir);
        //    Thread.Sleep(1000);
        //}

        

        Settings = settings;
        Settings.CustomSitesDir = App.CustomSiteDir;
        Settings.SiteManager = new SiteManager(Settings);
        DataContext = Settings;
            
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        BrowseNavigationButton.Click += delegate { DownloaderMenuCheckBox.IsChecked = false; SettingsMenuCheckBox.IsChecked = false; };
        Ex.ShowMessageAction += ShowMessage;

        // logo

        // menu
        DownloaderMenuCheckBox.Checked += DownloaderMenuCheckBoxCheckChanged;
        DownloaderMenuCheckBox.Unchecked += DownloaderMenuCheckBoxCheckChanged;
        
        // user ctrl
        SearchControl.Init(Settings);
        
        MoeDownloaderControl.Init(Settings);
        SettingsMenuCheckBox.Checked += OpenSettings;
        MoeExplorer.Init(Settings);

        // helper : collect ,log
        new LogWindowHelper().Init(LogButton, Settings);
        
        ImageSizeSlider.MouseWheel += ImageSizeSliderOnMouseWheel;


        

        // ali

        Settings.SiteManager.PropertyChanged += SiteManagerOnPropertyChanged;
    }

    private void SiteManagerOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.SiteManager.CurrentSelectedSite))
        {
            var isCsm = Settings.SiteManager.CurrentSelectedSite.Config?.IsCustomSite == true;
            if (Settings.IsCustomSiteMode == isCsm) return;

            Settings.IsCustomSiteMode = isCsm;
            LayoutRoot.GoElementState(isCsm ? nameof(CustomSitesState) : nameof(DefaultSitesState));
            LogoImage.Visibility = isCsm ? Visibility.Collapsed : Visibility.Visible;
            LogoImage2.Visibility = isCsm ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(Settings) { Owner = this };
        _settingsWindow.Closed += delegate
        {
            _settingsWindow = null;
            SettingsMenuCheckBox.IsChecked = false;
        };
        _settingsWindow.ShowDialog();
    }
    
    
    public void ShowMessage(string mes, string detailMes = null, Ex.MessagePos pos = Ex.MessagePos.Popup, bool ishighlight =false)
    {
        switch (pos)
        {
            case Ex.MessagePos.Popup:
                PopupMessageTextBlock.Text = mes;
                this.Sb("PopupMessageShowSb").Begin();
                break;
            case Ex.MessagePos.Window:
                MessageWindow.ShowDialog(mes, detailMes, this);
                break;

        }
    }

    private void DownloaderMenuCheckBoxCheckChanged(object sender, RoutedEventArgs e)
    {
        var ischecked = DownloaderMenuCheckBox.IsChecked == true;
        LayoutRoot.GoElementState(ischecked ? nameof(ShowDownloadPanelState) : nameof(HideDownloadPanelState));
        if (ischecked) MoeDownloaderControl.ScrollToBottom();
    }

    private void ImageSizeSliderOnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var v = ImageSizeSlider.Value;
        v += e.Delta / 5d;
        if (v > ImageSizeSlider.Minimum && v < ImageSizeSlider.Maximum) ImageSizeSlider.Value += e.Delta / 5d;
    }


    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1: // 用于测试功能
                ShowMessage("test");
                break;
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        Settings.Save(App.SettingJsonFilePath);
        if (!MoeDownloaderControl.Downloader.IsDownloading) return;
        var result = MessageBox.Show(this, "正在下载图片，确定要关闭吗？",
            App.DisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) e.Cancel = true;
    }
}
