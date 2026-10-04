using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Museek.Models;
using Museek.Services;

// Every input is generated under one scratch directory. No library files, user
// settings, Explorer registrations, native windows, or audio output are used.
var folder = Path.Combine(Path.GetTempPath(), "Museek TagChecks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var checks = 0;
try
{
    var service = new AudioTagService();
    var pngPath = Path.Combine(folder, "blue cover.png");
    var jpegPath = Path.Combine(folder, "red cover.jpg");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=0x238fa8:s=64x64:d=1", "-frames:v", "1", "-pix_fmt", "rgb24", "-update", "1", "-y", pngPath);
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=0xe15337:s=80x64:d=1", "-frames:v", "1", "-pix_fmt", "yuvj420p", "-update", "1", "-y", jpegPath);
    var png = await File.ReadAllBytesAsync(pngPath);
    var jpeg = await File.ReadAllBytesAsync(jpegPath);

    // Measure the real validation paths without including fixture creation, async
    // scheduling or reflection setup in the managed allocation count.
    var validateArtwork = typeof(AudioTagService).GetMethod("ValidatePatch", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<TagEditPatch, string?>>();
    var verifyPatch = typeof(AudioTagService).GetMethod("VerifyPatch", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<TagLib.File, TagEditPatch>>();
    var readFileTags = typeof(AudioTagService).GetMethod("ReadFileTags", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<TagLib.File, string, bool, AudioTags>>();
    var largeCoverPath = Path.Combine(folder, "large cover.png");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=0x238fa8:s=4098x4098:d=1", "-frames:v", "1", "-pix_fmt", "rgb24", "-update", "1", "-y", largeCoverPath);
    var largeCover = await File.ReadAllBytesAsync(largeCoverPath);
    var largePatch = new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = largeCover };
    validateArtwork(new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = png });
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var largeMime = validateArtwork(largePatch);
    var validationBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

    var verificationSource = Path.Combine(folder, "verification allocation source.flac");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=397:duration=0.1", "-c:a", "flac", "-y", verificationSource);
    long verificationBytes;
    long readBytes;
    bool isolatedArtwork;
    using (var verificationFile = TagLib.File.Create(verificationSource))
    {
        verificationFile.Tag.Title = "Selected title";
        verificationFile.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[6 * 1024 * 1024]))
            { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
        var textPatch = new TagEditPatch { Title = "Selected title" };
        verifyPatch(verificationFile, textPatch);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        verifyPatch(verificationFile, textPatch);
        verificationBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        readFileTags(verificationFile, verificationSource, true);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var readTags = readFileTags(verificationFile, verificationSource, true);
        readBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        readTags.ArtworkBytes![0] = 1;
        isolatedArtwork = verificationFile.Tag.Pictures[0].Data[0] == 0;
    }
    Console.WriteLine($"Managed allocations: large-cover validation {validationBytes:N0} bytes; text-only verification {verificationBytes:N0} bytes; artwork read {readBytes:N0} bytes.");
    Check(largeMime == "image/png", "large valid artwork with partial edge tiles is accepted");
    Check(validationBytes < 4 * 1024 * 1024, "artwork validation uses bounded managed scratch memory");
    Check(verificationBytes < 1024 * 1024, "text-only verification does not copy unrelated cover bytes");
    Check(readBytes < 7 * 1024 * 1024, "reading artwork allocates one owned cover buffer");
    Check(isolatedArtwork, "mutating returned artwork does not change the loaded tag data");
    // Retain a complete PNG signature/IHDR with valid dimensions, but no pixels.
    var corruptCover = largeCover.AsSpan(0, 33).ToArray();
    var corruptRejected = false;
    try { validateArtwork(new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = corruptCover }); }
    catch (InvalidDataException) { corruptRejected = true; }
    Check(corruptRejected, "artwork validation rejects an intact image header without encoded pixels");
    if (args.Contains("--allocation-checks-only", StringComparer.Ordinal)) return;
    await CheckReplacementRecoveryAsync(verificationSource);
    if (args.Contains("--replacement-checks-only", StringComparer.Ordinal)) return;

    var fixtures = new List<string>();
    foreach (var format in new (string Extension, string Codec)[]
    {
        (Extension: ".mp3", Codec: "libmp3lame"), (".flac", "flac"), (".m4a", "aac"),
        (".ogg", "libvorbis"), (".opus", "libopus"), (".wav", "pcm_s16le")
    })
    {
        var path = Path.Combine(folder, "音楽 ' & sample" + format.Extension);
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "sine=frequency=397:sample_rate=48000:duration=3", "-c:a", format.Codec,
            "-metadata", "title=Original title 音楽", "-metadata", "artist=Original artist",
            "-metadata", "album=Original album", "-metadata", "genre=Original genre", "-y", path);
        fixtures.Add(path);
        var pcmBefore = await PcmHashAsync(path);
        var packetsBefore = await AudioPacketHashAsync(path);
        var initial = await service.ReadAsync(path);
        Check(initial.Path == Path.GetFullPath(path) && initial.Title == "Original title 音楽" &&
            initial.Artist == "Original artist" && initial.Album == "Original album" && initial.AlbumArtist is null,
            format.Extension + " reads real Unicode title, artist, and album tags");

        var seed = new TagEditPatch
        {
            Title = "Original title 音楽", Artist = "Original artist", Album = "Original album",
            AlbumArtist = "Original album artist", Genre = "Original genre", Comment = "Original comment",
            Year = 2024, TrackNumber = 7, DiscNumber = 2
        };
        await ExpectSuccessAsync([path], seed, format.Extension + " stores all supported fields");
        var before = await service.ReadAsync(path);
        Check(before.Title == seed.Title && before.Artist == seed.Artist && before.Album == seed.Album &&
            before.AlbumArtist == seed.AlbumArtist && before.Genre == seed.Genre && before.Comment == seed.Comment &&
            before.Year == seed.Year && before.TrackNumber == seed.TrackNumber && before.DiscNumber == seed.DiscNumber,
            format.Extension + " round-trips all nine editable fields");

        await ExpectSuccessAsync([path], new TagEditPatch { Title = "Edited title Å 音楽" },
            format.Extension + " applies one selected text field");
        var after = await service.ReadAsync(path);
        Check(after.Title == "Edited title Å 音楽" && after.Artist == before.Artist && after.Album == before.Album &&
            after.AlbumArtist == before.AlbumArtist && after.Genre == before.Genre && after.Comment == before.Comment &&
            after.Year == before.Year && after.TrackNumber == before.TrackNumber && after.DiscNumber == before.DiscNumber,
            format.Extension + " preserves every unselected field");
        var externalTags = await ProbeTagsAsync(path);
        Check(GetTag(externalTags, "title") == after.Title && GetTag(externalTags, "artist") == before.Artist &&
            GetTag(externalTags, "album") == before.Album,
            format.Extension + " exposes the saved title, artist, and album to FFprobe");

        await ExpectSuccessAsync([path], new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = png },
            format.Extension + " adds PNG album artwork");
        Check((await service.ReadAsync(path)).ArtworkBytes?.SequenceEqual(png) == true,
            format.Extension + " stores the chosen PNG artwork bytes");
        var nativeCover = await new AlbumArtworkService().LoadAsync(path);
        Check(nativeCover is { Length: > 0 }, format.Extension + " exposes embedded PNG artwork to FFmpeg");
        await ExpectSuccessAsync([path], new TagEditPatch { Album = "Changed album" },
            format.Extension + " updates a tag while keeping artwork");
        Check((await service.ReadAsync(path)).ArtworkBytes?.SequenceEqual(png) == true,
            format.Extension + " keeps its cover when artwork is unselected");

        await ExpectSuccessAsync([path], new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = jpeg },
            format.Extension + " replaces album artwork with JPEG");
        Check((await service.ReadAsync(path)).ArtworkBytes?.SequenceEqual(jpeg) == true &&
            (await new AlbumArtworkService().LoadAsync(path)) is { Length: > 0 },
            format.Extension + " exposes the replaced JPEG artwork");
        await ExpectSuccessAsync([path], new TagEditPatch { ArtworkAction = ArtworkAction.Remove },
            format.Extension + " removes embedded album artwork");
        Check((await service.ReadAsync(path)).ArtworkBytes is null &&
            await new AlbumArtworkService().LoadAsync(path) is null,
            format.Extension + " removes artwork for both metadata and player readers");

        await ExpectSuccessAsync([path], new TagEditPatch { Title = "", Genre = "", Year = 0, TrackNumber = 0 },
            format.Extension + " explicitly clears chosen string and numeric tags");
        var cleared = await service.ReadAsync(path);
        Check(string.IsNullOrEmpty(cleared.Title) && string.IsNullOrEmpty(cleared.Genre) &&
            cleared.Year == 0 && cleared.TrackNumber == 0 && cleared.Artist == before.Artist &&
            cleared.Album == "Changed album" && cleared.DiscNumber == before.DiscNumber,
            format.Extension + " clears selected values and keeps unrelated ones");
        Check((await PcmHashAsync(path)).SequenceEqual(pcmBefore),
            format.Extension + " tag and cover edits preserve every decoded audio sample");
        Check(await AudioPacketHashAsync(path) == packetsBefore,
            format.Extension + " tag and cover edits preserve the encoded audio packets");
        CheckNoTemporaryFiles();
    }

    var batchInputs = fixtures.Take(3).ToArray();
    var oldBatch = await Task.WhenAll(batchInputs.Select(path => service.ReadAsync(path)));
    var progress = new List<TagEditProgress>();
    var batch = await service.ApplyAsync(batchInputs, new TagEditPatch { Genre = "Batch genre" },
        new InlineProgress<TagEditProgress>(value => progress.Add(value)));
    Check(!batch.Cancelled && batch.Files.Count == batchInputs.Length && batch.Files.All(result => result.Success) &&
        progress.Count == batchInputs.Length && progress.Select(value => value.Completed).SequenceEqual([1, 2, 3]) &&
        progress.All(value => value.Total == batchInputs.Length),
        "batch editing returns individual results and completed-file progress");
    for (var i = 0; i < batchInputs.Length; i++)
    {
        var edited = await service.ReadAsync(batchInputs[i]);
        Check(edited.Genre == "Batch genre" && edited.Title == oldBatch[i].Title &&
            edited.Artist == oldBatch[i].Artist && edited.Album == oldBatch[i].Album &&
            edited.Year == oldBatch[i].Year && edited.DiscNumber == oldBatch[i].DiscNumber,
            Path.GetExtension(batchInputs[i]) + " batch patches preserve per-file differences");
    }

    var invalid = Path.Combine(folder, "corrupt.mp3");
    await File.WriteAllTextAsync(invalid, "This is not audio.");
    var invalidHash = await HashAsync(invalid);
    var missing = Path.Combine(folder, "missing.mp3");
    var partial = await service.ApplyAsync([invalid, missing, fixtures[0]], new TagEditPatch { Genre = "After failure" });
    Check(!partial.Cancelled && partial.Files.Count == 3 && !partial.Files[0].Success && !partial.Files[1].Success &&
        partial.Files[2].Success && partial.Files.Take(2).All(result => !string.IsNullOrWhiteSpace(result.Error)),
        "one invalid or missing file reports an error without preventing later valid files");
    Check((await HashAsync(invalid)).SequenceEqual(invalidHash) && !File.Exists(missing),
        "failed tag edits preserve corrupt inputs and do not create missing originals");
    CheckNoTemporaryFiles();

    var protectedInput = fixtures[1];
    var protectedHash = await HashAsync(protectedInput);
    File.SetAttributes(protectedInput, File.GetAttributes(protectedInput) | FileAttributes.ReadOnly);
    try
    {
        var readOnlyResult = await service.ApplyAsync([protectedInput], new TagEditPatch { Title = "Must fail" });
        Check(readOnlyResult.Files.Count == 1 && !readOnlyResult.Files[0].Success &&
            (await HashAsync(protectedInput)).SequenceEqual(protectedHash),
            "a read-only file reports failure and stays byte-identical");
    }
    finally { File.SetAttributes(protectedInput, File.GetAttributes(protectedInput) & ~FileAttributes.ReadOnly); }
    using (var locked = new FileStream(protectedInput, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var lockedResult = await service.ApplyAsync([protectedInput], new TagEditPatch { Title = "Must fail" });
        Check(lockedResult.Files.Count == 1 && !lockedResult.Files[0].Success,
            "a file locked by another program reports failure");
    }
    Check((await HashAsync(protectedInput)).SequenceEqual(protectedHash),
        "a locked-file failure preserves its source after the handle is released");
    CheckNoTemporaryFiles();

    var link = Path.Combine(folder, "hard-linked audio.flac");
    if (!NativeMethods.CreateHardLink(link, protectedInput, IntPtr.Zero))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create scratch hard link.");
    var hardLinkResult = await service.ApplyAsync([link], new TagEditPatch { Title = "Must fail" });
    Check(hardLinkResult.Files.Count == 1 && !hardLinkResult.Files[0].Success &&
        (await HashAsync(protectedInput)).SequenceEqual(protectedHash) && (await HashAsync(link)).SequenceEqual(protectedHash),
        "editing a hard link is rejected without splitting or changing either name");
    File.Delete(link);

    var cancelHashes = await Task.WhenAll(fixtures.Take(3).Select(HashAsync));
    using (var alreadyCancelled = new CancellationTokenSource())
    {
        alreadyCancelled.Cancel();
        var cancelled = await service.ApplyAsync(fixtures.Take(3).ToArray(), new TagEditPatch { Album = "Must not apply" },
            cancellationToken: alreadyCancelled.Token);
        Check(cancelled.Cancelled && cancelled.Files.Count == 0,
            "an already-cancelled batch edits no files");
    }
    for (var i = 0; i < cancelHashes.Length; i++)
        Check((await HashAsync(fixtures[i])).SequenceEqual(cancelHashes[i]),
            Path.GetExtension(fixtures[i]) + " pre-cancelled input stays byte-identical");
    using (var afterFirst = new CancellationTokenSource())
    {
        var cancelled = await service.ApplyAsync(fixtures.Take(3).ToArray(), new TagEditPatch { Album = "One committed file" },
            new InlineProgress<TagEditProgress>(value => { if (value.Completed == 1) afterFirst.Cancel(); }), afterFirst.Token);
        Check(cancelled.Cancelled && cancelled.Files.Count == 1 && cancelled.Files[0].Success &&
            (await service.ReadAsync(fixtures[0])).Album == "One committed file",
            "cancelling after one completed file reports the committed partial batch");
    }
    Check((await HashAsync(fixtures[1])).SequenceEqual(cancelHashes[1]) &&
        (await HashAsync(fixtures[2])).SequenceEqual(cancelHashes[2]),
        "batch cancellation preserves all later sources");
    CheckNoTemporaryFiles();

    var validationSource = fixtures[0];
    var validationHash = await HashAsync(validationSource);
    foreach (var invalidPatch in new[]
    {
        new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = [1, 2, 3, 4] },
        new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = new byte[8 * 1024 * 1024 + 1] },
        new TagEditPatch { ArtworkAction = ArtworkAction.Replace },
        new TagEditPatch { Year = 10000 }
    })
    {
        var rejected = false;
        try { await service.ApplyAsync([validationSource], invalidPatch); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && (await HashAsync(validationSource)).SequenceEqual(validationHash),
            "invalid artwork or out-of-range year is rejected before any original changes");
    }
    CheckNoTemporaryFiles();

    var customWave = Path.Combine(folder, "custom unchecked WAV metadata.wav");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=811:sample_rate=48000:duration=3", "-map_metadata", "-1", "-c:a", "pcm_s16le", "-y", customWave);
    using (var custom = TagLib.File.Create(customWave))
    {
        custom.RemoveTags(TagLib.TagTypes.AllTags);
        var id3 = (TagLib.Id3v2.Tag)custom.GetTag(TagLib.TagTypes.Id3v2, true);
        id3.Clear();
        id3.Title = "Custom original title";
        id3.Performers = ["First untouched artist", "Second untouched artist"];
        id3.Album = "Untouched ID3 album";
        id3.AlbumArtists = ["Untouched album artist"];
        id3.Genres = ["Jazz", "Funk"];
        id3.Track = 7;
        id3.TrackCount = 19;
        id3.Disc = 2;
        id3.DiscCount = 4;
        id3.Composers = ["Untouched composer"];
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, "Museek custom TXXX", true).Text = ["Custom value 音楽"];
        id3.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(png)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png", Description = "Front unchanged" },
            new TagLib.Picture(new TagLib.ByteVector(jpeg)) { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg", Description = "Back unchanged" }
        ];
        var info = (TagLib.Riff.InfoTag)custom.GetTag(TagLib.TagTypes.RiffInfo, true);
        info.Clear();
        info.SetValue("INAM", "Custom original title");
        info.SetValue("IART", "Untouched RIFF artist");
        info.SetValue("IPRD", "Untouched RIFF album");
        info.SetValue("IKEY", "Custom INFO value 音楽");
        custom.Save();
    }
    var standardCustom = Path.Combine(folder, "standard custom WAV metadata.wav");
    File.Copy(customWave, standardCustom);
    foreach (var customInput in new[] { standardCustom, customWave })
    {
        if (customInput == customWave) SeparateWaveTagChunks(customInput);
        var customBefore = await service.ReadAsync(customInput);
        var pcmBefore = await PcmHashAsync(customInput);
        var packetBefore = await AudioPacketHashAsync(customInput);
        await ExpectSuccessAsync([customInput], new TagEditPatch { Title = "Only this title changed" },
            Path.GetFileName(customInput) + " accepts a title-only patch");
        var customAfter = await service.ReadAsync(customInput);
        Check(customAfter.Title == "Only this title changed" && customAfter.Artist == customBefore.Artist &&
            customAfter.Album == customBefore.Album && customAfter.AlbumArtist == customBefore.AlbumArtist &&
            customAfter.Genre == customBefore.Genre && customAfter.TrackNumber == customBefore.TrackNumber &&
            customAfter.DiscNumber == customBefore.DiscNumber,
            "a custom WAV title patch preserves every unrelated visible field");
        using (var saved = TagLib.File.Create(customInput))
        {
            var id3 = (TagLib.Id3v2.Tag)saved.GetTag(TagLib.TagTypes.Id3v2, false);
            var info = (TagLib.Riff.InfoTag)saved.GetTag(TagLib.TagTypes.RiffInfo, false);
            Check(id3.TrackCount == 19 && id3.DiscCount == 4 && id3.Performers.SequenceEqual(["First untouched artist", "Second untouched artist"]) &&
                id3.Composers.SequenceEqual(["Untouched composer"]) &&
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, "Museek custom TXXX", false)?.Text.SequenceEqual(["Custom value 音楽"]) == true &&
                info.GetValuesAsStrings("IKEY").SequenceEqual(["Custom INFO value 音楽"]) &&
                info.GetValuesAsStrings("IART").SequenceEqual(["Untouched RIFF artist"]) &&
                info.GetValuesAsStrings("IPRD").SequenceEqual(["Untouched RIFF album"]),
                "a custom WAV title patch keeps unknown INFO/TXXX tags, multiple artists, composer, and total counts");
            Check(id3.Pictures.Length == 2 && id3.Pictures[0].Data.Data.SequenceEqual(png) &&
                id3.Pictures[1].Data.Data.SequenceEqual(jpeg) && id3.Pictures[0].Description == "Front unchanged" &&
                id3.Pictures[1].Description == "Back unchanged",
                "a custom WAV title patch preserves both front and back artwork bytes and descriptions");
        }
        Check(GetTag(await ProbeTagsAsync(customInput), "title") == "Only this title changed",
            "FFprobe observes the selected title after updating a custom WAV");
        Check((await PcmHashAsync(customInput)).SequenceEqual(pcmBefore) && await AudioPacketHashAsync(customInput) == packetBefore,
            "custom WAV edits preserve decoded samples and encoded packets");
        if (customInput == customWave)
            Check(ReadWaveChunks(File.ReadAllBytes(customInput)).Any(chunk => Encoding.ASCII.GetString(chunk, 0, 4) == "msek" &&
                chunk.AsSpan(8, 7).SequenceEqual(new byte[] { 7, 1, 8, 2, 9, 3, 10 })),
                "a WAV metadata edit preserves an unrelated binary chunk between tags and audio");
    }
    CheckNoTemporaryFiles();

    var large = Path.Combine(folder, "large guarded source.wav");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "anullsrc=r=48000:cl=mono", "-t", "720", "-c:a", "pcm_s16le", "-y", large);
    var largeHash = await HashAsync(large);
    var displaced = Path.Combine(folder, "externally renamed original.wav");
    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var watcher = new FileSystemWatcher(folder, ".museek-tags-*") { EnableRaisingEvents = true })
    {
        var handled = 0;
        watcher.Created += (_, _) =>
        {
            if (Interlocked.Exchange(ref handled, 1) != 0) return;
            try
            {
                // A second application can rename a file opened with delete sharing.
                // Put a different valid audio file at the original name during staging.
                File.Move(large, displaced);
                File.Copy(fixtures[2], large);
                changed.TrySetResult();
            }
            catch (Exception exception) { changed.TrySetException(exception); }
        };
        var conflictTask = service.ApplyAsync([large], new TagEditPatch { Title = "Must not overwrite replacement" });
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var conflict = await conflictTask;
        Check(conflict.Files.Count == 1 && !conflict.Files[0].Success &&
            !string.IsNullOrWhiteSpace(conflict.Files[0].Error),
            "replacing the source path during staging reports a source conflict");
    }
    var replacementHash = await HashAsync(fixtures[2]);
    Check((await HashAsync(large)).SequenceEqual(replacementHash) &&
        (await HashAsync(displaced)).SequenceEqual(largeHash),
        "a source conflict preserves the external replacement and renamed original");
    CheckNoTemporaryFiles();
    File.Delete(large);
    File.Move(displaced, large);

    using (var duringCopy = new CancellationTokenSource())
    using (var watcher = new FileSystemWatcher(folder, ".museek-tags-*") { EnableRaisingEvents = true })
    {
        var stageCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => { duringCopy.Cancel(); stageCreated.TrySetResult(); };
        var operation = service.ApplyAsync([large], new TagEditPatch { Title = "Must not survive cancellation" },
            cancellationToken: duringCopy.Token);
        await stageCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = await operation;
        Check(cancelled.Cancelled && cancelled.Files.Count == 1 && !cancelled.Files[0].Success,
            "cancelling a real staged copy leaves its file uncommitted");
    }
    Check((await HashAsync(large)).SequenceEqual(largeHash),
        "mid-copy cancellation leaves the original byte-identical");
    CheckNoTemporaryFiles();

    Console.WriteLine($"All {checks} isolated tag and artwork preservation checks passed.");

    void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }
    void CheckNoTemporaryFiles()
        => Check(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path).StartsWith(".museek-", StringComparison.OrdinalIgnoreCase)),
            "tag edit cleanup leaves no staged Museek files");
    async Task ExpectSuccessAsync(IReadOnlyList<string> paths, TagEditPatch patch, string description)
    {
        var result = await service.ApplyAsync(paths, patch);
        Check(!result.Cancelled && result.Files.Count == paths.Count && result.Files.All(file => file.Success),
            description + (result.Files.Any(file => !file.Success) ? ": " + string.Join("; ", result.Files.Select(file => file.Error)) : ""));
    }

    async Task CheckReplacementRecoveryAsync(string template)
    {
        const int unableToRemoveReplaced = unchecked((int)0x80070497);
        var patch = new TagEditPatch { Title = "Recovered title" };
        string CopyInput(string name)
        {
            var path = Path.Combine(folder, name + ".flac");
            File.Copy(template, path);
            return path;
        }
        var recovered = CopyInput("recover replacement");
        var attempts = 0;
        var retrying = new AudioTagService((temporary, path) =>
        {
            if (++attempts == 1) throw new IOException("Injected replacement removal failure.", unableToRemoveReplaced);
            File.Replace(temporary, path, destinationBackupFileName: null);
        });
        var result = await retrying.ApplyAsync([recovered], patch);
        Check(!result.Cancelled && result.Files.Count == 1 && result.Files[0].Success && attempts == 2 &&
            (await service.ReadAsync(recovered)).Title == "Recovered title",
            "a transient native1175 replacement failure retries and commits verified tags");
        CheckNoTemporaryFiles();

        var exhausted = CopyInput("exhaust replacement");
        var originalHash = await HashAsync(exhausted);
        attempts = 0;
        var namesPreserved = true;
        retrying = new AudioTagService((temporary, path) =>
        {
            attempts++;
            namesPreserved &= File.Exists(temporary) && File.Exists(path) &&
                SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(originalHash);
            throw new IOException("Injected persistent replacement removal failure.", unableToRemoveReplaced);
        });
        result = await retrying.ApplyAsync([exhausted], patch);
        Check(!result.Cancelled && result.Files.Count == 1 && !result.Files[0].Success && attempts == 4 &&
            namesPreserved && (await HashAsync(exhausted)).SequenceEqual(originalHash),
            "exhausted replacement retries keep the original and staged names intact until cleanup");
        CheckNoTemporaryFiles();

        var cancelled = CopyInput("cancel replacement retry");
        originalHash = await HashAsync(cancelled);
        attempts = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            retrying = new AudioTagService((_, _) =>
            {
                attempts++;
                cancellation.Cancel();
                throw new IOException("Injected replacement removal failure before cancellation.", unableToRemoveReplaced);
            });
            result = await retrying.ApplyAsync([cancelled], patch, cancellationToken: cancellation.Token);
        }
        Check(result.Cancelled && result.Files.Count == 1 && !result.Files[0].Success && attempts == 1 &&
            (await HashAsync(cancelled)).SequenceEqual(originalHash),
            "cancelling replacement backoff prevents another attempt and preserves the original");
        CheckNoTemporaryFiles();

        var conflict = CopyInput("replace source between retries");
        originalHash = await HashAsync(conflict);
        var external = CopyInput("external replacement between retries");
        using (var externalFile = TagLib.File.Create(external))
        {
            externalFile.Tag.Title = "External replacement";
            externalFile.Save();
        }
        var externalHash = await HashAsync(external);
        var displaced = Path.Combine(folder, "displaced retry original.flac");
        attempts = 0;
        retrying = new AudioTagService((_, path) =>
        {
            attempts++;
            File.Move(path, displaced);
            File.Copy(external, path);
            throw new IOException("Injected replacement removal failure after external replacement.", unableToRemoveReplaced);
        });
        result = await retrying.ApplyAsync([conflict], patch);
        Check(!result.Cancelled && result.Files.Count == 1 && !result.Files[0].Success && attempts == 1 &&
            result.Files[0].Error?.Contains("source file changed", StringComparison.OrdinalIgnoreCase) == true &&
            (await HashAsync(conflict)).SequenceEqual(externalHash) &&
            (await HashAsync(displaced)).SequenceEqual(originalHash),
            "replacement retries reject a changed source and preserve the external replacement");
        CheckNoTemporaryFiles();

        foreach (var nativeError in new[] { 1176, 1177 })
        {
            var terminal = CopyInput("terminal replacement error " + nativeError);
            originalHash = await HashAsync(terminal);
            attempts = 0;
            retrying = new AudioTagService((_, _) =>
            {
                attempts++;
                throw new IOException("Injected terminal replacement error.", unchecked((int)(0x80070000u | (uint)nativeError)));
            });
            result = await retrying.ApplyAsync([terminal], patch);
            Check(!result.Cancelled && result.Files.Count == 1 && !result.Files[0].Success && attempts == 1 &&
                (await HashAsync(terminal)).SequenceEqual(originalHash),
                "native" + nativeError + " replacement errors are terminal without a fallback overwrite");
            CheckNoTemporaryFiles();
        }
    }
}
finally
{
    // Only this generated, absolute scratch root is removed.
    var resolved = Path.GetFullPath(folder);
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolved).StartsWith("Museek TagChecks ", StringComparison.Ordinal))
    {
        foreach (var file in Directory.EnumerateFiles(resolved))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(resolved, recursive: true);
    }
}

