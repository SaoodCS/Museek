using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Museek.Services;

var folder = Path.Combine(Path.GetTempPath(), "Museek ArtworkChecks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var audio = Path.Combine(folder, "音楽 source ' & audio.wav");
    await FfmpegAsync("-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "pcm_s16le", audio);
    var jpeg = Path.Combine(folder, "wide red cover.jpg");
    var png = Path.Combine(folder, "tall blue cover.png");
    var small = Path.Combine(folder, "small green cover.png");
    var nonSquarePixels = Path.Combine(folder, "non-square pixels blue cover.png");
    await FfmpegAsync("-f", "lavfi", "-i", "color=c=red:size=640x480", "-frames:v", "1", "-update", "1", jpeg);
    await FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=300x600", "-frames:v", "1", "-update", "1", png);
    await FfmpegAsync("-f", "lavfi", "-i", "color=c=green:size=48x48", "-frames:v", "1", "-update", "1", small);
    await FfmpegAsync("-f", "lavfi", "-i", "color=c=blue:size=100x200", "-vf", "setsar=2",
        "-frames:v", "1", "-update", "1", nonSquarePixels);

    var service = new AlbumArtworkService();
    var sources = new List<string>();
    foreach (var extension in new[] { ".mp3", ".flac" })
    {
        foreach (var image in new[] { jpeg, png })
        {
            var source = Path.Combine(folder, Path.GetFileNameWithoutExtension(image) + extension);
            sources.Add(source);
            await CreateAudioWithCoverAsync(audio, image, source);
            var sourceHash = await HashAsync(source);
            var filesBefore = Directory.EnumerateFiles(folder).Order().ToArray();
            var artwork = await service.LoadAsync(source);
            var expectedWidth = image == jpeg ? 384 : 192;
            var expectedHeight = image == jpeg ? 288 : 384;
            CheckPng(artwork, expectedWidth, expectedHeight, extension + " embedded " + Path.GetExtension(image));
            await CheckColorAsync(artwork!, image == jpeg ? "red" : "blue", "embedded cover content is preserved");
            var sourceHashAfter = await HashAsync(source);
            Check(sourceHash.SequenceEqual(sourceHashAfter), "artwork loading preserves the audio file");
            Check(filesBefore.SequenceEqual(Directory.EnumerateFiles(folder).Order()), "artwork loading creates no files");
        }
    }

    var tinyCoverAudio = Path.Combine(folder, "small artwork.mp3");
    await CreateAudioWithCoverAsync(audio, small, tinyCoverAudio);
    CheckPng(await service.LoadAsync(tinyCoverAudio), 48, 48, "small covers are not enlarged");
    var nonSquareCoverAudio = Path.Combine(folder, "non-square pixel artwork.flac");
    await CreateAudioWithCoverAsync(audio, nonSquarePixels, nonSquareCoverAudio);
    CheckPng(await service.LoadAsync(nonSquareCoverAudio), 100, 100, "non-square-pixel covers preserve their display proportions");

    var multipleCovers = Path.Combine(folder, "multiple artwork.mp3");
    await FfmpegAsync("-i", audio, "-i", jpeg, "-i", png, "-map", "0:a:0", "-map", "1:v:0", "-map", "2:v:0",
        "-c:a", "libmp3lame", "-c:v", "copy", "-disposition:v:0", "attached_pic", "-disposition:v:1", "attached_pic",
        "-metadata:s:v:0", "title=First cover", "-metadata:s:v:0", "comment=Cover (front)",
        "-metadata:s:v:1", "title=Second cover", "-metadata:s:v:1", "comment=Cover (back)", multipleCovers);
    var firstCover = await service.LoadAsync(multipleCovers);
    CheckPng(firstCover, 384, 288, "the first attached artwork stream is used");
    await CheckColorAsync(firstCover!, "red", "later artwork does not replace the first cover");

    Check(await service.LoadAsync(audio) is null, "audio without artwork returns no cover");
    var video = Path.Combine(folder, "ordinary video.mp4");
    await FfmpegAsync("-i", audio, "-f", "lavfi", "-i", "color=c=blue:size=64x64:rate=1:duration=2",
        "-map", "1:v:0", "-map", "0:a:0", "-c:v", "mpeg4", "-c:a", "aac", video);
    Check(await service.LoadAsync(video) is null, "ordinary video is never used as album artwork");
    var videoWithArt = Path.Combine(folder, "video and attached cover.mp4");
    await FfmpegAsync("-i", video, "-i", jpeg, "-map", "0", "-map", "1:v:0", "-c", "copy",
        "-disposition:v:1", "attached_pic", videoWithArt);
    var attachedInsteadOfVideo = await service.LoadAsync(videoWithArt);
    CheckPng(attachedInsteadOfVideo, 384, 288, "artwork after an ordinary video stream is mapped correctly");
    await CheckColorAsync(attachedInsteadOfVideo!, "red", "video frames are ignored when an attached cover exists");

    var invalid = Path.Combine(folder, "invalid audio.mp3");
    await File.WriteAllTextAsync(invalid, "This is not an audio file or image.");
    Check(await service.LoadAsync(invalid) is null, "invalid audio gracefully returns no cover");
    Check(await service.LoadAsync(Path.Combine(folder, "missing.mp3")) is null, "missing audio gracefully returns no cover");
    Check(await service.LoadAsync(string.Empty) is null, "empty paths gracefully return no cover");

    var noArtMp3 = Path.Combine(folder, "untagged.mp3");
    await FfmpegAsync("-i", audio, "-map_metadata", "-1", "-c:a", "libmp3lame", noArtMp3);
    var corruptCover = Path.Combine(folder, "broken embedded cover.mp3");
    await WriteCorruptCoverAsync(noArtMp3, corruptCover);
    Check(await service.LoadAsync(corruptCover) is null, "a malformed embedded cover gracefully returns no cover");

    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe")))
    {
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Check(await service.LoadAsync(sources[0]) is null, "missing tools gracefully return no cover");
        }
        finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
    }

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await ThrowsCancellationAsync(() => service.LoadAsync(sources[0], cancelled.Token), "requested cancellation propagates");
    }
    using (var cancellation = new CancellationTokenSource())
    {
        var toolIdsBefore = GetToolProcessIds();
        var loading = service.LoadAsync(sources[0], cancellation.Token);
        Check(!loading.IsCompleted, "cancellation interrupts an active artwork load");
        var startedIds = GetToolProcessIds().Except(toolIdsBefore).ToArray();
        cancellation.Cancel();
        await ThrowsCancellationAsync(() => loading, "active artwork loading can be cancelled");
        Check(startedIds.Length > 0, "active cancellation reaches a running media tool");
        Check(startedIds.All(HasExited), "cancelled artwork tools exit before loading completes");
    }
    Console.WriteLine("All artwork integration checks passed.");
}
finally
{
    // Remove only the unique directory created by this integration runner.
    Directory.Delete(folder, recursive: true);
}

