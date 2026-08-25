using System.Diagnostics;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>
/// Keeps streaming updates anchored for readers and animates newly appended
/// text plus tail-following without moving the caret on every response chunk.
/// </summary>
internal sealed class RichTextBoxMotionController : IDisposable
{
    private static readonly TimeSpan s_fadeDuration = TimeSpan.FromMilliseconds(180);
    private const double ScrollEasing = 0.26;

    private readonly RichTextBox _textBox;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _fadeStopwatch = new();
    private IReadOnlyList<RichTextFadeRun> _fadeRuns = [];
    private bool _followTail = true;
    private bool _applyingMotion;
    private bool _motionEnabled = true;
    private Point? _scrollTarget;

    public RichTextBoxMotionController(RichTextBox textBox)
    {
        _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
        _motionEnabled = SystemInformation.IsMenuAnimationEnabled;
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += HandleTick;
        _textBox.VScroll += HandleUserScroll;
        _textBox.MouseWheel += HandleUserScroll;
        _textBox.KeyDown += HandleNavigationKey;
    }

    public RichTextUpdateState BeginContentUpdate()
    {
        _applyingMotion = true;
        try
        {
            FinishFade();
            var state = new RichTextUpdateState(
                RichTextBoxScrollHelper.Capture(_textBox),
                _textBox.SelectionStart,
                _textBox.SelectionLength,
                _followTail);
            RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: false);
            return state;
        }
        catch
        {
            _applyingMotion = false;
            throw;
        }
    }

    public void EndContentUpdate(
        RichTextUpdateState state,
        IReadOnlyList<RichTextFadeRun>? fadeRuns = null)
    {
        try
        {
            int selectionStart = Math.Min(state.SelectionStart, _textBox.TextLength);
            int selectionLength = Math.Min(
                state.SelectionLength,
                Math.Max(0, _textBox.TextLength - state.SelectionStart));
            _textBox.Select(selectionStart, selectionLength);
            RichTextBoxScrollHelper.Restore(_textBox, state.ScrollPosition);
            _followTail = state.FollowTail;

            _fadeRuns = fadeRuns ?? [];
            if (_fadeRuns.Count > 0 && _motionEnabled)
            {
                ApplyFadeColors(progress: 0.08);
                _fadeStopwatch.Restart();
            }
            else
            {
                _fadeRuns = [];
                _fadeStopwatch.Reset();
            }

            _textBox.Select(selectionStart, selectionLength);
            RichTextBoxScrollHelper.Restore(_textBox, state.ScrollPosition);
            if (_followTail && _motionEnabled)
            {
                _scrollTarget = RichTextBoxScrollHelper.BottomPosition(_textBox);
            }
            else
            {
                _scrollTarget = null;
                if (_followTail)
                    RichTextBoxScrollHelper.Restore(
                        _textBox,
                        RichTextBoxScrollHelper.BottomPosition(_textBox));
            }
        }
        finally
        {
            RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: true);
            _textBox.Invalidate();
            _applyingMotion = false;
        }

        if (_scrollTarget is not null || _fadeRuns.Count > 0)
            _timer.Start();
    }

    public void Reset(bool followTail)
    {
        _applyingMotion = true;
        try
        {
            FinishFade();
            _timer.Stop();
            _scrollTarget = null;
            _followTail = followTail;
        }
        finally
        {
            _applyingMotion = false;
        }
    }

    public void AbortContentUpdate(RichTextUpdateState state)
    {
        try
        {
            int selectionStart = Math.Min(state.SelectionStart, _textBox.TextLength);
            int selectionLength = Math.Min(
                state.SelectionLength,
                Math.Max(0, _textBox.TextLength - state.SelectionStart));
            _textBox.Select(selectionStart, selectionLength);
            RichTextBoxScrollHelper.Restore(_textBox, state.ScrollPosition);
        }
        finally
        {
            RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: true);
            _textBox.Invalidate();
            _applyingMotion = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _textBox.VScroll -= HandleUserScroll;
        _textBox.MouseWheel -= HandleUserScroll;
        _textBox.KeyDown -= HandleNavigationKey;
    }

    private void HandleTick(object? sender, EventArgs eventArgs)
    {
        _applyingMotion = true;
        RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: false);
        Point scrollPosition = RichTextBoxScrollHelper.Capture(_textBox);
        Point finalScrollPosition = scrollPosition;
        int selectionStart = _textBox.SelectionStart;
        int selectionLength = _textBox.SelectionLength;
        try
        {
            if (_fadeRuns.Count > 0)
            {
                double progress = Math.Clamp(
                    _fadeStopwatch.Elapsed.TotalMilliseconds / s_fadeDuration.TotalMilliseconds,
                    0.0,
                    1.0);
                ApplyFadeColors(EaseOutCubic(progress));
                if (progress >= 1.0)
                {
                    _fadeRuns = [];
                    _fadeStopwatch.Reset();
                }
            }

            RichTextBoxScrollHelper.Restore(_textBox, scrollPosition);
            if (_scrollTarget is not null)
                AdvanceScroll();
            finalScrollPosition = RichTextBoxScrollHelper.Capture(_textBox);
        }
        finally
        {
            _textBox.Select(
                Math.Min(selectionStart, _textBox.TextLength),
                Math.Min(selectionLength, Math.Max(0, _textBox.TextLength - selectionStart)));
            RichTextBoxScrollHelper.Restore(_textBox, finalScrollPosition);
            RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: true);
            _textBox.Invalidate();
            _applyingMotion = false;
        }

        if (_fadeRuns.Count == 0 && _scrollTarget is null)
            _timer.Stop();
    }

    private void AdvanceScroll()
    {
        if (!_followTail)
        {
            _scrollTarget = null;
            return;
        }

        _scrollTarget = RichTextBoxScrollHelper.BottomPosition(_textBox);
        Point current = RichTextBoxScrollHelper.Capture(_textBox);
        int remaining = _scrollTarget.Value.Y - current.Y;
        if (Math.Abs(remaining) <= 1)
        {
            RichTextBoxScrollHelper.Restore(_textBox, _scrollTarget.Value);
            _scrollTarget = null;
            return;
        }

        int step = Math.Sign(remaining) * Math.Max(1, (int)Math.Ceiling(Math.Abs(remaining) * ScrollEasing));
        RichTextBoxScrollHelper.Restore(
            _textBox,
            new Point(_scrollTarget.Value.X, current.Y + step));
    }

    private void ApplyFadeColors(double progress)
    {
        Color background = _textBox.BackColor;
        foreach (RichTextFadeRun run in _fadeRuns)
        {
            if (run.Start >= _textBox.TextLength)
                continue;
            _textBox.Select(run.Start, Math.Min(run.Length, _textBox.TextLength - run.Start));
            _textBox.SelectionColor = Blend(background, run.TargetColor, progress);
        }
    }

    private void FinishFade()
    {
        if (_fadeRuns.Count == 0 || _textBox.IsDisposed)
            return;

        Point position = RichTextBoxScrollHelper.Capture(_textBox);
        int selectionStart = _textBox.SelectionStart;
        int selectionLength = _textBox.SelectionLength;
        RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: false);
        try
        {
            ApplyFadeColors(1.0);
            _textBox.Select(selectionStart, selectionLength);
            RichTextBoxScrollHelper.Restore(_textBox, position);
            _fadeRuns = [];
            _fadeStopwatch.Reset();
        }
        finally
        {
            RichTextBoxScrollHelper.SetRedraw(_textBox, enabled: true);
            _textBox.Invalidate();
        }
    }

    private void HandleUserScroll(object? sender, EventArgs eventArgs)
    {
        if (_applyingMotion)
            return;
        if (eventArgs is MouseEventArgs { Delta: > 0 })
        {
            _followTail = false;
            _scrollTarget = null;
            return;
        }
        _followTail = RichTextBoxScrollHelper.IsNearBottom(_textBox);
        if (!_followTail)
            _scrollTarget = null;
    }

    private void HandleNavigationKey(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.PageUp or Keys.Home or Keys.Up)
        {
            _followTail = false;
            _scrollTarget = null;
        }
    }

    private static double EaseOutCubic(double value)
        => 1.0 - Math.Pow(1.0 - value, 3.0);

    private static Color Blend(Color from, Color to, double amount)
    {
        int BlendChannel(int first, int second)
            => (int)Math.Round(first + ((second - first) * amount));
        return Color.FromArgb(
            BlendChannel(from.A, to.A),
            BlendChannel(from.R, to.R),
            BlendChannel(from.G, to.G),
            BlendChannel(from.B, to.B));
    }
}
