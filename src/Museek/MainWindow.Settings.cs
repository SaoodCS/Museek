using System.Reflection;
using System.Windows;
using Museek.Services;

namespace Museek;

public partial class MainWindow
{
    private void RefreshSingleWindowMode()
    {
        var previous = _settings.SingleWindowMode;
        try { _settings.Reload(); }
        catch (Exception ex) { StatusText.Text = $"Couldn't read Museek settings: {ex.Message}"; }
        SetSingleWindowCheck(_settings.SingleWindowMode);
        if (previous != _settings.SingleWindowMode) SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSingleWindowCheck(bool enabled)
    {
        _updatingSettings = true;
        try
        {
            SingleWindowModeMenuItem.IsChecked = enabled;
            EditTagsContextMenuItem.IsChecked = _settings.EditTagsContextMenu;
        }
        finally { _updatingSettings = false; }
    }

    private void SingleWindowMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        try
        {
            _settings.SetSingleWindowMode(SingleWindowModeMenuItem.IsChecked);
            SetSingleWindowCheck(_settings.SingleWindowMode);
            SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetSingleWindowCheck(_settings.SingleWindowMode);
            StatusText.Text = $"Couldn't save Museek settings: {ex.Message}";
        }
    }

    private void EditTagsContextMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        var previous = _settings.EditTagsContextMenu;
        var enabled = EditTagsContextMenuItem.IsChecked;
        try
        {
            _editTagsContextMenuRegistration(enabled);
            try { _settings.SetEditTagsContextMenu(enabled); }
            catch
            {
                _editTagsContextMenuRegistration(previous);
                throw;
            }
            SetSingleWindowCheck(_settings.SingleWindowMode);
            SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
            StatusText.Text = enabled ? "Edit Tags is available in Explorer under Show more options."
                : "Edit Tags was removed from the Explorer context menu.";
        }
        catch (Exception ex)
        {
            SetSingleWindowCheck(_settings.SingleWindowMode);
            StatusText.Text = $"Couldn't update the Edit Tags option: {ex.Message}";
        }
    }

    private void DefaultApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsIntegrationService.Register(Environment.ProcessPath!);
            WindowsIntegrationService.OpenDefaultAppsSettings();
            StatusText.Text = "Choose the audio file types you want Museek to open in Windows Settings.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "Unknown";
        MessageBox.Show(this,
            $"Museek {version}\nA simple audio player and trimmer.\n\nPlayback: VLC / LibVLCSharp\nTrimming: FFmpeg\n\nThird-party notices are included beside Museek.exe.", "About Museek", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
