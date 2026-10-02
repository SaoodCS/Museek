using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Museek.Services;

public sealed class AppSettingsService
{
    private readonly object _sync = new();
    private bool _singleWindowMode;
    private bool _editTagsContextMenu;

    public AppSettingsService(string? settingsPath = null)
    {
        SettingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Museek", "settings.json"));
    }

    public string SettingsPath { get; }

    public bool SingleWindowMode
    {
        get { lock (_sync) return _singleWindowMode; }
    }

    public bool EditTagsContextMenu { get { lock (_sync) return _editTagsContextMenu; } }

    public void Reload()
    {
        lock (_sync)
        {
            var document = ReadDocument();
            var single = ReadFlag(document, "singleWindowMode");
            var tags = ReadFlag(document, "editTagsContextMenu");
            _singleWindowMode = single;
            _editTagsContextMenu = tags;
        }
    }

    public void SetSingleWindowMode(bool enabled) => Save("singleWindowMode", enabled);
    public void SetEditTagsContextMenu(bool enabled) => Save("editTagsContextMenu", enabled);

    private JsonObject ReadDocument()
    {
        try
        {
            using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) as JsonObject
                ?? throw new InvalidDataException("Museek settings must contain a JSON object.");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException ex) { throw new InvalidDataException("Museek settings contain invalid JSON.", ex); }
    }

    private static bool ReadFlag(JsonObject document, string name)
    {
        var entry = document.LastOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null) return false;
        if (entry.Value is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        throw new InvalidDataException($"The {name} setting must be true or false.");
    }

    private void Save(string name, bool enabled)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory,
                $".{Path.GetFileName(SettingsPath)}-{Guid.NewGuid():N}.tmp");
            try
            {
                JsonObject document;
                try { document = ReadDocument(); }
                catch (InvalidDataException)
                {
                    document = new JsonObject { ["singleWindowMode"] = _singleWindowMode,
                        ["editTagsContextMenu"] = _editTagsContextMenu };
                }
                foreach (var key in document.Select(item => item.Key).Where(key => key.Equals(name,
                    StringComparison.OrdinalIgnoreCase)).ToArray()) document.Remove(key);
                document[name] = enabled;
                var single = ReadFlag(document, "singleWindowMode");
                var tags = ReadFlag(document, "editTagsContextMenu");
                var json = JsonSerializer.SerializeToUtf8Bytes(document,
                    new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, SettingsPath, overwrite: true);
                _singleWindowMode = single;
                _editTagsContextMenu = tags;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
