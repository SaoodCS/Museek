using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Museek.Services;

public sealed class AlbumArtworkService
{
    private const int MaximumArtworkBytes = 4 * 1024 * 1024;
    private const int MaximumProbeBytes = 256 * 1024;

    public async Task<byte[]?> LoadAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            var source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source)) return null;
            var probe = await RunAsync("ffprobe", [
                "-v", "error", "-protocol_whitelist", "file,pipe", "-select_streams", "v",
                "-show_entries", "stream=index,codec_type:stream_disposition=attached_pic", "-of", "json", source
            ], MaximumProbeBytes, cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0 || probe.Truncated) return null;
            var artworkIndex = FindArtworkStream(probe.Output);
            if (artworkIndex is null) return null;

            var image = await RunAsync("ffmpeg", [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-protocol_whitelist", "file,pipe",
                "-i", source, "-map", "0:" + artworkIndex.Value.ToString(CultureInfo.InvariantCulture),
                "-an", "-sn", "-dn", "-frames:v", "1",
                "-vf", "scale=w='min(384,iw)':h='min(384,ih)':force_original_aspect_ratio=decrease:reset_sar=1:flags=lanczos",
                "-c:v", "png", "-pix_fmt", "rgba", "-f", "image2pipe", "pipe:1"
            ], MaximumArtworkBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image.ExitCode == 0 && !image.Truncated && IsBoundedPng(image.Output) ? image.Output : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static int? FindArtworkStream(byte[] metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array) return null;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object ||
                !stream.TryGetProperty("codec_type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "video" ||
                !stream.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
                !index.TryGetInt32(out var streamIndex) || streamIndex < 0 ||
                !stream.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.Object ||
                !disposition.TryGetProperty("attached_pic", out var attached) || attached.ValueKind != JsonValueKind.Number ||
                !attached.TryGetInt32(out var isAttached) || isAttached != 1) continue;
            return streamIndex;
        }
        return null;
    }

    private static bool IsBoundedPng(byte[] bytes)
    {
        if (bytes.Length < 45 || bytes.Length > MaximumArtworkBytes ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 12, 4)) != 0 ||
            !bytes.AsSpan(bytes.Length - 8, 4).SequenceEqual("IEND"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= 384 && height is > 0 and <= 384;
    }

    private static async Task<ToolResult> RunAsync(string name, IEnumerable<string> arguments, int byteLimit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        var startInfo = new ProcessStartInfo(File.Exists(bundled) ? bundled : name + ".exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new ToolResult(-1, [], false);
        using var cancellation = cancellationToken.Register(() => TryKill(process));
        var output = ReadOutputAsync(process, byteLimit);
        var error = DrainErrorAsync(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ToolResult(process.ExitCode, result.Bytes, result.Truncated);
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            // Wait for both redirected pipes to close before disposing the child, including on cancellation.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<BoundedOutput> ReadOutputAsync(Process process, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var truncated = false;
        try
        {
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (truncated) continue;
                if (count > limit - output.Length)
                {
                    truncated = true;
                    TryKill(process);
                    continue;
                }
                output.Write(buffer, 0, count);
            }
            return new BoundedOutput(truncated ? [] : output.ToArray(), truncated);
        }
        catch { TryKill(process); throw; }
    }

    private static async Task DrainErrorAsync(Process process)
    {
        var buffer = new byte[8192];
        try
        {
            // Errors are intentionally discarded: a failed cover simply uses the UI placeholder.
            while (await process.StandardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
        }
        catch { TryKill(process); throw; }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private sealed record BoundedOutput(byte[] Bytes, bool Truncated);
    private sealed record ToolResult(int ExitCode, byte[] Output, bool Truncated);
}
