using System;

namespace Museek.Models;

public sealed record AudioInfo(TimeSpan Duration, string Title, string Format);
