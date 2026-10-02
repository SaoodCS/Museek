using System;
using System.IO;
using System.Text.Json;

namespace Museek.Services;

public sealed class AppSettingsService
{
    private readonly object _sync = new();
    private bool _singleWindowMode;

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

    public void Reload()
    {
        lock (_sync)
        {
            bool value;
            try
            {
                using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Museek settings must contain a JSON object.");

                value = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.Equals("singleWindowMode", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("The single-window setting must be true or false.");
                    value = property.Value.GetBoolean();
                }
            }
            catch (FileNotFoundException) { value = false; }
            catch (DirectoryNotFoundException) { value = false; }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Museek settings could not be read because the JSON is invalid.", ex);
            }

            _singleWindowMode = value;
        }
    }

    public void SetSingleWindowMode(bool enabled)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory,
                $".{Path.GetFileName(SettingsPath)}-{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(new { singleWindowMode = enabled },
                    new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, SettingsPath, overwrite: true);
                _singleWindowMode = enabled;
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
