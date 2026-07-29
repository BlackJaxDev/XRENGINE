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

public unsafe partial class VulkanRenderer
{
    private const EProgramStageMask GraphicsStageMask =
        EProgramStageMask.VertexShaderBit |
        EProgramStageMask.TessControlShaderBit |
        EProgramStageMask.TessEvaluationShaderBit |
        EProgramStageMask.GeometryShaderBit |
        EProgramStageMask.FragmentShaderBit |
        EProgramStageMask.MeshShaderBit |
        EProgramStageMask.TaskShaderBit;

    public partial class VkRenderProgram(VulkanRenderer renderer, XRRenderProgram data) : VkObject<XRRenderProgram>(renderer, data)
    {
        private readonly Dictionary<XRShader, VkShader> _shaderCache = new();
        private readonly Dictionary<EProgramStageMask, VkShader> _stageLookup = new();
        private DescriptorSetLayout[] _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
        private ulong _descriptorLayoutFingerprint;
        private ulong _descriptorSchemaFingerprint;
        private PipelineLayout _pipelineLayout;
        private readonly List<DescriptorBindingInfo> _programDescriptorBindings = new();
        private readonly Dictionary<string, AutoUniformBlockInfo> _autoUniformBlocks = new(StringComparer.Ordinal);
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
        private readonly ConcurrentDictionary<string, byte> _computeWarnings = new(StringComparer.Ordinal);
        private readonly Dictionary<ComputeUniformBufferKey, ComputeUniformBuffer> _computeUniformBuffers = new();
        private readonly HashSet<(uint ImageIndex, ulong BindingKey)> _reusableComputeDescriptorRefreshKeys = [];
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
            get => _isLinked;
            private set
            {
                if (_isLinked == value)
                    return;
                _isLinked = value;
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

        /// <summary>
        /// Exposes the concrete auto-uniform map to Vulkan hot paths so dictionary
        /// enumeration remains allocation-free.
        /// </summary>
        internal Dictionary<string, AutoUniformBlockInfo> AutoUniformBlockMap => _autoUniformBlocks;
        public bool DescriptorSetsRequireUpdateAfterBind => _descriptorSetsRequireUpdateAfterBind;
        public bool DescriptorSetsRequireVariableDescriptorCount => _descriptorSetsRequireVariableDescriptorCount;
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
            Data.DispatchComputeRequested += DispatchCompute;

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
            Data.DispatchComputeRequested -= DispatchCompute;

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

            if (Renderer.GetOrCreateAPIRenderObject(shader) is not VkShader vkShader)
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
            if (RuntimeEngine.InvokeOnMainThread(() => OnShaderInvalidated(shader), "VkRenderProgram.ShaderInvalidated"))
                return;

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

            if (Data.LinkReady && Renderer.IsLogicalDeviceReady)
                Link();
        }

