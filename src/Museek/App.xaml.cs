using System.Diagnostics;
using System.IO;
using System.Windows;
using Museek.Services;

namespace Museek;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Registration is explicit, never silently changes the user's defaults.
        if (e.Args.Length == 1 && e.Args[0] == "--register")
        {
            try
            {
                WindowsIntegrationService.Register(Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot locate Museek.exe."));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Museek registration", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        try
        {
            MainWindow = new MainWindow(e.Args.FirstOrDefault());
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Museek could not start.\n\n{ex.Message}\n\nKeep the app together with its libvlc and tools folders.",
                "Museek", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
