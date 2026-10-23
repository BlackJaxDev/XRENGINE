using System;
using XREngine.Rendering.UI.State;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Vertical alignment modes for <see cref="UITextComponent"/> within its <see cref="UIBoundableTransform"/>.
    /// </summary>
    public enum EVerticalAlignment
    {
        Top,
        Center,
        Bottom
    }

    /// <summary>
    /// Horizontal alignment modes for <see cref="UITextComponent"/> within its <see cref="UIBoundableTransform"/>.
    /// </summary>
    public enum EHorizontalAlignment
    {
        Left,
        Center,
        Right
    }

    /// <summary>
    /// Optional fragment-shader probes for isolating batched text rendering failures.
    /// </summary>
    public enum EBatchedTextDebugMode
    {
        None = 0,
        SolidGlyphQuads = 1,
        AtlasUv = 2,
        HardClipSpaceQuad = 3,
        AtlasCoverage = 4,
        InstanceColor = 5,
        InstanceAlpha = 6,
    }

    /// <summary>
    /// Renders 2D UI text by generating instanced quad glyphs from a <see cref="FontGlyphSet"/> atlas.
    /// </summary>
    /// <remarks>
    /// - Glyph quad transforms/UVs are written to shader storage buffers and pushed to the GPU on demand.
    /// - When <see cref="AnimatableTransforms"/> is enabled, per-glyph translation/scale/rotation can be applied via <see cref="GlyphRelativeTransforms"/>.
    /// - Auto sizing is supported through <see cref="UIBoundableTransform.CalcAutoWidthCallback"/> and <see cref="UIBoundableTransform.CalcAutoHeightCallback"/>.
    /// </remarks>
    public partial class UITextComponent : UIRenderableComponent
    {
        private static bool UseWebGpuBatchOnly =>
            RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked ||
            AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;

        #region Construction
        public UITextComponent()
        {
            RenderPass = (int)EDefaultRenderPass.TransparentForward;
            RenderCommand3D.Instances = 0;
            RenderCommand2D.Instances = 0;
        }

        private bool HasAuthoredOrMaterializedText
            => !string.IsNullOrEmpty(_text) || Mesh is not null || _glyphs.Count > 0;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            UITextShapingBackendRegistry.Changed += ShapingBackendChanged;
            // Cooked component hydration does not raise Text/Font property notifications.
            // Resolve the preloaded font and build glyph layout after transform wiring.
            if (!string.IsNullOrEmpty(Text))
                UpdateText(forceRemake: false);
        }

        protected override void OnComponentDeactivated()
        {
            UITextShapingBackendRegistry.Changed -= ShapingBackendChanged;
            base.OnComponentDeactivated();
        }

        private void ShapingBackendChanged()
        {
            if (!UseShapedText ||
                System.Threading.Interlocked.Exchange(ref _pendingShapingBackendInvalidation, 1) != 0)
                return;
            RuntimeEngine.InvokeOnAppThread(() =>
            {
                System.Threading.Interlocked.Exchange(ref _pendingShapingBackendInvalidation, 0);
                if (UseShapedText && System.Threading.Volatile.Read(ref _layoutFontObserverDisposed) == 0 &&
                    SceneNode?.Transform is UIBoundableTransform transform)
                    transform.InvalidateMeasure();
            }, "UITextComponent.ShapingBackendChanged");
        }

        #endregion

        #region Constants

        private const string TextColorUniformName = "TextColor";
        private const string OutlineColorUniformName = "OutlineColor";
        private const string OutlineThicknessUniformName = "OutlineThickness";
        private const string MsdfDistanceRangeUniformName = "MsdfDistanceRange";
        private const string MsdfDistanceRangeMiddleUniformName = "MsdfDistanceRangeMiddle";
        private const string MsdfFillBiasUniformName = "MsdfFillBias";
        private const float DefaultMsdfFillBias = 0.5f;
        private const float DefaultLineSpacing = 2.0f;

        #endregion

        #region Glyph State / Buffers

        private readonly List<(Vector4 transform, Vector4 uvs)> _glyphs = [];
        private UITextLayoutInput? _layoutInput;
        private UITextLayoutResult? _layoutResult;
        private Vector2 _layoutResultActualSize;
        private UITextureSnapshot? _publicationAtlasSnapshot;
        private FontGlyphSet? _layoutInputFont;
        private long _layoutInputBackendGeneration;
        private int _pendingShapingBackendInvalidation;
        private long _textRevision;
        private XRDataBuffer<Vector4>? _uvsBuffer;
        private XRDataBuffer<Vector4>? _transformsBuffer;
        private XRDataBuffer<float>? _rotationsBuffer;
        private readonly Lock _glyphLock = new();
        private FrozenDictionary<int, (Vector2 translation, Vector2 scale, float rotation)> _glyphOverrides =
            FrozenDictionary<int, (Vector2 translation, Vector2 scale, float rotation)>.Empty;
        private Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)>? _capturedGlyphOverrides;
        private long _glyphOverrideRevision;
        private long _writtenGlyphOverrideRevision = -1;
        private UITextLayoutResult? _writtenGlyphLayout;
        private XRDataBuffer? _writtenGlyphBuffer;

        /// <summary>
        /// When true, the next render will push full buffer contents (e.g., after a resize).
        /// </summary>
        private bool _pushFull = false;

        /// <summary>
        /// When true, glyph/buffer data needs to be pushed before rendering.
        /// </summary>
        private bool _dataChanged = false;
        private uint _allocatedGlyphCount = 20;
        private int _fpsTextRenderHookDiagCount = 0;
        private int _fpsTextWriteDiagCount = 0;

        #endregion

        #region Public API (Layout / Content)

        private Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)> _glyphRelativeTransforms = [];
        /// <summary>
        /// Optional per-glyph adjustments applied on top of the font-provided layout.
        /// </summary>
        /// <remarks>
        /// This is only applied when <see cref="AnimatableTransforms"/> is true.
        /// If you mutate the dictionary in-place (instead of replacing it), call <see cref="MarkGlyphTransformsChanged"/>.
        /// </remarks>
        public Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)> GlyphRelativeTransforms
        {
            get => _glyphRelativeTransforms;
            set => SetField(ref _glyphRelativeTransforms, value);
        }

        private EVerticalAlignment _verticalAlignment = EVerticalAlignment.Bottom;
        public EVerticalAlignment VerticalAlignment
        {
            get => _verticalAlignment;
            set => SetField(ref _verticalAlignment, value);
        }

        private EHorizontalAlignment _horizontalAlignment = EHorizontalAlignment.Left;
        public EHorizontalAlignment HorizontalAlignment
        {
            get => _horizontalAlignment;
            set => SetField(ref _horizontalAlignment, value);
        }

        private string? _text;
        /// <summary>
        /// The text to display.
        /// </summary>
        public string? Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }

        private FontGlyphSet? _font;
        private FontGlyphSet? _observedLayoutFont;
        private int _pendingFontMetricInvalidation;
        private int _layoutFontObserverDisposed;
        /// <summary>
        /// The font to use for the text.
        /// </summary>
        public FontGlyphSet? Font
        {
            get => _font;
            set
            {
                if (ReferenceEquals(_font, value))
                    return;
                if (_observedLayoutFont is not null)
                    _observedLayoutFont.LayoutMetricsChanged -= FontLayoutMetricsChanged;
                _observedLayoutFont = null;
                try { SetField(ref _font, value); }
                finally { ObserveLayoutFont(); }
            }
        }

        private FontGlyphSet EnsureLayoutFont()
        {
            _font ??= FontGlyphSet.LoadDefaultUIFont();
            ObserveLayoutFont();
            return _font;
        }

        private void ObserveLayoutFont()
        {
            if (ReferenceEquals(_observedLayoutFont, _font))
                return;
            if (_observedLayoutFont is not null)
                _observedLayoutFont.LayoutMetricsChanged -= FontLayoutMetricsChanged;
            _observedLayoutFont = _font;
            if (_observedLayoutFont is not null)
                _observedLayoutFont.LayoutMetricsChanged += FontLayoutMetricsChanged;
        }

        private void FontLayoutMetricsChanged(FontGlyphSet font)
        {
            if (!ReferenceEquals(_font, font) ||
                System.Threading.Volatile.Read(ref _layoutFontObserverDisposed) != 0 ||
                System.Threading.Interlocked.Exchange(ref _pendingFontMetricInvalidation, 1) != 0)
                return;

            RuntimeEngine.InvokeOnAppThread(() =>
            {
                System.Threading.Interlocked.Exchange(ref _pendingFontMetricInvalidation, 0);
                if (System.Threading.Volatile.Read(ref _layoutFontObserverDisposed) == 0 &&
                    ReferenceEquals(_font, _observedLayoutFont) &&
                    SceneNode?.Transform is UIBoundableTransform transform)
                    transform.InvalidateMeasure();
            }, "UITextComponent.FontLayoutMetricsChanged");
        }

        protected override void OnDestroying()
        {
            UITextShapingBackendRegistry.Changed -= ShapingBackendChanged;
            System.Threading.Volatile.Write(ref _layoutFontObserverDisposed, 1);
            if (_observedLayoutFont is not null)
                _observedLayoutFont.LayoutMetricsChanged -= FontLayoutMetricsChanged;
            _observedLayoutFont = null;
            base.OnDestroying();
        }

        private string? _publishedFontAssetPath;
        /// <summary>Portable cooked font identity set by the browser publisher and resolved before UI activation.</summary>
        public string? PublishedFontAssetPath
        {
            get => _publishedFontAssetPath;
            set => SetField(ref _publishedFontAssetPath, value);
        }

        private bool _animatableTransforms = false;
        /// <summary>
        /// When true, per-glyph adjustments from <see cref="GlyphRelativeTransforms"/> are applied during buffer writes.
        /// </summary>
        public bool AnimatableTransforms
        {
            get => _animatableTransforms;
            set => SetField(ref _animatableTransforms, value);
        }

        private bool _disableBatching = false;
        /// <summary>
        /// When true, this text component always uses the per-component render path instead of the batched UI text renderer.
        /// </summary>
        public bool DisableBatching
        {
            get => _disableBatching;
            set => SetField(ref _disableBatching, value);
        }

        private EBatchedTextDebugMode _batchedDebugMode = EBatchedTextDebugMode.None;
        /// <summary>
        /// Diagnostic mode used only by the batched UI text shader.
        /// </summary>
        public EBatchedTextDebugMode BatchedDebugMode
        {
            get => _batchedDebugMode;
            set => SetField(ref _batchedDebugMode, value);
        }

        private FontGlyphSet.EWrapMode _wordWrap = FontGlyphSet.EWrapMode.None;
        /// <summary>
        /// Controls how glyphs wrap when reaching the available bounds.
        /// </summary>
        public FontGlyphSet.EWrapMode WrapMode
        {
            get => _wordWrap;
            set => SetField(ref _wordWrap, value);
        }

        private float? _fontSize = 30.0f;
        /// <summary>
        /// The size of the font in points (pt).
        /// If null, the font size will be automatically calculated to fill the transform bounds.
        /// </summary>
        public float? FontSize
        {
            get => _fontSize;
            set => SetField(ref _fontSize, value);
        }

        private bool _hideOverflow = true;
        /// <summary>
        /// Controls whether overflow should be clipped to the bounds.
        /// </summary>
        /// <remarks>
        /// This flag is currently not enforced directly by <see cref="UITextComponent"/> (no scissor/clipping is applied here).
        /// It is kept as part of the public UI configuration surface for callers.
        /// </remarks>
        public bool HideOverflow
        {
            get => _hideOverflow;
            set => SetField(ref _hideOverflow, value);
        }

        #endregion

        #region Render Configuration

        private RenderingParameters _renderParameters = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new()
            {
                Enabled = ERenderParamUsage.Disabled,
                Function = EComparison.Always
            },
            StencilTest = new()
            {
                Enabled = ERenderParamUsage.Disabled
            },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };

        /// <summary>
        /// Render state options for the underlying material/renderer.
        /// </summary>
        public RenderingParameters RenderParameters
        {
            get => _renderParameters;
            set => SetField(ref _renderParameters, value);
        }

        private XRShader[]? _nonVertexShadersOverride;

        /// <summary>
        /// Optional override for all non-vertex shaders for the text material.
        /// </summary>
        /// <remarks>
        /// When null, the default text fragment shader is used.
        /// </remarks>
        public XRShader[]? NonVertexShadersOverride
        {
            get => _nonVertexShadersOverride;
            set => SetField(ref _nonVertexShadersOverride, value);
        }

        private ColorF4 _color = new(0.0f, 0.0f, 0.0f, 1.0f);

        /// <summary>
        /// Tint color applied to the text shader.
        /// </summary>
        public ColorF4 Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }

        private ColorF4 _outlineColor = new(0.0f, 0.0f, 0.0f, 0.0f);

        /// <summary>
        /// Optional stroke color rendered behind glyph coverage.
        /// </summary>
        public ColorF4 OutlineColor
        {
            get => _outlineColor;
            set => SetField(ref _outlineColor, value);
        }

        private float _outlineThickness = 0.0f;

        /// <summary>
        /// Stroke radius in atlas texels. Set to zero to disable outlining.
        /// </summary>
        public float OutlineThickness
        {
            get => _outlineThickness;
            set => SetField(ref _outlineThickness, MathF.Max(0.0f, value));
        }

        private bool _outlineAffectsSpacing = false;

        /// <summary>
        /// When true, glyph layout reserves extra horizontal and vertical spacing equal to <see cref="OutlineThickness"/>.
        /// </summary>
        public bool OutlineAffectsSpacing
        {
            get => _outlineAffectsSpacing;
            set => SetField(ref _outlineAffectsSpacing, value);
        }

        private float _msdfFillBias = DefaultMsdfFillBias;

        /// <summary>
        /// Positive coverage bias applied by the MSDF/MTSDF shaders so thin strokes reach solid fill sooner.
        /// </summary>
        public float MsdfFillBias
        {
            get => _msdfFillBias;
            set => SetField(ref _msdfFillBias, Math.Clamp(value, 0.0f, 1.0f));
        }

        #endregion

        #region Transform Wiring / Layout

        /// <inheritdoc />
        protected override void OnTransformChanging()
        {
            base.OnTransformChanging();

            if (!SceneNode.TryGetTransformAs<UIBoundableTransform>(out var tfm) || tfm is null)
                return;
            
            tfm.CalcAutoHeightCallback = null;
            tfm.CalcAutoWidthCallback = null;
        }

        /// <inheritdoc />
        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();

            if (!SceneNode.TryGetTransformAs<UIBoundableTransform>(out var tfm) || tfm is null)
                return;
            
            tfm.CalcAutoHeightCallback = CalcAutoHeight;
            tfm.CalcAutoWidthCallback = CalcAutoWidth;
        }

        /// <inheritdoc />
        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(UIBoundableTransform.ActualSize):
                    if (!BoundableTransform.IsApplyingCalculatedLayout && HasAuthoredOrMaterializedText && BoundsAffectGlyphLayout())
                        UpdateText(false, false);
                    break;
            }
        }

        private bool BoundsAffectGlyphLayout()
            => WrapMode != FontGlyphSet.EWrapMode.None
            || FontSize is null
            || HorizontalAlignment != EHorizontalAlignment.Left
            || VerticalAlignment != EVerticalAlignment.Bottom;

        #endregion

        #region Measurement

        public static float MeasureWidth(string name, FontGlyphSet font, float fontSize)
        {
            List<(Vector4 transform, Vector4 uvs)> glyphs = [];
            font.GetQuads(name, glyphs, fontSize, float.MaxValue, float.MaxValue, FontGlyphSet.EWrapMode.None, 0.0f);
            if (glyphs.Count == 0)
                return 0.0f;
            var (transform, _) = glyphs[^1];
            return transform.X + transform.Z;
        }

        private float CalcAutoWidth(UIBoundableTransform transform)
        {
            return MeasureIntrinsic(transform).X;
        }

        private float CalcAutoHeight(UIBoundableTransform transform)
        {
            return MeasureIntrinsic(transform).Y;
        }

        private Vector2 MeasureIntrinsic(UIBoundableTransform transform)
        {
            UITextLayoutInput? input = CaptureLayoutInput();
            if (input is null)
                return Vector2.Zero;
            float width = WrapMode == FontGlyphSet.EWrapMode.None ? float.MaxValue : transform.ActualWidth;
            if (width <= 0.0f)
                width = float.MaxValue;
            return UITextLayoutKernel.Measure(input, new(width, float.MaxValue)).IntrinsicSize;
        }

        /// <summary>Capture text and font values on the UI owner thread.</summary>
        public UITextLayoutInput? CaptureLayoutInput()
        {
            FontGlyphSet font = EnsureLayoutFont();
            if (!font.TryCaptureLayoutMetrics(out FrozenDictionary<int, UIRuneMetrics> metrics,
                out Vector2 atlasSize, out float emSize, out long fontRevision))
            {
                if (UseShapedText)
                    throw new NotSupportedException("UI.ShapedFontMetricsUnavailable: the font has no atlas metrics.");
                return null;
            }

            float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;
            float spacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);
            float dpiScale = BoundableTransform.GetCanvasTransform()?.DpiScale ?? 1.0f;
            string text = Text ?? string.Empty;
            float? fontSize = FontSize;
            FontGlyphSet.EWrapMode wrap = WrapMode;
            float lineSpacing = DefaultLineSpacing + outlineSpacing;
            EHorizontalAlignment horizontal = HorizontalAlignment;
            EVerticalAlignment vertical = VerticalAlignment;
            Vector4 margins = BoundableTransform.Margins;
            bool shapedText = UseShapedText;
            string language = ShapeLanguage;
            UITextDirection direction = ShapeDirection;
            string? featureSource = OpenTypeFeatures;
            long backendGeneration = UITextShapingBackendRegistry.Generation;
            UITextLayoutInput? previous = _layoutInput;
            if (previous is not null && ReferenceEquals(_layoutInputFont, _font) &&
                previous.Text == text &&
                previous.FontRevision == fontRevision && previous.FontSize == fontSize &&
                previous.Wrap == wrap && previous.Spacing == spacing &&
                previous.LineSpacing == lineSpacing &&
                previous.HorizontalAlignment == horizontal &&
                previous.VerticalAlignment == vertical &&
                previous.Margins == margins && previous.AtlasSize == atlasSize &&
                previous.EmSize == emSize && previous.DpiScale == dpiScale &&
                (previous.ShapedRun is not null) == shapedText &&
                (!shapedText || _layoutInputBackendGeneration == backendGeneration &&
                    previous.ShapedRun is { } shaped &&
                    shaped.FaceContentIdentity == GetRequiredShapingFace(font).ContentIdentity &&
                    shaped.Language == language && shaped.Direction == direction &&
                    shaped.FeatureSource == featureSource) &&
                (ReferenceEquals(previous.Runes, metrics) || MetricsEqual(previous.Runes, metrics)) &&
                (!shapedText || backendGeneration == UITextShapingBackendRegistry.Generation))
                return previous;

            IUITextShapingBackend? backend = shapedText ? UITextShapingBackendRegistry.Current : null;
            if (shapedText && backend is null)
                throw new NotSupportedException("UI.ShapingBackendUnavailable: install a text shaping backend.");
            UITextShapedRunInput? shapedRun = shapedText ? CaptureShapedRun(text, font,
                language, direction, featureSource, backend!) : null;
            if (shapedText && (backendGeneration != UITextShapingBackendRegistry.Generation ||
                !ReferenceEquals(backend, UITextShapingBackendRegistry.Current) ||
                !ReferenceEquals(font, _font) || (Text ?? string.Empty) != text ||
                FontSize != fontSize || WrapMode != wrap || ShapeLanguage != language ||
                ShapeDirection != direction || OpenTypeFeatures != featureSource ||
                UseShapedText != shapedText || HorizontalAlignment != horizontal ||
                VerticalAlignment != vertical || BoundableTransform.Margins != margins ||
                (BoundableTransform.GetCanvasTransform()?.DpiScale ?? 1.0f) != dpiScale ||
                ResolveLayoutSpacingForOutputPixels(OutlineAffectsSpacing ? OutlineThickness : 0.0f) != spacing ||
                DefaultLineSpacing + (OutlineAffectsSpacing ? OutlineThickness : 0.0f) != lineSpacing))
                throw new InvalidOperationException("UI.ShapingInputsChanged: text inputs changed during shaping.");
            if (shapedText && (!font.TryCaptureLayoutMetrics(out _, out Vector2 currentAtlas,
                out float currentEm, out long currentRevision) ||
                currentRevision != fontRevision || currentAtlas != atlasSize || currentEm != emSize))
                throw new InvalidOperationException("UI.ShapingInputsChanged: font metrics changed during shaping.");
            _layoutInput = new(text, metrics, ++_textRevision, fontRevision,
                emSize, atlasSize, fontSize, wrap, spacing,
                lineSpacing, horizontal, vertical,
                margins, dpiScale, ShapedRun: shapedRun);
            _layoutInputFont = _font;
            _layoutInputBackendGeneration = backendGeneration;
            return _layoutInput;
        }

        private static bool MetricsEqual(FrozenDictionary<int, UIRuneMetrics> left,
            FrozenDictionary<int, UIRuneMetrics> right)
        {
            if (left.Count != right.Count)
                return false;
            foreach ((int rune, UIRuneMetrics metric) in left)
                if (!right.TryGetValue(rune, out UIRuneMetrics other) || other != metric)
                    return false;
            return true;
        }

        private float CalcAutoWidthNoLock()
        {
            if (_glyphs is null || _glyphs.Count == 0)
            {
                if (string.IsNullOrEmpty(Text))
                    return 0.0f;

                FontGlyphSet font = EnsureLayoutFont();
                return MeasureWidth(Text, font, FontSize ?? 30.0f);
            }

            return _glyphs.Max(g => g.transform.X + g.transform.Z);
        }

        private float CalcAutoHeightNoLock()
        {
            if (_glyphs is null || _glyphs.Count == 0)
                return FontSize ?? 30.0f;

            float max = _glyphs.Max(g => g.transform.Y);
            float min = _glyphs.Min(g => g.transform.Y + g.transform.W);
            return max - min;
        }

        #endregion

        #region Property Change Handling

        protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
            => propertyName switch
            {
                nameof(Text) or nameof(Font) or nameof(FontSize) or nameof(WrapMode) or
                nameof(UseShapedText) or nameof(ShapeLanguage) or nameof(ShapeDirection) or
                nameof(OpenTypeFeatures) or
                nameof(HorizontalAlignment) or nameof(VerticalAlignment) or nameof(OutlineAffectsSpacing) =>
                    UIChangeDomain.Text | UIChangeDomain.Measure | UIChangeDomain.Arrange | UIChangeDomain.Visual,
                nameof(OutlineThickness) when OutlineAffectsSpacing =>
                    UIChangeDomain.Text | UIChangeDomain.Measure | UIChangeDomain.Arrange | UIChangeDomain.Visual,
                nameof(GlyphRelativeTransforms) or nameof(AnimatableTransforms) => UIChangeDomain.Text | UIChangeDomain.Visual,
                nameof(NonVertexShadersOverride) or nameof(DisableBatching) => UIChangeDomain.Visual | UIChangeDomain.Topology,
                nameof(HideOverflow) => UIChangeDomain.Clip | UIChangeDomain.HitTest,
                _ => base.GetUIChangeDomain(propertyName),
            };

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            // YAML assigns properties before ComponentsSerialized gives the component an owner.
            // OnComponentActivated builds the authored text after its transform is wired.
            if (SceneNode is null)
                return;

            if (propName == nameof(GlyphRelativeTransforms))
            {
                MarkGlyphTransformsChanged();
                return;
            }

            if (!HasAuthoredOrMaterializedText)
                return;

            switch (propName)
            {
                case nameof(Font):
                case nameof(AnimatableTransforms):
                case nameof(NonVertexShadersOverride):
                    UpdateText(true);
                    break;
                case nameof(Text):
                    UpdateText(false);
                    break;
                case nameof(FontSize):
                case nameof(WrapMode):
                case nameof(UseShapedText):
                case nameof(ShapeLanguage):
                case nameof(ShapeDirection):
                case nameof(OpenTypeFeatures):
                case nameof(HorizontalAlignment):
                case nameof(VerticalAlignment):
                    UpdateText(false);
                    break;
                case nameof(RenderPass):
                    {
                        var mat = RenderCommand3D.Mesh?.Material;
                        if (mat is not null)
                            mat.RenderPass = RenderPass;
                        else
                            UpdateText(true);
                    }
                    break;
                case nameof(RenderParameters):
                    {
                        var mat = RenderCommand3D.Mesh?.Material;
                        if (mat is not null)
                            mat.RenderOptions = RenderParameters;
                        else
                            UpdateText(true);
                    }
                    break;
                case nameof(Color):
                    {
                        var mat = Mesh?.Material;
                        if (mat is not null)
                            mat.SetVector4(TextColorUniformName, Color);
                        else
                            UpdateText(true);
                    }
                    break;
                case nameof(OutlineColor):
                    {
                        var mat = Mesh?.Material;
                        if (mat is not null)
                            mat.SetVector4(OutlineColorUniformName, OutlineColor);
                        else
                            UpdateText(true);
                    }
                    break;
                case nameof(OutlineThickness):
                    {
                        var mat = Mesh?.Material;
                        if (mat is not null)
                            mat.SetFloat(OutlineThicknessUniformName, OutlineThickness);
                        else
                            UpdateText(true);

                        if (OutlineAffectsSpacing)
                            UpdateText(false);
                    }
                    break;
                case nameof(OutlineAffectsSpacing):
                    {
                        UpdateText(false);
                    }
                    break;
                case nameof(MsdfFillBias):
                    {
                        var mat = Mesh?.Material;
                        if (mat is not null)
                            mat.SetFloat(MsdfFillBiasUniformName, MsdfFillBias);
                        else
                            UpdateText(true);
                    }
                    break;
            }
        }

        #endregion

        #region Text Layout + Mesh/Material Creation

        /// <summary>
        /// Retrieves glyph data from the font and resizes SSBOs if necessary.
        /// Verifies that the mesh is created and the font atlas is loaded.
        /// </summary>
        /// <param name="forceRemake"></param>
        protected virtual void UpdateText(bool forceRemake, bool invalidateLayout = true)
        {
            FontGlyphSet font = EnsureLayoutFont();
            if (!ReferenceEquals(_capturedGlyphOverrides, GlyphRelativeTransforms))
                CaptureGlyphOverrides();
            //Task.Run(() =>
            //{
                VerifyCreated(forceRemake, font.Atlas);
                uint count;
                UITextLayoutInput? input = CaptureLayoutInput();
                using (_glyphLock.EnterScope())
                {
                    var tfm = BoundableTransform;
                    float w = tfm.ActualWidth;
                    float h = tfm.ActualHeight;
                    if (w <= 0.0f)
                        w = float.MaxValue;
                    if (h <= 0.0f)
                        h = float.MaxValue;
                    _glyphs.Clear();
                    _layoutResult = input is null ? null : UITextLayoutKernel.Measure(input, new(w, h));
                    _layoutResultActualSize = tfm.ActualSize;
                    if (_layoutResult is not null)
                        foreach (UITextLayoutGlyph glyph in _layoutResult.Glyphs.Span)
                            _glyphs.Add((glyph.Transform, glyph.Uvs));
                    count = (uint)_glyphs.Count;
                }
                ResizeGlyphCount(count, invalidateLayout);
            //});
        }

        /// <summary>Apply text geometry from an accepted layout generation on the UI owner thread.</summary>
        public bool ApplyLayoutResult(UITextLayoutResult result, Vector2 actualSize)
        {
            UITextLayoutInput? input = _layoutInput;
            if (input is null || result.TextRevision != input.TextRevision ||
                result.FontRevision != input.FontRevision ||
                BoundableTransform.ActualSize != actualSize)
                return false;

            FontGlyphSet font = EnsureLayoutFont();
            VerifyCreated(false, font.Atlas);
            using (_glyphLock.EnterScope())
            {
                _glyphs.Clear();
                foreach (UITextLayoutGlyph glyph in result.Glyphs.Span)
                    _glyphs.Add((glyph.Transform, glyph.Uvs));
                _layoutResult = result;
                _layoutResultActualSize = actualSize;
                ResizeGlyphCount((uint)_glyphs.Count, invalidateLayout: false);
            }
            return true;
        }

        /// <summary>Returns placed caret geometry only for the currently captured text and font.</summary>
        public bool TryGetCurrentLayoutResult(out UITextLayoutResult result)
        {
            UITextLayoutInput? input = CaptureLayoutInput();
            UITextLayoutResult? current = _layoutResult;
            if (input is not null && current is not null &&
                current.TextRevision == input.TextRevision && current.FontRevision == input.FontRevision &&
                _layoutResultActualSize == BoundableTransform.ActualSize)
            {
                result = current;
                return true;
            }
            result = null!;
            return false;
        }

        /// <summary>Rebuilds attached text after a restore changes its value without property notifications.</summary>
        internal void RefreshTextLayoutAfterSuppressedChange()
        {
            if (SceneNode is not null)
                UpdateText(forceRemake: false);
        }

        private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)
        {
            if (outputSpacing <= 0.0f)
                return 0.0f;

            float layoutEmSize = MathF.Max(Font?.LayoutEmSize ?? FontSize ?? 1.0f, 1e-6f);
            float resolvedFontSize = MathF.Max(FontSize ?? layoutEmSize, 1e-6f);
            return outputSpacing * layoutEmSize / resolvedFontSize;
        }

        private void AlignQuads(UIBoundableTransform tfm, float w, float h)
        {
            if (VerticalAlignment != EVerticalAlignment.Bottom)
                AlignQuadsVertical(tfm, h);
            if (HorizontalAlignment != EHorizontalAlignment.Left)
                AlignQuadsHorizontal(tfm, w);
        }

        private void AlignQuadsHorizontal(UIBoundableTransform tfm, float w)
        {
            float marginRight = BoundableTransform.Margins.Z;

            if (HorizontalAlignment == EHorizontalAlignment.Center)
            {
                // Per-line centering: center each line independently within the region.
                // When auto-sizing (w unconstrained), center within the widest line's width.
                float regionW = w < float.MaxValue * 0.5f ? w : CalcAutoWidthNoLock();

                // Detect lines by X-position reset (robust for LTR text with varying glyph Y offsets).
                int i = 0;
                while (i < _glyphs.Count)
                {
                    int lineStart = i;
                    float lineWidth = _glyphs[i].transform.X + _glyphs[i].transform.Z;
                    i++;
                    while (i < _glyphs.Count && _glyphs[i].transform.X >= _glyphs[i - 1].transform.X - 0.1f)
                    {
                        float right = _glyphs[i].transform.X + _glyphs[i].transform.Z;
                        if (right > lineWidth)
                            lineWidth = right;
                        i++;
                    }
                    float offset = (regionW - lineWidth - marginRight) / 2.0f;
                    for (int j = lineStart; j < i; j++)
                    {
                        (Vector4 transform, Vector4 uvs) = _glyphs[j];
                        _glyphs[j] = (new(transform.X + offset, transform.Y, transform.Z, transform.W), uvs);
                    }
                }
            }
            else
            {
                // Block-level alignment (Right)
                float textW = CalcAutoWidthNoLock();
                float offset = HorizontalAlignment switch
                {
                    EHorizontalAlignment.Right => w - textW - marginRight,
                    _ => 0.0f
                };
                for (int i = 0; i < _glyphs.Count; i++)
                {
                    (Vector4 transform, Vector4 uvs) = _glyphs[i];
                    _glyphs[i] = (new(transform.X + offset, transform.Y, transform.Z, transform.W), uvs);
                }
            }
        }

        private void AlignQuadsVertical(UIBoundableTransform tfm, float h)
        {
            //Calc max glyph height
            float textH = CalcAutoHeightNoLock();
            //Calc offset, which is a percentage of the remaining space not taken up by the text
            float offset = VerticalAlignment switch
            {
                EVerticalAlignment.Top => (h - textH - BoundableTransform.Margins.W),
                EVerticalAlignment.Center => (h - textH - BoundableTransform.Margins.W) / 2.0f,
                EVerticalAlignment.Bottom => 0.0f,
                _ => 0.0f
            };
            for (int i = 0; i < _glyphs.Count; i++)
            {
                (Vector4 transform, Vector4 uvs) = _glyphs[i];
                _glyphs[i] = (new(transform.X, transform.Y + offset, transform.Z, transform.W), uvs);
            }
        }

        /// <summary>
        /// Ensures that the mesh is created and the font atlas is loaded, and creates the material and SSBOs.
        /// </summary>
        /// <param name="forceRemake"></param>
        /// <param name="atlas"></param>
        private EFontAtlasType? _createdAtlasType;

        private void VerifyCreated(bool forceRemake, XRTexture2D? atlas)
        {
            // Browser screen text is rendered by the shared bitmap batch. Building this
            // legacy per-component mesh would synchronously load desktop GLSL stages.
            if (UseWebGpuBatchOnly)
                return;

            var mesh = Mesh;
            if (atlas is null)
                return;
            if (!forceRemake && mesh is not null && _createdAtlasType == Font?.AtlasType)
            {
                // The font can replace its atlas without changing text geometry.
                if (mesh.Material is { } material)
                {
                    if (material.Textures.Count > 0 && !ReferenceEquals(material.Textures[0], atlas))
                        material.Textures[0] = atlas;
                    material.SetFloat(MsdfDistanceRangeUniformName, Font?.DistanceRange ?? 0.0f);
                    material.SetFloat(MsdfDistanceRangeMiddleUniformName, Font?.DistanceRangeMiddle ?? 0.5f);
                }
                return;
            }

            if (mesh is not null)
            {
                mesh.SettingUniforms -= MeshRend_SettingUniforms;
                mesh.PreparingRenderData -= MeshRend_PreparingRenderData;
                mesh.Destroy();
            }

            var rend = new XRMeshRenderer(
                XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
                CreateMaterial(atlas));

            rend.SettingUniforms += MeshRend_SettingUniforms;
            rend.PreparingRenderData += MeshRend_PreparingRenderData;
            CreateSSBOs(rend);
            Mesh = rend;
            _createdAtlasType = Font?.AtlasType;
        }

        /// <summary>
        /// Override this method to create a fully custom material for the text using the font's atlas.
        /// </summary>
        /// <param name="atlas"></param>
        /// <returns></returns>
        protected virtual XRMaterial CreateMaterial(XRTexture2D atlas)
        {
            if (UseWebGpuBatchOnly)
                throw new NotSupportedException("WebGPU.UI.IndividualTextUnsupported: screen bitmap text must use the shared UI batch.");

            string fragmentShaderName = Font?.AtlasType switch
            {
                EFontAtlasType.Mtsdf => "TextMtsdfScreen.fs",
                EFontAtlasType.Msdf => "TextMsdf.fs",
                _ => "Text.fs"
            };
            XRShader vertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatable.vs" : "Text.vs"), EShaderType.Vertex);
            XRShader stereoVertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatableStereo.vs" : "TextStereo.vs"), EShaderType.Vertex);
            XRShader[] nonVertexShaders = NonVertexShadersOverride ?? [XRShader.EngineShader(Path.Combine("Common", fragmentShaderName), EShaderType.Fragment)];
            Debug.WriteAuxiliaryLog("text-material-diagnostics.log", $"UITextComponent.CreateMaterial: shader={fragmentShaderName}, atlasType={Font?.AtlasType}, glyphs={Font?.LayoutGlyphCount ?? 0}, atlas='{atlas.OriginalPath ?? atlas.FilePath ?? "<null>"}', fontAsset='{Font?.FilePath ?? "<null>"}', fontOriginal='{Font?.OriginalPath ?? "<null>"}', text='{SummarizeTextForDiagnostics(Text ?? string.Empty)}'");
            ShaderVar[] parameters =
            [
                new ShaderVector4(Color, TextColorUniformName),
                new ShaderVector4(OutlineColor, OutlineColorUniformName),
                new ShaderFloat(OutlineThickness, OutlineThicknessUniformName),
                new ShaderFloat(Font?.DistanceRange ?? 0.0f, MsdfDistanceRangeUniformName),
                new ShaderFloat(Font?.DistanceRangeMiddle ?? 0.5f, MsdfDistanceRangeMiddleUniformName),
                new ShaderFloat(MsdfFillBias, MsdfFillBiasUniformName),
            ];

            return new(parameters, [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))
            {
                RenderPass = RenderPass,
                RenderOptions = RenderParameters
            };
        }

        private static string SummarizeTextForDiagnostics(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string sanitized = text.Replace("\r", "\\r").Replace("\n", "\\n");
            return sanitized.Length <= 80 ? sanitized : sanitized[..80] + "...";
        }

        #endregion

        #region Buffer Updates / Rendering Hook

        /// <summary>
        /// If data has changed, this method will update the SSBOs with the new glyph data and push it to the GPU.
        /// </summary>
        /// <param name="vertexProgram"></param>
        /// <param name="materialProgram"></param>
        private void MeshRend_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            LogFpsTextRenderHook("SettingUniforms");
            PushGlyphDataIfNeeded();
        }

        private void MeshRend_PreparingRenderData()
        {
            LogFpsTextRenderHook("PreparingRenderData");
            PushGlyphDataIfNeeded();
        }

        private void PushGlyphDataIfNeeded()
        {
            if (!_dataChanged)
            {
                LogFpsTextRenderHook("PushGlyphDataIfNeeded skipped unchanged");
                return;
            }

            _dataChanged = false;

            if (_pushFull)
            {
                LogFpsTextRenderHook("PushGlyphDataIfNeeded full");
                _pushFull = false;
                PushBuffers();
            }
            else
            {
                LogFpsTextRenderHook("PushGlyphDataIfNeeded sub");
                PushSubBuffers();
            }
        }

        private static readonly bool FpsTextDiagnosticsEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_UI_BATCH_DIAGNOSTICS"), "1", StringComparison.Ordinal);

        private bool IsFpsTextDiagnosticTarget()
            => FpsTextDiagnosticsEnabled &&
               SceneNode?.Name == "TestTextNode";

        private void LogFpsTextRenderHook(string hook)
        {
            if (!IsFpsTextDiagnosticTarget() || _fpsTextRenderHookDiagCount++ >= 40)
                return;

            var tfm = BoundableTransform;
            var world = RenderCommand2D.WorldMatrix;
            var atlas = Font?.Atlas;
            Debug.Log(
                ELogCategory.UI,
                "[FpsTextDiag] UITextComponent.{0} #{1} node='{2}' instances2D={3} instances3D={4} dataChanged={5} pushFull={6} glyphs={7} alloc={8} renderPass={9} actual=({10:F1},{11:F1}) bottomLeft=({12:F1},{13:F1}) worldT=({14:F1},{15:F1},{16:F1}) atlas=({17}x{18}, mips={19}, min={20}, autoMip={21}, largest={22}, smallest={23})",
                hook,
                _fpsTextRenderHookDiagCount,
                SceneNode?.Name ?? "<null>",
                RenderCommand2D.Instances,
                RenderCommand3D.Instances,
                _dataChanged,
                _pushFull,
                _glyphs.Count,
                _allocatedGlyphCount,
                RenderPass,
                tfm.ActualWidth,
                tfm.ActualHeight,
                tfm.ActualLocalBottomLeftTranslation.X,
                tfm.ActualLocalBottomLeftTranslation.Y,
                world.M41,
                world.M42,
                world.M43,
                atlas?.Width ?? 0,
                atlas?.Height ?? 0,
                atlas?.Mipmaps?.Length ?? 0,
                atlas?.MinFilter.ToString() ?? "<null>",
                atlas?.AutoGenerateMipmaps.ToString() ?? "<null>",
                atlas?.LargestMipmapLevel ?? -1,
                atlas?.SmallestAllowedMipmapLevel ?? -1);
        }

        /// <summary>
        /// Resizes all SSBOs, sets glyph instance count, and invalidates layout if auto-sizing is enabled.
        /// </summary>
        /// <param name="count"></param>
        private static int _resizeGlyphDiagCount = 0;
        private void ResizeGlyphCount(uint count, bool invalidateLayout)
        {
            using (_glyphLock.EnterScope())
            {
                if (IsFpsTextDiagnosticTarget() && _resizeGlyphDiagCount < 10)
                {
                    _resizeGlyphDiagCount++;
                    Debug.Log(ELogCategory.UI, $"[FpsTextDiag] ResizeGlyphCount #{_resizeGlyphDiagCount} node='{SceneNode?.Name}' count={count} textLen={Text?.Length ?? -1} alloc={_allocatedGlyphCount} mesh={(Mesh is not null)} material={(Mesh?.Material is not null)}");
                }
                if (_allocatedGlyphCount < count)
                {
                    _transformsBuffer?.Resize(count);
                    _uvsBuffer?.Resize(count);
                    _rotationsBuffer?.Resize(count);
                    _allocatedGlyphCount = count;
                    _pushFull = true;
                }
                RenderCommand3D.Instances = count;
                RenderCommand2D.Instances = count;
                _dataChanged = true;

                if (invalidateLayout)
                {
                    var tfm = BoundableTransform;
                    if (tfm.UsesAutoSizing)
                        tfm.InvalidateLayout();
                }
            }
        }

        /// <summary>
        /// Returns a power of 2 allocation length for the number of glyphs.
        /// This is similar to how a list object would allocate memory with an internal array.
        /// </summary>
        /// <param name="numGlyphs"></param>
        /// <returns></returns>
        private static uint GetAllocationLength(uint numGlyphs)
        {
            //Get nearest power of 2 for the number of glyphs
            uint powerOf2 = 1u;
            while (powerOf2 < numGlyphs)
                powerOf2 *= 2u;
            return powerOf2;
        }

        /// <summary>
        /// Recreates SSBOs for the text glyph data and assigns them to the mesh renderer.
        /// </summary>
        /// <param name="meshRend"></param>
        private void CreateSSBOs(XRMeshRenderer meshRend)
        {
            using (_glyphLock.EnterScope())
            {
                string transformsBindingName = "GlyphTransformsBuffer";
                string uvsBindingName = $"GlyphTexCoordsBuffer";
                string rotationsBindingName = "GlyphRotationsBuffer";

                //TODO: use memory mapping instead of pushing

                //_allocatedGlyphCount = GetAllocationLength();

                meshRend.Buffers.Remove(transformsBindingName);
                _transformsBuffer?.Destroy();
                _transformsBuffer = new(transformsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount)
                {
                    //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                    //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                    Usage = AnimatableTransforms ? EBufferUsage.StreamDraw : EBufferUsage.StaticCopy,
                    BindingIndexOverride = 0,
                    DisposeOnPush = false
                };
                meshRend.Buffers.Add(transformsBindingName, _transformsBuffer);

                meshRend.Buffers.Remove(uvsBindingName);
                _uvsBuffer?.Destroy();
                _uvsBuffer = new(uvsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount)
                {
                    //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                    //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                    Usage = EBufferUsage.StaticCopy,
                    BindingIndexOverride = 1,
                    DisposeOnPush = false
                };
                meshRend.Buffers.Add(uvsBindingName, _uvsBuffer);

                meshRend.Buffers.Remove(rotationsBindingName);
                _rotationsBuffer?.Destroy();
                _rotationsBuffer = null;

                if (AnimatableTransforms)
                {
                    _rotationsBuffer = new(rotationsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount)
                    {
                        //RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
                        //StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.ClientStorage;
                        Usage = EBufferUsage.StaticCopy,
                        BindingIndexOverride = 2,
                        DisposeOnPush = false
                    };
                    meshRend.Buffers.Add(rotationsBindingName, _rotationsBuffer);
                }

                _dataChanged = true;
                _pushFull = true;
                LogFpsTextRenderHook("CreateSSBOs");
            }
        }

        /// <summary>
        /// MarkGlyphTransformsChanged should be called after updating GlyphRelativeTransforms.
        /// This will update the transforms buffer with the new values.
        /// </summary>
        public void MarkGlyphTransformsChanged()
        {
            CaptureGlyphOverrides();
            if (SceneNode is not null)
                UITransform.RecordUIChange(UIChangeDomain.Text | UIChangeDomain.Visual);
            _dataChanged = true;
        }

        private void CaptureGlyphOverrides()
        {
            if (GlyphRelativeTransforms.Count > UIDrawStateCapture.MaximumArrayLength)
                throw new NotSupportedException("Text glyph overrides exceed the bounded draw capture profile.");
            var snapshot = GlyphRelativeTransforms.ToFrozenDictionary();
            using (_glyphLock.EnterScope())
            {
                _glyphOverrides = snapshot;
                _capturedGlyphOverrides = GlyphRelativeTransforms;
                _glyphOverrideRevision++;
            }
        }

        #endregion

        #region SSBO Write / Push

        /// <summary>
        /// Writes the glyph data to the SSBOs.
        /// </summary>
        private void WriteData(bool force = false)
        {
            using (_glyphLock.EnterScope())
            {
                if (_layoutResult is not { } layout || _transformsBuffer?.ClientSideSource is null ||
                    _uvsBuffer?.ClientSideSource is null || !HasGlyphStorage(layout.Glyphs.Length))
                    return;
                if (!force && ReferenceEquals(_writtenGlyphLayout, layout) &&
                    ReferenceEquals(_writtenGlyphBuffer, _transformsBuffer) &&
                    _writtenGlyphOverrideRevision == _glyphOverrideRevision)
                    return;
                int changedGlyphs = UIGlyphBufferWriter.Write(layout, _glyphOverrides, AnimatableTransforms,
                    _transformsBuffer!, _uvsBuffer!, _rotationsBuffer, force);
                _writtenGlyphLayout = layout;
                _writtenGlyphOverrideRevision = _glyphOverrideRevision;
                _writtenGlyphBuffer = _transformsBuffer;
                _dataChanged = false;
                _pushFull = false;
                if (changedGlyphs > 0)
                {
                    UIRuntimeCounters.Add(UIWorkCounter.GlyphCopies);
                    UIRuntimeCounters.Add(UIWorkCounter.GlyphsCopied, changedGlyphs);
                }
                if (IsFpsTextDiagnosticTarget() && _fpsTextWriteDiagCount++ < 40)
                    Debug.Log(ELogCategory.UI, "UITextComponent.WriteData glyphs={0} changed={1}", layout.Glyphs.Length, changedGlyphs);
            }
        }

        private bool HasGlyphStorage(int glyphCount)
            => UIGlyphBufferWriter.HasStorage(glyphCount, _allocatedGlyphCount, AnimatableTransforms,
                _transformsBuffer, _uvsBuffer, _rotationsBuffer);

        /// <summary>Commits only the changed, used glyph spans.</summary>
        private void PushSubBuffers() => WriteData();

        /// <summary>Commits the used glyph spans after storage changes.</summary>
        private void PushBuffers() => WriteData(force: true);

        #endregion

        #region Batched Rendering

        protected override bool CanCapturePublicationUniforms => GetType() == typeof(UITextComponent);

        /// <summary>Copies glyph values from the accepted text result before deferred draw capture.</summary>
        protected override bool TryCapturePublicationPayload(in UILayoutNodeOutput output,
            UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
        {
            payload = null;
            failure = null;
            VerifyCreated(false, Font?.Atlas);
            XRMeshRenderer? renderer = RenderCommand2D.Mesh;
            if (!CanCapturePublicationUniforms || renderer is null || !renderer.HasOnlySettingUniformsHandler(MeshRend_SettingUniforms) ||
                !renderer.HasOnlyRenderDataPreparationHandler(MeshRend_PreparingRenderData))
            {
                failure = "UI.TextCapture.CallbackAdapterRequired: custom text callbacks require an owned data adapter.";
                return false;
            }
            if (renderer.Material is { } material)
            {
                material.SetVector4(TextColorUniformName, Color);
                material.SetVector4(OutlineColorUniformName, OutlineColor);
                material.SetFloat(OutlineThicknessUniformName, OutlineThickness);
                material.SetFloat(MsdfDistanceRangeUniformName, Font?.DistanceRange ?? 0.0f);
                material.SetFloat(MsdfDistanceRangeMiddleUniformName, Font?.DistanceRangeMiddle ?? 0.5f);
                material.SetFloat(MsdfFillBiasUniformName, MsdfFillBias);
            }
            WriteData();
            using (_glyphLock.EnterScope())
            {
                if (_layoutResult is not { } layout || _transformsBuffer?.ClientSideSource is null ||
                    _uvsBuffer?.ClientSideSource is null || !HasGlyphStorage(layout.Glyphs.Length))
                {
                    failure = "UI.TextCapture.LayoutPending: the accepted text result and glyph storage must be ready.";
                    return false;
                }
                RenderCommand2D.Instances = (uint)layout.Glyphs.Length;
                builder.UseCommittedGlyphRevisions = true;
                builder.UsedGlyphCount = NonVertexShadersOverride is null ? (uint)layout.Glyphs.Length : null;
                return builder.TryCapture(RenderCommand2D, true, out payload, out failure);
            }
        }

        /// <summary>Captures immutable text geometry and draw values for one canvas publication.</summary>
        public override UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
        {
            UITextLayoutResult? layout;
            using (_glyphLock.EnterScope())
                layout = _layoutResult;
            FontGlyphSet? font = Font;
            UIRenderRecord record = base.CapturePublicationRenderRecord(in output);
            XRTexture2D? atlas = font?.Atlas;
            UITextureSnapshot? snapshot = null;
            string? failure = record.CaptureFailure;
            bool visible = record.Visible;
            if (layout is null && !string.IsNullOrEmpty(Text) && visible)
                failure = "UI.TextCapture.LayoutPending: text requires an accepted layout result.";
            else if (layout is null || layout.Glyphs.Length == 0)
                visible = false;
            if (visible && record.SupportsBatch)
            {
                if (atlas is null)
                    failure = "UI.TextCapture.AtlasPending: text requires a complete font atlas.";
                else if (_publicationAtlasSnapshot is { } current && current.Matches(atlas))
                    snapshot = current;
                else if (!UITextureSnapshot.TryAcquire(atlas, out snapshot, out string? textureFailure))
                    failure = textureFailure;
                else
                {
                    ReleasePublicationAtlas();
                    _publicationAtlasSnapshot = snapshot;
                    snapshot!.SourceChanged += PublicationAtlasChanged;
                }
                if (snapshot is not null)
                    atlas = snapshot.Texture;
            }
            else
                ReleasePublicationAtlas();
            return record with
            {
                Visible = visible,
                CaptureFailure = failure,
                AtlasSnapshot = snapshot,
                IsText = true,
                TextColor = new Vector4(Color.R, Color.G, Color.B, Color.A),
                OutlineColor = new Vector4(OutlineColor.R, OutlineColor.G, OutlineColor.B, OutlineColor.A),
                OutlineThickness = OutlineThickness,
                Atlas = atlas,
                AtlasType = (int)(font?.AtlasType ?? default),
                DistanceRange = font?.DistanceRange ?? 0.0f,
                DistanceRangeMiddle = font?.DistanceRangeMiddle ?? 0.5f,
                MsdfFillBias = MsdfFillBias,
                DebugMode = (int)BatchedDebugMode,
                TextLayout = layout,
            };
        }

        private void PublicationAtlasChanged()
        {
            if (SceneNode?.Transform is UIBoundableTransform transform)
            {
                transform.RecordUIChange(UIChangeDomain.Text | UIChangeDomain.Visual);
                transform.InvalidateMeasure();
            }
        }

        private void ReleasePublicationAtlas()
        {
            UITextureSnapshot? previous = _publicationAtlasSnapshot;
            _publicationAtlasSnapshot = null;
            if (previous is null)
                return;
            previous.SourceChanged -= PublicationAtlasChanged;
            previous.Release();
        }

        /// <summary>
        /// Text components use the shared batch when their shader and raster state match it.
        /// </summary>
        public override bool SupportsBatchedRendering
            => !DisableBatching &&
               !AnimatableTransforms &&
               NonVertexShadersOverride is null &&
               UIBatchCollector.HasWebGpuRasterProfile(RenderParameters);

        protected override bool RegisterWithBatchCollector(UIBatchCollector collector, RenderCommandCollection passes)
        {
            UICanvasComponent? canvas = BoundableTransform.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
            if (canvas is null || !canvas.TryGetCollectingRenderRecord(RenderInfo2D.MembershipId, out UIRenderRecord record) ||
                !record.Visible || !record.SupportsBatch || record.TextLayout is not { } layout)
                return false;
            if (layout.Glyphs.Length == 0)
                return true;
            if (record.Atlas is not { } atlas)
                return false;
            Matrix4x4 worldMatrix = record.WorldMatrix;
            Vector4 textColor = record.TextColor;
            Vector4 outlineColor = record.OutlineColor;
            Vector4 bounds = record.Bounds;
            collector.AddTextQuad(record.RenderPass, record.ZIndex, passes, atlas,
                in worldMatrix, in textColor, in outlineColor, record.OutlineThickness, in bounds,
                record.AtlasType, record.DistanceRange, record.DistanceRangeMiddle, record.MsdfFillBias,
                record.DebugMode, layout, record.Crop);
            return true;
        }

        #endregion
    }
}
