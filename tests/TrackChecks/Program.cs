using System.Security.Cryptography;
using System.Text;
using Museek.Services;

// Silent disposable files only: no player, user settings, or music library.
var scratch = Path.Combine(Path.GetTempPath(), "Museek TrackChecks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var checks = 0;
var failures = new List<string>();
try
{
    var tracks = new[]
    {
        new FolderTrack("d.wav", "Delta", "Alpha", "Zulu", "Rock"),
        new FolderTrack("b.wav", "Beta", "Zulu", "Alpha", "Pop"),
        new FolderTrack("a.wav", "alpha", "Beta", "Beta", "Jazz"),
        new FolderTrack("c.wav", "Charlie", "", "", ""),
        new FolderTrack("e.wav", "Echo", "  ", "\t", "\n")
    };
    foreach (var (sort, expected) in new[]
    {
        (TrackSortBy.Title, new[] { "a.wav", "b.wav", "c.wav", "d.wav", "e.wav" }),
        (TrackSortBy.Artist, new[] { "d.wav", "a.wav", "b.wav", "c.wav", "e.wav" }),
        (TrackSortBy.Album, new[] { "b.wav", "a.wav", "d.wav", "c.wav", "e.wav" }),
        (TrackSortBy.Genre, new[] { "a.wav", "b.wav", "d.wav", "c.wav", "e.wav" })
    })
        Check(Paths(FolderTrackService.Sort(tracks, sort)).SequenceEqual(expected),
            $"{sort} sorts alphabetically, with missing tags last");

    var blankKeys = new[]
    {
        new FolderTrack("b.wav", "Beta", "", "", ""),
        new FolderTrack("a.wav", "Alpha", "  ", "\t", "\n")
    };
    foreach (var sort in new[] { TrackSortBy.Artist, TrackSortBy.Album, TrackSortBy.Genre })
        Check(Paths(FolderTrackService.Sort(blankKeys, sort)).SequenceEqual(["a.wav", "b.wav"]),
            $"{sort} resolves all blank keys by title regardless of whitespace");

    var tied = new[]
    {
        new FolderTrack(@"Z:\folder\b.wav", "Same", "Artist", "Album", "Genre"),
        new FolderTrack(@"Z:\folder\a.wav", "same", "artist", "album", "genre"),
        new FolderTrack(@"A:\folder\a.wav", "Same", "Artist", "Album", "Genre"),
        new FolderTrack(@"Z:\folder\c.wav", "Earlier", "Artist", "Album", "Genre")
    };
    var tieOrder = new[] { @"Z:\folder\c.wav", @"A:\folder\a.wav", @"Z:\folder\a.wav", @"Z:\folder\b.wav" };
    foreach (var sort in Enum.GetValues<TrackSortBy>())
    {
        Check(Paths(FolderTrackService.Sort(tied, sort)).SequenceEqual(tieOrder),
            $"{sort} resolves equal keys by title, filename, then full path");
        Check(Paths(FolderTrackService.Sort([tied[1], tied[0], tied[3], tied[2]], sort)).SequenceEqual(tieOrder),
            $"{sort} order is independent of file enumeration order");
    }
    Check(Paths(tracks).SequenceEqual(["d.wav", "b.wav", "a.wav", "c.wav", "e.wav"]),
        "sorting does not mutate the caller's list");

    var source = WriteWave(scratch, "z-file.wav", "Alpha tag", "Zulu artist", "Beta album", "Jazz");
    var second = WriteWave(scratch, "a-file.WAV", "Zulu tag", "Alpha artist", "Zulu album", "Rock");
    var untagged = WriteWave(scratch, "Missing tags.wav");
    var corrupt = Path.Combine(scratch, "Unreadable metadata.mp3");
    File.WriteAllText(corrupt, "not an audio container");
    File.WriteAllText(Path.Combine(scratch, "notes.txt"), "excluded");
    var nested = Directory.CreateDirectory(Path.Combine(scratch, "nested")).FullName;
    WriteWave(nested, "not in current folder.wav", "A nested title");
    var sibling = Directory.CreateDirectory(Path.Combine(scratch, "sibling")).FullName;
    var siblingSource = WriteWave(sibling, "only sibling.wav", "Only sibling");
    var before = FileHashes(scratch);
    var service = new FolderTrackService();
    var loaded = await service.LoadAsync(Path.Combine(scratch, "nested", "..", Path.GetFileName(source)));
    Check(loaded.Count == 4 && loaded.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
        .SetEquals([source, second, untagged, corrupt]), "loads supported files from the direct source folder only");
    Check(loaded.All(t => Path.IsPathFullyQualified(t.Path) && t.Path == Path.GetFullPath(t.Path)),
        "normalizes source and candidate paths");
    var tagged = loaded.SingleOrDefault(t => t.Path == source);
    Check(tagged is { Title: "Alpha tag", Artist: "Zulu artist", Album: "Beta album", Genre: "Jazz" },
        "reads title, artist, album and genre metadata");
    Check(loaded.SingleOrDefault(t => t.Path == untagged) is
        { Title: "Missing tags", Artist: "", Album: "", Genre: "" }, "missing tags use filename title and empty sort keys");
    Check(loaded.SingleOrDefault(t => t.Path == corrupt) is
        { Title: "Unreadable metadata", Artist: "", Album: "", Genre: "" }, "corrupt metadata keeps the file navigable");
    Check(Paths(FolderTrackService.Sort(loaded, TrackSortBy.Title))
        .SequenceEqual([source, untagged, corrupt, second]), "tagged titles determine order instead of filenames");
    Check(Paths(FolderTrackService.Sort(loaded, TrackSortBy.Artist))
        .SequenceEqual([second, source, untagged, corrupt]), "loaded artist tags determine order");
    Check(Paths(FolderTrackService.Sort(loaded, TrackSortBy.Album))
        .SequenceEqual([source, second, untagged, corrupt]), "loaded album tags determine order");
    Check(Paths(FolderTrackService.Sort(loaded, TrackSortBy.Genre))
        .SequenceEqual([source, second, untagged, corrupt]), "loaded genre tags determine order");
    var siblingLoaded = await service.LoadAsync(siblingSource);
    Check(siblingLoaded.Count == 1 && siblingLoaded[0].Path == siblingSource,
        "changing the current file changes the folder boundary");
    Check(before.OrderBy(x => x.Key).SequenceEqual(FileHashes(scratch).OrderBy(x => x.Key)),
        "loading and sorting preserve every fixture byte");

    using (var locked = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var lockedLoaded = await service.LoadAsync(source);
        Check(lockedLoaded.SingleOrDefault(t => t.Path == second) is { Title: "a-file", Artist: "" },
            "metadata locked by another program keeps filename fallback");
    }

    var added = WriteWave(scratch, "new track.wav", "Added track");
    File.Delete(untagged);
    var refreshed = await service.LoadAsync(source);
    Check(refreshed.Any(t => t.Path == added) && refreshed.All(t => t.Path != untagged),
        "fresh loads reflect files added and removed after the previous load");

    var unusual = Path.Combine(scratch, "Current track.custom");
    File.WriteAllText(unusual, "unsupported metadata");
    var unusualLoaded = await service.LoadAsync(unusual);
    Check(unusualLoaded.Any(t => t.Path == unusual && t.Title == "Current track"),
        "includes an existing current file opened through All files with an unknown extension");
    Check((await service.LoadAsync(source)).All(t => t.Path != unusual),
        "unknown extensions other than the current file are excluded");

    var riff = WriteWave(sibling, "standard RIFF tags.wav");
    using (var file = TagLib.File.Create(riff))
    {
        file.RemoveTags(TagLib.TagTypes.AllTags);
        var info = (TagLib.Riff.InfoTag)file.GetTag(TagLib.TagTypes.RiffInfo, true);
        info.SetValue("INAM", "RIFF title");
        info.SetValue("IART", "RIFF artist");
        info.SetValue("ISTR", "Other performer");
        info.SetValue("IPRD", "RIFF album");
        info.SetValue("DIRC", "Other album");
        info.SetValue("IGNR", "RIFF genre");
        file.Save();
    }
    var riffBefore = SHA256.HashData(File.ReadAllBytes(riff));
    var riffLoaded = (await service.LoadAsync(riff)).SingleOrDefault(t => t.Path == riff);
    Check(riffLoaded is { Title: "RIFF title", Artist: "RIFF artist", Album: "RIFF album", Genre: "RIFF genre" },
        "RIFF-only WAV uses IART/IPRD over conflicting ISTR/DIRC fields");
    Check(riffBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(riff))),
        "discarding synthetic WAV tags during reads preserves the original file bytes");
    using (var file = TagLib.File.Create(riff))
    {
        var id3 = file.GetTag(TagLib.TagTypes.Id3v2, true);
        id3.Title = "ID3 title";
        id3.Performers = ["ID3 artist"];
        id3.Album = "ID3 album";
        id3.Genres = ["ID3 genre"];
        file.Save();
    }
    Check((await service.LoadAsync(riff)).SingleOrDefault(t => t.Path == riff) is
        { Title: "ID3 title", Artist: "ID3 artist", Album: "ID3 album", Genre: "ID3 genre" },
        "explicit WAV ID3 tags take precedence over RIFF tags");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await ExpectException<OperationCanceledException>(() => service.LoadAsync(source, cancellation.Token),
        "a cancelled load does not return a stale or partial track list");
    await ExpectException<FileNotFoundException>(() => service.LoadAsync(Path.Combine(scratch, "missing.wav")),
        "a missing current file is reported to the caller");
    await ExpectException<DirectoryNotFoundException>(() => service.LoadAsync(Path.Combine(scratch, "absent", "missing.wav")),
        "a missing current folder is reported instead of silently returning an empty list");
    await ExpectException<ArgumentException>(() => service.LoadAsync(" "),
        "an empty source path is rejected");

    Console.WriteLine($"Track checks: {checks - failures.Count}/{checks} passed.");
    if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
}
finally
{
    // The absolute target is the scratch directory allocated above, never user data.
    Directory.Delete(scratch, recursive: true);
}

void Check(bool condition, string name)
{
    checks++;
    if (condition) Console.WriteLine("PASS: " + name);
    else { failures.Add(name); Console.WriteLine("FAIL: " + name); }
}

async Task ExpectException<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); Check(false, name); }
    catch (T) { Check(true, name); }
}

static string[] Paths(IEnumerable<FolderTrack> values) => values.Select(t => t.Path).ToArray();

static Dictionary<string, string> FileHashes(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
    .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

static string WriteWave(string folder, string name, string? title = null, string? artist = null,
    string? album = null, string? genre = null)
{
    var path = Path.Combine(folder, name);
    const int samples = 800;
    using (var stream = File.Create(path))
    using (var writer = new BinaryWriter(stream, Encoding.ASCII))
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }
    if (title is not null || artist is not null || album is not null || genre is not null)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Performers = artist is null ? [] : [artist];
        file.Tag.Album = album;
        file.Tag.Genres = genre is null ? [] : [genre];
        file.Save();
    }
    return path;
}
