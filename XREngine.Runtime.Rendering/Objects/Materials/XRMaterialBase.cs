using XREngine.Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using YamlDotNet.Serialization;
using XREngine.Data.Transforms;

namespace XREngine.Rendering
{
    public abstract class XRMaterialBase : GenericRenderObject
    {
        private int _renderPass = (int)EDefaultRenderPass.OpaqueForward;
        /// <summary>
        /// This is the render pass bucket that any meshes using this material will be put in.
        /// Render passes are used to separate different types of rendering, such as opaque, transparent, etc.
        /// The number of passes and what each pass does is determined by the camera's render pipeline object!
        /// Use EDefaultRenderPass for this value and DefaultRenderPipeline as the render pipeline to use the default rendering setup.
        /// </summary>
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        public event Action<XRMaterialBase, XRRenderProgram>? SettingUniforms;
        public void OnSettingUniforms(XRRenderProgram program)
            => SettingUniforms?.Invoke(this, program);

        /// <summary>
        /// Uploads material-owned deformation inputs to the active vertex
        /// program, including generated auxiliary-pass programs.
        /// </summary>
        public event Action<XRRenderProgram>? SettingVertexUniforms;

        public void OnSettingVertexUniforms(XRRenderProgram program)
            => SettingVertexUniforms?.Invoke(program);
        public bool HasSettingUniformsHandlers
            => SettingUniforms is not null;

        /// <summary>
        /// Typed, generation-owned numeric binding publishers eligible for
        /// immutable backend capture and frequency-scoped reuse.
        /// </summary>
        [Browsable(false)]
        [YamlIgnore]
        public RenderBindingPublisherCollection BindingPublishers { get; } = new();

        public event Action<XRMaterialBase, XRRenderProgram>? SettingShadowUniforms;
        public void OnSettingShadowUniforms(XRRenderProgram program)
            => SettingShadowUniforms?.Invoke(this, program);
        public bool HasSettingShadowUniformHandlers
            => SettingShadowUniforms is not null;

        private ulong _bindingLayoutVersion = 1;
        [Browsable(false)]
        [YamlIgnore]
        public ulong BindingLayoutVersion
            => _bindingLayoutVersion;

        private ulong _bindingValueVersion = 1;
        /// <summary>
        /// Monotonic revision for material parameter values captured by queued render backends.
        /// Unlike <see cref="BindingLayoutVersion"/>, this changes when an existing
        /// <see cref="ShaderVar"/> value changes without altering the binding layout.
        /// </summary>
        [Browsable(false)]
        [YamlIgnore]
        public ulong BindingValueVersion
            => _bindingValueVersion;

        private ulong _bindingResourceVersion = 1;
        /// <summary>
        /// Monotonic revision for descriptor resources owned by this material.
        /// Numeric parameter mutations advance <see cref="BindingValueVersion"/>
        /// without invalidating descriptor publication.
        /// </summary>
        [Browsable(false)]
        [YamlIgnore]
        public ulong BindingResourceVersion
            => _bindingResourceVersion;

        public XRMaterialBase()
            => AttachTextureListHandlers(_textures);
        protected XRMaterialBase(ShaderVar[] parameters) : this()
        {
            Parameters = [.. parameters]; //Make copy
        }
        protected XRMaterialBase(XRTexture?[] textures) : this()
        {
            Textures = [.. textures];
        }
        protected XRMaterialBase(ShaderVar[] parameters, XRTexture?[] textures) : this()
        {
            Parameters = [.. parameters]; //Make copy
            Textures = [.. textures];
        }

        private XRRenderProgram? _shaderPipelineProgram;
        /// <summary>
        /// This is the program that represents this material.
        /// Will only be set if the renderer is using shader pipelines, so it can be combined later.
        /// May contain all kinds of shaders, including vertex, fragment, geometry, compute, etc.
        /// If it contains a vertex shader, the default generated vertex shader will not be used.
        /// </summary>
        [YamlIgnore]
        public XRRenderProgram? ShaderPipelineProgram
        {
            get => _shaderPipelineProgram;
            protected set => SetField(ref _shaderPipelineProgram, value);
        }

