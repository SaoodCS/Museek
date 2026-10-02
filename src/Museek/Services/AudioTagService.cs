using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using Museek.Models;

namespace Museek.Services;

public sealed class AudioTagService
{
    private const int MaximumArtworkBytes = 8 * 1024 * 1024;
    private const long MaximumArtworkPixels = 32 * 1024 * 1024;

    public Task<AudioTags> ReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => ReadTags(NormalizePath(path), cancellationToken), cancellationToken);

    public async Task<TagEditBatchResult> ApplyAsync(IReadOnlyList<string> paths, TagEditPatch patch,
        IProgress<TagEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(patch);
        var files = paths.ToArray();
        if (cancellationToken.IsCancellationRequested)
            return new TagEditBatchResult(Array.Empty<TagEditResult>(), true);

        // Copy caller-owned artwork before validation and before any source file can change.
        patch = patch with { ArtworkBytes = patch.ArtworkBytes?.ToArray() };
        string? artworkMime;
        try { artworkMime = await Task.Run(() => ValidatePatch(patch), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TagEditBatchResult(Array.Empty<TagEditResult>(), true);
        }
        var results = new List<TagEditResult>(files.Length);
        var cancelled = false;
        for (var index = 0; index < files.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var path = files[index];
            TagEditResult result;
            try
            {
                path = NormalizePath(path);
                await Task.Run(() => ApplyFileAsync(path, patch, artworkMime, cancellationToken))
                    .ConfigureAwait(false);
                result = new TagEditResult(path, true, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new TagEditResult(path, false, "Cancelled before the file was saved.");
                cancelled = true;
            }
            catch (Exception ex)
            {
                result = new TagEditResult(path, false, ex.Message);
            }
            results.Add(result);
            progress?.Report(new TagEditProgress(results.Count, files.Length, path, result));
            if (cancelled) break;
        }
        return new TagEditBatchResult(results, cancelled || cancellationToken.IsCancellationRequested);
    }

    private static AudioTags ReadTags(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = OpenTagFile(path);
        ValidateAudio(file);
        var tags = ReadFileTags(file, path);
        token.ThrowIfCancellationRequested();
        return tags;
    }

    private static AudioTags ReadFileTags(TagLib.File file, string path)
    {
        var tag = file.Tag;
        var pictures = tag.Pictures;
        var cover = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
            ?? pictures.FirstOrDefault();
        var artwork = cover?.Data.Count is > 0 and <= MaximumArtworkBytes ? cover.Data.Data.ToArray() : null;
        if (file is TagLib.Riff.File)
        {
            // RIFF INFO uses IART/IPRD in common WAV writers. TagLib instead maps
            // IART to AlbumArtists and uses ISTR/DIRC for Performers/Album.
            var id3 = file.GetTag(TagLib.TagTypes.Id3v2, false);
            var info = file.GetTag(TagLib.TagTypes.RiffInfo, false) as TagLib.Riff.InfoTag;
            string? InfoText(string key) => info is null ? null : Join(info.GetValuesAsStrings(key));
            return new AudioTags(path, EmptyToNull(id3?.Title) ?? EmptyToNull(tag.Title),
                Join(id3?.Performers ?? []) ?? InfoText("IART") ?? Join(tag.Performers),
                EmptyToNull(id3?.Album) ?? InfoText("IPRD") ?? EmptyToNull(tag.Album),
                Join(id3?.AlbumArtists ?? []) ?? InfoText("IAAR"),
                Join(id3?.Genres ?? []) ?? Join(tag.Genres),
                id3?.Year is > 0 ? id3.Year : tag.Year,
                id3?.Track is > 0 ? id3.Track : (info?.GetValueAsUInt("ITRK") is > 0 ? info.GetValueAsUInt("ITRK") : tag.Track),
                id3?.Disc is > 0 ? id3.Disc : tag.Disc,
                EmptyToNull(id3?.Comment) ?? EmptyToNull(tag.Comment), artwork);
        }
        return new AudioTags(path, EmptyToNull(tag.Title), Join(tag.Performers), EmptyToNull(tag.Album),
            Join(tag.AlbumArtists), Join(tag.Genres), tag.Year, tag.Track, tag.Disc,
            EmptyToNull(tag.Comment), artwork);
    }

    private static async Task ApplyFileAsync(string path, TagEditPatch patch, string? artworkMime,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectSymbolicLinks(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var original = GetSourceInformation(source.SafeFileHandle);
        ValidateWritableSource(original);
        if (!HasChanges(patch))
        {
            _ = ReadTags(path, token);
            return;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(path)!,
            $".museek-tags-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            byte[] originalHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[65_536];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await staged.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                await staged.FlushAsync(token).ConfigureAwait(false);
                originalHash = hash.GetHashAndReset();
            }

            token.ThrowIfCancellationRequested();
            AudioProperties before;
            using (var file = OpenTagFile(temporary))
            {
                ValidateAudio(file);
                before = AudioProperties.From(file.Properties);
                ApplyFilePatch(file, patch, artworkMime);
                token.ThrowIfCancellationRequested();
                file.Save();
            }
            token.ThrowIfCancellationRequested();
            using (var file = OpenTagFile(temporary))
            {
                ValidateAudio(file);
                if (!before.Matches(AudioProperties.From(file.Properties)))
                    throw new InvalidDataException("The edited copy's audio properties changed. The original was kept.");
                VerifyPatch(file, patch);
            }
            // Flush edited bytes before the same-volume atomic replacement.
            using (var staged = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                staged.Flush(flushToDisk: true);

            token.ThrowIfCancellationRequested();
            RejectSymbolicLinks(path);
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var currentInformation = GetSourceInformation(current.SafeFileHandle);
            ValidateWritableSource(currentInformation);
            if (!original.SameVersion(currentInformation))
                throw new IOException("The source file changed while tags were being edited. The original was kept.");
            var currentHash = await SHA256.HashDataAsync(current, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, currentHash))
                throw new IOException("The source file changed while tags were being edited. The original was kept.");
            token.ThrowIfCancellationRequested();
            RejectSymbolicLinks(path);
            using (var finalHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete))
            {
                var finalInformation = GetSourceInformation(finalHandle);
                ValidateWritableSource(finalInformation);
                if (!original.SameVersion(finalInformation))
                    throw new IOException("The source file changed before saving. The original was kept.");
                token.ThrowIfCancellationRequested();
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ApplyFilePatch(TagLib.File file, TagEditPatch patch, string? artworkMime)
    {
        if (file is not TagLib.Riff.File)
        {
            ApplyPatch(file.Tag, patch, artworkMime);
            return;
        }
        var diskTypes = file.TagTypesOnDisk;
        var info = (TagLib.Riff.InfoTag)file.GetTag(TagLib.TagTypes.RiffInfo, true);
        if ((diskTypes & TagLib.TagTypes.RiffInfo) == 0) info.Clear();
        var id3 = file.GetTag(TagLib.TagTypes.Id3v2, true);
        if ((diskTypes & TagLib.TagTypes.Id3v2) == 0) id3.Clear();
        ApplyPatch(id3, patch, artworkMime);
        foreach (var type in new[] { TagLib.TagTypes.MovieId, TagLib.TagTypes.DivX })
            if ((diskTypes & type) != 0 && file.GetTag(type, false) is { } existing)
                ApplyPatch(existing, patch, artworkMime);
        void Set(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) info.RemoveValue(key);
            else info.SetValue(key, value);
        }
        if (patch.Title is not null) Set("INAM", patch.Title);
        if (patch.Artist is not null)
        {
            Set("IART", Join(Split(patch.Artist)));
            info.RemoveValue("ISTR");
        }
        if (patch.Album is not null)
        {
            Set("IPRD", patch.Album);
            info.RemoveValue("DIRC");
        }
        if (patch.AlbumArtist is not null) Set("IAAR", Join(Split(patch.AlbumArtist)));
        if (patch.Genre is not null) Set("IGNR", Join(Split(patch.Genre)));
        if (patch.Year.HasValue) info.Year = patch.Year.Value;
        if (patch.TrackNumber.HasValue)
        {
            info.Track = patch.TrackNumber.Value;
            info.SetValue("ITRK", patch.TrackNumber.Value);
        }
        if (patch.Comment is not null) Set("ICMT", patch.Comment);
    }

    private static void ApplyPatch(TagLib.Tag tag, TagEditPatch patch, string? artworkMime)
    {
        if (patch.Title is not null) tag.Title = EmptyToNull(patch.Title);
        if (patch.Artist is not null) tag.Performers = Split(patch.Artist);
        if (patch.Album is not null) tag.Album = EmptyToNull(patch.Album);
        if (patch.AlbumArtist is not null) tag.AlbumArtists = Split(patch.AlbumArtist);
        if (patch.Genre is not null) tag.Genres = Split(patch.Genre);
        if (patch.Year.HasValue) tag.Year = patch.Year.Value;
        if (patch.TrackNumber.HasValue) tag.Track = patch.TrackNumber.Value;
        if (patch.DiscNumber.HasValue) tag.Disc = patch.DiscNumber.Value;
        if (patch.Comment is not null) tag.Comment = EmptyToNull(patch.Comment);
        if (patch.ArtworkAction == ArtworkAction.Remove) tag.Pictures = Array.Empty<TagLib.IPicture>();
        if (patch.ArtworkAction == ArtworkAction.Replace)
            tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(patch.ArtworkBytes!))
            {
                Type = TagLib.PictureType.FrontCover, MimeType = artworkMime!, Description = "Cover"
            }];
    }

    private static void VerifyPatch(TagLib.File file, TagEditPatch patch)
    {
        var values = ReadFileTags(file, string.Empty);
        var tag = file.Tag;
        var valid = (patch.Title is null || values.Title == EmptyToNull(patch.Title))
            && (patch.Artist is null || values.Artist == Join(Split(patch.Artist)))
            && (patch.Album is null || values.Album == EmptyToNull(patch.Album))
            && (patch.AlbumArtist is null || values.AlbumArtist == Join(Split(patch.AlbumArtist)))
            && (patch.Genre is null || values.Genre == Join(Split(patch.Genre)))
            && (!patch.Year.HasValue || values.Year == patch.Year)
            && (!patch.TrackNumber.HasValue || values.TrackNumber == patch.TrackNumber)
            && (!patch.DiscNumber.HasValue || values.DiscNumber == patch.DiscNumber)
            && (patch.Comment is null || values.Comment == EmptyToNull(patch.Comment));
        if (patch.ArtworkAction == ArtworkAction.Remove) valid &= tag.Pictures.Length == 0;
        if (patch.ArtworkAction == ArtworkAction.Replace)
            valid &= tag.Pictures.Length == 1 && tag.Pictures[0].Type == TagLib.PictureType.FrontCover
                && tag.Pictures[0].Data.Data.AsSpan().SequenceEqual(patch.ArtworkBytes);
        if (!valid)
            throw new InvalidDataException("This file format could not retain all selected tag changes. The original was kept.");
    }

    private static string? ValidatePatch(TagEditPatch patch)
    {
        if (!Enum.IsDefined(patch.ArtworkAction)) throw new ArgumentException("Choose a valid artwork action.");
        if (patch.Year is > 9999) throw new ArgumentOutOfRangeException(nameof(patch), "Year must be between 0 and 9999.");
        if (patch.ArtworkAction != ArtworkAction.Replace) return null;
        var bytes = patch.ArtworkBytes;
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaximumArtworkBytes)
            throw new InvalidDataException("Choose a PNG or JPEG cover image no larger than 8 MB.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var mime = decoder switch
            {
                PngBitmapDecoder => "image/png", JpegBitmapDecoder => "image/jpeg",
                _ => throw new InvalidDataException("Cover artwork must be a PNG or JPEG image.")
            };
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
                || (long)frame.PixelWidth * frame.PixelHeight > MaximumArtworkPixels)
                throw new InvalidDataException("The cover image dimensions are too large.");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(frame.PixelWidth * 4);
            converted.CopyPixels(new byte[checked(stride * frame.PixelHeight)], stride, 0);
            return mime;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("The PNG or JPEG cover image could not be decoded.", ex);
        }
    }

    private static bool HasChanges(TagEditPatch p) => p.Title is not null || p.Artist is not null
        || p.Album is not null || p.AlbumArtist is not null || p.Genre is not null || p.Year.HasValue
        || p.TrackNumber.HasValue || p.DiscNumber.HasValue || p.Comment is not null
        || p.ArtworkAction != ArtworkAction.Keep;

    private static string[] Split(string text) => text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string? Join(string[] values) => EmptyToNull(string.Join("; ", values.Where(v => !string.IsNullOrEmpty(v))));
    private static string? EmptyToNull(string? text) => string.IsNullOrEmpty(text) ? null : text;
    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static void ValidateAudio(TagLib.File file)
    {
        if ((file.Properties.MediaTypes & TagLib.MediaTypes.Audio) == 0 || file.Properties.Duration <= TimeSpan.Zero)
            throw new InvalidDataException("This file does not contain readable audio supported by the tag editor.");
    }

    private static TagLib.File OpenTagFile(string path)
    {
        var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
        try
        {
            // RIFF's constructor creates four tag types and copies existing values
            // into them. Keep only tags actually present; those copies otherwise
            // resurrect cleared fields and turn IART into a fake ID3 album artist.
            if (file is TagLib.Riff.File) file.RemoveTags(file.TagTypes & ~file.TagTypesOnDisk);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void RejectSymbolicLinks(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Symbolic links cannot be edited safely. Select the original audio file.");
        for (var parent = new FileInfo(path).Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null)
                throw new IOException("Files inside symbolic links or junctions cannot be edited safely. Select the original path.");
    }

    private static SourceInformation GetSourceInformation(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Tag editing requires Windows.");
        if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SourceInformation(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow,
            ((ulong)(uint)information.LastWriteTime.dwHighDateTime << 32) | (uint)information.LastWriteTime.dwLowDateTime,
            information.NumberOfLinks, (FileAttributes)information.FileAttributes);
    }

    private static void ValidateWritableSource(SourceInformation information)
    {
        if ((information.Attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("This audio file is read-only. The original was kept.");
        if (information.Links != 1)
            throw new IOException("Hard-linked audio files cannot be edited safely. Select a separate original file.");
    }

    private sealed record AudioProperties(TimeSpan Duration, int SampleRate, int Channels, int BitsPerSample)
    {
        public static AudioProperties From(TagLib.Properties p) => new(p.Duration, p.AudioSampleRate, p.AudioChannels, p.BitsPerSample);
        public bool Matches(AudioProperties other) => SampleRate == other.SampleRate && Channels == other.Channels
            && BitsPerSample == other.BitsPerSample && (Duration - other.Duration).Duration() <= TimeSpan.FromMilliseconds(5);
    }

    private sealed record SourceInformation(uint Volume, ulong Id, ulong Length, ulong LastWrite,
        uint Links, FileAttributes Attributes)
    {
        public bool SameVersion(SourceInformation other) => Volume == other.Volume && Id == other.Id
            && Length == other.Length && LastWrite == other.LastWrite;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

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
}
