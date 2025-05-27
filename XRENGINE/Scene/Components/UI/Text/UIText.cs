using Extensions;
using System.Numerics;
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
        public Vector3 Translation
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

        protected void OnTransformWorldMatrixChanged(TransformBase transform)
            => UpdateTextMatrix();

        public void UpdateTextMatrix()
        {
            var cam = Engine.State.MainPlayer.ControlledPawn?.GetCamera();
            TransformBase? camTfm = cam?.Transform;
            if (camTfm is null)
                return;

            Vector3 pos = (TextTransform?.WorldTranslation ?? Vector3.Zero) + Translation;
            Vector3 camUp = camTfm.WorldUp;
            float scale = cam!.Camera.Transform.WorldTranslation.Distance(pos) * Scale;
            if (Roll != 0.0f)
                camUp = Vector3.Transform(camUp, Quaternion.CreateFromAxisAngle(camTfm.WorldForward, float.DegreesToRadians(Roll)));
            TextMatrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(float.DegreesToRadians(Roll)) * Matrix4x4.CreateWorld(pos, camTfm.WorldForward, camUp);
        }

        private const string TextColorUniformName = "TextColor";

        private readonly List<(Vector4 transform, Vector4 uvs)> _glyphs = [];
        private XRDataBuffer? _uvsBuffer;
        private XRDataBuffer? _transformsBuffer;
        private XRDataBuffer? _rotationsBuffer;
        private readonly object _glyphLock = new();
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
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(TextTransform):
                        if (TextTransform is not null)
                            TextTransform.WorldMatrixChanged -= OnTransformWorldMatrixChanged;
                        break;
                }
            }
            return change;
        }

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
                case nameof(TextTransform):
                    if (TextTransform is not null)
                        TextTransform.WorldMatrixChanged += OnTransformWorldMatrixChanged;
                    UpdateTextMatrix();
                    break;
                case nameof(Translation):
                    UpdateTextMatrix();
                    break;
                case nameof(Roll):
                    UpdateTextMatrix();
                    break;
                case nameof(Scale):
                    UpdateTextMatrix();
                    break;
                case nameof(GlyphRelativeTransforms):
                    MarkGlyphTransformsChanged();
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
            }
        }

        /// <summary>
        /// Retrieves glyph data from the font and resizes SSBOs if necessary.
        /// Verifies that the mesh is created and the font atlas is loaded.
        /// </summary>
        /// <param name="forceRemake"></param>
        protected virtual void UpdateText(bool forceRemake)
        {
            Font ??= FontGlyphSet.LoadDefaultFont();
            VerifyCreated(forceRemake, Font.Atlas);
            uint count;
            lock (_glyphLock)
            {
                Font.GetQuads(Text, _glyphs, FontSize, 0, 0, FontGlyphSet.EWrapMode.None, 5.0f, 2.0f);
                count = (uint)(_glyphs?.Count ?? 0);
            }
            ResizeGlyphCount(count);
        }

        /// <summary>
        /// Ensures that the mesh is created and the font atlas is loaded, and creates the material and SSBOs.
        /// </summary>
        /// <param name="forceRemake"></param>
        /// <param name="atlas"></param>
        private void VerifyCreated(bool forceRemake, XRTexture2D? atlas)
        {
            var mesh = Mesh;
            if (!forceRemake && mesh is not null || atlas is null)
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

        private int _renderPass = (int)EDefaultRenderPass.TransparentForward;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        public void Render()
        {
            if (Mesh is null || _glyphCount <= 0)
                return;

            Mesh.Render(TextMatrix, null, _glyphCount);
        }

        /// <summary>
        /// Override this method to create a fully custom material for the text using the font's atlas.
        /// </summary>
        /// <param name="atlas"></param>
        /// <returns></returns>
        protected virtual XRMaterial CreateMaterial(XRTexture2D atlas)
        {
            XRShader vertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatable.vs" : "Text.vs"), EShaderType.Vertex);
            XRShader stereoVertexShader = XRShader.EngineShader(Path.Combine("Common", AnimatableTransforms ? "TextRotatableStereo.vs" : "TextStereo.vs"), EShaderType.Vertex);
            XRShader[] nonVertexShaders = NonVertexShadersOverride ?? [XRShader.EngineShader(Path.Combine("Common", "Text.fs"), EShaderType.Fragment)];
            return new([new ShaderVector4(Color, TextColorUniformName)], [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))
            {
                RenderPass = RenderPass,
                RenderOptions = RenderParameters
            };
        }

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

        private uint _glyphCount = 0;

        /// <summary>
        /// Resizes all SSBOs, sets glyph instance count, and invalidates layout if auto-sizing is enabled.
        /// </summary>
        /// <param name="count"></param>
        private void ResizeGlyphCount(uint count)
        {
            _glyphCount = count;
            if (_allocatedGlyphCount < count)
            {
                _allocatedGlyphCount = count;
                _transformsBuffer?.Resize(_allocatedGlyphCount);
                _uvsBuffer?.Resize(_allocatedGlyphCount);
                _rotationsBuffer?.Resize(_allocatedGlyphCount);
                _pushFull = true;
            }
            _dataChanged = true;
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

        /// <summary>
        /// Writes the glyph data to the SSBOs.
        /// </summary>
        private unsafe void WriteData()
        {
            if (_transformsBuffer is null || _uvsBuffer is null)
                return;

            (Vector4 transform, Vector4 uvs)[] glyphsCopy;
            lock (_glyphLock)
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

        public void OnPoolableReset()
        {

        }

        public void OnPoolableReleased()
        {

        }

        public void OnPoolableDestroyed()
        {
            Mesh?.Destroy();
        }
    }
}
