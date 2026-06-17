using System.Collections.ObjectModel;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed class RenderPipelineResourceLayoutBuilder(RenderPipelineResourceProfile profile)
{
    private readonly List<RenderPipelineResourceSpec> _specs = [];

    public RenderPipelineResourceLayoutBuilder()
        : this(RenderPipelineResourceProfile.Empty)
    {
    }

    public RenderPipelineResourceProfile Profile { get; } = profile;

    public TextureSpecBuilder Texture(string name)
        => new(this, name);

    public TextureViewSpecBuilder TextureView(string name, string sourceTextureName)
        => new(this, name, sourceTextureName);

    public RenderBufferSpecBuilder RenderBuffer(string name)
        => new(this, name);

    public BufferSpecBuilder Buffer(string name)
        => new(this, name);

    public FrameBufferSpecBuilder FrameBuffer(string name)
        => new(this, name);

    public RenderPipelineResourceLayoutBuilder Add(RenderPipelineResourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _specs.Add(spec);
        return this;
    }

    public RenderPipelineResourceLayout Build(RenderPipelineResourceProfile profile)
    {
        List<string> diagnostics = [];
        List<RenderPipelineResourceSpec> enabled = [];
        Dictionary<string, RenderPipelineResourceSpec> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (RenderPipelineResourceSpec spec in _specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Name))
            {
                diagnostics.Add("Resource name must not be empty.");
                continue;
            }

            if (!spec.IsEnabled(profile))
                continue;

            if (!byName.TryAdd(spec.Name, spec))
            {
                diagnostics.Add($"Duplicate resource '{spec.Name}'.");
                continue;
            }

            enabled.Add(spec);
        }

        foreach (RenderPipelineResourceSpec spec in enabled)
            ValidateSpec(spec, byName, diagnostics);

        if (diagnostics.Count != 0)
            throw new InvalidOperationException("Render pipeline resource layout is invalid: " + string.Join(" ", diagnostics));

        List<RenderPipelineResourceSpec> ordered = TopologicallySort(enabled, byName);
        return new RenderPipelineResourceLayout(
            profile,
            ordered.AsReadOnly(),
            new ReadOnlyDictionary<string, RenderPipelineResourceSpec>(byName));
    }

    private static void ValidateSpec(
        RenderPipelineResourceSpec spec,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName,
        List<string> diagnostics)
    {
        if (spec.SizePolicy.SizeClass == RenderResourceSizeClass.AbsolutePixels
            && (spec.SizePolicy.Width == 0 || spec.SizePolicy.Height == 0)
            && spec.Kind is not RenderPipelineResourceKind.Buffer and not RenderPipelineResourceKind.External)
        {
            diagnostics.Add($"Resource '{spec.Name}' has an absolute size policy with zero width or height.");
        }

        foreach (string dependency in spec.Dependencies)
        {
            if (!byName.ContainsKey(dependency))
                diagnostics.Add($"Resource '{spec.Name}' depends on missing resource '{dependency}'.");
        }

        if (spec is TextureViewSpec viewSpec && !byName.ContainsKey(viewSpec.SourceTextureName))
            diagnostics.Add($"Texture view '{viewSpec.Name}' references missing source texture '{viewSpec.SourceTextureName}'.");

        if (spec is FrameBufferSpec frameBufferSpec)
        {
            if (frameBufferSpec.Attachments.Count == 0)
                diagnostics.Add($"Framebuffer '{frameBufferSpec.Name}' has no attachments.");

            foreach (FrameBufferAttachmentDescriptor attachment in frameBufferSpec.Attachments)
            {
                if (!byName.ContainsKey(attachment.ResourceName))
                {
                    diagnostics.Add($"Framebuffer '{frameBufferSpec.Name}' references missing attachment resource '{attachment.ResourceName}'.");
                    continue;
                }
            }
        }
    }

    private static List<RenderPipelineResourceSpec> TopologicallySort(
        IReadOnlyList<RenderPipelineResourceSpec> specs,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName)
    {
        Dictionary<string, int> visitState = new(StringComparer.OrdinalIgnoreCase);
        List<RenderPipelineResourceSpec> ordered = new(specs.Count);

        foreach (RenderPipelineResourceSpec spec in specs)
            Visit(spec, byName, visitState, ordered);

        return ordered;
    }

    private static void Visit(
        RenderPipelineResourceSpec spec,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName,
        Dictionary<string, int> visitState,
        List<RenderPipelineResourceSpec> ordered)
    {
        if (visitState.TryGetValue(spec.Name, out int state))
        {
            if (state == 1)
                throw new InvalidOperationException($"Render pipeline resource layout has a dependency cycle at '{spec.Name}'.");
            return;
        }

        visitState[spec.Name] = 1;
        foreach (string dependency in spec.Dependencies)
            if (byName.TryGetValue(dependency, out RenderPipelineResourceSpec? dependencySpec))
                Visit(dependencySpec, byName, visitState, ordered);

        if (spec is TextureViewSpec viewSpec
            && byName.TryGetValue(viewSpec.SourceTextureName, out RenderPipelineResourceSpec? sourceSpec))
        {
            Visit(sourceSpec, byName, visitState, ordered);
        }

        if (spec is FrameBufferSpec frameBufferSpec)
        {
            foreach (FrameBufferAttachmentDescriptor attachment in frameBufferSpec.Attachments)
                if (byName.TryGetValue(attachment.ResourceName, out RenderPipelineResourceSpec? attachmentSpec))
                    Visit(attachmentSpec, byName, visitState, ordered);
        }

        visitState[spec.Name] = 2;
        ordered.Add(spec);
    }

    public abstract class SpecBuilder<TBuilder>(RenderPipelineResourceLayoutBuilder owner, string name)
        where TBuilder : SpecBuilder<TBuilder>
    {
        private readonly List<string> _dependencies = [];
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;
        private RenderResourceSizePolicy _sizePolicy = RenderResourceSizePolicy.Internal();
        private RenderPipelineResourceUsage _usage = RenderPipelineResourceUsage.None;
        private RenderPipelineResourcePredicate? _predicate;
        private RenderResourceHistoryPolicy _historyPolicy = RenderResourceHistoryPolicy.None;
        private string? _debugLabel;
        private bool _required = true;

        protected RenderPipelineResourceLayoutBuilder Owner { get; } = owner;
        protected string Name { get; } = name;
        protected RenderResourceLifetime LifetimeValue => _lifetime;
        protected RenderResourceSizePolicy SizePolicyValue => _sizePolicy;
        protected RenderPipelineResourceUsage UsageValue => _usage;
        protected IReadOnlyList<string> DependenciesValue => _dependencies.ToArray();
        protected RenderPipelineResourcePredicate? PredicateValue => _predicate;
        protected RenderResourceHistoryPolicy HistoryPolicyValue => _historyPolicy;
        protected string? DebugLabelValue => _debugLabel;
        protected bool RequiredValue => _required;

        public TBuilder Lifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return (TBuilder)this;
        }

        public TBuilder Size(RenderResourceSizePolicy sizePolicy)
        {
            _sizePolicy = sizePolicy;
            return (TBuilder)this;
        }

        public TBuilder Usage(RenderPipelineResourceUsage usage)
        {
            _usage = usage;
            return (TBuilder)this;
        }

        public TBuilder DependsOn(params string[] dependencies)
        {
            if (dependencies is null)
                return (TBuilder)this;

            for (int i = 0; i < dependencies.Length; i++)
                if (!string.IsNullOrWhiteSpace(dependencies[i]))
                    _dependencies.Add(dependencies[i]);

            return (TBuilder)this;
        }

        public TBuilder When(RenderPipelineResourcePredicate predicate)
        {
            _predicate = predicate;
            return (TBuilder)this;
        }

        public TBuilder History(RenderResourceHistoryPolicy historyPolicy)
        {
            _historyPolicy = historyPolicy;
            return (TBuilder)this;
        }

        public TBuilder DebugLabel(string debugLabel)
        {
            _debugLabel = debugLabel;
            return (TBuilder)this;
        }

        public TBuilder Optional()
        {
            _required = false;
            return (TBuilder)this;
        }
    }

    public sealed class TextureSpecBuilder : SpecBuilder<TextureSpecBuilder>
    {
        private EPixelInternalFormat? _internalFormat;
        private EPixelFormat? _pixelFormat;
        private EPixelType? _pixelType;
        private ESizedInternalFormat? _sizedInternalFormat;
        private uint _samples = 1u;
        private uint _layers = 1u;
        private RenderResourceMipPolicy _mipPolicy = new();
        private bool _stereoCompatible;
        private bool _requiresStorageUsage;
        private Func<XRTexture>? _factory;

        internal TextureSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        public TextureSpecBuilder Format(EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType)
        {
            _internalFormat = internalFormat;
            _pixelFormat = pixelFormat;
            _pixelType = pixelType;
            return this;
        }

        public TextureSpecBuilder SizedFormat(ESizedInternalFormat sizedInternalFormat)
        {
            _sizedInternalFormat = sizedInternalFormat;
            return this;
        }

        public TextureSpecBuilder Samples(uint samples)
        {
            _samples = Math.Max(1u, samples);
            return this;
        }

        public TextureSpecBuilder Layers(uint layers)
        {
            _layers = Math.Max(1u, layers);
            return this;
        }

        public TextureSpecBuilder Mips(RenderResourceMipPolicy mipPolicy)
        {
            _mipPolicy = mipPolicy;
            return this;
        }

        public TextureSpecBuilder StereoCompatible(bool stereoCompatible = true)
        {
            _stereoCompatible = stereoCompatible;
            return this;
        }

        public TextureSpecBuilder RequiresStorageUsage(bool requiresStorageUsage = true)
        {
            _requiresStorageUsage = requiresStorageUsage;
            return this;
        }

        public TextureSpecBuilder Factory(Func<XRTexture> factory)
        {
            _factory = factory;
            return this;
        }

        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new TextureSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _internalFormat,
                _pixelFormat,
                _pixelType,
                _sizedInternalFormat,
                _samples,
                _layers,
                _mipPolicy,
                _stereoCompatible,
                _requiresStorageUsage,
                _factory));
    }

    public sealed class TextureViewSpecBuilder : SpecBuilder<TextureViewSpecBuilder>
    {
        private readonly string _sourceTextureName;
        private uint _baseMipLevel;
        private uint _mipLevelCount = 1u;
        private uint _baseLayer;
        private uint _layerCount = 1u;
        private ESizedInternalFormat? _sizedInternalFormat;
        private EDepthStencilFmt _depthStencilAspect = EDepthStencilFmt.None;
        private bool _arrayTarget;
        private bool _multisample;
        private Func<XRTexture>? _factory;

        internal TextureViewSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name, string sourceTextureName)
            : base(owner, name)
        {
            _sourceTextureName = sourceTextureName;
            DependsOn(sourceTextureName);
        }

        public TextureViewSpecBuilder MipRange(uint baseMipLevel, uint mipLevelCount)
        {
            _baseMipLevel = baseMipLevel;
            _mipLevelCount = Math.Max(1u, mipLevelCount);
            return this;
        }

        public TextureViewSpecBuilder LayerRange(uint baseLayer, uint layerCount)
        {
            _baseLayer = baseLayer;
            _layerCount = Math.Max(1u, layerCount);
            return this;
        }

        public TextureViewSpecBuilder SizedFormat(ESizedInternalFormat sizedInternalFormat)
        {
            _sizedInternalFormat = sizedInternalFormat;
            return this;
        }

        public TextureViewSpecBuilder DepthStencilAspect(EDepthStencilFmt aspect)
        {
            _depthStencilAspect = aspect;
            return this;
        }

        public TextureViewSpecBuilder Target(bool array, bool multisample)
        {
            _arrayTarget = array;
            _multisample = multisample;
            return this;
        }

        public TextureViewSpecBuilder Factory(Func<XRTexture> factory)
        {
            _factory = factory;
            return this;
        }

        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new TextureViewSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _sourceTextureName,
                _baseMipLevel,
                _mipLevelCount,
                _baseLayer,
                _layerCount,
                _sizedInternalFormat,
                _depthStencilAspect,
                _arrayTarget,
                _multisample,
                _factory));
    }

    public sealed class RenderBufferSpecBuilder : SpecBuilder<RenderBufferSpecBuilder>
    {
        private ERenderBufferStorage _storageFormat = ERenderBufferStorage.Rgba8;
        private uint _samples = 1u;
        private EFrameBufferAttachment? _defaultAttachment;
        private Func<XRRenderBuffer>? _factory;

        internal RenderBufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        public RenderBufferSpecBuilder Storage(ERenderBufferStorage storageFormat)
        {
            _storageFormat = storageFormat;
            return this;
        }

        public RenderBufferSpecBuilder Samples(uint samples)
        {
            _samples = Math.Max(1u, samples);
            return this;
        }

        public RenderBufferSpecBuilder DefaultAttachment(EFrameBufferAttachment attachment)
        {
            _defaultAttachment = attachment;
            return this;
        }

        public RenderBufferSpecBuilder Factory(Func<XRRenderBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new RenderBufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _storageFormat,
                _samples,
                _defaultAttachment,
                _factory));
    }

    public sealed class BufferSpecBuilder : SpecBuilder<BufferSpecBuilder>
    {
        private ulong _sizeInBytes = 1UL;
        private EBufferTarget _target = EBufferTarget.ArrayBuffer;
        private EBufferUsage _usage = EBufferUsage.DynamicDraw;
        private uint _elementStride;
        private uint _elementCount;
        private EBufferAccessPattern _accessPattern = EBufferAccessPattern.ReadWrite;
        private Func<XRDataBuffer>? _factory;

        internal BufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        public BufferSpecBuilder BufferFormat(ulong sizeInBytes, EBufferTarget target, EBufferUsage usage)
        {
            _sizeInBytes = Math.Max(1UL, sizeInBytes);
            _target = target;
            _usage = usage;
            return this;
        }

        public BufferSpecBuilder Elements(uint elementStride, uint elementCount)
        {
            _elementStride = elementStride;
            _elementCount = elementCount;
            return this;
        }

        public BufferSpecBuilder Access(EBufferAccessPattern accessPattern)
        {
            _accessPattern = accessPattern;
            return this;
        }

        public BufferSpecBuilder Factory(Func<XRDataBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new BufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _sizeInBytes,
                _target,
                _usage,
                _elementStride,
                _elementCount,
                _accessPattern,
                _factory));
    }

    public sealed class FrameBufferSpecBuilder : SpecBuilder<FrameBufferSpecBuilder>
    {
        private readonly List<FrameBufferAttachmentDescriptor> _attachments = [];
        private Func<XRFrameBuffer>? _factory;

        internal FrameBufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        public FrameBufferSpecBuilder Color(int index, string resourceName, int mipLevel = 0, int layerIndex = -1)
        {
            EFrameBufferAttachment attachment = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + index);
            return Attachment(resourceName, attachment, mipLevel, layerIndex);
        }

        public FrameBufferSpecBuilder DepthStencil(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.DepthStencilAttachment, mipLevel, layerIndex);

        public FrameBufferSpecBuilder Depth(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.DepthAttachment, mipLevel, layerIndex);

        public FrameBufferSpecBuilder Stencil(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.StencilAttachment, mipLevel, layerIndex);

        public FrameBufferSpecBuilder Attachment(string resourceName, EFrameBufferAttachment attachment, int mipLevel = 0, int layerIndex = -1)
        {
            _attachments.Add(new FrameBufferAttachmentDescriptor(resourceName, attachment, mipLevel, layerIndex));
            DependsOn(resourceName);
            return this;
        }

        public FrameBufferSpecBuilder Factory(Func<XRFrameBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new FrameBufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _attachments.ToArray(),
                _factory));
    }
}
