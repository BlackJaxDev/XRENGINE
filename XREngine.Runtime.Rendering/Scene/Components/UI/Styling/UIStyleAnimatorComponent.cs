using System;
using System.Diagnostics;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>
/// Animates <see cref="UIStyleProperties"/> on a UI node using zero-allocation timed curves and
/// damped springs driven by unscaled wall-clock time.
/// </summary>
[RequiresTransform(typeof(UIBoundableTransform))]
public sealed class UIStyleAnimatorComponent : UIComponent
{
    private const float SettleEpsilon = 0.0005f;
    private const float VelocitySettleEpsilon = 0.005f;
    private const float MaxFrameDeltaSeconds = 0.05f;
    private const float MaxSpringSubStepSeconds = 0.016f;

    private struct ResolvedStyleSnapshot
    {
        public Vector4 BackgroundColor;
        public Vector4 GradientBottomColor;
        public Vector4 TextColor;
        public Vector4 CornerRadii;
        public float CornerExponent;
        public float BorderWidth;
        public Vector4 BorderColor;
        public float BorderHighlightBoost;
        public Vector4 ShadowColor;
        public Vector4 ShadowParams;
        public float BackdropBlurRadius;
        public float BackdropBlurBlend;
        public float BackdropVibrancySaturation;
        public float BackdropVibrancyBrightness;
        public float Opacity;
        public Vector2 VisualScale;
        public Vector2 VisualOffset;
        public bool HasBackgroundColor;
        public bool HasGradientBottom;
        public bool HasTextColor;
        public bool HasShadow;
        public bool HasOpacity;
        public bool HasVisualTransform;
        public bool DecodeSrgbColors;

        public readonly UIQuadStyle ToQuadStyle()
        {
            EUIQuadStyleFlags flags = EUIQuadStyleFlags.None;
            if (CornerRadii != Vector4.Zero || BorderWidth > 0.0f)
                flags |= EUIQuadStyleFlags.StyledBox;
            if (DecodeSrgbColors)
                flags |= EUIQuadStyleFlags.DecodeSrgbColors;
            if (HasGradientBottom)
                flags |= EUIQuadStyleFlags.VerticalGradient;
            if (HasShadow && ShadowColor.W > 0.0001f && (ShadowParams.Z > 0.0f || ShadowParams.W != 0.0f))
                flags |= EUIQuadStyleFlags.DropShadow;
            if (BackdropBlurRadius > 0.001f && BackdropBlurBlend > 0.001f)
                flags |= EUIQuadStyleFlags.BackdropBlur | EUIQuadStyleFlags.StyledBox;

            return new UIQuadStyle
            {
                CornerRadii = CornerRadii,
                CornerExponent = CornerExponent > 0.0f ? CornerExponent : 2.0f,
                BorderWidth = BorderWidth,
                BorderHighlightBoost = BorderHighlightBoost,
                BorderColor = BorderColor,
                GradientBottomColor = GradientBottomColor,
                ShadowColor = ShadowColor,
                ShadowParams = ShadowParams,
                BackdropBlurParams = new Vector4(
                    BackdropBlurRadius,
                    BackdropBlurBlend,
                    BackdropVibrancySaturation,
                    BackdropVibrancyBrightness),
                Flags = flags,
            };
        }
    }

    private ResolvedStyleSnapshot _current;
    private ResolvedStyleSnapshot _start;
    private ResolvedStyleSnapshot _target;
    private bool _hasCurrent;

    private UITransitionSpec _colorSpec;
    private UITransitionSpec _transformSpec;
    private UITransitionSpec _blurSpec;

    private float _colorElapsed;
    private float _transformElapsed;
    private float _blurElapsed;

    private Vector2 _scaleVelocity;
    private Vector2 _offsetVelocity;

    private bool _colorActive;
    private bool _transformActive;
    private bool _blurActive;

    private bool _isSubscribed;
    private long _lastStepTicks;
    private Action? _onCompleted;

    /// <summary>
    /// Gets a value indicating whether any style channel is currently animating.
    /// </summary>
    public bool IsAnimating => _colorActive || _transformActive || _blurActive;

    /// <summary>
    /// Applies the given style properties immediately without animation and cancels any active transition.
    /// </summary>
    public void ApplyImmediate(in UIStyleProperties properties)
    {
        ResolvedStyleSnapshot snapshot = BuildSnapshot(in properties, _hasCurrent ? _current : CaptureLiveBaseline());
        _current = snapshot;
        _start = snapshot;
        _target = snapshot;
        _hasCurrent = true;

        _colorActive = false;
        _transformActive = false;
        _blurActive = false;
        _scaleVelocity = Vector2.Zero;
        _offsetVelocity = Vector2.Zero;
        _onCompleted = null;
        UpdateSubscription();

        PushToComponents(in _current);
    }