        private EProgramPriority _shaderProgramPriority = EProgramPriority.Main;
        /// <summary>
        /// Priority assigned to shader pipeline programs created for this material.
        /// </summary>
        [YamlIgnore]
        public EProgramPriority ShaderProgramPriority
        {
            get => _shaderProgramPriority;
            set
            {
                if (SetField(ref _shaderProgramPriority, value) && _shaderPipelineProgram is not null)
                    _shaderPipelineProgram.Priority = value;
            }
        }

        private RenderingParameters _renderOptions = new();
        /// <summary>
        /// These are special rendering options that the API can use to set its state separately from the shaders.
        /// </summary>
        public RenderingParameters RenderOptions
        {
            get => _renderOptions ??= new();
            set => SetField(ref _renderOptions, value ?? new());
        }

        protected ShaderVar[] _parameters = [];
        /// <summary>
        /// These are the uniforms that each shader in the program has requested.
        /// </summary>
        public ShaderVar[] Parameters
        {
            get => _parameters ??= [];
            set
            {
                if (SetField(ref _parameters, value ?? []))
                    ResetNameIndexCache();
            }
        }

        protected EventList<XRTexture?> _textures = [];
        /// <summary>
        /// These are the texture samplers that each shader in the program has requested.
        /// </summary>
        public EventList<XRTexture?> Textures
        {
            get => _textures;
            set => SetField(ref _textures, value ?? []);
        }

        /// <summary>
        /// Retrieves the material's uniform parameter at the given index.
        /// Use this to set uniform values to be passed to the fragment shader.
        /// </summary>
        public T2? Parameter<T2>(int index) where T2 : ShaderVar
            => Parameters.IndexInRangeArrayT(index) ? Parameters[index] as T2 : null;

        /// <summary>
        /// Retrieves the material's uniform parameter with the given name.
        /// Use this to set uniform values to be passed to the fragment shader.
        /// </summary>
        public T2? Parameter<T2>(string name) where T2 : ShaderVar
        {
            if (_nameIndexCache.TryGetValue(name, out var index))
            {
                if (Parameters.IndexInRangeArrayT(index) &&
                    string.Equals(Parameters[index]?.Name, name, StringComparison.Ordinal))
                {
                    return Parameter<T2>(index);
                }

                // Parameter arrays are exposed for serialization and editor mutation,
                // so callers can reorder or replace entries without invoking the
                // property setter. Never let a stale cached index resolve a different
                // uniform, because Uber resource reconstruction uses this lookup to
                // decide whether a typed parameter needs to be restored.
                _nameIndexCache.Remove(name);
            }

            for (var i = 0; i < Parameters.Length; i++)
            {
                if (string.Equals(Parameters[i]?.Name, name, StringComparison.Ordinal))
                {
                    _nameIndexCache[name] = i;
                    return Parameter<T2>(i);
                }
            }
            return null;
        }

        [YamlIgnore]
        private readonly Dictionary<string, int> _nameIndexCache = [];

        public void ResetNameIndexCache()
            => _nameIndexCache.Clear();

        private void AttachTextureListHandlers(EventList<XRTexture?>? textures)
        {
            if (textures is null)
                return;

            textures.PostModified += TexturesModified;
        }

        private void DetachTextureListHandlers(EventList<XRTexture?>? textures)
        {
            if (textures is null)
                return;

            textures.PostModified -= TexturesModified;
        }

        private void TexturesModified()
        {
            IncrementBindingLayoutVersion();
            IncrementBindingValueVersion();
            IncrementBindingResourceVersion();
        }

