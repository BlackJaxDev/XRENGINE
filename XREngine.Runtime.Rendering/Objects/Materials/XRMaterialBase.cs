using MemoryPack;
using XREngine.Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Data.Core;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using YamlDotNet.Serialization;
using XREngine.Data.Transforms;

namespace XREngine.Rendering
{
    public abstract class XRMaterialBase : GenericRenderObject
    {
        private bool _materialTeardownRequested;
        protected void BeginMaterialTeardown() => _materialTeardownRequested = true;
        protected bool IsMaterialTeardownRequested => _materialTeardownRequested;

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
        public virtual void OnSettingUniforms(XRRenderProgram program)
            => SettingUniforms?.Invoke(this, program);

        /// <summary>
        /// Uploads material-owned deformation inputs to the active vertex
        /// program, including generated auxiliary-pass programs.
        /// </summary>
        public event Action<XRRenderProgram>? SettingVertexUniforms;

        public void OnSettingVertexUniforms(XRRenderProgram program)
            => SettingVertexUniforms?.Invoke(program);
        public bool HasSettingVertexUniformHandlers => SettingVertexUniforms is not null;
        public bool HasSettingUniformsHandlers
            => SettingUniforms is not null;

        /// <summary>Checks one engine-owned publisher without allocating a delegate invocation list.</summary>
        protected bool HasOnlySettingUniformsHandler(Action<XRMaterialBase, XRRenderProgram>? handler)
            => SettingUniforms == handler;

        /// <summary>Checks a cold cook's explicitly recognized publishers without admitting arbitrary callbacks.</summary>
        protected bool HasOnlySettingUniformsHandlers(IReadOnlySet<Action<XRMaterialBase, XRRenderProgram>> handlers,
            Action<XRMaterialBase, XRRenderProgram>? additionalHandler = null)
        {
            if (SettingUniforms is null)
                return true;
            foreach (Delegate handler in SettingUniforms.GetInvocationList())
                if (handler is not Action<XRMaterialBase, XRRenderProgram> typed ||
                    typed != additionalHandler && !handlers.Contains(typed))
                    return false;
            return true;
        }

