using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IDForge.Controls;

/// <summary>
/// Shared photo position/size editor (New Card now, Review in phase 5).
/// Emits intents only — the owner applies them to its own transform state
/// and calls <see cref="SetState"/> to keep the controls in sync.
/// Zoom factor range matches the existing nudge slider: 0.4–3.0 (40–300 %).
/// </summary>
public partial class PhotoPositionPanel : UserControl
{
    private bool _syncing;
    private double _zoom = 1.0, _x, _y;

    /// <summary>(dx, dy) pixel nudge; caller applies Shift scaling via Step.</summary>
    public event Action<int, int>? NudgeRequested;
    /// <summary>Absolute offset commit (px).</summary>
    public event Action<double, double>? OffsetCommitted;
    /// <summary>Zoom factor commit (0.4–3.0).</summary>
    public event Action<double>? ZoomChanged;
    public event Action? ResetRequested;

    public PhotoPositionPanel()
    {
        InitializeComponent();
    }

    /// <summary>Pushes current transform values into the controls without firing events.</summary>
    public void SetState(double zoom, double offsetX, double offsetY)
    {
        _zoom = zoom; _x = offsetX; _y = offsetY;
        _syncing = true;
        SldZoom.Value = Math.Clamp(zoom, SldZoom.Minimum, SldZoom.Maximum);
        TxtZoomPct.Text = $"{(int)Math.Round(zoom * 100)}";
        TxtX.Text = ((int)offsetX).ToString();
        TxtY.Text = ((int)offsetY).ToString();
        _syncing = false;
    }

    private static int Step => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;

    private void Arrow_Click(object sender, RoutedEventArgs e)
    {
        var s = Step;
        var (dx, dy) = sender switch
        {
            _ when ReferenceEquals(sender, BtnUp) => (0, -s),
            _ when ReferenceEquals(sender, BtnDown) => (0, s),
            _ when ReferenceEquals(sender, BtnLeft) => (-s, 0),
            _ => (s, 0)
        };
        NudgeRequested?.Invoke(dx, dy);
    }

    private void OffsetText_Changed(object sender, TextChangedEventArgs e)
    {
        // Live-parse so the field doesn't fight the user; commit on LostFocus/Enter.
    }

    private void CommitOffsets()
    {
        if (!double.TryParse(TxtX.Text, out var x)) x = _x;
        if (!double.TryParse(TxtY.Text, out var y)) y = _y;
        OffsetCommitted?.Invoke(x, y);
    }

    private void Offset_Commit(object sender, RoutedEventArgs e) => CommitOffsets();

    private void Offset_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitOffsets(); e.Handled = true; }
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || TxtZoomPct == null) return;   // ValueChanged fires during InitializeComponent
        _zoom = SldZoom.Value;
        _syncing = true;
        TxtZoomPct.Text = $"{(int)Math.Round(_zoom * 100)}";
        _syncing = false;
        ZoomChanged?.Invoke(_zoom);
    }

    private void ZoomText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || SldZoom == null) return;
        if (!int.TryParse(TxtZoomPct.Text, out var pct)) return;
        var z = Math.Clamp(pct / 100.0, SldZoom.Minimum, SldZoom.Maximum);
        _zoom = z;
        _syncing = true;
        SldZoom.Value = z;
        _syncing = false;
        ZoomChanged?.Invoke(z);
    }

    private void Zoom_Commit(object sender, RoutedEventArgs e)
    {
        // Snap the box back to the clamped value.
        SetState(_zoom, _x, _y);
    }

    private void Zoom_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Zoom_Commit(sender, e); e.Handled = true; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();

    /// <summary>Positions the "100 %" label under the slider position for zoom = 1.0
    /// and the "300 %" label at the right end. Min label stays at x = 0.</summary>
    private void ZoomScale_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutZoomScale();

    private void LayoutZoomScale()
    {
        if (SldZoom == null || ZoomScale == null || ZoomScale.ActualWidth <= 0) return;
        var w = ZoomScale.ActualWidth;
        var frac = (1.0 - SldZoom.Minimum) / (SldZoom.Maximum - SldZoom.Minimum);
        var minW = LblZoomMin?.ActualWidth ?? 0;
        var midW = LblZoomHundred?.ActualWidth ?? 0;
        var maxW = LblZoomMax?.ActualWidth ?? 0;
        if (minW == 0 || midW == 0 || maxW == 0)
        {
            // labels not measured yet — retry once after layout
            Dispatcher.BeginInvoke(new Action(LayoutZoomScale));
            return;
        }
        var (minLeft, midLeft, maxLeft, showMid) =
            ZoomLabelLayout(w, minW, midW, maxW, frac);
        Canvas.SetLeft(LblZoomMin, minLeft);
        Canvas.SetLeft(LblZoomMax, maxLeft);
        LblZoomHundred.Visibility = showMid ? Visibility.Visible : Visibility.Collapsed;
        if (showMid) Canvas.SetLeft(LblZoomHundred, midLeft);
        // tick marks the zoom = 1.0 position under the slider
        Canvas.SetLeft(ZoomTick, frac * w - ZoomTick.Width / 2);
        Canvas.SetTop(ZoomTick, ZoomScale.Height - ZoomTick.Height - 1);
    }

    /// <summary>
    /// Positions the 40 %/100 %/300 % labels under the slider. The 100 % label is
    /// centred on the zoom = 1.0 fraction; when it would overlap an end label it
    /// is hidden and only the tick mark remains (returns showMid = false).
    /// </summary>
    internal static (double MinLeft, double MidLeft, double MaxLeft, bool ShowMid)
        ZoomLabelLayout(double w, double minW, double midW, double maxW, double frac)
    {
        const double gap = 3.0;
        var minLeft = 0.0;
        var maxLeft = Math.Max(0, w - maxW);
        var midCenter = frac * w;
        var midLeft = midCenter - midW / 2;
        var overlaps = midLeft < minLeft + minW + gap || midLeft + midW > maxLeft - gap;
        return overlaps
            ? (minLeft, midLeft, maxLeft, false)
            : (minLeft, midLeft, maxLeft, true);
    }
}