    /// <summary>
    /// Smoothly transitions visual properties from the current live state to <paramref name="target"/>.
    /// Interrupting an active transition reverses smoothly from the live values and preserves spring momentum.
    /// </summary>
    public void TransitionTo(
        in UIStyleProperties target,
        in UITransitionSpec colorTransition,
        in UITransitionSpec transformTransition,
        in UITransitionSpec blurTransition,
        Action? onCompleted = null)
    {
        if (!_hasCurrent)
        {
            _current = CaptureLiveBaseline();
            _hasCurrent = true;
        }

        _start = _current;
        _target = BuildSnapshot(in target, _current);

        // Preserve structural/discrete flags immediately while continuous values interpolate.
        _current.HasBackgroundColor = _target.HasBackgroundColor;
        _current.HasGradientBottom = _target.HasGradientBottom;
        _current.HasTextColor = _target.HasTextColor;
        _current.HasShadow = _target.HasShadow;
        _current.HasOpacity = _target.HasOpacity;
        _current.HasVisualTransform = _target.HasVisualTransform;
        _current.DecodeSrgbColors = _target.DecodeSrgbColors;
        _current.CornerExponent = _target.CornerExponent;

        _colorSpec = colorTransition;
        _transformSpec = transformTransition;
        _blurSpec = blurTransition;
        _colorElapsed = 0.0f;
        _transformElapsed = 0.0f;
        _blurElapsed = 0.0f;
        _onCompleted = onCompleted;

        _colorActive = NeedsTimedOrSpring(_colorSpec);
        _transformActive = NeedsTimedOrSpring(_transformSpec);
        _blurActive = NeedsTimedOrSpring(_blurSpec);

        if (!_colorActive)
            SnapColorChannel(ref _current, in _target);
        if (!_transformActive)
        {
            SnapTransformChannel(ref _current, in _target);
            _scaleVelocity = Vector2.Zero;
            _offsetVelocity = Vector2.Zero;
        }
        if (!_blurActive)
            SnapBlurChannel(ref _current, in _target);

        PushToComponents(in _current);

        if (!IsAnimating)
        {
            Action? callback = _onCompleted;
            _onCompleted = null;
            UpdateSubscription();
            callback?.Invoke();
            return;
        }

        _lastStepTicks = Stopwatch.GetTimestamp();
        UpdateSubscription();
    }

    /// <summary>
    /// Advances active style transitions by <paramref name="deltaSeconds"/>.
    /// Exposed for deterministic unit tests and manual stepping.
    /// </summary>
    public void Step(float deltaSeconds)
    {
        if (!IsAnimating || IsDestroyed)
            return;

        float dt = Math.Clamp(deltaSeconds, 0.0001f, MaxFrameDeltaSeconds);

        if (_colorActive)
            StepColorChannel(dt);

        if (_transformActive)
            StepTransformChannel(dt);

        if (_blurActive)
            StepBlurChannel(dt);

        PushToComponents(in _current);

        if (!IsAnimating)
        {
            Action? callback = _onCompleted;
            _onCompleted = null;
            UpdateSubscription();
            callback?.Invoke();
        }
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        Unsubscribe();
    }

    protected override void OnDestroying()
    {
        _colorActive = false;
        _transformActive = false;
        _blurActive = false;
        _onCompleted = null;
        Unsubscribe();
        base.OnDestroying();
    }

