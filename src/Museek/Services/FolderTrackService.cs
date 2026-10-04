using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Museek.Services;

public enum TrackSortBy { Title, Artist, Album, Genre }

public sealed record FolderTrack(string Path, string Title, string Artist, string Album, string Genre);

public sealed class FolderTrackService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(WindowsIntegrationService.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<FolderTrack>> LoadAsync(string sourcePath, CancellationToken token = default)
        => Task.Run(() => Load(sourcePath, token), token);

    public static IReadOnlyList<FolderTrack> Sort(IEnumerable<FolderTrack> tracks, TrackSortBy sortBy)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Func<FolderTrack, string> key = sortBy switch
        {
            TrackSortBy.Title => track => track.Title,
            TrackSortBy.Artist => track => track.Artist,
            TrackSortBy.Album => track => track.Album,
            TrackSortBy.Genre => track => track.Genre,
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy))
        };
        return tracks.OrderBy(track => string.IsNullOrWhiteSpace(key(track)))
            .ThenBy(track => FirstText(key(track)), StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => Path.GetFileName(track.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(track => track.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<FolderTrack> Load(string sourcePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        var folder = Path.GetDirectoryName(source)!;
        // Folder access failures reach the caller; they must not look like an empty folder.
        var paths = Directory.GetFiles(folder);
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(source)) throw new ArgumentException("Choose an audio file.", nameof(sourcePath));
        if (!File.Exists(source)) throw new FileNotFoundException("The current audio file no longer exists.", source);

        var result = new List<FolderTrack>();
        var sourceIncluded = false;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(path);
            var isSource = string.Equals(normalized, source, StringComparison.OrdinalIgnoreCase);
            if (!isSource && !SupportedExtensions.Contains(Path.GetExtension(normalized))) continue;
            result.Add(ReadTrack(normalized));
            sourceIncluded |= isSource;
        }
        // The current file may use an unknown extension via the All files picker.
        if (!sourceIncluded)
        {
            token.ThrowIfCancellationRequested();
            result.Add(ReadTrack(source));
        }
        token.ThrowIfCancellationRequested();
        return result.ToArray();
    }

    private static FolderTrack ReadTrack(string path)
    {
        var fallback = new FolderTrack(path, Path.GetFileNameWithoutExtension(path), "", "", "");
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            // RIFF creates synthetic ID3 tags and copies INFO values into them.
            // Discard those in memory so only explicit disk tags get ID3 precedence.
            if (file is TagLib.Riff.File) file.RemoveTags(file.TagTypes & ~file.TagTypesOnDisk);
            var tag = file.Tag;
            if (file is TagLib.Riff.File)
            {
                // Match the tag editor's precedence for common WAV IART/IPRD fields.
                var id3 = file.GetTag(TagLib.TagTypes.Id3v2, false);
                var info = file.GetTag(TagLib.TagTypes.RiffInfo, false) as TagLib.Riff.InfoTag;
                string Info(string key) => info is null ? "" : Join(info.GetValuesAsStrings(key));
                return new FolderTrack(path, FirstText(id3?.Title, tag.Title, fallback.Title),
                    FirstText(Join(id3?.Performers), Info("IART"), Join(tag.Performers)),
                    FirstText(id3?.Album, Info("IPRD"), tag.Album),
                    FirstText(Join(id3?.Genres), Join(tag.Genres)));
            }
            return new FolderTrack(path, FirstText(tag.Title, fallback.Title), Join(tag.Performers),
                FirstText(tag.Album), Join(tag.Genres));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
        {
            // One unreadable/unsupported tag must not remove that audio file from navigation.
            return fallback;
        }
    }

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Join(IEnumerable<string>? values)
        => values is null ? "" : string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));
}
