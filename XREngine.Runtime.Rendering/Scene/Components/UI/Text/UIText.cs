using XREngine.Extensions;
using System.Numerics;
using System.Collections.Frozen;
using XREngine.Data;
using XREngine.Rendering.UI.State;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public class UIText : XRBase, IPoolable
    {
        private TransformBase? _textTransform;
        public TransformBase? TextTransform
        {
            get => _textTransform;
            set => SetField(ref _textTransform, value);
        }

        private Vector3 _position = Vector3.Zero;
        public Vector3 LocalTranslation
        {
            get => _position;
            set => SetField(ref _position, value);
        }

        private float _roll = 0.0f;
        public float Roll
        {
            get => _roll;
            set => SetField(ref _roll, value);
        }

        private float _scale = 1.0f;
        public float Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
        }

        private Matrix4x4 _textMatrix = Matrix4x4.Identity;
        public Matrix4x4 TextMatrix
        {
            get => _textMatrix;
            private set => SetField(ref _textMatrix, value);
        }

        protected void OnTransformRenderMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            InvalidateTextMatrix();
        }

        public void InvalidateTextMatrix()
        {
            using (_matrixLock.EnterScope())
                _textMatrixInvalidated = true;
        }

        private Matrix4x4 CreateTextMatrix(Matrix4x4 renderMatrix)
        {
            var cam = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.SceneCamera;

            Vector3 camUp, camFwd, camPos;
            TransformBase? camTfm = cam?.Transform;
            if (camTfm is null)
            {
                camUp = Globals.Up;
                camFwd = Globals.Forward;
                camPos = Vector3.Zero;
            }
            else
            {
                camUp = camTfm.RenderUp;
                camFwd = camTfm.RenderForward;
                camPos = camTfm.RenderTranslation;
            }

            Vector3 textPos = renderMatrix.Translation + LocalTranslation;
            Vector3 scale = new(camPos.Distance(textPos) * Scale);

            if (RuntimeEngine.Rendering.State.IsReflectedMirrorPass)
                scale.X *= -1.0f; //Mirror text scale for reflected mirror pass

            if (Roll != 0.0f)
                camUp = Vector3.Transform(camUp, Quaternion.CreateFromAxisAngle(camFwd, float.DegreesToRadians(Roll)));

            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(float.DegreesToRadians(Roll)) * Matrix4x4.CreateWorld(textPos, camFwd, camUp);
        }

        private const string TextColorUniformName = "TextColor";
        private const string OutlineColorUniformName = "OutlineColor";
        private const string OutlineThicknessUniformName = "OutlineThickness";
        private const string MsdfDistanceRangeUniformName = "MsdfDistanceRange";
        private const string MsdfDistanceRangeMiddleUniformName = "MsdfDistanceRangeMiddle";
        private const string MsdfFillBiasUniformName = "MsdfFillBias";
        private const float DefaultMsdfFillBias = 0.5f;
        private const float DefaultLineSpacing = 2.0f;

        private UITextLayoutInput? _layoutInput;
        private UITextLayoutInput? _acceptedLayoutInput;
        private UITextLayoutResult? _layoutResult;
        private FontGlyphSet? _layoutInputFont;
        private long _textRevision;
        private UITextFontObserver? _fontObserver;
        private FontGlyphSet? _observedFont;
        private bool _released;
        private bool _destroyed;
        private int _fontDirty;
        internal UITextLayoutResult? LayoutResult => _layoutResult;
        private XRDataBuffer<Vector4>? _uvsBuffer;
        private XRDataBuffer<Vector4>? _transformsBuffer;
        private XRDataBuffer<float>? _rotationsBuffer;
        private readonly Lock _glyphLock = new();
        private FrozenDictionary<int, (Vector2 translation, Vector2 scale, float rotation)> _glyphOverrides =
            FrozenDictionary<int, (Vector2 translation, Vector2 scale, float rotation)>.Empty;
        private long _glyphOverrideRevision;
        private long _writtenGlyphOverrideRevision = -1;
        private UITextLayoutResult? _writtenGlyphLayout;
        private XRDataBuffer? _writtenGlyphBuffer;
        public bool _pushFull = false;
        public bool _dataChanged = false;
        private uint _allocatedGlyphCount = 20;

        private Dictionary<int, (Vector2 translation, Vector2 scale, float rotation)> _glyphRelativeTransforms = [];
        /// <summary>
        /// If AnimatableTransforms is true, this dictionary can be used to update the position, scale, and rotation of individual glyphs.
        /// </summary>
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
        /// If true, individual text character positions can be updated.
        /// </summary>
        public bool AnimatableTransforms
        {
            get => _animatableTransforms;
            set => SetField(ref _animatableTransforms, value);
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

        private XRMeshRenderer? _mesh;
        public XRMeshRenderer? Mesh
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            using (_glyphLock.EnterScope())
            {
                bool change = base.OnPropertyChanging(propName, field, @new);
                if (change)
                {
                    switch (propName)
                    {
                        case nameof(Font):
                            ReleaseFontObserver();
                            break;
                        case nameof(TextTransform):
                            if (TextTransform is not null)
                                TextTransform.RenderMatrixChanged -= OnTransformRenderMatrixChanged;
                            break;
                    }
                }
                return change;
            }
        }

        private bool HasAuthoredOrMaterializedText
            => !string.IsNullOrEmpty(_text) || Mesh is not null || _layoutResult is { Glyphs.Length: > 0 };

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            using (_glyphLock.EnterScope())
            {
                base.OnPropertyChanged(propName, prev, field);
                if (_destroyed)
                    return;
                if (propName == nameof(Font))
                    ObserveFont();
                switch (propName)
                {
                    case nameof(TextTransform):
                        if (!_released && TextTransform is not null)
                            TextTransform.RenderMatrixChanged += OnTransformRenderMatrixChanged;
                        InvalidateTextMatrix();
                        return;
                    case nameof(LocalTranslation):
                        InvalidateTextMatrix();
                        return;
                    case nameof(Roll):
                        InvalidateTextMatrix();
                        return;
                    case nameof(Scale):
                        InvalidateTextMatrix();
                        return;
                    case nameof(GlyphRelativeTransforms):
                        MarkGlyphTransformsChanged();
                        return;
                }

                if (_released || !HasAuthoredOrMaterializedText)
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
                    case nameof(RenderParameters):
                        {
                            var mat = Mesh?.Material;
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
        }

        /// <summary>
        /// Retrieves glyph data from the font and resizes SSBOs if necessary.
        /// Verifies that the mesh is created and the font atlas is loaded.
        /// </summary>
        /// <param name="forceRemake"></param>
        protected virtual void UpdateText(bool forceRemake)
        {
            using (_glyphLock.EnterScope())
            {
                if (_released || _destroyed)
                    return;
                Interlocked.Exchange(ref _fontDirty, 0);
                _font ??= FontGlyphSet.LoadDefaultFont();
                ObserveFont();
                VerifyCreated(forceRemake, _font.Atlas);
                UITextLayoutInput? input = CaptureLayoutInput();
                if (input is null)
                    _layoutResult = null;
                else if (!ReferenceEquals(_acceptedLayoutInput, input) || _layoutResult is null)
                    _layoutResult = UITextLayoutKernel.Measure(input, Vector2.Zero);
                _acceptedLayoutInput = input;
                ResizeGlyphCount((uint)(_layoutResult?.Glyphs.Length ?? 0));
            }
        }

        /// <summary>Captures reusable numeric font values without retaining mutable glyph objects.</summary>
        internal UITextLayoutInput? CaptureLayoutInput()
        {
            using (_glyphLock.EnterScope())
            {
                if (Font is not { } font || !font.TryCaptureLayoutMetrics(out var metrics,
                    out Vector2 atlasSize, out float emSize, out long fontRevision))
                    return null;
                float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;
                float spacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);
                UITextLayoutInput? previous = _layoutInput;
                if (previous is not null && ReferenceEquals(_layoutInputFont, font) &&
                    previous.Text == (Text ?? string.Empty) && previous.FontRevision == fontRevision &&
                    ReferenceEquals(previous.Runes, metrics) && previous.AtlasSize == atlasSize &&
                    previous.EmSize == emSize && previous.FontSize == FontSize &&
                    previous.Spacing == spacing && previous.LineSpacing == DefaultLineSpacing + outlineSpacing)
                    return previous;
                _layoutInputFont = font;
                _layoutInput = new(Text ?? string.Empty, metrics, ++_textRevision, fontRevision,
                    emSize, atlasSize, FontSize, FontGlyphSet.EWrapMode.None, spacing,
                    DefaultLineSpacing + outlineSpacing, EHorizontalAlignment.Left, EVerticalAlignment.Bottom, Vector4.Zero);
                return _layoutInput;
            }
        }

        private void ObserveFont()
        {
            if (_released || _destroyed || ReferenceEquals(_observedFont, Font))
                return;
            ReleaseFontObserver();
            _observedFont = Font;
            if (_observedFont is not null)
                _fontObserver = new UITextFontObserver(_observedFont, FontChanged);
        }

        private void ReleaseFontObserver()
        {
            _fontObserver?.Dispose();
            _fontObserver = null;
            _observedFont = null;
        }

        private void FontChanged() => Interlocked.Exchange(ref _fontDirty, 1);

        private void ConsumeFontChanges()
        {
            if (Interlocked.Exchange(ref _fontDirty, 0) != 0 &&
                !_released && !_destroyed && HasAuthoredOrMaterializedText)
                UpdateText(false);
        }

        private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)
        {
            if (outputSpacing <= 0.0f)
                return 0.0f;

            float layoutEmSize = MathF.Max(Font?.LayoutEmSize ?? FontSize ?? 1.0f, 1e-6f);
            float resolvedFontSize = MathF.Max(FontSize ?? layoutEmSize, 1e-6f);
            return outputSpacing * layoutEmSize / resolvedFontSize;
        }

        /// <summary>
        /// Ensures that the mesh is created and the font atlas is loaded, and creates the material and SSBOs.
        /// </summary>
        /// <param name="forceRemake"></param>
        /// <param name="atlas"></param>
        private EFontAtlasType? _createdAtlasType;
        private void VerifyCreated(bool forceRemake, XRTexture2D? atlas)
        {
            var mesh = Mesh;
            if (atlas is null)
                return;
            if (!forceRemake && mesh is not null && _createdAtlasType == Font?.AtlasType)
            {
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

        private RenderingParameters _renderParameters = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new()
            {
                Enabled = ERenderParamUsage.Enabled,
                Function = EComparison.Lequal,
                UpdateDepth = false
            },
            StencilTest = new()
            {
                Enabled = ERenderParamUsage.Disabled
            },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };
        public RenderingParameters RenderParameters
        {
            get => _renderParameters;
            set => SetField(ref _renderParameters, value);
        }

        private XRShader[]? _nonVertexShadersOverride;
        /// <summary>
        /// Override property for all non-vertex shaders for the text material.
        /// When null, the default fragment shader for text is used.
        /// </summary>
        public XRShader[]? NonVertexShadersOverride
        {
            get => _nonVertexShadersOverride;
            set => SetField(ref _nonVertexShadersOverride, value);
        }

        private ColorF4 _color = new(0.0f, 0.0f, 0.0f, 1.0f);
        public ColorF4 Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }

        private ColorF4 _outlineColor = new(0.0f, 0.0f, 0.0f, 0.0f);
        public ColorF4 OutlineColor
        {
            get => _outlineColor;
            set => SetField(ref _outlineColor, value);
        }

        private float _outlineThickness = 0.0f;
        public float OutlineThickness
        {
            get => _outlineThickness;
            set => SetField(ref _outlineThickness, MathF.Max(0.0f, value));
        }

        private bool _outlineAffectsSpacing = false;
        public bool OutlineAffectsSpacing
        {
            get => _outlineAffectsSpacing;
            set => SetField(ref _outlineAffectsSpacing, value);
        }

        private float _msdfFillBias = DefaultMsdfFillBias;
        public float MsdfFillBias
        {
            get => _msdfFillBias;
            set => SetField(ref _msdfFillBias, Math.Clamp(value, 0.0f, 1.0f));
        }

        private int _renderPass = (int)EDefaultRenderPass.TransparentForward;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        private readonly Lock _matrixLock = new();
        private bool _textMatrixInvalidated = false;
        public void Render()
        {
            using (_glyphLock.EnterScope())
            {
                ConsumeFontChanges();
                if (_released || _destroyed || Mesh is null || _glyphCount <= 0)
                    return;

                using (_matrixLock.EnterScope())
                {
                    if (_textMatrixInvalidated)
                    {
                        TextMatrix = CreateTextMatrix(TextTransform?.RenderMatrix ?? Matrix4x4.Identity);
                        _textMatrixInvalidated = false;
                    }
                }
                Mesh.Render(TextMatrix, TextMatrix, null, _glyphCount);
            }
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
                EFontAtlasType.Mtsdf => "TextMtsdf.fs",
                EFontAtlasType.Msdf => "TextMsdf.fs",
                _ => "Text.fs"
            };
            XRShader vertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatable.vs" : "Text.vs"), EShaderType.Vertex);
            XRShader stereoVertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatableStereo.vs" : "TextStereo.vs"), EShaderType.Vertex);
            XRShader[] nonVertexShaders = NonVertexShadersOverride ?? [XRShader.EngineShader(Path.Combine("Common", fragmentShaderName), EShaderType.Fragment)];
            Debug.WriteAuxiliaryLog("text-material-diagnostics.log", $"UIText.CreateMaterial: shader={fragmentShaderName}, atlasType={Font?.AtlasType}, glyphs={Font?.LayoutGlyphCount ?? 0}, atlas='{atlas.OriginalPath ?? atlas.FilePath ?? "<null>"}', fontAsset='{Font?.FilePath ?? "<null>"}', fontOriginal='{Font?.OriginalPath ?? "<null>"}', text='{SummarizeTextForDiagnostics(Text ?? string.Empty)}'");
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

        /// <summary>
        /// If data has changed, this method will update the SSBOs with the new glyph data and push it to the GPU.
        /// </summary>
        /// <param name="vertexProgram"></param>
        /// <param name="materialProgram"></param>
        private void MeshRend_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => WritePendingGlyphData();

        private void MeshRend_PreparingRenderData()
            => WritePendingGlyphData();

        internal void PushGlyphDataIfNeeded()
        {
            using (_glyphLock.EnterScope())
            {
                ConsumeFontChanges();
                WritePendingGlyphData();
            }
        }

        private void WritePendingGlyphData()
        {
            using (_glyphLock.EnterScope())
            {
                if (_released || _destroyed || !_dataChanged)
                    return;
                WriteData(force: _pushFull);
            }
        }

        private uint _glyphCount = 0;

        /// <summary>
        /// Resizes all SSBOs, sets glyph instance count, and invalidates layout if auto-sizing is enabled.
        /// </summary>
        /// <param name="count"></param>
        private void ResizeGlyphCount(uint count)
        {
            using (_glyphLock.EnterScope())
            {
                if (_allocatedGlyphCount < count)
                {
                    _transformsBuffer?.Resize(count);
                    _uvsBuffer?.Resize(count);
                    _rotationsBuffer?.Resize(count);
                    _allocatedGlyphCount = count;
                    _pushFull = true;
                }
                _glyphCount = count;
                _dataChanged = true;
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
            }
        }

        /// <summary>
        /// MarkGlyphTransformsChanged should be called after updating GlyphRelativeTransforms.
        /// This will update the transforms buffer with the new values.
        /// </summary>
        public void MarkGlyphTransformsChanged()
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            if (GlyphRelativeTransforms.Count > UIDrawStateCapture.MaximumArrayLength)
                throw new NotSupportedException("Text glyph overrides exceed the bounded draw capture profile.");
            var snapshot = GlyphRelativeTransforms.ToFrozenDictionary();
            using (_glyphLock.EnterScope())
            {
                _glyphOverrides = snapshot;
                _glyphOverrideRevision++;
                _dataChanged = true;
            }
        }

        /// <summary>
        /// Writes the glyph data to the SSBOs.
        /// </summary>
        private unsafe void WriteData(bool force = false)
        {
            using (_glyphLock.EnterScope())
            {
                if (_layoutResult is not { } layout)
                    return;
                if (_transformsBuffer?.ClientSideSource is not { } transforms ||
                    _uvsBuffer?.ClientSideSource is not { } uvs || !HasGlyphStorage(layout.Glyphs.Length))
                    throw new InvalidOperationException("UI.Text.GlyphStoragePending: complete glyph CPU storage is required before drawing.");
                if (!force && ReferenceEquals(_writtenGlyphLayout, layout) &&
                    ReferenceEquals(_writtenGlyphBuffer, _transformsBuffer) &&
                    _writtenGlyphOverrideRevision == _glyphOverrideRevision)
                {
                    _dataChanged = false;
                    _pushFull = false;
                    return;
                }
                ReadOnlySpan<UITextLayoutGlyph> glyphs = layout.Glyphs.Span;
                Span<Vector4> transformValues = new(transforms.Address.Pointer, glyphs.Length);
                Span<Vector4> uvValues = new(uvs.Address.Pointer, glyphs.Length);
                Span<float> rotations = AnimatableTransforms && _rotationsBuffer?.ClientSideSource is { } rotationSource
                    ? new Span<float>(rotationSource.Address.Pointer, glyphs.Length) : Span<float>.Empty;
                int transformFirst = glyphs.Length, transformLast = -1;
                int uvFirst = glyphs.Length, uvLast = -1;
                int rotationFirst = glyphs.Length, rotationLast = -1;
                int changedGlyphs = 0;
                for (int index = 0; index < glyphs.Length; index++)
                {
                    Vector4 transform = glyphs[index].Transform;
                    float rotation = 0;
                    if (AnimatableTransforms && _glyphOverrides.TryGetValue(index, out var relative))
                    {
                        transform.X += relative.translation.X;
                        transform.Y += relative.translation.Y;
                        transform.Z *= relative.scale.X;
                        transform.W *= relative.scale.Y;
                        rotation = relative.rotation;
                    }
                    bool changed = false;
                    if (force || transformValues[index] != transform)
                    {
                        transformValues[index] = transform;
                        transformFirst = Math.Min(transformFirst, index);
                        transformLast = index;
                        changed = true;
                    }
                    if (force || uvValues[index] != glyphs[index].Uvs)
                    {
                        uvValues[index] = glyphs[index].Uvs;
                        uvFirst = Math.Min(uvFirst, index);
                        uvLast = index;
                        changed = true;
                    }
                    if (!rotations.IsEmpty && (force || rotations[index] != rotation))
                    {
                        rotations[index] = rotation;
                        rotationFirst = Math.Min(rotationFirst, index);
                        rotationLast = index;
                        changed = true;
                    }
                    if (changed)
                        changedGlyphs++;
                }
                CommitGlyphRange(_transformsBuffer, transformFirst, transformLast, 16u);
                CommitGlyphRange(_uvsBuffer, uvFirst, uvLast, 16u);
                CommitGlyphRange(_rotationsBuffer, rotationFirst, rotationLast, 4u);
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
            }
        }

        private bool HasGlyphStorage(int glyphCount)
        {
            ulong vectorBytes = checked((ulong)glyphCount * 16UL);
            ulong rotationBytes = checked((ulong)glyphCount * 4UL);
            return glyphCount <= _allocatedGlyphCount &&
                _transformsBuffer is { IsDestroyed: false, ClientSideSource: { } transforms } &&
                _uvsBuffer is { IsDestroyed: false, ClientSideSource: { } uvs } &&
                transforms.Length >= vectorBytes && uvs.Length >= vectorBytes &&
                (vectorBytes == 0 || (transforms.Address != VoidPtr.Zero && uvs.Address != VoidPtr.Zero)) &&
                (!AnimatableTransforms ||
                 (_rotationsBuffer is { IsDestroyed: false, ClientSideSource: { } rotations } &&
                  rotations.Length >= rotationBytes && (rotationBytes == 0 || rotations.Address != VoidPtr.Zero)));
        }

        private static void CommitGlyphRange(XRDataBuffer? buffer, int first, int last, uint stride)
        {
            if (buffer is not null && last >= first)
                buffer.CommitDirtyBytes(checked((uint)first * stride), checked((uint)(last - first + 1) * stride));
        }

        /// <summary>Commits only the changed, used glyph spans.</summary>
        private void PushSubBuffers() => WriteData();

        /// <summary>Commits the used glyph spans after storage changes.</summary>
        private void PushBuffers() => WriteData(force: true);


        public void OnPoolableReset()
        {
            using (_glyphLock.EnterScope())
            {
                if (_destroyed)
                    throw new ObjectDisposedException(nameof(UIText));
                _released = false;
                if (TextTransform is not null)
                {
                    TextTransform.RenderMatrixChanged -= OnTransformRenderMatrixChanged;
                    TextTransform.RenderMatrixChanged += OnTransformRenderMatrixChanged;
                }
                ObserveFont();
                if (HasAuthoredOrMaterializedText)
                    UpdateText(false);
                InvalidateTextMatrix();
            }
        }

        public void OnPoolableReleased()
        {
            using (_glyphLock.EnterScope())
            {
                _released = true;
                Interlocked.Exchange(ref _fontDirty, 0);
                ReleaseFontObserver();
                if (TextTransform is not null)
                    TextTransform.RenderMatrixChanged -= OnTransformRenderMatrixChanged;
                _layoutInput = null;
                _acceptedLayoutInput = null;
                _layoutResult = null;
                _layoutInputFont = null;
                _writtenGlyphLayout = null;
                _glyphCount = 0;
                _dataChanged = false;
            }
        }

        public void OnPoolableDestroyed()
        {
            using (_glyphLock.EnterScope())
            {
                if (_destroyed)
                    return;
                OnPoolableReleased();
                _destroyed = true;
                if (Mesh is { } mesh)
                {
                    mesh.SettingUniforms -= MeshRend_SettingUniforms;
                    mesh.PreparingRenderData -= MeshRend_PreparingRenderData;
                    mesh.Destroy();
                }
            }
        }
    }
}