    private void OnUpdateFrame()
    {
        if (IsDestroyed || !IsAnimating)
        {
            Unsubscribe();
            return;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        float dt = _lastStepTicks == 0
            ? 1.0f / 60.0f
            : (float)(nowTicks - _lastStepTicks) / Stopwatch.Frequency;
        _lastStepTicks = nowTicks;
        Step(dt);
    }

    private void UpdateSubscription()
    {
        if (IsAnimating && !IsDestroyed)
        {
            if (!_isSubscribed)
            {
                RuntimeEngine.Time.Timer.UpdateFrame += OnUpdateFrame;
                _isSubscribed = true;
            }
        }
        else
        {
            Unsubscribe();
        }
    }

    private void Unsubscribe()
    {
        if (!_isSubscribed)
            return;

        RuntimeEngine.Time.Timer.UpdateFrame -= OnUpdateFrame;
        _isSubscribed = false;
    }

    private void StepColorChannel(float dt)
    {
        _colorElapsed += dt;
        float progress = _colorSpec.DurationSeconds <= 1e-5f
            ? 1.0f
            : Math.Clamp(_colorElapsed / _colorSpec.DurationSeconds, 0.0f, 1.0f);
        float k = _colorSpec.EvaluateTimed(progress);

        _current.BackgroundColor = Vector4.Lerp(_start.BackgroundColor, _target.BackgroundColor, k);
        _current.GradientBottomColor = Vector4.Lerp(_start.GradientBottomColor, _target.GradientBottomColor, k);
        _current.TextColor = Vector4.Lerp(_start.TextColor, _target.TextColor, k);
        _current.CornerRadii = Vector4.Lerp(_start.CornerRadii, _target.CornerRadii, k);
        _current.BorderWidth = Lerp(_start.BorderWidth, _target.BorderWidth, k);
        _current.BorderColor = Vector4.Lerp(_start.BorderColor, _target.BorderColor, k);
        _current.BorderHighlightBoost = Lerp(_start.BorderHighlightBoost, _target.BorderHighlightBoost, k);
        _current.ShadowColor = Vector4.Lerp(_start.ShadowColor, _target.ShadowColor, k);
        _current.ShadowParams = Vector4.Lerp(_start.ShadowParams, _target.ShadowParams, k);
        _current.Opacity = Lerp(_start.Opacity, _target.Opacity, k);

        if (progress >= 1.0f)
        {
            SnapColorChannel(ref _current, in _target);
            _colorActive = false;
        }
    }

    private void StepTransformChannel(float dt)
    {
        if (_transformSpec.Curve == EUITransitionCurve.Spring)
        {
            float stiffness = _transformSpec.SpringStiffness > 0.0f ? _transformSpec.SpringStiffness : 400.0f;
            float damping = _transformSpec.SpringDamping > 0.0f ? _transformSpec.SpringDamping : 32.0f;
            float remaining = dt;
            while (remaining > 1e-5f)
            {
                float step = MathF.Min(remaining, MaxSpringSubStepSeconds);
                remaining -= step;
                StepSpringVector2(ref _current.VisualScale, ref _scaleVelocity, _target.VisualScale, stiffness, damping, step);
                StepSpringVector2(ref _current.VisualOffset, ref _offsetVelocity, _target.VisualOffset, stiffness, damping, step);
            }

            bool scaleSettled = Vector2.Distance(_current.VisualScale, _target.VisualScale) < SettleEpsilon &&
                _scaleVelocity.Length() < VelocitySettleEpsilon;
            bool offsetSettled = Vector2.Distance(_current.VisualOffset, _target.VisualOffset) < SettleEpsilon &&
                _offsetVelocity.Length() < VelocitySettleEpsilon;
            if (scaleSettled && offsetSettled)
            {
                SnapTransformChannel(ref _current, in _target);
                _scaleVelocity = Vector2.Zero;
                _offsetVelocity = Vector2.Zero;
                _transformActive = false;
            }
            return;
        }

        _transformElapsed += dt;
        float progress = _transformSpec.DurationSeconds <= 1e-5f
            ? 1.0f
            : Math.Clamp(_transformElapsed / _transformSpec.DurationSeconds, 0.0f, 1.0f);
        float k = _transformSpec.EvaluateTimed(progress);

        _current.VisualScale = Vector2.Lerp(_start.VisualScale, _target.VisualScale, k);
        _current.VisualOffset = Vector2.Lerp(_start.VisualOffset, _target.VisualOffset, k);

        if (progress >= 1.0f)
        {
            SnapTransformChannel(ref _current, in _target);
            _scaleVelocity = Vector2.Zero;
            _offsetVelocity = Vector2.Zero;
            _transformActive = false;
        }
    }

    private void StepBlurChannel(float dt)
    {
        _blurElapsed += dt;
        float progress = _blurSpec.DurationSeconds <= 1e-5f
            ? 1.0f
            : Math.Clamp(_blurElapsed / _blurSpec.DurationSeconds, 0.0f, 1.0f);
        float k = _blurSpec.EvaluateTimed(progress);

        _current.BackdropBlurRadius = Lerp(_start.BackdropBlurRadius, _target.BackdropBlurRadius, k);
        _current.BackdropBlurBlend = Lerp(_start.BackdropBlurBlend, _target.BackdropBlurBlend, k);
        _current.BackdropVibrancySaturation = Lerp(_start.BackdropVibrancySaturation, _target.BackdropVibrancySaturation, k);
        _current.BackdropVibrancyBrightness = Lerp(_start.BackdropVibrancyBrightness, _target.BackdropVibrancyBrightness, k);

        if (progress >= 1.0f)
        {
            SnapBlurChannel(ref _current, in _target);
            _blurActive = false;
        }
    }

    private ResolvedStyleSnapshot CaptureLiveBaseline()
    {
        UIBoundableTransform? tfm = Transform as UIBoundableTransform;
        UIMaterialComponent? materialComp = GetSiblingComponent<UIMaterialComponent>();
        UITextComponent? textComp = SceneNode?.FirstChild?.GetComponent<UITextComponent>() ?? GetSiblingComponent<UITextComponent>();

        Vector4 bgColor = materialComp?.Material?.Parameter<ShaderVector4>("MatColor")?.Value ?? Vector4.Zero;
        UIQuadStyle style = materialComp?.QuadStyle ?? UIQuadStyle.Default;

        return new ResolvedStyleSnapshot
        {
            BackgroundColor = bgColor,
            GradientBottomColor = style.GradientBottomColor,
            TextColor = textComp is not null ? ToVector4(textComp.Color) : Vector4.One,
            CornerRadii = style.CornerRadii,
            CornerExponent = style.CornerExponent > 0.0f ? style.CornerExponent : 2.0f,
            BorderWidth = style.BorderWidth,
            BorderColor = style.BorderColor,
            BorderHighlightBoost = style.BorderHighlightBoost,
            ShadowColor = style.ShadowColor,
            ShadowParams = style.ShadowParams,
            BackdropBlurRadius = style.BackdropBlurParams.X,
            BackdropBlurBlend = style.BackdropBlurParams.Y,
            BackdropVibrancySaturation = style.BackdropBlurParams.Z > 0.0f ? style.BackdropBlurParams.Z : 1.25f,
            BackdropVibrancyBrightness = style.BackdropBlurParams.W > 0.0f ? style.BackdropBlurParams.W : 1.0f,
            Opacity = tfm?.Opacity ?? 1.0f,
            VisualScale = tfm?.VisualScale ?? Vector2.One,
            VisualOffset = tfm?.VisualOffset ?? Vector2.Zero,
            HasBackgroundColor = materialComp is not null,
            HasGradientBottom = (style.Flags & EUIQuadStyleFlags.VerticalGradient) != 0,
            HasTextColor = textComp is not null,
            HasShadow = (style.Flags & EUIQuadStyleFlags.DropShadow) != 0,
            HasOpacity = tfm is not null,
            HasVisualTransform = tfm is not null,
            DecodeSrgbColors = (style.Flags & EUIQuadStyleFlags.DecodeSrgbColors) != 0,
        };
    }

    private static ResolvedStyleSnapshot BuildSnapshot(in UIStyleProperties properties, in ResolvedStyleSnapshot fallback)
    {
        float blurRadius = properties.BackdropBlurRadius ?? fallback.BackdropBlurRadius;
        float blurBlend = properties.BackdropBlurBlend
            ?? (properties.BackdropBlurRadius.HasValue ? (blurRadius > 0.001f ? 1.0f : 0.0f) : fallback.BackdropBlurBlend);

        return new ResolvedStyleSnapshot
        {
            BackgroundColor = properties.BackgroundColor is { } bg ? ToVector4(bg) : fallback.BackgroundColor,
            GradientBottomColor = properties.GradientBottomColor is { } gb ? ToVector4(gb) : fallback.GradientBottomColor,
            TextColor = properties.TextColor is { } tc ? ToVector4(tc) : fallback.TextColor,
            CornerRadii = properties.CornerRadii ?? fallback.CornerRadii,
            CornerExponent = properties.CornerExponent ?? fallback.CornerExponent,
            BorderWidth = properties.BorderWidth ?? fallback.BorderWidth,
            BorderColor = properties.BorderColor is { } bc ? ToVector4(bc) : fallback.BorderColor,
            BorderHighlightBoost = properties.BorderHighlightBoost ?? fallback.BorderHighlightBoost,
            ShadowColor = properties.ShadowColor is { } sc ? ToVector4(sc) : fallback.ShadowColor,
            ShadowParams = properties.ShadowParams ?? fallback.ShadowParams,
            BackdropBlurRadius = blurRadius,
            BackdropBlurBlend = blurBlend,
            BackdropVibrancySaturation = properties.BackdropVibrancySaturation ?? fallback.BackdropVibrancySaturation,
            BackdropVibrancyBrightness = properties.BackdropVibrancyBrightness ?? fallback.BackdropVibrancyBrightness,
            Opacity = properties.Opacity ?? fallback.Opacity,
            VisualScale = properties.VisualScale ?? fallback.VisualScale,
            VisualOffset = properties.VisualOffset ?? fallback.VisualOffset,
            HasBackgroundColor = properties.BackgroundColor.HasValue || fallback.HasBackgroundColor,
            HasGradientBottom = properties.GradientBottomColor.HasValue || fallback.HasGradientBottom,
            HasTextColor = properties.TextColor.HasValue || fallback.HasTextColor,
            HasShadow = properties.ShadowColor.HasValue || fallback.HasShadow,
            HasOpacity = properties.Opacity.HasValue || fallback.HasOpacity,
            HasVisualTransform = properties.VisualScale.HasValue || properties.VisualOffset.HasValue || fallback.HasVisualTransform,
            DecodeSrgbColors = properties.DecodeSrgbColors ?? fallback.DecodeSrgbColors,
        };
    }

    private static Vector4 ToVector4(ColorF4 color) => new(color.R, color.G, color.B, color.A);

    private void PushToComponents(in ResolvedStyleSnapshot state)
    {
        if (Transform is UIBoundableTransform tfm)
        {
            if (state.HasOpacity)
                tfm.Opacity = state.Opacity;
            if (state.HasVisualTransform)
            {
                tfm.VisualScale = state.VisualScale;
                tfm.VisualOffset = state.VisualOffset;
            }
        }

        UIMaterialComponent? materialComp = GetSiblingComponent<UIMaterialComponent>();
        if (materialComp is not null)
        {
            if (state.HasBackgroundColor)
                // SetQuadColor also records the visual change, so a retained canvas republishes the quad.
                materialComp.SetQuadColor("MatColor", state.BackgroundColor);
            materialComp.QuadStyle = state.ToQuadStyle();
        }

        if (state.HasTextColor)
        {
            UITextComponent? textComp = SceneNode?.FirstChild?.GetComponent<UITextComponent>() ?? GetSiblingComponent<UITextComponent>();
            if (textComp is not null)
                textComp.Color = new ColorF4(state.TextColor.X, state.TextColor.Y, state.TextColor.Z, state.TextColor.W);
        }
    }

    private static bool NeedsTimedOrSpring(in UITransitionSpec spec)
        => spec.Curve == EUITransitionCurve.Spring || spec.DurationSeconds > 1e-5f;

    private static void SnapColorChannel(ref ResolvedStyleSnapshot dst, in ResolvedStyleSnapshot src)
    {
        dst.BackgroundColor = src.BackgroundColor;
        dst.GradientBottomColor = src.GradientBottomColor;
        dst.TextColor = src.TextColor;
        dst.CornerRadii = src.CornerRadii;
        dst.CornerExponent = src.CornerExponent;
        dst.BorderWidth = src.BorderWidth;
        dst.BorderColor = src.BorderColor;
        dst.BorderHighlightBoost = src.BorderHighlightBoost;
        dst.ShadowColor = src.ShadowColor;
        dst.ShadowParams = src.ShadowParams;
        dst.Opacity = src.Opacity;
    }

    private static void SnapTransformChannel(ref ResolvedStyleSnapshot dst, in ResolvedStyleSnapshot src)
    {
        dst.VisualScale = src.VisualScale;
        dst.VisualOffset = src.VisualOffset;
    }

    private static void SnapBlurChannel(ref ResolvedStyleSnapshot dst, in ResolvedStyleSnapshot src)
    {
        dst.BackdropBlurRadius = src.BackdropBlurRadius;
        dst.BackdropBlurBlend = src.BackdropBlurBlend;
        dst.BackdropVibrancySaturation = src.BackdropVibrancySaturation;
        dst.BackdropVibrancyBrightness = src.BackdropVibrancyBrightness;
    }

    private static void StepSpringVector2(
        ref Vector2 value,
        ref Vector2 velocity,
        Vector2 target,
        float stiffness,
        float damping,
        float dt)
    {
        // Semi-implicit Euler spring integration (stable for small substeps).
        Vector2 displacement = value - target;
        Vector2 acceleration = (-stiffness * displacement) - (damping * velocity);
        velocity += acceleration * dt;
        value += velocity * dt;
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
