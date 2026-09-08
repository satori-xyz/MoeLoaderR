using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public partial class App
{
    public static string DisplayName => "MoeLoaderR";
    public static string Name => "MoeLoaderR";
    public static string SettingJsonFilePath => Path.Combine(AppDataDir, "Settings.json");

    public static string AppDataDir
    {
        get
        {
            var path = Path.Combine(SysAppDataDir, Name);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string ExeDir
    {
        get
        {
            var mainModuleFileName = Process.GetCurrentProcess().MainModule?.FileName;
            if (mainModuleFileName != null) return Directory.GetParent(mainModuleFileName)?.FullName;
            return null;
        }
    }


    public static string SysAppDataDir => Environment.GetEnvironmentVariable("APPDATA");

    public static string MoePicFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MoeLoader +1s");
    

    public static string CustomSiteDir => Path.Combine(AppDataDir, "CustomSites");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Debugger.IsAttached) return;
        const string str = "MoeLoaderR 遇到未处理的错误，即将退出。请保存下方错误详情，便于排查问题。";
        var mainWnd = Current.MainWindow;
        MessageWindow.ShowDialog(e.Exception, str, mainWnd);
        Environment.Exit(0);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            try { await UpdateInstaller.ApplyAsync(e.Args[1]); }
            catch (Exception error)
            {
                MessageBox.Show("更新未完成：" + error.Message + "\n请从原位置重新打开程序。", "MoeLoaderR 更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }
        Settings settings;
        try
        {
            settings = Settings.Load(SettingJsonFilePath);
            if (settings.ImageSavePath.IsEmpty())
            {
                settings.ImageSavePath = MoePicFolder;
            }
        }
        catch (Exception ex)
        {
            //if (Debugger.IsAttached) throw;
            Ex.Log(ex);
            var result = MessageBox.Show("启动失败，是否尝试清除设置？", "错误", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (result == MessageBoxResult.Yes)
            {
                settings = new Settings
                {
                    ImageSavePath = MoePicFolder
                };
            }
            else
            {
                settings = null;
                Current.Shutdown();
            }
        }

        var mainWin = new MainWindow();
        mainWin.Init(settings);
        mainWin.Show();
    }


}
