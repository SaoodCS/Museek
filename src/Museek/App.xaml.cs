using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Museek.Services;

namespace Museek;

public partial class App : Application
{
    private SingleWindowService? _singleWindow;
    private IDisposable? _tagContextServer;
    private DispatcherTimer? _tagServerIdle;
    private readonly HashSet<TagEditorWindow> _tagEditors = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Registration is explicit, never silently changes the user's defaults.
        var registrationCommand = e.Args.FirstOrDefault();
        if (registrationCommand is "--register" or "--unregister" &&
            e.Args.Length <= 2 && e.Args.Skip(1).All(argument => argument == "--quiet"))
        {
            var quiet = e.Args.Contains("--quiet");
            try
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot locate Museek.exe.");
                if (registrationCommand == "--unregister")
                {
                    TagContextMenuService.Unregister(executable);
                    WindowsIntegrationService.Unregister(executable);
                }
                else
                {
                    WindowsIntegrationService.Register(executable);
                    var settings = new AppSettingsService();
                    try { settings.Reload(); }
                    catch (Exception ex) { Debug.WriteLine($"Optional tag-menu preferences could not be read: {ex.Message}"); }
                    if (settings.EditTagsContextMenu) TagContextMenuService.Register(executable);
                }
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Museek {registrationCommand} failed: {ex}");
                if (!quiet) MessageBox.Show(ex.Message, "Museek registration", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        try
        {
            // Editing is independent of playback and never forwards through single-window mode.
            if (e.Args.FirstOrDefault() == "--edit-tags")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                OpenTagEditor(e.Args.Skip(1).ToArray());
                return;
            }
            if (e.Args.FirstOrDefault() == "--tag-context-server")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _tagServerIdle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _tagServerIdle.Tick += (_, _) => { if (_tagEditors.Count == 0) Shutdown(); };
                _tagContextServer = TagContextMenuService.RegisterServer(paths =>
                    Dispatcher.Invoke(() => OpenTagEditor(paths)));
                _tagServerIdle.Start();
                return;
            }
            var argument = e.Args.FirstOrDefault();
            var path = string.IsNullOrWhiteSpace(argument) ? null : Path.GetFullPath(argument);
            var settings = new AppSettingsService();
            string? settingsError = null;
            try { settings.Reload(); }
            catch (Exception ex) { settingsError = $"Couldn't read Museek settings: {ex.Message}"; }
            _singleWindow = new SingleWindowService();

            var routingFailed = false;
            string? routingError = null;
            try
            {
                if (settings.SingleWindowMode)
                {
                    // Ownership can outlive pipe startup briefly, or disappear when an owner closes.
                    var deadline = Stopwatch.StartNew();
                    while (!_singleWindow.TryAcquire(AcceptOpenRequestAsync))
                    {
                        if (await _singleWindow.TryForwardAsync(path, TimeSpan.FromSeconds(2)))
                        {
                            Shutdown(0);
                            return;
                        }
                        if (deadline.Elapsed >= TimeSpan.FromSeconds(6)) { routingFailed = true; break; }
                        await Task.Delay(80);
                        try { settings.Reload(); }
                        catch (Exception ex) { settingsError = $"Couldn't read Museek settings: {ex.Message}"; break; }
                        if (!settings.SingleWindowMode) break;
                    }
                }
            }
            catch (Exception ex) { routingError = $"Single-window mode is unavailable: {ex.Message}"; }

            var window = new MainWindow(path, settings);
            MainWindow = window;
            window.SingleWindowModeChanged += (_, _) => UpdateSingleWindowMode(window.SingleWindowMode);
            window.Closing += (_, _) => ReleaseSingleWindow();
            UpdateSingleWindowMode(window.SingleWindowMode);
            window.Show();
            if (settingsError is not null) window.ShowStatus(settingsError);
            else if (routingError is not null) window.ShowStatus(routingError);
            else if (routingFailed) window.ShowStatus("Opened here because the other Museek window wasn't responding.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Museek could not start.\n\n{ex.Message}\n\nKeep the app together with its libvlc and tools folders.",
                "Museek", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OpenTagEditor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("Select one or more audio files to edit.");
        var editor = new TagEditorWindow(paths);
        _tagServerIdle?.Stop();
        _tagEditors.Add(editor);
        editor.Closed += (_, _) =>
        {
            _tagEditors.Remove(editor);
            if (_tagEditors.Count != 0) return;
            if (_tagServerIdle is not null)
            {
                _tagServerIdle.Interval = TimeSpan.FromSeconds(2);
                _tagServerIdle.Start();
            }
            else Shutdown();
        };
        try
        {
            editor.Show();
            editor.Activate();
        }
        catch
        {
            _tagEditors.Remove(editor);
            try { editor.Close(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            if (_tagEditors.Count == 0) _tagServerIdle?.Start();
            throw;
        }
    }

    private Task<bool> AcceptOpenRequestAsync(string? path, CancellationToken cancellationToken)
        => Dispatcher.InvokeAsync(() => !cancellationToken.IsCancellationRequested && MainWindow is MainWindow window && window.AcceptOpenRequest(path),
            DispatcherPriority.Normal, cancellationToken).Task;

    private void UpdateSingleWindowMode(bool enabled)
    {
        if (_singleWindow is null) return;
        try
        {
            if (enabled) _singleWindow.TryAcquire(AcceptOpenRequestAsync);
            else _singleWindow.Release();
        }
        catch (Exception ex) { (MainWindow as MainWindow)?.ShowStatus($"Single-window mode is unavailable: {ex.Message}"); }
    }

    private void ReleaseSingleWindow()
    {
        try { _singleWindow?.Release(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tagServerIdle?.Stop();
        try { _tagContextServer?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { _singleWindow?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        base.OnExit(e);
    }
}
