using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
        MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
        Options.Init(settings);
        DoneButton.Click += (_, _) => Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Commit focused text fields before persisting the shared settings.
        Keyboard.Focus(DoneButton);
        base.OnClosing(e);
        if (!e.Cancel) _settings.Save(App.SettingJsonFilePath);
    }
}