static async Task<byte[]> HashAsync(string path) => SHA256.HashData(await File.ReadAllBytesAsync(path));
static async Task<byte[]> PcmHashAsync(string path)
{
    var rawPath = path + ".pcm";
    try
    {
        await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path,
            "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "s16le", "-y", rawPath);
        return await HashAsync(rawPath);
    }
    finally { File.Delete(rawPath); }
}
static async Task<string> AudioPacketHashAsync(string path)
    => (await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", path,
        "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-")).Trim();
static List<byte[]> ReadWaveChunks(byte[] bytes)
{
    if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        throw new InvalidDataException("The custom fixture must be a RIFF WAVE file.");
    var result = new List<byte[]>();
    var offset = 12;
    while (offset <= bytes.Length - 8)
    {
        var payloadLength = checked((int)BitConverter.ToUInt32(bytes, offset + 4));
        var chunkLength = checked(8 + payloadLength + (payloadLength & 1));
        if (offset + chunkLength > bytes.Length) throw new InvalidDataException("Incomplete scratch WAV chunk.");
        result.Add(bytes.AsSpan(offset, chunkLength).ToArray());
        offset += chunkLength;
    }
    return result;
}
static void SeparateWaveTagChunks(string path)
{
    var chunks = ReadWaveChunks(File.ReadAllBytes(path));
    static string Id(byte[] chunk) => Encoding.ASCII.GetString(chunk, 0, 4);
    var id3 = chunks.Where(chunk => Id(chunk).Trim().Equals("id3", StringComparison.OrdinalIgnoreCase)).ToArray();
    var info = chunks.Where(chunk => Id(chunk) == "LIST" && chunk.Length >= 12 && Encoding.ASCII.GetString(chunk, 8, 4) == "INFO").ToArray();
    var data = chunks.Where(chunk => Id(chunk) == "data").ToArray();
    if (id3.Length != 1 || info.Length != 1 || data.Length != 1)
        throw new InvalidDataException("The separated WAV fixture needs one INFO, ID3 and data block.");
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(0u);
    writer.Write(Encoding.ASCII.GetBytes("WAVE"));
    foreach (var chunk in chunks.Except(id3).Except(info).Except(data)) writer.Write(chunk);
    foreach (var chunk in info) writer.Write(chunk);
    writer.Write(Encoding.ASCII.GetBytes("msek"));
    writer.Write(7u);
    writer.Write(new byte[] { 7, 1, 8, 2, 9, 3, 10, 0 });
    foreach (var chunk in data) writer.Write(chunk);
    foreach (var chunk in id3) writer.Write(chunk);
    writer.Flush();
    stream.Position = 4;
    writer.Write(checked((uint)stream.Length - 8));
    writer.Flush();
    File.WriteAllBytes(path, stream.ToArray());
}
static async Task<Dictionary<string, string>> ProbeTagsAsync(string path)
{
    var output = await RunToolAsync("ffprobe", "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", path);
    using var document = JsonDocument.Parse(output);
    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (document.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var global))
        foreach (var entry in global.EnumerateObject()) tags.TryAdd(entry.Name, entry.Value.GetString() ?? "");
    if (document.RootElement.TryGetProperty("streams", out var streams))
        foreach (var stream in streams.EnumerateArray())
            if (stream.TryGetProperty("tags", out var values))
                foreach (var entry in values.EnumerateObject()) tags.TryAdd(entry.Name, entry.Value.GetString() ?? "");
    return tags;
}
static string? GetTag(IReadOnlyDictionary<string, string> tags, string name)
    => tags.TryGetValue(name, out var value) ? value : null;
static async Task<string> RunToolAsync(string tool, params string[] arguments)
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "tools", tool + ".exe");
    var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : tool + ".exe")
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Could not start " + tool);
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    if (process.ExitCode != 0) throw new Exception(tool + " failed: " + await error);
    await error;
    return await output;
}
file sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
file static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
