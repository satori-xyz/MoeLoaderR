using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf.ControlParts;

public partial class UpdateControl
{
    private Settings _settings;
    private UpdateRelease _release;
    private string _download;
    private CancellationTokenSource _operation;
    private readonly Version _current = typeof(App).Assembly.GetName().Version;

    public UpdateControl()
    {
        InitializeComponent();
        VersionText.Text = "当前版本：" + _current.ToString(3);
        CheckButton.Click += async (_, _) => await CheckAsync();
        DownloadButton.Click += async (_, _) => await DownloadAsync();
        InstallButton.Click += (_, _) => Install();
        CancelButton.Click += (_, _) => _operation?.Cancel();
        Unloaded += (_, _) => { _operation?.Cancel(); DiscardDownload(); };
        SaveTokenButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(TokenBox.Password)) { StatusText.Text = "请先填写令牌。"; return; }
            try { GitHubUpdateSupport.SaveToken(TokenBox.Password); TokenBox.Clear(); StatusText.Text = "授权已在本机加密保存。"; }
            catch { StatusText.Text = "保存授权失败，请检查配置目录权限。"; }
        };
        ClearTokenButton.Click += (_, _) =>
        {
            try { GitHubUpdateSupport.SaveToken(null); TokenBox.Clear(); StatusText.Text = "已清除保存的授权；仍可使用本机 GitHub CLI 登录。"; }
            catch { StatusText.Text = "清除授权失败，请检查配置目录权限。"; }
        };
    }

    public void Init(Settings settings) => _settings = settings;

    private async Task RunAsync(TimeSpan timeout, Func<CancellationToken, Task> action)
    {
        if (_operation != null) return;
        using var cts = new CancellationTokenSource(timeout);
        _operation = cts;
        CheckButton.IsEnabled = DownloadButton.IsEnabled = InstallButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消或超时，可以重试。"; }
        catch (Exception exception) { StatusText.Text = "更新失败：" + exception.Message; }
        finally
        {
            _operation = null;
            CheckButton.IsEnabled = DownloadButton.IsEnabled = InstallButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private Task CheckAsync() => RunAsync(TimeSpan.FromSeconds(45), async token =>
    {
        _release = null;
        DiscardDownload();
        DownloadButton.Visibility = InstallButton.Visibility = DownloadProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在检查 GitHub Release…";
        var credential = await GitHubUpdateSupport.GetTokenAsync(token);
        if (string.IsNullOrEmpty(credential)) throw new InvalidOperationException("请先在“GitHub 授权”中填写令牌，或使用 GitHub CLI 登录。");
        using var http = GitHubUpdateSupport.CreateClient(_settings);
        _release = await new GitHubUpdateClient(http).CheckAsync(_current, credential, token);
        if (_release == null) { StatusText.Text = "当前已是最新正式版本。"; return; }
        DownloadButton.Visibility = Visibility.Visible;
        StatusText.Text = $"发现 {_release.Tag}，下载大小约 {_release.Size / 1024d / 1024d:F1} MiB。";
    });

    private Task DownloadAsync() => RunAsync(TimeSpan.FromMinutes(30), async token =>
    {
        if (_release == null) return;
        DiscardDownload();
        var path = UpdateInstaller.CreateDownloadPath();
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        InstallButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在下载更新…";
        using var http = GitHubUpdateSupport.CreateClient(_settings);
        var credential = await GitHubUpdateSupport.GetTokenAsync(token);
        await new GitHubUpdateClient(http).DownloadAsync(_release, credential, path, new Progress<int>(value =>
        {
            DownloadProgress.Value = value;
            StatusText.Text = $"正在下载更新：{value}%";
        }), token);
        _download = path;
        DownloadButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Visible;
        StatusText.Text = "下载和 SHA-256 校验完成，点击安装并重启。";
    });

    private void Install()
    {
        if (_release == null || _download == null) return;
        if (Application.Current.MainWindow is MainWindow main && main.MoeDownloaderControl.Downloader.IsDownloading)
        { StatusText.Text = "请等待图片下载完成，或先停止下载，再安装更新。"; return; }
        try
        {
            _settings.Save(App.SettingJsonFilePath);
            UpdateInstaller.Start(_download, _release.Sha256);
            _download = null;
            Application.Current.Shutdown();
        }
        catch (Exception exception) { StatusText.Text = "无法安装更新：" + exception.Message; }
    }

    private void DiscardDownload()
    {
        if (_download != null && File.Exists(_download)) File.Delete(_download);
        _download = null;
    }
}
