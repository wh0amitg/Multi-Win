using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WinMultiInstaller;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Multi-Win");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"),
                $"[{DateTime.Now}] {e.Exception}\n");
        }
        catch { }
        MessageBox.Show(e.Exception.GetBaseException().Message, "Multi-Win startup error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

