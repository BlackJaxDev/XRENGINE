using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
    /// Renders 2D UI text by generating instanced quad glyphs from a <see cref="FontGlyphSet"/> atlas.
    /// </summary>
    /// <remarks>
    /// - Glyph quad transforms/UVs are written to shader storage buffers and pushed to the GPU on demand.
    /// - When <see cref="AnimatableTransforms"/> is enabled, per-glyph translation/scale/rotation can be applied via <see cref="GlyphRelativeTransforms"/>.
    /// - Auto sizing is supported through <see cref="UIBoundableTransform.CalcAutoWidthCallback"/> and <see cref="UIBoundableTransform.CalcAutoHeightCallback"/>.
    /// </remarks>
    public class UITextComponent : UIRenderableComponent
    {
        #region Construction
        public UITextComponent()
        {
            RenderPass = (int)EDefaultRenderPass.TransparentForward;
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

        #endregion

        #region Glyph State / Buffers

        private readonly List<(Vector4 transform, Vector4 uvs)> _glyphs = [];
        private XRDataBuffer? _uvsBuffer;
        private XRDataBuffer? _transformsBuffer;
        private XRDataBuffer? _rotationsBuffer;
        private readonly Lock _glyphLock = new();

        /// <summary>
        /// When true, the next render will push full buffer contents (e.g., after a resize).
        /// </summary>
        private bool _pushFull = false;

        /// <summary>
        /// When true, glyph/buffer data needs to be pushed before rendering.
        /// </summary>
        private bool _dataChanged = false;
        private uint _allocatedGlyphCount = 20;

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
        /// <summary>
        /// The font to use for the text.
        /// </summary>
        public FontGlyphSet? Font
        {
            get => _font;
            set => SetField(ref _font, value);
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
                    if (BoundsAffectGlyphLayout())
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

        //TODO: return and cache max width and height when calculating glyphs instead
        private float CalcAutoWidth(UIBoundableTransform transform)
        {
            using (_glyphLock.EnterScope())
                return CalcAutoWidthNoLock();
        }

        private float CalcAutoHeight(UIBoundableTransform transform)
        {
            using (_glyphLock.EnterScope())
                return CalcAutoHeightNoLock();
        }

        private float CalcAutoWidthNoLock()
        {
            if (_glyphs is null || _glyphs.Count == 0)
            {
                if (string.IsNullOrEmpty(Text))
                    return 0.0f;

                Font ??= FontGlyphSet.LoadDefaultUIFont();
                return MeasureWidth(Text, Font, FontSize ?? 30.0f);
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

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
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
                case nameof(GlyphRelativeTransforms):
                    MarkGlyphTransformsChanged();
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
            Font ??= FontGlyphSet.LoadDefaultUIFont();
            //Task.Run(() =>
            //{
                VerifyCreated(forceRemake, Font.Atlas);
                uint count;
                using (_glyphLock.EnterScope())
                {
                    var tfm = BoundableTransform;
                    float w = tfm.ActualWidth;
                    float h = tfm.ActualHeight;
                    if (w <= 0.0f)
                        w = float.MaxValue;
                    if (h <= 0.0f)
                        h = float.MaxValue;
                    Font.GetQuads(Text, _glyphs, FontSize, w, h, WrapMode, 0.0f, 2.0f);
                    AlignQuads(tfm, w, h);
                    count = (uint)(_glyphs?.Count ?? 0);
                }
                ResizeGlyphCount(count, invalidateLayout);
            //});
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
        private void VerifyCreated(bool forceRemake, XRTexture2D? atlas)
        {
            var mesh = Mesh;
            if ((!forceRemake && mesh is not null) || atlas is null)
                return;

            if (mesh is not null)
            {
                mesh.SettingUniforms -= MeshRend_SettingUniforms;
                mesh.Destroy();
            }

            var rend = new XRMeshRenderer(
                XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false)),
                CreateMaterial(atlas));

            rend.SettingUniforms += MeshRend_SettingUniforms;
            CreateSSBOs(rend);
            Mesh = rend;
        }

        /// <summary>
        /// Override this method to create a fully custom material for the text using the font's atlas.
        /// </summary>
        /// <param name="atlas"></param>
        /// <returns></returns>
        protected virtual XRMaterial CreateMaterial(XRTexture2D atlas)
        {
            string fragmentShaderName = Font?.AtlasType switch
            {
                EFontAtlasType.Mtsdf => "TextMtsdfScreen.fs",
                EFontAtlasType.Msdf => "TextMsdf.fs",
                _ => "Text.fs"
            };
            XRShader vertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatable.vs" : "Text.vs"), EShaderType.Vertex);
            XRShader stereoVertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatableStereo.vs" : "TextStereo.vs"), EShaderType.Vertex);
            XRShader[] nonVertexShaders = NonVertexShadersOverride ?? [XRShader.EngineShader(Path.Combine("Common", fragmentShaderName), EShaderType.Fragment)];
            Debug.WriteAuxiliaryLog("text-material-diagnostics.log", $"UITextComponent.CreateMaterial: shader={fragmentShaderName}, atlasType={Font?.AtlasType}, glyphs={Font?.Glyphs?.Count ?? 0}, atlas='{atlas.OriginalPath ?? atlas.FilePath ?? "<null>"}', fontAsset='{Font?.FilePath ?? "<null>"}', fontOriginal='{Font?.OriginalPath ?? "<null>"}', text='{SummarizeTextForDiagnostics(Text ?? string.Empty)}'");
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
            if (!_dataChanged)
                return;

            _dataChanged = false;

            if (_pushFull)
            {
                _pushFull = false;
                PushBuffers();
            }
            else
                PushSubBuffers();
        }

        /// <summary>
        /// Resizes all SSBOs, sets glyph instance count, and invalidates layout if auto-sizing is enabled.
        /// </summary>
        /// <param name="count"></param>
        private void ResizeGlyphCount(uint count, bool invalidateLayout)
        {
            RenderCommand3D.Instances = count;
            RenderCommand2D.Instances = count;
            if (_allocatedGlyphCount < count)
            {
                _allocatedGlyphCount = count;
                _transformsBuffer?.Resize(_allocatedGlyphCount);
                _uvsBuffer?.Resize(_allocatedGlyphCount);
                _rotationsBuffer?.Resize(_allocatedGlyphCount);
                _pushFull = true;
            }
            _dataChanged = true;

            if (invalidateLayout)
            {
                var tfm = BoundableTransform;
                if (tfm.UsesAutoSizing)
                    tfm.InvalidateLayout();
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
            string transformsBindingName = "GlyphTransformsBuffer";
            string uvsBindingName = $"GlyphTexCoordsBuffer";
            string rotationsBindingName = "GlyphRotationsBuffer";

            //TODO: use memory mapping instead of pushing

            //_allocatedGlyphCount = GetAllocationLength();

            meshRend.Buffers.Remove(transformsBindingName);
            _transformsBuffer?.Destroy();
            _transformsBuffer = new(transformsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount, EComponentType.Float, 4, false, false)
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
            _uvsBuffer = new(uvsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount, EComponentType.Float, 4, false, false)
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

            if (AnimatableTransforms)
            {
                _rotationsBuffer = new(rotationsBindingName, EBufferTarget.ShaderStorageBuffer, _allocatedGlyphCount, EComponentType.Float, 1, false, false)
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
        }

        /// <summary>
        /// MarkGlyphTransformsChanged should be called after updating GlyphRelativeTransforms.
        /// This will update the transforms buffer with the new values.
        /// </summary>
        public void MarkGlyphTransformsChanged()
            => _dataChanged = true;

        #endregion

        #region SSBO Write / Push

        /// <summary>
        /// Writes the glyph data to the SSBOs.
        /// </summary>
        private unsafe void WriteData()
        {
            if (_transformsBuffer is null || _uvsBuffer is null)
                return;
            
            (Vector4 transform, Vector4 uvs)[] glyphsCopy;
            using (_glyphLock.EnterScope())
                glyphsCopy = [.. _glyphs];

            float* tfmPtr = (float*)_transformsBuffer.ClientSideSource!.Address.Pointer;
            float* uvsPtr = (float*)_uvsBuffer.ClientSideSource!.Address.Pointer;

            for (int i = 0; i < glyphsCopy.Length; i++)
            {
                (Vector4 transform, Vector4 uvs) = glyphsCopy[i];

                if (AnimatableTransforms && GlyphRelativeTransforms.TryGetValue(i, out var relative))
                {
                    transform.X += relative.translation.X;
                    transform.Y += relative.translation.Y;
                    transform.Z *= relative.scale.X;
                    transform.W *= relative.scale.Y;

                    if (_rotationsBuffer is not null)
                        ((float*)_rotationsBuffer.ClientSideSource!.Address.Pointer)[i] = relative.rotation;
                }

                *tfmPtr++ = transform.X;
                *tfmPtr++ = transform.Y;
                *tfmPtr++ = transform.Z;
                *tfmPtr++ = transform.W;
                //Debug.Out(i.ToString() + ": " + transform.ToString());

                *uvsPtr++ = uvs.X;
                *uvsPtr++ = uvs.Y;
                *uvsPtr++ = uvs.Z;
                *uvsPtr++ = uvs.W;
                //Debug.Out(uvs.ToString());
            }
        }

        /// <summary>
        /// Pushes the sub-data of the SSBOs to the GPU.
        /// </summary>
        private void PushSubBuffers()
        {
            WriteData();
            _transformsBuffer?.PushSubData();
            _uvsBuffer?.PushSubData();
            _rotationsBuffer?.PushSubData();
        }

        /// <summary>
        /// Pushes the full data of the SSBOs to the GPU.
        /// </summary>
        private void PushBuffers()
        {
            WriteData();
            _transformsBuffer?.PushData();
            _uvsBuffer?.PushData();
            _rotationsBuffer?.PushData();
        }

        #endregion

        #region Batched Rendering

        /// <summary>
        /// Text components support batching unless they have clip-to-bounds enabled,
        /// use animatable (per-glyph rotation) transforms, or have no glyphs to render.
        /// </summary>
        public override bool SupportsBatchedRendering
            => !DisableBatching && !ClipToBounds && !AnimatableTransforms;

        protected override bool RegisterWithBatchCollector(UIBatchCollector collector, RenderCommandCollection passes)
        {
            var font = Font;
            var atlas = font?.Atlas;
            if (atlas is null)
                return false; // Font not loaded yet — fall back to individual rendering

            (Vector4 transform, Vector4 uvs)[] glyphsCopy;
            using (_glyphLock.EnterScope())
            {
                if (_glyphs.Count == 0)
                    return false; // No glyphs — fall back to individual rendering
                glyphsCopy = [.. _glyphs];
            }

            var tfm = BoundableTransform;
            var worldMatrix = GetRenderWorldMatrix(tfm);
            var textColor = new Vector4(Color.R, Color.G, Color.B, Color.A);
            var bottomLeft = tfm.ActualLocalBottomLeftTranslation;
            var bounds = new Vector4(bottomLeft.X, bottomLeft.Y, tfm.ActualWidth, tfm.ActualHeight);

            collector.AddTextQuad(RenderPass, RenderCommand2D.ZIndex, passes, atlas, in worldMatrix, in textColor, in bounds, glyphsCopy);
            return true;
        }

        #endregion
    }
}