        private void IncrementBindingLayoutVersion()
        {
            unchecked
            {
                ulong next = _bindingLayoutVersion + 1;
                SetField(ref _bindingLayoutVersion, next == 0 ? 1 : next, nameof(BindingLayoutVersion));
            }
        }

        private void AttachParameterHandlers(ShaderVar[]? parameters)
        {
            if (parameters is null)
                return;

            for (int index = 0; index < parameters.Length; index++)
                if (parameters[index] is { } parameter)
                    parameter.ValueChanged += ParameterValueChanged;
        }

        private void DetachParameterHandlers(ShaderVar[]? parameters)
        {
            if (parameters is null)
                return;

            for (int index = 0; index < parameters.Length; index++)
                if (parameters[index] is { } parameter)
                    parameter.ValueChanged -= ParameterValueChanged;
        }

        private void ParameterValueChanged(ShaderVar _)
            => IncrementBindingValueVersion();

        private void IncrementBindingValueVersion()
        {
            unchecked
            {
                ulong next = _bindingValueVersion + 1;
                SetField(ref _bindingValueVersion, next == 0 ? 1 : next, nameof(BindingValueVersion));
            }
        }

        private void IncrementBindingResourceVersion()
        {
            unchecked
            {
                ulong next = _bindingResourceVersion + 1;
                SetField(
                    ref _bindingResourceVersion,
                    next == 0 ? 1 : next,
                    nameof(BindingResourceVersion));
            }
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Parameters):
                    DetachParameterHandlers(prev as ShaderVar[]);
                    AttachParameterHandlers(field as ShaderVar[]);
                    ResetNameIndexCache();
                    IncrementBindingLayoutVersion();
                    IncrementBindingValueVersion();
                    break;
                case nameof(Textures):
                    DetachTextureListHandlers(prev as EventList<XRTexture?>);
                    AttachTextureListHandlers(field as EventList<XRTexture?>);
                    IncrementBindingLayoutVersion();
                    IncrementBindingValueVersion();
                    IncrementBindingResourceVersion();
                    break;
            }
        }

        public void SetFloat(string name, float value)
            => Parameter<ShaderFloat>(name)?.Value = value;
        public void SetFloat(int index, float value)
            => Parameter<ShaderFloat>(index)?.Value = value;
        public void SetInt(string name, int value)
            => Parameter<ShaderInt>(name)?.Value = value;
        public void SetInt(int index, int value)
            => Parameter<ShaderInt>(index)?.Value = value;
        public void SetUInt(string name, uint value)
            => Parameter<ShaderUInt>(name)?.Value = value;
        public void SetUInt(int index, uint value)
            => Parameter<ShaderUInt>(index)?.Value = value;
        public void SetVector2(string name, Vector2 value)
            => Parameter<ShaderVector2>(name)?.Value = value;
        public void SetVector2(int index, Vector2 value)
            => Parameter<ShaderVector2>(index)?.Value = value;
        public void SetVector3(string name, Vector3 value)
            => Parameter<ShaderVector3>(name)?.Value = value;
        public void SetVector3(int index, Vector3 value)
            => Parameter<ShaderVector3>(index)?.Value = value;
        public void SetVector4(string name, Vector4 value)
            => Parameter<ShaderVector4>(name)?.Value = value;
        public void SetVector4(int index, Vector4 value)
            => Parameter<ShaderVector4>(index)?.Value = value;
        public void SetMatrix4(string name, Matrix4x4 value)
            => Parameter<ShaderMat4>(name)?.Value = value;
        public void SetMatrix4(int index, Matrix4x4 value)
            => Parameter<ShaderMat4>(index)?.Value = value;
        public void SetMatrixAffine(string name, AffineMatrix4x3 value)
            => Parameter<ShaderMat4>(name)?.Value = value.ToMatrix4x4();
        public void SetMatrixAffine(int index, AffineMatrix4x3 value)
            => Parameter<ShaderMat4>(index)?.Value = value.ToMatrix4x4();
    }
}
