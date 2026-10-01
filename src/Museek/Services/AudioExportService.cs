using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Museek.Models;

namespace Museek.Services;

public sealed class AudioExportService
{
    private const int MaximumErrorLength = 16_384;
    private const int MaximumProbeLength = 262_144;
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(50);

    public async Task<AudioInfo> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = ValidateSourcePath(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await RunAsync("ffprobe", [
            "-v", "error", "-select_streams", "a:0", "-show_entries",
            "stream=codec_name,duration:stream_tags=title:format=duration,format_name:format_tags=title",
            "-of", "json", source
        ], reader => CaptureAsync(reader, MaximumProbeLength, keepTail: false), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException(DescribeFailure("This file could not be read as audio", result.Error));
        if (result.Output.Truncated)
            throw new InvalidDataException("The file's audio metadata is too large to read.");

        try
        {
            using var json = JsonDocument.Parse(result.Output.Text);
            var root = json.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                throw new InvalidDataException("This file does not contain an audio stream.");
            var audio = streams[0];
            root.TryGetProperty("format", out var format);
            var duration = ReadDuration(audio) ?? ReadDuration(format)
                ?? throw new InvalidDataException("The audio duration could not be determined.");
            var title = ReadTitle(format) ?? ReadTitle(audio) ?? Path.GetFileNameWithoutExtension(source);
            var formatName = ReadString(format, "format_name")?.Split(',')[0]
                ?? ReadString(audio, "codec_name") ?? Path.GetExtension(source).TrimStart('.');
            var extension = Path.GetExtension(source);
            if (formatName.Equals("mov", StringComparison.OrdinalIgnoreCase) &&
                (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase)))
                formatName = extension.TrimStart('.');
            return new AudioInfo(duration, title, formatName.ToUpperInvariant());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The audio metadata could not be read.", exception);
        }
    }

    public async Task ExportAsync(string sourcePath, string destinationPath, TimeSpan start, TimeSpan end,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = ValidateSourcePath(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        EnsureDifferentFile(source, destination);
        if (start < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(start), "The start time cannot be negative.");
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end), "The end time must be after the start time.");
        var encoding = GetEncoding(Path.GetExtension(destination));
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The output folder does not exist.");

        var info = await ProbeAsync(source, cancellationToken).ConfigureAwait(false);
        if (end.TotalSeconds > info.Duration.TotalSeconds + DurationTolerance.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(end), "The end time is past the end of the audio.");
        end = end > info.Duration ? info.Duration : end;
        if (start >= end)
            throw new ArgumentOutOfRangeException(nameof(start), "The start time must be before the end of the audio.");
        var length = end - start;
        var temporary = Path.Combine(directory, ".museek-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reserve a unique file in the destination folder so the final replacement stays on one filesystem.
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            progress?.Report(0);
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-y",
                "-progress", "pipe:1", "-stats_period", "0.1", "-ss", Seconds(start), "-accurate_seek",
                "-i", source, "-t", Seconds(length), "-map", "0:a:0", "-vn", "-sn", "-dn",
                // Ogg/Opus store tags on the audio stream; also copy them to formats with global tags.
                "-map_metadata", "0", "-map_metadata", "0:s:a:0"
            };
            arguments.AddRange(encoding);
            arguments.Add(temporary);
            var result = await RunAsync("ffmpeg", arguments,
                reader => ReadProgressAsync(reader, length, progress), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(DescribeFailure("The audio export failed", result.Error));
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("The encoder did not produce an audio file.");
            cancellationToken.ThrowIfCancellationRequested();
            // Check again in case the destination became an alias while encoding.
            EnsureDifferentFile(source, destination);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            // Only our unique output is removed; the source and any existing destination survive a failed export.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ValidateSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The audio file was not found.", source);
        return source;
    }