        private void OnLinkRequested(XRRenderProgram program)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => OnLinkRequested(program), "VkRenderProgram.LinkRequested"))
                return;

            if (!Renderer.IsLogicalDeviceReady)
            {
                Renderer.QueueProgramLinkUntilDeviceReady(this);
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

            if (!Renderer.IsLogicalDeviceReady)
            {
                Renderer.QueueProgramLinkUntilDeviceReady(this);
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

        private static DescriptorLayoutBuildResult BuildDescriptorLayoutsShared(VulkanRenderer renderer, Device device, IEnumerable<DescriptorBindingInfo> bindings, string programName)
        {
            List<DescriptorBindingInfo> reflectedBindings = bindings
                .Select(NormalizeGraphicsFrameDataBinding)
                .ToList();
            if (VulkanFeatureProfile.EnableDescriptorContractValidation &&
                !VulkanDescriptorContracts.TryValidateContract(reflectedBindings, out string contractError))
            {
                throw new InvalidOperationException($"Descriptor contract validation failed for program '{programName}': {contractError}");
            }

            Dictionary<(uint set, uint binding), DescriptorSetLayoutBindingBuilder> builders = new();
            foreach (DescriptorBindingInfo binding in reflectedBindings)
            {
                var key = (binding.Set, binding.Binding);
                if (!builders.TryGetValue(key, out DescriptorSetLayoutBindingBuilder? builder))
                {
                    builder = new DescriptorSetLayoutBindingBuilder(binding);
                    builders.Add(key, builder);
                }
                else
                {
                    builder.Merge(binding);
                }
            }

            if (builders.Count == 0)
                return new DescriptorLayoutBuildResult(
                    Array.Empty<DescriptorSetLayout>(),
                    new List<DescriptorBindingInfo>(),
                    Array.Empty<bool>(),
                    false,
                    false);

            List<DescriptorSetLayout> layouts = new();
            List<bool> setUsesUpdateAfterBind = new();
            bool requiresUpdateAfterBind = false;
            bool requiresVariableDescriptorCount = false;
            uint maxDeclaredSet = builders.Values.Max(b => b.Set);
            uint maxSet = Math.Max(maxDeclaredSet, DescriptorSetTierCount - 1);

            Dictionary<uint, List<DescriptorSetLayoutBindingBuilder>> groupsBySet = builders.Values
                .GroupBy(b => b.Set)
                .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Binding).ToList());

            for (uint setIndex = 0; setIndex <= maxSet; setIndex++)
            {
                DescriptorSetLayoutBinding[] vkBindings = groupsBySet.TryGetValue(setIndex, out List<DescriptorSetLayoutBindingBuilder>? setBuilders)
                    ? [.. setBuilders.Select(b => b.ToBinding())]
                    : Array.Empty<DescriptorSetLayoutBinding>();

                if (!renderer.TryAcquireCachedDescriptorSetLayout(
                    setIndex,
                    vkBindings,
                    out DescriptorSetLayout layout,
                    out bool usesUpdateAfterBind,
                    out bool usesVariableDescriptorCount))
                    throw new InvalidOperationException($"Failed to create descriptor set layout for program '{programName}'.");

                requiresUpdateAfterBind |= usesUpdateAfterBind;
                requiresVariableDescriptorCount |= usesVariableDescriptorCount;
                layouts.Add(layout);
                setUsesUpdateAfterBind.Add(usesUpdateAfterBind);
            }

            List<DescriptorBindingInfo> mergedBindings = builders.Values
                .OrderBy(b => b.Set)
                .ThenBy(b => b.Binding)
                .Select(b => b.ToDescriptorBindingInfo())
                .ToList();

            return new DescriptorLayoutBuildResult(
                layouts.ToArray(),
                mergedBindings,
                setUsesUpdateAfterBind.ToArray(),
                requiresUpdateAfterBind,
                requiresVariableDescriptorCount);
        }

        private static DescriptorBindingInfo NormalizeGraphicsFrameDataBinding(DescriptorBindingInfo binding)
        {
            bool graphicsUniform = binding.Set == DescriptorSetGlobals &&
                binding.DescriptorType == DescriptorType.UniformBuffer &&
                (binding.StageFlags & ShaderStageFlags.ComputeBit) == 0;
            return graphicsUniform
                ? binding with { DescriptorType = DescriptorType.UniformBufferDynamic }
                : binding;
        }

        private static ReadOnlySpan<EProgramStageMask> StageOrder =>
        [
            EProgramStageMask.TaskShaderBit,
            EProgramStageMask.MeshShaderBit,
            EProgramStageMask.VertexShaderBit,
            EProgramStageMask.TessControlShaderBit,
            EProgramStageMask.TessEvaluationShaderBit,
            EProgramStageMask.GeometryShaderBit,
            EProgramStageMask.FragmentShaderBit,
            EProgramStageMask.ComputeShaderBit,
        ];

        private static IEnumerable<EProgramStageMask> EnumerateStages(EProgramStageMask mask)
        {
            if (mask.HasFlag(EProgramStageMask.TaskShaderBit))
                yield return EProgramStageMask.TaskShaderBit;
            if (mask.HasFlag(EProgramStageMask.MeshShaderBit))
                yield return EProgramStageMask.MeshShaderBit;
            if (mask.HasFlag(EProgramStageMask.VertexShaderBit))
                yield return EProgramStageMask.VertexShaderBit;
            if (mask.HasFlag(EProgramStageMask.TessControlShaderBit))
                yield return EProgramStageMask.TessControlShaderBit;
            if (mask.HasFlag(EProgramStageMask.TessEvaluationShaderBit))
                yield return EProgramStageMask.TessEvaluationShaderBit;
            if (mask.HasFlag(EProgramStageMask.GeometryShaderBit))
                yield return EProgramStageMask.GeometryShaderBit;
            if (mask.HasFlag(EProgramStageMask.FragmentShaderBit))
                yield return EProgramStageMask.FragmentShaderBit;
            if (mask.HasFlag(EProgramStageMask.ComputeShaderBit))
                yield return EProgramStageMask.ComputeShaderBit;
        }

    }
