using System.Windows;

namespace MoeLoaderR.Wpf;

public partial class MainWindow
{
    private ImageCheckWindow _imageCheckWindow;

    private void OpenImageCheck(object sender, RoutedEventArgs e)
    {
        if (_imageCheckWindow != null)
        {
            if (_imageCheckWindow.WindowState == WindowState.Minimized) _imageCheckWindow.WindowState = WindowState.Normal;
            _imageCheckWindow.Activate();
            return;
        }
        _imageCheckWindow = new ImageCheckWindow(Settings?.ImageSavePath, Settings) { Owner = this };
        _imageCheckWindow.Closed += (_, _) => _imageCheckWindow = null;
        _imageCheckWindow.Show();
    }
}
