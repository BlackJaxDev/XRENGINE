using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram(
    VulkanBackendObjectContext backendContext,
    XRRenderProgram data) : VkObject<XRRenderProgram>(backendContext, data)
{
    protected override void BindOperationPorts(VulkanWrapperPortBinding binding)
        => binding.AttachPlannerOperationHandlers(this);
    private readonly Dictionary<XRShader, VkShader> _shaderCache = new();
    private readonly Dictionary<EProgramStageMask, VkShader> _stageLookup = new();
    private readonly Lock _linkLock = new();
    private DescriptorSetLayout[] _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
    private DescriptorSetLayout[] _descriptorSetLayoutsBeforeGlobalMaterial = Array.Empty<DescriptorSetLayout>();
    private bool _hasGlobalTextureArrayOnlySet;
    private bool _canBindGlobalTextureArraySeparately;
    private ulong _descriptorLayoutFingerprint;
    private ulong _descriptorSchemaFingerprint;
    private PipelineLayout _pipelineLayout;
    private readonly List<DescriptorBindingInfo> _programDescriptorBindings = new();
    private readonly Dictionary<string, AutoUniformBlockInfo> _autoUniformBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<(uint Set, uint Binding), AutoUniformBlockInfo> _autoUniformBlocksByBinding = [];
    private VulkanProgramBindingSchema? _bindingSchema;
    private readonly object _bindingLock = new();
    private readonly Dictionary<string, ProgramUniformValue> _uniformValues = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> VertexSuffixedUniformNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> VertexBaseUniformNames = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, XRTexture> _samplersByUnit = new();
    private readonly Dictionary<uint, string> _samplerNamesByUnit = new();
    private readonly Dictionary<string, XRTexture> _samplersByName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, ProgramImageBinding> _imagesByUnit = new();
    private readonly Dictionary<uint, XRDataBuffer> _buffersByBinding = new();
    private ComputeDispatchSnapshot? _appliedBindingSnapshot;
    private readonly List<ComputeDispatchSnapshot> _frameBindingSnapshotPool = [];
    private ulong _frameBindingSnapshotPoolFrame;
    private int _frameBindingSnapshotPoolCursor;
    private readonly Dictionary<MaterialBindingSnapshotCacheKey, ComputeDispatchSnapshot?> _frameMaterialBindingSnapshots = [];
    private ulong _frameMaterialBindingSnapshotCacheFrame;
    private readonly object _persistentProgramBindingArtifactSync = new();
    private readonly Dictionary<
        PersistentProgramBindingArtifactSlotKey,
        (PersistentProgramBindingArtifactGeneration Generation,
         RenderBindingPublisherGenerationSnapshot PublisherGenerations,
         ComputeDispatchSnapshot? Artifact)>
        _persistentProgramBindingArtifacts = [];
    private readonly Dictionary<string, Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan>> _autoUniformMaterialWritePlans =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutoUniformMaterialWritePlan> _frequencyOwnedAutoUniformWritePlans =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _computeWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<ComputeUniformBufferKey, ComputeUniformBuffer> _computeUniformBuffers = new();
    private readonly Dictionary<(uint ImageIndex, ulong SchemaFingerprint, ulong BindingKey), ulong>
        _reusableComputeDescriptorResourceSignatures = [];
    private Pipeline _computePipeline;
    private DescriptorHeapProgramLayout? _descriptorHeapLayout;
    private bool[] _descriptorSetUsesUpdateAfterBind = Array.Empty<bool>();
    private bool _descriptorSetsRequireUpdateAfterBind;
    private bool _descriptorSetsRequireVariableDescriptorCount;
    private long _linkGeneration;
    private int _linkedShaderConfigVersion = -1;
    private bool _linkedUsesVulkanClipDepthRemap;
    private EShaderType? _linkedVulkanClipDepthRemapStage;
    private ulong _linkedTransformFeedbackLayoutVersion = ulong.MaxValue;

    public override VkObjectType Type => VkObjectType.Program;
    public override bool IsGenerated => IsActive;
    private bool _isLinked;
    public bool IsLinked
    {
        get => Volatile.Read(ref _isLinked);
        private set
        {
            if (Volatile.Read(ref _isLinked) == value)
                return;
            Volatile.Write(ref _isLinked, value);
            Data.SetBackendLinked(value);
        }
    }
    public PipelineLayout PipelineLayout => _pipelineLayout;
    internal ulong LinkGeneration => unchecked((ulong)Volatile.Read(ref _linkGeneration));
    internal DescriptorHeapProgramLayout? DescriptorHeapLayout => _descriptorHeapLayout;
    internal ulong DescriptorLayoutFingerprint => _descriptorLayoutFingerprint;
    internal ulong DescriptorSchemaFingerprint => _descriptorSchemaFingerprint;
    public IReadOnlyList<DescriptorSetLayout> DescriptorSetLayouts => _descriptorSetLayouts;
    public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _programDescriptorBindings;
    public IReadOnlyDictionary<string, AutoUniformBlockInfo> AutoUniformBlocks => _autoUniformBlocks;
    internal VulkanProgramBindingSchema? BindingSchema => _bindingSchema;
    internal VulkanProgramCreationPort MeshTaskProgramServices => ProgramCreationPort;
    internal VulkanBackendObjectContext MeshTaskBackendContext => BackendContext;

    /// <summary>
    /// Exposes the concrete auto-uniform map to Vulkan hot paths so dictionary
    /// enumeration remains allocation-free.
    /// </summary>
    internal Dictionary<string, AutoUniformBlockInfo> AutoUniformBlockMap => _autoUniformBlocks;
    public bool DescriptorSetsRequireUpdateAfterBind => _descriptorSetsRequireUpdateAfterBind;
    public bool DescriptorSetsRequireVariableDescriptorCount => _descriptorSetsRequireVariableDescriptorCount;
    internal bool HasGlobalTextureArrayOnlySet => _hasGlobalTextureArrayOnlySet;
    internal bool CanBindGlobalTextureArraySeparately => _canBindGlobalTextureArraySeparately;
    public bool DescriptorSetUsesUpdateAfterBind(uint setIndex)
        => setIndex < _descriptorSetUsesUpdateAfterBind.Length && _descriptorSetUsesUpdateAfterBind[setIndex];

    protected override uint CreateObjectInternal() => CacheObject(this);

    protected override void DeleteObjectInternal()
    {
        DestroyLayouts();
        RemoveCachedObject(BindingId);
    }

    protected override void LinkData()
    {
        Data.UniformSetVector2Requested += Uniform;
        Data.UniformSetVector3Requested += Uniform;
        Data.UniformSetVector4Requested += Uniform;
        Data.UniformSetQuaternionRequested += Uniform;
        Data.UniformSetIntRequested += Uniform;
        Data.UniformSetFloatRequested += Uniform;
        Data.UniformSetUIntRequested += Uniform;
        Data.UniformSetDoubleRequested += Uniform;
        Data.UniformSetMatrix4x4Requested += Uniform;

        Data.UniformSetVector2ArrayRequested += Uniform;
        Data.UniformSetVector3ArrayRequested += Uniform;
        Data.UniformSetVector4ArrayRequested += Uniform;
        Data.UniformSetQuaternionArrayRequested += Uniform;
        Data.UniformSetIntArrayRequested += Uniform;
        Data.UniformSetFloatArrayRequested += Uniform;
        Data.UniformSetFloatSpanRequested += Uniform;
        Data.UniformSetUIntArrayRequested += Uniform;
        Data.UniformSetDoubleArrayRequested += Uniform;
        Data.UniformSetMatrix4x4ArrayRequested += Uniform;

        Data.UniformSetIVector2Requested += Uniform;
        Data.UniformSetIVector3Requested += Uniform;
        Data.UniformSetIVector4Requested += Uniform;
        Data.UniformSetIVector2ArrayRequested += Uniform;
        Data.UniformSetIVector3ArrayRequested += Uniform;
        Data.UniformSetIVector4ArrayRequested += Uniform;

        Data.UniformSetUVector2Requested += Uniform;
        Data.UniformSetUVector3Requested += Uniform;
        Data.UniformSetUVector4Requested += Uniform;
        Data.UniformSetUVector2ArrayRequested += Uniform;
        Data.UniformSetUVector3ArrayRequested += Uniform;
        Data.UniformSetUVector4ArrayRequested += Uniform;

        Data.UniformSetBoolRequested += Uniform;
        Data.UniformSetBoolArrayRequested += Uniform;
        Data.UniformSetBoolVector2Requested += Uniform;
        Data.UniformSetBoolVector3Requested += Uniform;
        Data.UniformSetBoolVector4Requested += Uniform;
        Data.UniformSetBoolVector2ArrayRequested += Uniform;
        Data.UniformSetBoolVector3ArrayRequested += Uniform;
        Data.UniformSetBoolVector4ArrayRequested += Uniform;

        Data.UniformSetDVector2Requested += Uniform;
        Data.UniformSetDVector3Requested += Uniform;
        Data.UniformSetDVector4Requested += Uniform;
        Data.UniformSetDVector2ArrayRequested += Uniform;
        Data.UniformSetDVector3ArrayRequested += Uniform;
        Data.UniformSetDVector4ArrayRequested += Uniform;

        Data.SamplerRequested += Sampler;
        Data.SamplerRequestedByLocation += Sampler;
        Data.BindImageTextureRequested += BindImageTexture;
        Data.BindBufferRequested += BindBuffer;

        Data.LinkRequested += OnLinkRequested;
        Data.UseRequested += OnUseRequested;
        Data.TransformFeedbackLayoutChanged += OnTransformFeedbackLayoutChanged;
        Data.Shaders.PostAnythingAdded += ShaderAdded;
        Data.Shaders.PostAnythingRemoved += ShaderRemoved;

        foreach (XRShader shader in Data.Shaders)
            ShaderAdded(shader);
    }

    protected override void UnlinkData()
    {
        Data.UniformSetVector2Requested -= Uniform;
        Data.UniformSetVector3Requested -= Uniform;
        Data.UniformSetVector4Requested -= Uniform;
        Data.UniformSetQuaternionRequested -= Uniform;
        Data.UniformSetIntRequested -= Uniform;
        Data.UniformSetFloatRequested -= Uniform;
        Data.UniformSetUIntRequested -= Uniform;
        Data.UniformSetDoubleRequested -= Uniform;
        Data.UniformSetMatrix4x4Requested -= Uniform;

        Data.UniformSetVector2ArrayRequested -= Uniform;
        Data.UniformSetVector3ArrayRequested -= Uniform;
        Data.UniformSetVector4ArrayRequested -= Uniform;
        Data.UniformSetQuaternionArrayRequested -= Uniform;
        Data.UniformSetIntArrayRequested -= Uniform;
        Data.UniformSetFloatArrayRequested -= Uniform;
        Data.UniformSetFloatSpanRequested -= Uniform;
        Data.UniformSetUIntArrayRequested -= Uniform;
        Data.UniformSetDoubleArrayRequested -= Uniform;
        Data.UniformSetMatrix4x4ArrayRequested -= Uniform;

        Data.UniformSetIVector2Requested -= Uniform;
        Data.UniformSetIVector3Requested -= Uniform;
        Data.UniformSetIVector4Requested -= Uniform;
        Data.UniformSetIVector2ArrayRequested -= Uniform;
        Data.UniformSetIVector3ArrayRequested -= Uniform;
        Data.UniformSetIVector4ArrayRequested -= Uniform;

        Data.UniformSetUVector2Requested -= Uniform;
        Data.UniformSetUVector3Requested -= Uniform;
        Data.UniformSetUVector4Requested -= Uniform;
        Data.UniformSetUVector2ArrayRequested -= Uniform;
        Data.UniformSetUVector3ArrayRequested -= Uniform;
        Data.UniformSetUVector4ArrayRequested -= Uniform;

        Data.UniformSetBoolRequested -= Uniform;
        Data.UniformSetBoolArrayRequested -= Uniform;
        Data.UniformSetBoolVector2Requested -= Uniform;
        Data.UniformSetBoolVector3Requested -= Uniform;
        Data.UniformSetBoolVector4Requested -= Uniform;
        Data.UniformSetBoolVector2ArrayRequested -= Uniform;
        Data.UniformSetBoolVector3ArrayRequested -= Uniform;
        Data.UniformSetBoolVector4ArrayRequested -= Uniform;

        Data.UniformSetDVector2Requested -= Uniform;
        Data.UniformSetDVector3Requested -= Uniform;
        Data.UniformSetDVector4Requested -= Uniform;
        Data.UniformSetDVector2ArrayRequested -= Uniform;
        Data.UniformSetDVector3ArrayRequested -= Uniform;
        Data.UniformSetDVector4ArrayRequested -= Uniform;

        Data.SamplerRequested -= Sampler;
        Data.SamplerRequestedByLocation -= Sampler;
        Data.BindImageTextureRequested -= BindImageTexture;
        Data.BindBufferRequested -= BindBuffer;

        Data.LinkRequested -= OnLinkRequested;
        Data.UseRequested -= OnUseRequested;
        Data.TransformFeedbackLayoutChanged -= OnTransformFeedbackLayoutChanged;
        Data.Shaders.PostAnythingAdded -= ShaderAdded;
        Data.Shaders.PostAnythingRemoved -= ShaderRemoved;

        foreach (XRShader shader in Data.Shaders)
            ShaderRemoved(shader);

        ClearBindings();
        DestroyLayouts();
    }

    private void ShaderAdded(XRShader shader)
    {
        if (_shaderCache.ContainsKey(shader))
            return;

        if (ProgramCreationPort.GetOrCreateShader(shader) is not { } vkShader)
            return;

        _shaderCache.Add(shader, vkShader);
        vkShader.ShaderInvalidated += OnShaderInvalidated;
        IsLinked = false;
    }

    private void ShaderRemoved(XRShader shader)
    {
        if (_shaderCache.Remove(shader, out VkShader? vkShader) && vkShader is not null)
        {
            vkShader.ShaderInvalidated -= OnShaderInvalidated;
            vkShader.Destroy();
        }

        IsLinked = false;
    }

    private void OnShaderInvalidated(VkShader shader)
    {
        if (RuntimeRenderingHostServices.HasConcreteHost &&
            !RuntimeRenderingHostServices.Scheduling.IsFrameSwapThread)
        {
            RuntimeRenderingHostServices.Scheduling.EnqueueFrameSwapTask(
                () => OnShaderInvalidated(shader),
                "VkRenderProgram.ShaderInvalidated");
            return;
        }

        DestroyLayouts();
        _stageLookup.Clear();
        _autoUniformBlocks.Clear();
        Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
            XRRenderProgram.EShaderProgramBackendStage.SourceQueued,
            0.0,
            0.0,
            null,
            Backend: "Vulkan",
            Detail: $"shader invalidated: {shader.StageDebugLabel}",
            Fingerprint: shader.CompileStatus.ArtifactIdentity));
    }

    private void OnTransformFeedbackLayoutChanged(XRRenderProgram program)
    {
        if (RuntimeEngine.InvokeOnMainThread(() => OnTransformFeedbackLayoutChanged(program), "VkRenderProgram.TransformFeedbackLayoutChanged"))
            return;

        DestroyLayouts();
        _stageLookup.Clear();
        _autoUniformBlocks.Clear();
        IsLinked = false;
        _linkedTransformFeedbackLayoutVersion = ulong.MaxValue;

        if (Data.LinkReady && BackendContext.IsLogicalDeviceReady)
            Link();
    }

    private void OnLinkRequested(XRRenderProgram program)
    {
        if (RuntimeEngine.InvokeOnMainThread(() => OnLinkRequested(program), "VkRenderProgram.LinkRequested"))
            return;

        if (!BackendContext.IsLogicalDeviceReady)
        {
            BackendContext.Resources.PipelineManager.QueueProgramLinkUntilDeviceReady(this);
            return;
        }

        if (!Link(ShouldUseAsyncShaderCompileForLinkRequest()) &&
            Data.ShaderMetadata.Backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed)
        {
            Debug.VulkanWarning($"Failed to link Vulkan program '{Data.Name ?? "UnnamedProgram"}'.");
        }
    }

    private bool ShouldUseAsyncShaderCompileForLinkRequest()
    {
        if (Data.AllowAsyncBackendCompile)
            return true;

        foreach (VkShader shader in _shaderCache.Values)
            if (shader.Data.Type == EShaderType.Compute)
                return false;

        return true;
    }

    private void OnUseRequested(XRRenderProgram program)
    {
        if (RuntimeEngine.InvokeOnMainThread(() => OnUseRequested(program), "VkRenderProgram.UseRequested"))
            return;

        if (!BackendContext.IsLogicalDeviceReady)
        {
            BackendContext.Resources.PipelineManager.QueueProgramLinkUntilDeviceReady(this);
            return;
        }

        if (!IsLinked)
            Link();
    }

    /// <summary>
    /// Opens a private per-thread writer for one immutable material-binding
    /// snapshot. Shared immediate bindings continue to use <see cref="_bindingLock"/>.
    /// </summary>
    private static EProgramStageMask ToProgramStageMask(ShaderStageFlags stage)
        => stage switch
        {
            ShaderStageFlags.VertexBit => EProgramStageMask.VertexShaderBit,
            ShaderStageFlags.TessellationControlBit => EProgramStageMask.TessControlShaderBit,
            ShaderStageFlags.TessellationEvaluationBit => EProgramStageMask.TessEvaluationShaderBit,
            ShaderStageFlags.GeometryBit => EProgramStageMask.GeometryShaderBit,
            ShaderStageFlags.FragmentBit => EProgramStageMask.FragmentShaderBit,
            ShaderStageFlags.ComputeBit => EProgramStageMask.ComputeShaderBit,
            ShaderStageFlags.MeshBitNV => EProgramStageMask.MeshShaderBit,
            ShaderStageFlags.TaskBitNV => EProgramStageMask.TaskShaderBit,
            _ => EProgramStageMask.None
        };

}