static async Task CreateAudioWithCoverAsync(string audio, string image, string destination)
{
    await FfmpegAsync("-i", audio, "-i", image, "-map", "0:a:0", "-map", "1:v:0",
        "-c:a", Path.GetExtension(destination) == ".mp3" ? "libmp3lame" : "flac", "-c:v", "copy",
        "-disposition:v:0", "attached_pic", destination);
}

static Task<byte[]> FfmpegAsync(params string[] arguments)
    => RunToolAsync("ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-y", .. arguments]);

static async Task<byte[]> RunToolAsync(string executable, string[] arguments, byte[]? input = null)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null
    };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new Exception("Unable to start " + executable);
    using var output = new MemoryStream();
    var stdout = process.StandardOutput.BaseStream.CopyToAsync(output);
    var stderr = process.StandardError.ReadToEndAsync();
    if (input is not null)
    {
        await process.StandardInput.BaseStream.WriteAsync(input);
        process.StandardInput.Close();
    }
    await process.WaitForExitAsync();
    await stdout;
    var error = await stderr;
    if (process.ExitCode != 0) throw new Exception(executable + ": " + error);
    return output.ToArray();
}

static void CheckPng(byte[]? artwork, int width, int height, string description)
{
    Check(artwork is { Length: > 24 } && artwork.Length <= 4 * 1024 * 1024 &&
        artwork.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        BinaryPrimitives.ReadUInt32BigEndian(artwork.AsSpan(16, 4)) == width &&
        BinaryPrimitives.ReadUInt32BigEndian(artwork.AsSpan(20, 4)) == height, description);
}

