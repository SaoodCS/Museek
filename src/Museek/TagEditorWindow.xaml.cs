using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Museek.Models;
using Museek.Services;

namespace Museek;

public partial class TagEditorWindow : Window
{
    private readonly AudioTagService _service;
    private readonly string[] _paths;
    private readonly List<AudioTags> _tags = [];
    private readonly Dictionary<string, (CheckBox Apply, TextBox Value, TextBlock Hint)> _fields = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _saveCancellation;
    private Task? _loadTask;
    private Task? _saveTask;
    private ArtworkAction _artworkAction;
    private byte[]? _replacementArtwork;
    private bool _initializing;
    private bool _busy;
    private bool _saving;
    private bool _closeRequested;
    private bool _closeReady;

    public TagEditorWindow(IReadOnlyList<string> paths, AudioTagService? service = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _service = service ?? new AudioTagService();
        InitializeComponent();
        CreateField("Title", "Title");
        CreateField("Artist", "Artist");
        CreateField("Album", "Album");
        CreateField("AlbumArtist", "Album artist");
        CreateField("Genre", "Genre");
        CreateField("Year", "Year");
        CreateField("TrackNumber", "Track number");
        CreateField("DiscNumber", "Disc number");
        CreateField("Comment", "Comment", multiline: true);
        FilesList.ItemsSource = _paths.Select(Path.GetFileName).ToArray();
        SelectionSummary.Text = _paths.Length == 1 ? "1 audio file" : $"{_paths.Length} audio files";
        SetBusy(true);
    }