    private static void EnsureDifferentFile(string source, string destination)
    {
        var sameFile = StringComparer.OrdinalIgnoreCase.Equals(source, destination);
        if (!sameFile && File.Exists(destination))
        {
            if (OperatingSystem.IsWindows())
            {
                // File IDs catch hard links, short 8.3 names, and paths through junctions or symlinks.
                const FileShare sharing = FileShare.ReadWrite | FileShare.Delete;
                using var sourceHandle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, sharing);
                using var destinationHandle = File.OpenHandle(destination, FileMode.Open, FileAccess.Read, sharing);
                var sourceInfo = ReadFileIdentity(sourceHandle);
                var destinationInfo = ReadFileIdentity(destinationHandle);
                sameFile = sourceInfo.VolumeSerialNumber == destinationInfo.VolumeSerialNumber &&
                    sourceInfo.FileIndexHigh == destinationInfo.FileIndexHigh &&
                    sourceInfo.FileIndexLow == destinationInfo.FileIndexLow;
            }
            else
            {
                var sourceTarget = File.ResolveLinkTarget(source, returnFinalTarget: true)?.FullName ?? source;
                var destinationTarget = File.ResolveLinkTarget(destination, returnFinalTarget: true)?.FullName ?? destination;
                sameFile = StringComparer.Ordinal.Equals(sourceTarget, destinationTarget);
            }
        }
        if (sameFile)
            throw new ArgumentException("Choose a different output file to keep the original audio safe.", "destinationPath");
    }

    private static ByHandleFileInformation ReadFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("The output file could not be checked safely.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static string[] GetEncoding(string extension) => extension.ToLowerInvariant() switch
    {
        ".wav" => ["-c:a", "pcm_s16le", "-f", "wav"],
        ".mp3" => ["-c:a", "libmp3lame", "-q:a", "2", "-ac", "2", "-f", "mp3"],
        ".flac" => ["-c:a", "flac", "-compression_level", "5", "-f", "flac"],
        ".m4a" => ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "ipod"],
        ".ogg" => ["-c:a", "libvorbis", "-q:a", "5", "-f", "ogg"],
        ".opus" => ["-c:a", "libopus", "-b:a", "128k", "-f", "opus"],
        _ => throw new NotSupportedException("Choose WAV, MP3, FLAC, M4A, OGG, or Opus for the output file.")
    };

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture);

    private static TimeSpan? ReadDuration(JsonElement element)
    {
        var text = ReadString(element, "duration");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? ReadTitle(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("tags", out var tags)) return null;
        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) && tag.Value.ValueKind == JsonValueKind.String)
            {
                var title = tag.Value.GetString();
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static async Task<ProcessResult> RunAsync(string name, IEnumerable<string> arguments,
        Func<StreamReader, Task<CapturedText>> readOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        var executable = File.Exists(bundled) ? bundled : name + ".exe";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start " + name + ".");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new FileNotFoundException("The audio tools are missing. Reinstall Museek or install FFmpeg.", executable, exception);
        }

        using var cancellation = cancellationToken.Register(() => TryKill(process));
        var stdout = readOutput(process.StandardOutput);
        var stderr = CaptureAsync(process.StandardError, MaximumErrorLength, keepTail: true);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, output, error.Text);
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            // Drain both pipes before disposal, even when a reader or progress observer failed.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch { }
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<CapturedText> CaptureAsync(StreamReader reader, int limit, bool keepTail)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (keepTail)
            {
                text.Append(buffer, 0, read);
                if (text.Length > limit)
                {
                    text.Remove(0, text.Length - limit);
                    truncated = true;
                }
            }
            else
            {
                var remaining = limit - text.Length;
                text.Append(buffer, 0, Math.Min(remaining, read));
                if (read > remaining) truncated = true;
            }
        }
        return new CapturedText(text.ToString(), truncated);
    }

    private static async Task<CapturedText> ReadProgressAsync(StreamReader reader, TimeSpan length, IProgress<double>? progress)
    {
        var last = 0.0;
        Exception? observerError = null;
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                continue;
            var value = Math.Clamp(microseconds / 1_000_000.0 / length.TotalSeconds, 0, 0.999);
            if (value <= last || observerError != null) continue;
            last = value;
            try { progress?.Report(value); }
            catch (Exception exception) { observerError = exception; }
        }
        if (observerError != null) throw observerError;
        return new CapturedText(string.Empty, false);
    }

    private static string DescribeFailure(string description, string error)
        => string.IsNullOrWhiteSpace(error) ? description + "." : description + ": " + error.Trim();

    private sealed record CapturedText(string Text, bool Truncated);
    private sealed record ProcessResult(int ExitCode, CapturedText Output, string Error);
}
