using System.ComponentModel;

namespace PatchCast.Client;

// A horizontal volume control drawn directly on top of an audio level meter.
// The meter fills the groove from the left in proportion to the incoming level
// (measured before volume and mute are applied). The groove uses the form
// background so only activity is visible, and the draggable handle, painted over
// the meter, sets the volume from 0 to 100.
internal sealed class VolumeSlider : Control
{
    private const int TrackPadding = 9;   // room at each end so the handle never clips
    private const int GrooveHeight = 12;
    private const int HandleWidth = 10;

    private int currentValue = 100;
    private float level;
    private bool dragging;

    public event EventHandler? ValueChanged;

    public VolumeSlider()
    {
        DoubleBuffered = true;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
    }

    public int Minimum => 0;
    public int Maximum => 100;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => currentValue;
        set => SetValue(value, raiseEvent: true);
    }

    // Sets the incoming audio level (0..1) shown by the meter behind the handle.
    public void SetLevel(float incomingLevel)
    {
        incomingLevel = Math.Clamp(incomingLevel, 0f, 1f);
        if (Math.Abs(incomingLevel - level) < 0.004f)
            return;
        level = incomingLevel;
        Invalidate();
    }

    private int TrackLeft => TrackPadding;
    private int TrackRight => Math.Max(TrackPadding + 1, Width - TrackPadding);
    private int TrackSpan => TrackRight - TrackLeft;

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);

        var grooveTop = (Height - GrooveHeight) / 2;
        var groove = new Rectangle(TrackLeft, grooveTop, TrackSpan, GrooveHeight);

        // Only the active portion of the meter is painted; the rest stays the form
        // background so an idle meter blends into the window.
        var fillWidth = (int)Math.Round((groove.Width - 2) * level);
        if (fillWidth > 0)
        {
            var fillColor = level >= 0.9f ? Color.FromArgb(220, 60, 60)
                : level >= 0.7f ? Color.FromArgb(230, 190, 60)
                : Color.FromArgb(70, 200, 90);
            using var fill = new SolidBrush(fillColor);
            graphics.FillRectangle(fill, groove.X + 1, groove.Y + 1, fillWidth, groove.Height - 2);
        }

        using (var border = new Pen(Color.FromArgb(140, 140, 140)))
            graphics.DrawRectangle(border, groove);

        // The handle sits on top of the meter.
        var handleX = TrackLeft + (int)Math.Round(TrackSpan * (currentValue / 100d)) - HandleWidth / 2;
        handleX = Math.Clamp(handleX, 0, Math.Max(0, Width - HandleWidth));
        var handle = new Rectangle(handleX, 1, HandleWidth, Math.Max(1, Height - 3));
        using (var handleFill = new SolidBrush(Enabled ? SystemColors.ControlDarkDark : SystemColors.ControlDark))
            graphics.FillRectangle(handleFill, handle);
        if (Focused)
        {
            using var focus = new Pen(SystemColors.Highlight);
            graphics.DrawRectangle(focus, handle);
        }
    }

    private void SetValue(int requested, bool raiseEvent)
    {
        var clamped = Math.Clamp(requested, Minimum, Maximum);
        if (clamped == currentValue)
            return;
        currentValue = clamped;
        Invalidate();
        if (raiseEvent)
            ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetValueFromX(int x)
    {
        var fraction = (x - TrackLeft) / (double)TrackSpan;
        SetValue((int)Math.Round(fraction * 100), raiseEvent: true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        Focus();
        dragging = true;
        SetValueFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
            SetValueFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetValue(currentValue + Math.Sign(e.Delta) * 4, raiseEvent: true);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: SetValue(currentValue - 1, true); e.Handled = true; break;
            case Keys.Right or Keys.Up: SetValue(currentValue + 1, true); e.Handled = true; break;
            case Keys.PageDown: SetValue(currentValue - 10, true); e.Handled = true; break;
            case Keys.PageUp: SetValue(currentValue + 10, true); e.Handled = true; break;
            case Keys.Home: SetValue(Minimum, true); e.Handled = true; break;
            case Keys.End: SetValue(Maximum, true); e.Handled = true; break;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
