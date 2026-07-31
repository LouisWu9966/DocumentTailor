using System.Windows;
using System.Windows.Threading;

namespace DocumentTailor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"發生未預期的錯誤：\n{e.Exception.Message}", "Document Tailor",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