        /// <summary>
        /// Typed, generation-owned numeric binding publishers eligible for
        /// immutable backend capture and frequency-scoped reuse.
        /// </summary>
        [Browsable(false), RuntimeOnly, YamlIgnore, MemoryPackIgnore]
        [field: RuntimeOnly, YamlIgnore, MemoryPackIgnore]
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
        {
            _ownedRenderOptions = _renderOptions;
            _ownedInitialTextures = _textures;
            AttachTextureListHandlers(_textures);
            _subscribedTextures = _textures;
            ReconcileLegacyTextureBindings();
        }
        protected XRMaterialBase(ShaderVar[] parameters) : this()
        {
            Parameters = [.. parameters]; //Make copy
        }
        protected XRMaterialBase(XRTexture?[] textures) : this()
        {
            _ownedConstructorTextures = [.. textures];
            Textures = _ownedConstructorTextures;
        }
        protected XRMaterialBase(ShaderVar[] parameters, XRTexture?[] textures) : this()
        {
            Parameters = [.. parameters]; //Make copy
            _ownedConstructorTextures = [.. textures];
            Textures = _ownedConstructorTextures;
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
        private readonly RenderingParameters _ownedRenderOptions;
        /// <summary>
        /// These are special rendering options that the API can use to set its state separately from the shaders.
        /// </summary>
        public RenderingParameters RenderOptions
        {
            get => _renderOptions ??= new();
            set
            {
                ObjectDisposedException.ThrowIf(_materialTeardownRequested, this);
                SetField(ref _renderOptions, value ?? new());
            }
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
        private readonly EventList<XRTexture?> _ownedInitialTextures;
        private EventList<XRTexture?>? _ownedConstructorTextures;
        private EventList<XRTexture?>? _subscribedTextures;
        private int _textureSetterDepth;
        private LegacyTextureBinding[] _legacyTextureBindings = [];
        private LegacyTextureBinding[] _legacyTextureBindingScratch = [];
        private string[] _legacyIndexedSamplerNames = [];
        private int _legacyTextureBindingCount;
        private bool _reconcilingLegacyTextures;
        private bool _legacyTextureReconcilePending;
        private bool _unknownLegacyTextureLayoutDirty;
        private bool _legacyTextureHandlersAttached;

        private readonly record struct LegacyTextureBinding(
            XRTexture? Texture,
            XRTexture? ViewedTexture,
            string SamplerName,
            ETextureTarget Target,
            Type? UnknownTextureType);
        /// <summary>
        /// These are the texture samplers that each shader in the program has requested.
        /// </summary>
        public EventList<XRTexture?> Textures
        {
            get => _textures;
            set
            {
                ObjectDisposedException.ThrowIf(_materialTeardownRequested, this);
                _textureSetterDepth++;
                try { SetField(ref _textures, value ?? []); }
                finally
                {
                    if (--_textureSetterDepth == 0)
                    {
                        ReconcileTextureListHandlers();
                        ReconcileLegacyTextureBindings();
                    }
                }
            }
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

        private void ReconcileTextureListHandlers()
        {
            if (_materialTeardownRequested)
            {
                DetachTextureListHandlers(_subscribedTextures);
                _subscribedTextures = null;
                return;
            }
            if (ReferenceEquals(_subscribedTextures, _textures))
                return;
            DetachTextureListHandlers(_subscribedTextures);
            _subscribedTextures = _textures;
            AttachTextureListHandlers(_subscribedTextures);
        }

        private void TexturesModified()
        {
            if (_textureSetterDepth == 0)
                ReconcileLegacyTextureBindings();
        }

        private void LegacyTexturePropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
        {
            if (sender is XRTexture texture && IsUnknownLegacyTexture(texture))
            {
                _unknownLegacyTextureLayoutDirty = true;
                ReconcileLegacyTextureBindings();
                return;
            }
            if (args.PropertyName is nameof(XRTexture.SamplerName) or
                nameof(XRTexture2D.MultiSampleCount) or nameof(XRTexture2DArray.MultiSample) or
                nameof(XRTexture2D.Rectangle) or nameof(XRTextureViewBase.TextureTarget) or
                nameof(XRTextureViewBase.NumLayers) or "Array" or "View2D" or
                "View1D" or "As2D" or "Multisample")
                ReconcileLegacyTextureBindings();
        }

        private void LegacyViewedTextureChanged()
            => ReconcileLegacyTextureBindings();

        private bool IsUnknownLegacyTexture(XRTexture texture)
        {
            for (int index = 0; index < _legacyTextureBindingCount; index++)
                if (ReferenceEquals(_legacyTextureBindings[index].Texture, texture) &&
                    _legacyTextureBindings[index].UnknownTextureType is not null)
                    return true;
            return false;
        }

        private void ReconcileLegacyTextureBindings()
        {
            if (_materialTeardownRequested)
                return;
            if (_textureSetterDepth != 0)
            {
                _legacyTextureReconcilePending = true;
                return;
            }
            if (_reconcilingLegacyTextures)
            {
                _legacyTextureReconcilePending = true;
                return;
            }

            _reconcilingLegacyTextures = true;
            try
            {
                do
                {
                    if (_materialTeardownRequested)
                        break;
                    _legacyTextureReconcilePending = false;
                    EventList<XRTexture?> textures = _textures;
                    int count = textures.Count;
                    bool layoutChanged = count != _legacyTextureBindingCount ||
                        _unknownLegacyTextureLayoutDirty;
                    _unknownLegacyTextureLayoutDirty = false;
                    bool resourceChanged = layoutChanged;
                    if (_legacyTextureBindingScratch.Length < count)
                        Array.Resize(ref _legacyTextureBindingScratch, count);
                    LegacyTextureBinding[] next = _legacyTextureBindingScratch;
                    for (int index = 0; index < count; index++)
                    {
                        XRTexture? texture = textures[index];
                        ETextureTarget target = GetLegacyTextureTarget(texture);
                        LegacyTextureBinding binding = new(
                            texture,
                            (texture as XRTextureViewBase)?.GetViewedTexture(),
                            ResolveLegacySamplerName(texture, index),
                            target,
                            target == 0 ? texture?.GetType() : null);
                        next[index] = binding;
                        if (index >= _legacyTextureBindingCount)
                            continue;

                        LegacyTextureBinding previous = _legacyTextureBindings[index];
                        if (previous.SamplerName != binding.SamplerName ||
                            previous.Target != binding.Target ||
                            previous.UnknownTextureType != binding.UnknownTextureType ||
                            (previous.Texture is null) != (texture is null))
                            layoutChanged = true;
                        if (!ReferenceEquals(previous.Texture, texture) ||
                            !ReferenceEquals(previous.ViewedTexture, binding.ViewedTexture))
                        {
                            resourceChanged = true;
                            // An unknown subtype can change its target without a known target property.
                            if (previous.UnknownTextureType is not null || binding.UnknownTextureType is not null)
                                layoutChanged = true;
                        }
                    }

                    bool bindingsChanged = layoutChanged || resourceChanged;
                    if (bindingsChanged && _legacyTextureHandlersAttached)
                    {
                        // Keep existing subscriptions when a slot changes only its metadata.
                        for (int index = 0; index < _legacyTextureBindingCount; index++)
                        {
                            XRTexture? texture = _legacyTextureBindings[index].Texture;
                            if (texture is null || WasTextureSeenEarlier(_legacyTextureBindings, index, texture) ||
                                ContainsTexture(next, count, texture))
                                continue;
                            DetachLegacyTextureHandlers(texture);
                        }
                    }
                    if (bindingsChanged)
                    {
                        // A shorter list must not keep removed textures in spare slots.
                        Array.Clear(next, count, next.Length - count);
                        LegacyTextureBinding[] previousBindings = _legacyTextureBindings;
                        int previousCount = _legacyTextureBindingCount;
                        _legacyTextureBindings = next;
                        _legacyTextureBindingScratch = previousBindings;
                        _legacyTextureBindingCount = count;
                        for (int index = 0; index < count; index++)
                        {
                            XRTexture? texture = next[index].Texture;
                            if (texture is null || WasTextureSeenEarlier(next, index, texture) ||
                                _legacyTextureHandlersAttached && ContainsTexture(previousBindings, previousCount, texture))
                                continue;
                            AttachLegacyTextureHandlers(texture);
                        }
                        Array.Clear(previousBindings, 0, previousBindings.Length);
                    }
                    else
                    {
                        if (!_legacyTextureHandlersAttached)
                            for (int index = 0; index < count; index++)
                            {
                                XRTexture? texture = _legacyTextureBindings[index].Texture;
                                if (texture is not null && !WasTextureSeenEarlier(_legacyTextureBindings, index, texture))
                                    AttachLegacyTextureHandlers(texture);
                            }
                        Array.Clear(next, 0, count);
                    }
                    _legacyTextureHandlersAttached = true;

                    if (layoutChanged)
                        IncrementBindingLayoutVersion();
                    if (_materialTeardownRequested)
                        break;
                    if (layoutChanged || resourceChanged)
                    {
                        IncrementBindingValueVersion();
                        if (_materialTeardownRequested)
                            break;
                        IncrementBindingResourceVersion();
                    }
                    if (_materialTeardownRequested)
                        break;
                    if (_textureSetterDepth == 0 && !ReferenceEquals(_subscribedTextures, _textures))
                        ReconcileTextureListHandlers();
                }
                while (_legacyTextureReconcilePending && _textureSetterDepth == 0);
            }
            finally
            {
                _reconcilingLegacyTextures = false;
            }
        }

        private static bool WasTextureSeenEarlier(LegacyTextureBinding[] bindings, int index, XRTexture texture)
        {
            for (int earlier = 0; earlier < index; earlier++)
                if (ReferenceEquals(bindings[earlier].Texture, texture))
                    return true;
            return false;
        }

        private static bool ContainsTexture(LegacyTextureBinding[] bindings, int count, XRTexture texture)
        {
            for (int index = 0; index < count; index++)
                if (ReferenceEquals(bindings[index].Texture, texture))
                    return true;
            return false;
        }

        private string ResolveLegacySamplerName(XRTexture? texture, int index)
        {
            if (texture?.SamplerName is { } name)
                return name;
            if (index < 32)
                return XRTexture.GetIndexedSamplerName(index);
            if (_legacyIndexedSamplerNames.Length <= index)
                Array.Resize(ref _legacyIndexedSamplerNames, index + 1);
            return _legacyIndexedSamplerNames[index] ??= XRTexture.GetIndexedSamplerName(index);
        }

        private void AttachLegacyTextureHandlers(XRTexture texture)
        {
            texture.PropertyChanged += LegacyTexturePropertyChanged;
            if (texture is XRTextureViewBase view)
                view.ViewedTextureChanged += LegacyViewedTextureChanged;
        }

        private void DetachLegacyTextureHandlers(XRTexture texture)
        {
            texture.PropertyChanged -= LegacyTexturePropertyChanged;
            if (texture is XRTextureViewBase view)
                view.ViewedTextureChanged -= LegacyViewedTextureChanged;
        }

        private static ETextureTarget GetLegacyTextureTarget(XRTexture? texture)
            => texture switch
            {
                XRTexture1DView or XRTexture1DArrayView or XRTexture2DView or
                    XRTexture2DArrayView or XRTexture3DView or XRTextureCubeView or
                    XRTextureCubeArrayView or XRTextureViewCubeArray or
                    XRTextureViewRectangle or XRTextureBufferView
                    => ((XRTextureViewBase)texture).TextureTarget,
                XRTexture2D image when image.Rectangle => ETextureTarget.TextureRectangle,
                XRTexture2D image when image.MultiSample => ETextureTarget.Texture2DMultisample,
                XRTexture2D => ETextureTarget.Texture2D,
                XRTexture2DArray array when array.MultiSample => ETextureTarget.Texture2DMultisampleArray,
                XRTexture2DArray => ETextureTarget.Texture2DArray,
                XRTexture1DArray => ETextureTarget.Texture1DArray,
                XRTexture1D => ETextureTarget.Texture1D,
                XRTexture3D => ETextureTarget.Texture3D,
                XRTextureCubeArray => ETextureTarget.TextureCubeMapArray,
                XRTextureCube => ETextureTarget.TextureCubeMap,
                XRTextureBuffer => ETextureTarget.TextureBuffer,
                _ => 0,
            };

        protected void IncrementBindingLayoutVersion()
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

        /// <summary>Restores value subscriptions after a cooked reflection load suppressed property callbacks.</summary>
        protected void RestoreCookedParameterSubscriptions()
        {
            DetachParameterHandlers(_parameters);
            AttachParameterHandlers(_parameters);
            ResetNameIndexCache();
            IncrementBindingLayoutVersion();
            IncrementBindingValueVersion();
        }

        private void ParameterValueChanged(ShaderVar _)
            => IncrementBindingValueVersion();

        protected void IncrementBindingValueVersion()
        {
            unchecked
            {
                ulong next = _bindingValueVersion + 1;
                SetField(ref _bindingValueVersion, next == 0 ? 1 : next, nameof(BindingValueVersion));
            }
        }

        protected void IncrementBindingResourceVersion()
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
                    if (_textureSetterDepth == 0)
                        ReconcileLegacyTextureBindings();
                    break;
            }
        }

        /// <summary>Restores owned storage and subscriptions after a successful parent revival.</summary>
        public override void Generate()
        {
            bool reviving = IsDestroyed;
            base.Generate();
            if (!reviving || IsDestroyed)
                return;

            if (_ownedConstructorTextures is { IsDestroyed: true })
                _ownedConstructorTextures.Generate();
            if (_ownedInitialTextures.IsDestroyed)
                _ownedInitialTextures.Generate();
            if (_ownedRenderOptions.IsDestroyed)
                _ownedRenderOptions.Generate();
            _materialTeardownRequested = false;
            ReconcileTextureListHandlers();
            ReconcileLegacyTextureBindings();
            DetachParameterHandlers(_parameters);
            AttachParameterHandlers(_parameters);
        }

        protected override void OnDestroying()
        {
            BeginMaterialTeardown();
            List<Exception>? failures = null;
            try { DetachParameterHandlers(_parameters); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            try { ReconcileTextureListHandlers(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            for (int index = 0; index < _legacyTextureBindingCount; index++)
            {
                XRTexture? texture = _legacyTextureBindings[index].Texture;
                if (texture is null || WasTextureSeenEarlier(_legacyTextureBindings, index, texture))
                    continue;
                try { DetachLegacyTextureHandlers(texture); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
            _legacyTextureHandlersAttached = false;
            try { DestroyOwnedContainer(_ownedConstructorTextures); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            try { DestroyOwnedContainer(_ownedInitialTextures); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            try { DestroyOwnedContainer(_ownedRenderOptions); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            try { base.OnDestroying(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            if (failures is not null)
                throw new AggregateException("Material-owned storage could not be released.", failures);
        }

        private static void DestroyOwnedContainer(XRObjectBase? value)
        {
            if (value is null || value.IsDestroyed)
                return;
            value.Destroy(true);
            if (!value.IsDestroyed)
                throw new InvalidOperationException("Material-owned container destruction was vetoed.");
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