static async Task CheckColorAsync(byte[] artwork, string color, string description)
{
    var pixels = await RunToolAsync("ffmpeg.exe", ["-hide_banner", "-loglevel", "error", "-f", "image2pipe",
        "-i", "pipe:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], artwork);
    Check(pixels.Length >= 3 && (color == "red" ? pixels[0] > 245 && pixels[1] < 10 && pixels[2] < 10
        : pixels[0] < 10 && pixels[1] < 10 && pixels[2] > 245), description);
}

static async Task WriteCorruptCoverAsync(string source, string destination)
{
    var audio = await File.ReadAllBytesAsync(source);
    var offset = audio.AsSpan(0, 3).SequenceEqual("ID3"u8) ? 10 +
        ((audio[6] << 21) | (audio[7] << 14) | (audio[8] << 7) | audio[9]) : 0;
    byte[] picture = [0, .. Encoding.ASCII.GetBytes("image/jpeg"), 0, 3, 0, 255, 216, 255, 0, 1, 2, 3];
    var frame = new byte[10 + picture.Length];
    "APIC"u8.CopyTo(frame);
    BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), picture.Length);
    picture.CopyTo(frame, 10);
    var tag = new byte[10 + frame.Length + audio.Length - offset];
    "ID3"u8.CopyTo(tag);
    tag[3] = 3;
    tag[6] = (byte)((frame.Length >> 21) & 127);
    tag[7] = (byte)((frame.Length >> 14) & 127);
    tag[8] = (byte)((frame.Length >> 7) & 127);
    tag[9] = (byte)(frame.Length & 127);
    frame.CopyTo(tag, 10);
    audio.AsSpan(offset).CopyTo(tag.AsSpan(10 + frame.Length));
    await File.WriteAllBytesAsync(destination, tag);
}

static async Task<byte[]> HashAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return await SHA256.HashDataAsync(stream);
}

static HashSet<int> GetToolProcessIds()
{
    if (OperatingSystem.IsWindows()) return NativeTestMethods.GetOwnToolIds();
    var ids = new HashSet<int>();
    foreach (var name in new[] { "ffprobe", "ffmpeg" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process) ids.Add(process.Id);
        }
    return ids;
}

static bool HasExited(int processId)
{
    try { using var process = Process.GetProcessById(processId); return process.HasExited; }
    catch (ArgumentException) { return true; }
}

static async Task ThrowsCancellationAsync(Func<Task> operation, string description)
{
    try { await operation(); }
    catch (OperationCanceledException) { Console.WriteLine("PASS: " + description); return; }
    throw new Exception("FAIL: " + description);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static class NativeTestMethods
{
    public static HashSet<int> GetOwnToolIds()
    {
        var ids = new HashSet<int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) return ids;
        do
        {
            if (entry.ParentProcessId == Environment.ProcessId &&
                (entry.Executable.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) ||
                 entry.Executable.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)))
                ids.Add((int)entry.ProcessId);
        } while (Process32Next(snapshot, ref entry));
        return ids;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
