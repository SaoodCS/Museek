using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Museek.Services;

namespace Museek;

public partial class UpdateWindow : Window
{
    private readonly IAppUpdateService _updater;
    private readonly bool _ownsUpdater;
    private readonly Action<DownloadedInstaller> _installUpdate;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _operation;
    private UpdateRelease? _release;
    private DownloadedInstaller? _installer;
    private bool _closing;
    private bool _closeReady;
    private bool _handedOff;

    public Task Completion => _completion.Task;

    public UpdateWindow(IAppUpdateService? updater = null, Action<DownloadedInstaller>? installUpdate = null)
    {
        _updater = updater ?? new AppUpdateService();
        _ownsUpdater = updater is null;
        _installUpdate = installUpdate ?? (_ => throw new InvalidOperationException("Open updates from Museek's Help menu to install."));
        InitializeComponent();
        UpdateCurrentVersion.Text = $"Installed version: {_updater.CurrentVersion.ToString(3)}";
    }

    public Task CheckAsync() => StartOperationAsync(async token =>
    {
        DiscardInstaller();
        _release = null;
        UpdateStatus.Text = "Checking for updates…";
        UpdateDetails.Text = "Looking for the latest Museek release.";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        _release = await _updater.CheckAsync(token);
        if (_closing) return;
        UpdateStatus.Text = _release is null ? "You're up to date." : $"Museek {_release.Version.ToString(3)} is available.";
        UpdateDetails.Text = _release is null ? "You have the latest version of Museek."
            : "Download the update when you're ready. Your settings and audio files will be kept.";
    });

    public Task DownloadAsync() => StartOperationAsync(async token =>
    {
        if (_release is null) return;
        DiscardInstaller();
        UpdateStatus.Text = $"Downloading Museek {_release.Version.ToString(3)}…";
        UpdateDetails.Text = "Starting download…";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = 0;
        var downloadCompleted = false;
        var progress = new Progress<double>(value =>
        {
            if (_closing || downloadCompleted || !double.IsFinite(value)) return;
            UpdateProgress.Value = Math.Clamp(value, 0, 1) * 100;
            UpdateDetails.Text = $"{UpdateProgress.Value:F0}% downloaded";
        });
        DownloadedInstaller installer;
        try { installer = await _updater.DownloadAsync(_release, progress, token); }
        finally { downloadCompleted = true; }
        if (_closing) { installer.Dispose(); return; }
        _installer = installer;
        UpdateStatus.Text = $"Ready to install Museek {_release.Version.ToString(3)}.";
        UpdateDetails.Text = "Installing will close Museek. Close any other Museek or tag editor windows before continuing. Your settings and audio files will be kept.";
    });

    public Task InstallAsync() => StartOperationAsync(async token =>
    {
        if (_installer is null) return;
        UpdateStatus.Text = "Preparing update…";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        try
        {
            await _updater.ValidateInstallerAsync(_installer, token);
            token.ThrowIfCancellationRequested();
            _handedOff = true;
            try { _installUpdate(_installer); }
            catch { _handedOff = false; throw; }
            Close();
        }
        catch { DiscardInstaller(); throw; }
    });

    private Task StartOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_closing) return Task.CompletedTask;
        if (_operation is { IsCompleted: false }) return _operation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _ = RunOperationAsync(action, completion);
        return _operation;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, TaskCompletionSource completion)
    {
        try
        {
            UpdateActionButton.IsEnabled = false;
            UpdateCloseButton.Content = "Cancel";
            await action(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (!_closing)
            {
                UpdateStatus.Text = "Couldn't complete the update.";
                UpdateDetails.Text = ex.Message.Length <= 300 ? ex.Message : ex.Message[..300];
            }
        }
        finally
        {
            try
            {
                if (!_closing)
                {
                    UpdateProgress.Visibility = Visibility.Collapsed;
                    UpdateActionButton.Content = _installer is not null ? "Install update" : _release is not null ? "Download update" : "Check again";
                    UpdateActionButton.IsEnabled = true;
                    UpdateCloseButton.Content = "Close";
                }
            }
            finally { completion.TrySetResult(); }
        }
    }

    private void DiscardInstaller()
    {
        if (!_handedOff) _installer?.Dispose();
        _installer = null;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await CheckAsync();
    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowThemeService.Apply(this);
    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_installer is not null) await InstallAsync();
        else if (_release is not null) await DownloadAsync();
        else await CheckAsync();
    }
    private void UpdateClose_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        _lifetime.Cancel();
        if (_operation is not { IsCompleted: false }) { DiscardInstaller(); return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try { await _operation; }
        catch (Exception ex) { Debug.WriteLine(ex); }
        DiscardInstaller();
        _closeReady = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        DiscardInstaller();
        if (_ownsUpdater && _updater is IDisposable disposable) disposable.Dispose();
        _lifetime.Dispose();
        _completion.TrySetResult();
    }
}
