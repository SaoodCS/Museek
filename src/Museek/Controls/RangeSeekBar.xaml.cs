using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Museek.Core;

namespace Museek.Controls;

public partial class RangeSeekBar : UserControl
{
    private readonly TrimSelection _selection = new(0);
    private double _position;
    private bool _trimMode;
    private double TrackWidth => Math.Max(1, Surface.ActualWidth - 24);
    public double Duration => _selection.Duration;
    public double SelectionStart => _selection.Start;
    public double SelectionEnd => _selection.End;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler? SelectionChanged;

    public double Position
    {
        get => _position;
        set
        {
            var position = Math.Clamp(double.IsFinite(value) ? value : 0, 0, Duration);
            if (_position == position) return;
            _position = position;
            RenderPosition();
        }
    }

    public bool IsTrimMode
    {
        get => _trimMode;
        set
        {
            if (_trimMode == value) return;
            _trimMode = value;
            StartHandle.Visibility = EndHandle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            RenderSelection();
        }
    }

    public RangeSeekBar() => InitializeComponent();

    public void SetDuration(double seconds)
    {
        _selection.Reset(seconds);
        _position = 0;
        Render();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetSelection()
    {
        _selection.Reset(Duration);
        RenderSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private double X(double seconds) => 12 + (Duration > 0 ? seconds / Duration * TrackWidth : 0);
    private double Seconds(double pixels) => pixels / TrackWidth * Duration;

    private void Render()
    {
        if (Surface is null) return;
        TrackBackground.Width = TrackWidth;
        RenderSelection();
        RenderPosition();
    }

    private void RenderPosition()
    {
        if (Surface is null) return;
        Canvas.SetLeft(Playhead, X(Position) - 7);
        if (!IsTrimMode)
            TrackFill.Width = Duration > 0 ? Math.Max(0, Position / Duration * TrackWidth) : 0;
    }

    private void RenderSelection()
    {
        if (Surface is null) return;
        Canvas.SetLeft(TrackFill, X(IsTrimMode ? SelectionStart : 0));
        TrackFill.Width = Duration > 0 ? Math.Max(0, (IsTrimMode ? SelectionEnd - SelectionStart : Position) / Duration * TrackWidth) : 0;
        Canvas.SetLeft(StartHandle, X(SelectionStart) - 7);
        Canvas.SetLeft(EndHandle, X(SelectionEnd) - 7);
        StartHandle.ToolTip = $"Start: {TimeSpan.FromSeconds(SelectionStart):h\\:mm\\:ss\\.fff} • Arrow keys to adjust";
        EndHandle.ToolTip = $"End: {TimeSpan.FromSeconds(SelectionEnd):h\\:mm\\:ss\\.fff} • Arrow keys to adjust";
    }

    private void Seek(double seconds)
    {
        Position = IsTrimMode ? Math.Clamp(seconds, SelectionStart, SelectionEnd) : seconds;
        SeekRequested?.Invoke(this, Position);
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => Render();
    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb || Duration <= 0) return;
        Seek(Seconds(e.GetPosition(Surface).X - 12));
        Playhead.Focus();
        e.Handled = true;
    }
    private void Playhead_DragDelta(object sender, DragDeltaEventArgs e) => Seek(Position + Seconds(e.HorizontalChange));
    private void StartHandle_DragDelta(object sender, DragDeltaEventArgs e) => ChangeStart(SelectionStart + Seconds(e.HorizontalChange));
    private void EndHandle_DragDelta(object sender, DragDeltaEventArgs e) => ChangeEnd(SelectionEnd + Seconds(e.HorizontalChange));

    private void ChangeStart(double value)
    {
        _selection.SetStart(value);
        RenderSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ChangeEnd(double value)
    {
        _selection.SetEnd(value);
        RenderSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private double? KeyDelta(KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5.0 : 0.25;
        return e.Key switch { Key.Left or Key.Down => -step, Key.Right or Key.Up => step, _ => null };
    }
    private void Playhead_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyDelta(e) is { } delta) { Seek(Position + delta); e.Handled = true; }
        else if (e.Key is Key.Home or Key.End) { Seek(e.Key == Key.Home ? (IsTrimMode ? SelectionStart : 0) : (IsTrimMode ? SelectionEnd : Duration)); e.Handled = true; }
    }
    private void StartHandle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyDelta(e) is { } delta) { ChangeStart(SelectionStart + delta); e.Handled = true; }
    }
    private void EndHandle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyDelta(e) is { } delta) { ChangeEnd(SelectionEnd + delta); e.Handled = true; }
    }
}
