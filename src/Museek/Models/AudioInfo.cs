using System;

namespace Museek.Models;

public sealed record AudioInfo(TimeSpan Duration, string Title, string Format, string? Artist = null, string? Album = null);