    private void CreateField(string name, string label, bool multiline = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var apply = new CheckBox { Name = name + "Apply", Content = label, IsChecked = false };
        var value = new TextBox { Name = name + "Value", MinHeight = multiline ? 72 : 36,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        var hint = new TextBlock { Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(value, label);
        AutomationProperties.SetName(apply, "Apply " + label);
        value.TextChanged += (_, _) => { if (!_initializing) apply.IsChecked = true; };
        RegisterName(apply.Name, apply);
        RegisterName(value.Name, value);
        _fields.Add(name, (apply, value, hint));
        panel.Children.Add(apply);
        panel.Children.Add(value);
        panel.Children.Add(hint);
        FieldsPanel.Children.Add(panel);
    }

    public Task LoadAsync() => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        SetBusy(true);
        var errors = new List<string>();
        try
        {
            foreach (var path in _paths)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                try { _tags.Add(await _service.ReadAsync(path, _lifetime.Token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            PopulateFields();
            TagStatus.Text = errors.Count == 0
                ? "Choose the fields or artwork to change, then save. Separate multiple artists or genres with semicolons."
                : $"Loaded {_tags.Count} of {_paths.Length} files. Unreadable files will be skipped.\n" + string.Join("\n", errors);
        }
        catch (OperationCanceledException) { TagStatus.Text = "Reading tags cancelled."; }
        finally { SetBusy(false); }
    }

    private void PopulateFields()
    {
        _initializing = true;
        try
        {
            foreach (var (name, field) in _fields)
            {
                var values = _tags.Select(tag => GetValue(tag, name)).Distinct(StringComparer.Ordinal).ToArray();
                var mixed = values.Length > 1;
                field.Value.Text = values.Length == 1 ? values[0] : "";
                field.Apply.IsChecked = false;
                field.Hint.Text = mixed ? "Mixed values — left unchanged unless checked" : "";
                field.Hint.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
            }
            _artworkAction = ArtworkAction.Keep;
            _replacementArtwork = null;
            ShowOriginalArtwork();
        }
        finally { _initializing = false; }
    }

    private static string GetValue(AudioTags tag, string field) => field switch
    {
        "Title" => tag.Title ?? "", "Artist" => tag.Artist ?? "", "Album" => tag.Album ?? "",
        "AlbumArtist" => tag.AlbumArtist ?? "", "Genre" => tag.Genre ?? "", "Comment" => tag.Comment ?? "",
        "Year" => FormatNumber(tag.Year), "TrackNumber" => FormatNumber(tag.TrackNumber),
        "DiscNumber" => FormatNumber(tag.DiscNumber), _ => throw new ArgumentException("Unknown field.")
    };

    private static string FormatNumber(uint value) => value == 0 ? "" : value.ToString(CultureInfo.InvariantCulture);
    private string? TextValue(string name) => _fields[name].Apply.IsChecked == true ? _fields[name].Value.Text : null;
    private uint? NumberValue(string name)
    {
        var text = TextValue(name);
        if (text is null) return null;
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            if (name == "Year" && number > 9999) throw new ArgumentException("Year must be between 0 and 9999.");
            return number;
        }
        throw new ArgumentException($"{_fields[name].Apply.Content} must be a whole number, or empty to clear it.");
    }

    public TagEditPatch BuildPatch() => new()
    {
        Title = TextValue("Title"), Artist = TextValue("Artist"), Album = TextValue("Album"),
        AlbumArtist = TextValue("AlbumArtist"), Genre = TextValue("Genre"), Comment = TextValue("Comment"),
        Year = NumberValue("Year"), TrackNumber = NumberValue("TrackNumber"), DiscNumber = NumberValue("DiscNumber"),
        ArtworkAction = _artworkAction, ArtworkBytes = _replacementArtwork?.ToArray()
    };

    public Task SaveAsync()
    {
        if (_busy || _tags.Count == 0) return Task.CompletedTask;
        return _saveTask = SaveCoreAsync();
    }

    private async Task SaveCoreAsync()
    {
        TagEditPatch patch;
        try { patch = BuildPatch(); }
        catch (ArgumentException ex) { TagStatus.Text = ex.Message; return; }
        if (!_fields.Values.Any(field => field.Apply.IsChecked == true) && _artworkAction == ArtworkAction.Keep)
        {
            TagStatus.Text = "Choose a field or artwork to change first.";
            return;
        }
        _saving = true;
        _saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetBusy(true);
        TagStatus.Text = "Saving tags…";
        try
        {
            var progress = new Progress<TagEditProgress>(p => { if (_saving) TagStatus.Text = $"Processed {p.Completed} of {p.Total}: {Path.GetFileName(p.Path)}"; });
            var result = await _service.ApplyAsync(_tags.Select(tag => tag.Path).ToArray(), patch, progress, _saveCancellation.Token);
            var saved = result.Files.Count(file => file.Success);
            var failures = result.Files.Where(file => !file.Success).Select(file => $"{Path.GetFileName(file.Path)}: {file.Error}").ToArray();
            // Refresh the committed files so repeated edits start from the current tags.
            for (var index = 0; index < _tags.Count && !_lifetime.IsCancellationRequested; index++)
                if (result.Files.Any(file => file.Success && file.Path.Equals(_tags[index].Path, StringComparison.OrdinalIgnoreCase)))
                    try { _tags[index] = await _service.ReadAsync(_tags[index].Path, _lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            if (!result.Cancelled && failures.Length == 0) PopulateFields();
            TagStatus.Text = $"{(result.Cancelled ? "Stopped. " : "") }Saved tags for {saved} of {_tags.Count} files."
                + (failures.Length == 0 ? "" : "\n" + string.Join("\n", failures));
        }
        catch (OperationCanceledException) { TagStatus.Text = "Saving cancelled. Files already saved keep their changes."; }
        catch (Exception ex) { TagStatus.Text = $"Couldn't save tags: {ex.Message}"; }
        finally
        {
            _saveCancellation.Dispose();
            _saveCancellation = null;
            _saving = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        FieldsPanel.IsEnabled = !busy && _tags.Count > 0;
        ArtworkButtons.IsEnabled = !busy && _tags.Count > 0;
        KeepArtworkButton.IsEnabled = !busy && _tags.Count > 0;
        SaveTagsButton.IsEnabled = !busy && _tags.Count > 0;
        CancelTagsButton.Content = _saving ? "Stop saving" : "Cancel";
    }

    private void ShowOriginalArtwork()
    {
        var first = _tags.FirstOrDefault()?.ArtworkBytes;
        var common = _tags.All(tag => first is null ? tag.ArtworkBytes is null : tag.ArtworkBytes is not null && first.SequenceEqual(tag.ArtworkBytes));
        ShowArtwork(common ? first : null, common ? "No artwork" : "Different artwork");
        ArtworkStatus.Text = "Keep existing artwork";
    }

    private void ShowArtwork(byte[]? bytes, string placeholder)
    {
        ArtworkImage.Source = null;
        ArtworkImage.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Text = placeholder;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        if (bytes is null) return;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 420;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ArtworkImage.Source = bitmap;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); ArtworkPlaceholder.Text = "Artwork unavailable"; }
    }

    private void ChangeArtwork_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose album artwork", Filter = "JPEG or PNG images|*.jpg;*.jpeg;*.png", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > 8 * 1024 * 1024) throw new ArgumentException("Choose a JPEG or PNG image smaller than 8 MB.");
            SetArtwork(File.ReadAllBytes(info.FullName));
        }
        catch (Exception ex) { TagStatus.Text = $"Couldn't use this artwork: {ex.Message}"; }
    }

    public void SetArtwork(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > 8 * 1024 * 1024) throw new ArgumentException("Choose a JPEG or PNG image smaller than 8 MB.");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not (JpegBitmapDecoder or PngBitmapDecoder) || decoder.Frames.Count == 0 ||
            (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 32 * 1024 * 1024)
            throw new ArgumentException("Choose a valid JPEG or PNG image up to 32 megapixels.");
        _replacementArtwork = bytes.ToArray();
        _artworkAction = ArtworkAction.Replace;
        ShowArtwork(bytes, "Artwork unavailable");
        ArtworkStatus.Text = "Replace artwork in all selected files";
    }

    private void RemoveArtwork_Click(object sender, RoutedEventArgs e)
    {
        _artworkAction = ArtworkAction.Remove;
        _replacementArtwork = null;
        ShowArtwork(null, "Artwork will be removed");
        ArtworkStatus.Text = "Remove artwork from all selected files";
    }

    private void KeepArtwork_Click(object sender, RoutedEventArgs e)
    {
        _artworkAction = ArtworkAction.Keep;
        _replacementArtwork = null;
        ShowOriginalArtwork();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowThemeService.Apply(this);
    private async void SaveTags_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private void CancelTags_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) { _saveCancellation?.Cancel(); TagStatus.Text = "Stopping after the current operation…"; }
        else Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady || !_busy) { _lifetime.Cancel(); return; }
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        _lifetime.Cancel();
        CancelTagsButton.IsEnabled = false;
        TagStatus.Text = "Finishing the current operation…";
        try { await Task.WhenAll(new[] { _loadTask, _saveTask }.OfType<Task>()); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        _closeReady = true;
        _ = Dispatcher.BeginInvoke((Action)Close);
    }

    private void Window_Closed(object? sender, EventArgs e) => _lifetime.Dispose();
}
