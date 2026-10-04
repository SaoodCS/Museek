using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Museek;

public partial class MainWindow
{
    private static readonly DoubleAnimation TrackFade = CreateTrackAnimation(0.65, 1);
    private static readonly DoubleAnimation TrackFromLeft = CreateTrackAnimation(-10, 0);
    private static readonly DoubleAnimation TrackFromRight = CreateTrackAnimation(10, 0);
    private AnimationClock? _trackTransitionClock;

    private static DoubleAnimation CreateTrackAnimation(double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Freeze();
        return animation;
    }

    private void AnimateTrackTransition(int direction)
    {
        ResetTrackTransition();
        if (_closing || direction == 0 || !SystemParameters.ClientAreaAnimation) return;

        // Animate only rendering properties: no layout work, snapshots, extra artwork
        // buffers or per-frame callbacks. Replacement never accumulates clocks.
        var movement = direction < 0 ? TrackFromLeft : TrackFromRight;
        TrackTranslation.ApplyAnimationClock(TranslateTransform.XProperty, movement.CreateClock());
        _trackTransitionClock = TrackFade.CreateClock();
        _trackTransitionClock.Completed += TrackTransition_Completed;
        TrackPresentation.ApplyAnimationClock(UIElement.OpacityProperty, _trackTransitionClock);
    }

    private void TrackTransition_Completed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _trackTransitionClock)) ResetTrackTransition();
    }

    private void ResetTrackTransition()
    {
        if (_trackTransitionClock is not null)
        {
            _trackTransitionClock.Completed -= TrackTransition_Completed;
            _trackTransitionClock = null;
        }
        TrackPresentation.ApplyAnimationClock(UIElement.OpacityProperty, null);
        TrackTranslation.ApplyAnimationClock(TranslateTransform.XProperty, null);
    }
}
