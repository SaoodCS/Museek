using System.Collections.Generic;

namespace Museek.Models;

public sealed record AudioTags(string Path, string? Title, string? Artist, string? Album,
    string? AlbumArtist, string? Genre, uint Year, uint TrackNumber, uint DiscNumber,
    string? Comment, byte[]? ArtworkBytes);

public enum ArtworkAction { Keep, Replace, Remove }

public sealed record TagEditPatch
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }
    public uint? Year { get; init; }
    public uint? TrackNumber { get; init; }
    public uint? DiscNumber { get; init; }
    public string? Comment { get; init; }
    public ArtworkAction ArtworkAction { get; init; } = ArtworkAction.Keep;
    public byte[]? ArtworkBytes { get; init; }
}

public sealed record TagEditResult(string Path, bool Success, string? Error);
public sealed record TagEditBatchResult(IReadOnlyList<TagEditResult> Files, bool Cancelled);
public sealed record TagEditProgress(int Completed, int Total, string Path, TagEditResult Result);
