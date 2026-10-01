namespace Museek.Core;

/// <summary>A bounded, non-crossing selection measured in seconds.</summary>
public sealed class TrimSelection
{
    public double Duration { get; private set; }
    public double Start { get; private set; }
    public double End { get; private set; }
    public double MinimumLength => Math.Min(0.05, Duration);

    public TrimSelection(double duration) => Reset(duration);

    public void Reset(double duration)
    {
        Duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        Start = 0;
        End = Duration;
    }

    public void SetStart(double seconds)
    {
        if (double.IsFinite(seconds))
            Start = Math.Clamp(seconds, 0, Math.Max(0, End - MinimumLength));
    }

    public void SetEnd(double seconds)
    {
        if (double.IsFinite(seconds))
            End = Math.Clamp(seconds, Math.Min(Duration, Start + MinimumLength), Duration);
    }
}
