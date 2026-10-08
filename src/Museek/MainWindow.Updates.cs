using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Museek.Services;

namespace Museek;

public partial class MainWindow
{
    private UpdateWindow? _updateWindow;

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        if (_exporting)
        {
            StatusText.Text = "Finish saving your trimmed audio before updating Museek.";
            return;
        }
        var window = new UpdateWindow(installUpdate: InstallUpdate) { Owner = this };
        _updateWindow = window;
        try { window.ShowDialog(); }
        finally { _updateWindow = null; }
    }

    private void InstallUpdate(DownloadedInstaller installer)
    {
        if (_closing || _exporting) throw new InvalidOperationException("Museek is busy. Finish the current operation and try again.");
        var start = new ProcessStartInfo(installer.InstallerPath) { UseShellExecute = false };
        start.ArgumentList.Add("/UPDATEPID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The update installer could not start.");
        // Setup waits for this process before replacing managed files. Normal
        // window closure cancels work and releases audio resources first.
        Close();
    }
}
