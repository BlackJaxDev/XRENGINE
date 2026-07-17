using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly object _pendingDeviceReadyProgramLinksLock = new();
    private readonly HashSet<VkRenderProgram> _pendingDeviceReadyProgramLinks = new();

    internal void QueueProgramLinkUntilDeviceReady(VkRenderProgram program)
    {
        lock (_pendingDeviceReadyProgramLinksLock)
            _pendingDeviceReadyProgramLinks.Add(program);
    }

    private void FlushPendingDeviceReadyProgramLinks()
    {
        if (!IsLogicalDeviceReady)
            return;

        VkRenderProgram[] pendingPrograms;
        lock (_pendingDeviceReadyProgramLinksLock)
        {
            if (_pendingDeviceReadyProgramLinks.Count == 0)
                return;

            pendingPrograms = [.. _pendingDeviceReadyProgramLinks];
            _pendingDeviceReadyProgramLinks.Clear();
        }

        foreach (VkRenderProgram program in pendingPrograms)
        {
            if (program.IsLinked || !program.Data.LinkReady)
                continue;

            if (!program.Link())
                Debug.VulkanWarning($"Failed to link Vulkan program '{program.Data.Name ?? "UnnamedProgram"}' after logical device creation.");
        }
    }

    private void ClearPendingDeviceReadyProgramLinks()
    {
        lock (_pendingDeviceReadyProgramLinksLock)
            _pendingDeviceReadyProgramLinks.Clear();
    }

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
        private readonly ConcurrentDictionary<string, byte> _computeWarnings = new(StringComparer.Ordinal);
        private readonly Dictionary<ComputeUniformBufferKey, ComputeUniformBuffer> _computeUniformBuffers = new();
        private readonly HashSet<(uint ImageIndex, ulong BindingKey)> _reusableComputeDescriptorRefreshKeys = [];
        private Pipeline _computePipeline;
        private DescriptorHeapProgramLayout? _descriptorHeapLayout;
        private bool[] _descriptorSetUsesUpdateAfterBind = Array.Empty<bool>();
        private bool _descriptorSetsRequireUpdateAfterBind;
        private bool _descriptorSetsRequireVariableDescriptorCount;
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
        internal DescriptorHeapProgramLayout? DescriptorHeapLayout => _descriptorHeapLayout;
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

        internal void ClearBindings()
        {
            lock (_bindingLock)
            {
                _uniformValues.Clear();
                _samplersByUnit.Clear();
                _samplerNamesByUnit.Clear();
                _samplersByName.Clear();
                _imagesByUnit.Clear();
                _buffersByBinding.Clear();
            }
        }

        private void SetUniformValue(string name, EShaderVarType type, object value, bool isArray = false)
            => SetUniformValue(name, new ProgramUniformValue(type, value, isArray));

        private void SetUniformValue(string name, in ProgramUniformValue value)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            lock (_bindingLock)
                _uniformValues[name] = value;
        }

        internal bool TryGetUniformValue(string name, out ProgramUniformValue value)
        {
            lock (_bindingLock)
            {
                if (_uniformValues.TryGetValue(name, out value))
                    return true;

                // Keep parity with vertex suffix-based engine uniforms.
                if (name.EndsWith("_VTX", StringComparison.Ordinal))
                {
                    string stripped = VertexBaseUniformNames.GetOrAdd(
                        name,
                        static uniformName => uniformName[..^4]);
                    if (_uniformValues.TryGetValue(stripped, out value))
                        return true;
                }
                else if (_uniformValues.TryGetValue(
                    VertexSuffixedUniformNames.GetOrAdd(
                        name,
                        static uniformName => string.Concat(uniformName, "_VTX")),
                    out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        internal void ApplyBindingSnapshot(ComputeDispatchSnapshot snapshot)
        {
            lock (_bindingLock)
            {
                _uniformValues.Clear();
                _samplersByUnit.Clear();
                _samplerNamesByUnit.Clear();
                _samplersByName.Clear();
                _imagesByUnit.Clear();
                _buffersByBinding.Clear();

                foreach (var pair in snapshot.Uniforms)
                    _uniformValues[pair.Key] = pair.Value;

                foreach (var pair in snapshot.Samplers)
                    _samplersByUnit[pair.Key] = pair.Value;

                foreach (var pair in snapshot.SamplerNamesByUnit)
                    _samplerNamesByUnit[pair.Key] = pair.Value;

                foreach (var pair in snapshot.SamplersByName)
                    _samplersByName[pair.Key] = pair.Value;

                foreach (var pair in snapshot.Images)
                    _imagesByUnit[pair.Key] = pair.Value;

                foreach (var pair in snapshot.Buffers)
                    _buffersByBinding[pair.Key] = pair.Value.Data;
            }
        }

        internal ComputeDispatchSnapshot CaptureComputeSnapshot()
        {
            lock (_bindingLock)
            {
                Dictionary<uint, VulkanComputeBufferBinding> buffers = new(_buffersByBinding.Count);
                Dictionary<string, VulkanComputeBufferBinding> buffersByName = new(StringComparer.Ordinal);
                bool allowSynchronousUpload = Renderer.AllowSynchronousResourceUploads;
                foreach (KeyValuePair<uint, XRDataBuffer> pair in _buffersByBinding)
                {
                    XRDataBuffer buffer = pair.Value;
                    if (Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: allowSynchronousUpload) is not VkDataBuffer vkBuffer ||
                        !vkBuffer.TryCaptureComputeBufferSnapshot(allowSynchronousUpload, out VulkanComputeBufferBinding bufferBinding))
                    {
                        bufferBinding = new VulkanComputeBufferBinding(buffer, default, 0UL, 0);
                    }

                    buffers[pair.Key] = bufferBinding;
                    if (!string.IsNullOrWhiteSpace(buffer.AttributeName))
                        buffersByName.TryAdd(buffer.AttributeName, bufferBinding);
                }

                return new ComputeDispatchSnapshot(
                    new Dictionary<string, ProgramUniformValue>(_uniformValues, StringComparer.Ordinal),
                    new Dictionary<uint, XRTexture>(_samplersByUnit),
                    new Dictionary<uint, string>(_samplerNamesByUnit),
                    new Dictionary<string, XRTexture>(_samplersByName, StringComparer.Ordinal),
                    new Dictionary<uint, ProgramImageBinding>(_imagesByUnit),
                    buffers,
                    buffersByName);
            }
        }

        internal bool HasBoundDescriptorResources()
        {
            lock (_bindingLock)
                return _samplersByName.Count != 0 || _buffersByBinding.Count != 0;
        }

        private void Uniform(string name, Matrix4x4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._mat4, value));
        private void Uniform(string name, Quaternion value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec4, new Vector4(value.X, value.Y, value.Z, value.W)));
        private void Uniform(string name, Matrix4x4[] value) => SetUniformValue(name, EShaderVarType._mat4, value.ToArray(), true);
        private void Uniform(string name, Quaternion[] value)
        {
            Vector4[] converted = value.Select(q => new Vector4(q.X, q.Y, q.Z, q.W)).ToArray();
            SetUniformValue(name, EShaderVarType._vec4, converted, true);
        }

        private void Uniform(string name, bool value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._bool, value));
        private void Uniform(string name, BoolVector2 value) => SetUniformValue(name, EShaderVarType._bvec2, value);
        private void Uniform(string name, BoolVector3 value) => SetUniformValue(name, EShaderVarType._bvec3, value);
        private void Uniform(string name, BoolVector4 value) => SetUniformValue(name, EShaderVarType._bvec4, value);
        private void Uniform(string name, bool[] value) => SetUniformValue(name, EShaderVarType._bool, value.ToArray(), true);
        private void Uniform(string name, BoolVector2[] value) => SetUniformValue(name, EShaderVarType._bvec2, value.ToArray(), true);
        private void Uniform(string name, BoolVector3[] value) => SetUniformValue(name, EShaderVarType._bvec3, value.ToArray(), true);
        private void Uniform(string name, BoolVector4[] value) => SetUniformValue(name, EShaderVarType._bvec4, value.ToArray(), true);

        private void Uniform(string name, float value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._float, value));
        private void Uniform(string name, Vector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec2, value));
        private void Uniform(string name, Vector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec3, value));
        private void Uniform(string name, Vector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._vec4, value));
        private void Uniform(string name, float[] value) => SetUniformValue(name, EShaderVarType._float, value.ToArray(), true);
        private void Uniform(string name, Span<float> value) => SetUniformValue(name, EShaderVarType._float, value.ToArray(), true);
        private void Uniform(string name, Vector2[] value) => SetUniformValue(name, EShaderVarType._vec2, value.ToArray(), true);
        private void Uniform(string name, Vector3[] value) => SetUniformValue(name, EShaderVarType._vec3, value.ToArray(), true);
        private void Uniform(string name, Vector4[] value) => SetUniformValue(name, EShaderVarType._vec4, value.ToArray(), true);

        private void Uniform(string name, double value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._double, value));
        private void Uniform(string name, DVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec2, value));
        private void Uniform(string name, DVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec3, value));
        private void Uniform(string name, DVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._dvec4, value));
        private void Uniform(string name, double[] value) => SetUniformValue(name, EShaderVarType._double, value.ToArray(), true);
        private void Uniform(string name, DVector2[] value) => SetUniformValue(name, EShaderVarType._dvec2, value.ToArray(), true);
        private void Uniform(string name, DVector3[] value) => SetUniformValue(name, EShaderVarType._dvec3, value.ToArray(), true);
        private void Uniform(string name, DVector4[] value) => SetUniformValue(name, EShaderVarType._dvec4, value.ToArray(), true);

        private void Uniform(string name, int value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._int, value));
        private void Uniform(string name, IVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec2, value));
        private void Uniform(string name, IVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec3, value));
        private void Uniform(string name, IVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._ivec4, value));
        private void Uniform(string name, int[] value) => SetUniformValue(name, EShaderVarType._int, value.ToArray(), true);
        private void Uniform(string name, IVector2[] value) => SetUniformValue(name, EShaderVarType._ivec2, value.ToArray(), true);
        private void Uniform(string name, IVector3[] value) => SetUniformValue(name, EShaderVarType._ivec3, value.ToArray(), true);
        private void Uniform(string name, IVector4[] value) => SetUniformValue(name, EShaderVarType._ivec4, value.ToArray(), true);

        private void Uniform(string name, uint value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uint, value));
        private void Uniform(string name, UVector2 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec2, value));
        private void Uniform(string name, UVector3 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec3, value));
        private void Uniform(string name, UVector4 value) => SetUniformValue(name, new ProgramUniformValue(EShaderVarType._uvec4, value));
        private void Uniform(string name, uint[] value) => SetUniformValue(name, EShaderVarType._uint, value.ToArray(), true);
        private void Uniform(string name, UVector2[] value) => SetUniformValue(name, EShaderVarType._uvec2, value.ToArray(), true);
        private void Uniform(string name, UVector3[] value) => SetUniformValue(name, EShaderVarType._uvec3, value.ToArray(), true);
        private void Uniform(string name, UVector4[] value) => SetUniformValue(name, EShaderVarType._uvec4, value.ToArray(), true);

        private void Sampler(string name, IRenderTextureResource texture, int textureUnit)
        {
            if (texture is not XRTexture xrTexture)
                return;

            uint unit = textureUnit < 0 ? 0u : (uint)textureUnit;
            lock (_bindingLock)
            {
                _samplersByUnit[unit] = xrTexture;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _samplerNamesByUnit[unit] = name;
                    _samplersByName[name] = xrTexture;
                }
                else
                {
                    _samplerNamesByUnit.Remove(unit);
                }
            }

            Renderer.TrackTextureBinding(xrTexture);
        }

        private void Sampler(int location, IRenderTextureResource texture, int textureUnit)
            => Sampler(location.ToString(), texture, textureUnit);

        private void BindImageTexture(uint unit, IRenderTextureResource texture, int level, bool layered, int layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)
        {
            if (texture is not XRTexture xrTexture)
                return;

            lock (_bindingLock)
                _imagesByUnit[unit] = new ProgramImageBinding(xrTexture, level, layered, layer, access, format);

            Renderer.TrackTextureBinding(xrTexture);
        }

        private void BindBuffer(uint index, XRDataBuffer buffer)
        {
            if (buffer is null)
                return;

            lock (_bindingLock)
                _buffersByBinding[index] = buffer;

            Renderer.TrackBufferBinding(buffer);
        }

        private void DispatchCompute(
            uint x,
            uint y,
            uint z,
            IEnumerable<(uint unit, IRenderTextureResource texture, int level, int? layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)>? textures = null)
        {
            if (textures is not null)
            {
                foreach (var (unit, texture, level, layer, access, format) in textures)
                    BindImageTexture(unit, texture, level, layer.HasValue, layer ?? 0, access, format);
            }

            int gx = x > int.MaxValue ? int.MaxValue : (int)x;
            int gy = y > int.MaxValue ? int.MaxValue : (int)y;
            int gz = z > int.MaxValue ? int.MaxValue : (int)z;
            Renderer.DispatchCompute(Data, gx, gy, gz);
        }

        public bool Link(bool allowAsyncShaderCompile = false)
        {
            if (Renderer.IsDeviceLost)
                return false;

            global::System.Diagnostics.Stopwatch buildWatch = global::System.Diagnostics.Stopwatch.StartNew();
            double compileMilliseconds = 0.0;
            int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
            bool usesVulkanClipDepthRemap = RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap;
            EShaderType? vulkanClipDepthRemapStage = ResolveVulkanClipDepthRemapStage();
            if (IsLinked &&
                _linkedShaderConfigVersion == shaderConfigVersion &&
                _linkedUsesVulkanClipDepthRemap == usesVulkanClipDepthRemap &&
                _linkedVulkanClipDepthRemapStage == vulkanClipDepthRemapStage &&
                _linkedTransformFeedbackLayoutVersion == Data.TransformFeedbackLayoutVersion)
                return true;

            if (IsLinked)
                DestroyLayouts();

            if (!Renderer.IsLogicalDeviceReady)
            {
                Renderer.QueueProgramLinkUntilDeviceReady(this);
                return false;
            }

            if (!IsActive)
                Generate();

            if (!IsActive)
                return false;

            if (!Data.LinkReady)
                return false;

            if (_shaderCache.Count == 0)
            {
                Debug.VulkanWarning($"Cannot link Vulkan program '{Data.Name ?? "UnnamedProgram"}' because it contains no shaders.");
                Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                    XRRenderProgram.EShaderProgramBackendStage.Failed,
                    0.0,
                    0.0,
                    "program contains no shaders",
                    Backend: "Vulkan",
                    Detail: Data.Name));
                return false;
            }

            if (!TryApplyTransformFeedbackCompilePlans(out string? transformFeedbackFailure))
            {
                IsLinked = false;
                Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                    XRRenderProgram.EShaderProgramBackendStage.Failed,
                    0.0,
                    0.0,
                    transformFeedbackFailure,
                    Backend: "Vulkan",
                    Detail: "transform feedback layout validation failed"));
                return false;
            }

            Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                XRRenderProgram.EShaderProgramBackendStage.Compiling,
                0.0,
                0.0,
                null,
                Backend: "Vulkan",
                Detail: DescribeShaderStages()));

            foreach (VkShader shader in _shaderCache.Values)
            {
                try
                {
                    global::System.Diagnostics.Stopwatch shaderWatch = global::System.Diagnostics.Stopwatch.StartNew();
                    bool shaderUsesVulkanClipDepthRemap =
                        vulkanClipDepthRemapStage.HasValue &&
                        shader.Data.Type == vulkanClipDepthRemapStage.Value;
                    if (allowAsyncShaderCompile)
                    {
                        if (!shader.TryGenerateFromAsyncCompile(shaderUsesVulkanClipDepthRemap, out string asyncReason))
                        {
                            shaderWatch.Stop();
                            compileMilliseconds += shaderWatch.Elapsed.TotalMilliseconds;
                            IsLinked = false;
                            Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                                shader.CompileStatus.HasFailure
                                    ? XRRenderProgram.EShaderProgramBackendStage.Failed
                                    : XRRenderProgram.EShaderProgramBackendStage.SourceQueued,
                                compileMilliseconds,
                                0.0,
                                shader.CompileStatus.FailureReason,
                                Backend: "Vulkan",
                                Detail: $"{shader.StageDebugLabel}: {asyncReason}",
                                Fingerprint: shader.CompileStatus.ArtifactIdentity));
                            return false;
                        }
                    }
                    else
                    {
                        shader.SetVulkanClipDepthRemapEnabled(shaderUsesVulkanClipDepthRemap);
                        shader.EnsureCompilePolicyCurrent();
                        shader.Generate();
                    }
                    shaderWatch.Stop();
                    compileMilliseconds += shaderWatch.Elapsed.TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    IsLinked = false;
                    Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                        XRRenderProgram.EShaderProgramBackendStage.Failed,
                        compileMilliseconds,
                        0.0,
                        shader.CompileStatus.FailureReason ?? ex.Message,
                        Backend: "Vulkan",
                        Detail: shader.StageDebugLabel,
                        Fingerprint: shader.CompileStatus.ArtifactIdentity));
                    return false;
                }

                if (!shader.IsGenerated || !shader.IsCompiled)
                {
                    IsLinked = false;
                    Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                        XRRenderProgram.EShaderProgramBackendStage.Failed,
                        compileMilliseconds,
                        0.0,
                        shader.CompileStatus.FailureReason ?? "shader module was not generated",
                        Backend: "Vulkan",
                        Detail: shader.StageDebugLabel,
                        Fingerprint: shader.CompileStatus.ArtifactIdentity));
                    return false;
                }
            }

            global::System.Diagnostics.Stopwatch linkWatch = global::System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                    XRRenderProgram.EShaderProgramBackendStage.Linking,
                    compileMilliseconds,
                    0.0,
                    null,
                    Backend: "Vulkan",
                    Detail: DescribeShaderStages()));

                BuildStageLookup();
                BuildDescriptorLayouts();
            }
            catch (Exception ex)
            {
                linkWatch.Stop();
                IsLinked = false;
                Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                    XRRenderProgram.EShaderProgramBackendStage.Failed,
                    compileMilliseconds,
                    linkWatch.Elapsed.TotalMilliseconds,
                    ex.Message,
                    Backend: "Vulkan",
                    Detail: "descriptor layout or pipeline interface build failed",
                    Fingerprint: DescribeShaderStages()));
                return false;
            }

            IsLinked = true;
            _linkedShaderConfigVersion = shaderConfigVersion;
            _linkedUsesVulkanClipDepthRemap = usesVulkanClipDepthRemap;
            _linkedVulkanClipDepthRemapStage = vulkanClipDepthRemapStage;
            _linkedTransformFeedbackLayoutVersion = Data.TransformFeedbackLayoutVersion;
            linkWatch.Stop();
            buildWatch.Stop();
            Data.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
                XRRenderProgram.EShaderProgramBackendStage.Ready,
                compileMilliseconds,
                linkWatch.Elapsed.TotalMilliseconds,
                null,
                Backend: "Vulkan",
                Detail: DescribeShaderStages(),
                Fingerprint: ComputeProgramArtifactFingerprint()));
            return true;
        }

        private bool TryApplyTransformFeedbackCompilePlans(out string? failure)
        {
            failure = null;
            VulkanTransformFeedbackCompilePlan? plan = null;
            EShaderType? captureStage = null;

            bool hasRequestedCaptures = Data.TransformFeedbacks.Any(static feedback =>
                feedback.Names is { Length: > 0 } &&
                feedback.Names.Any(static name => !string.IsNullOrWhiteSpace(name)));

            if (hasRequestedCaptures)
            {
                if (!Renderer.SupportsTransformFeedback)
                {
                    failure = "VK_EXT_transform_feedback is not enabled on the active Vulkan device.";
                    return false;
                }

                captureStage = ResolveTransformFeedbackCaptureStage();
                if (!captureStage.HasValue)
                {
                    failure = "Vulkan transform feedback requires a vertex, tessellation evaluation, or geometry shader capture stage. Mesh/task shader capture is not supported by this wrapper.";
                    return false;
                }

                if (!TryBuildTransformFeedbackCompilePlan(out plan, out failure))
                    return false;
            }

            foreach (VkShader shader in _shaderCache.Values)
            {
                shader.SetTransformFeedbackCompilePlan(
                    captureStage.HasValue && shader.Data.Type == captureStage.Value
                        ? plan
                        : null);
            }

            return true;
        }

        private bool TryBuildTransformFeedbackCompilePlan(
            out VulkanTransformFeedbackCompilePlan? plan,
            out string? failure)
        {
            plan = null;
            failure = null;

            List<VulkanTransformFeedbackBufferCapture> buffers = [];
            HashSet<uint> bindings = [];
            foreach (XRTransformFeedback feedback in Data.TransformFeedbacks.OrderBy(static feedback => feedback.BindingLocation))
            {
                string[] names = feedback.Names
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();
                if (names.Length == 0)
                    continue;

                if (feedback.BindingLocation >= Renderer.TransformFeedbackProperties.MaxTransformFeedbackBuffers)
                {
                    failure =
                        $"Vulkan transform feedback binding {feedback.BindingLocation} exceeds device limit " +
                        $"{Renderer.TransformFeedbackProperties.MaxTransformFeedbackBuffers}.";
                    return false;
                }

                if (!bindings.Add(feedback.BindingLocation))
                {
                    failure =
                        $"Vulkan transform feedback binding {feedback.BindingLocation} is used by more than one XRTransformFeedback object. " +
                        "Use one XRTransformFeedback per binding.";
                    return false;
                }

                if (feedback.Type == EFeedbackType.OutValues && names.Length != 1)
                {
                    failure =
                        "Vulkan OutValues transform feedback captures require exactly one varying name per XRTransformFeedback. " +
                        "Use PerVertex when multiple varyings should be interleaved into one feedback buffer.";
                    return false;
                }

                buffers.Add(new VulkanTransformFeedbackBufferCapture(feedback.BindingLocation, feedback.Type, names));
            }

            plan = buffers.Count == 0
                ? null
                : new VulkanTransformFeedbackCompilePlan(buffers);
            return true;
        }

        private EShaderType? ResolveTransformFeedbackCaptureStage()
        {
            if (HasShaderStage(EShaderType.Geometry))
                return EShaderType.Geometry;
            if (HasShaderStage(EShaderType.TessEvaluation))
                return EShaderType.TessEvaluation;
            if (HasShaderStage(EShaderType.Vertex))
                return EShaderType.Vertex;

            return null;
        }

        private EShaderType? ResolveVulkanClipDepthRemapStage()
        {
            if (!RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap)
                return null;

            if (HasShaderStage(EShaderType.Mesh))
                return EShaderType.Mesh;
            if (HasShaderStage(EShaderType.Geometry))
                return EShaderType.Geometry;
            if (HasShaderStage(EShaderType.TessEvaluation))
                return EShaderType.TessEvaluation;
            if (HasShaderStage(EShaderType.Vertex))
                return EShaderType.Vertex;

            return null;
        }

        private bool HasShaderStage(EShaderType shaderType)
        {
            foreach (VkShader shader in _shaderCache.Values)
            {
                if (shader.Data.Type == shaderType)
                    return true;
            }

            return false;
        }

        private void BuildStageLookup()
        {
            _stageLookup.Clear();
            foreach (VkShader shader in _shaderCache.Values)
            {
                EProgramStageMask mask = ToProgramStageMask(shader.StageFlags);
                if (mask == EProgramStageMask.None)
                    continue;

                _stageLookup[mask] = shader;
            }
        }

        /// <summary>
        /// True when the vertex stage exposes at least one reflected input attribute
        /// location, enabling semantic (by-name) vertex buffer binding.
        /// </summary>
        internal bool HasReflectedVertexInputs
            => _stageLookup.TryGetValue(EProgramStageMask.VertexShaderBit, out VkShader? vertexShader)
               && vertexShader.VertexInputLocations.Count > 0;

        /// <summary>
        /// When a vertex stage is present, reports how many input attribute locations it
        /// reflects. A present vertex stage that reflects zero inputs (e.g. the fullscreen
        /// triangle which derives clip positions from <c>gl_VertexID</c>) consumes no
        /// vertex buffers, so binding any would trip the validation layer.
        /// </summary>
        internal bool TryGetVertexStageInputCount(out int inputCount)
        {
            if (_stageLookup.TryGetValue(EProgramStageMask.VertexShaderBit, out VkShader? vertexShader))
            {
                inputCount = vertexShader.VertexInputLocations.Count;
                return true;
            }

            inputCount = 0;
            return false;
        }


        /// <summary>
        /// Resolves the vertex input attribute location declared in the vertex shader
        /// for the given attribute name. Mirrors the OpenGL by-name binding path.
        /// </summary>
        internal bool TryGetVertexInputLocation(string attributeName, out uint location)
        {
            location = 0;
            if (string.IsNullOrEmpty(attributeName))
                return false;

            return _stageLookup.TryGetValue(EProgramStageMask.VertexShaderBit, out VkShader? vertexShader)
                && vertexShader.VertexInputLocations.TryGetValue(attributeName, out location);
        }

        /// <summary>
        /// Resolves a program-bound sampler texture by its shader uniform name. These are
        /// registered via <see cref="Sampler(string, IRenderTextureResource, int)"/> and
        /// cover both material textures and engine/FBO blit bindings.
        /// </summary>
        internal bool TryGetSamplerTexture(string samplerName, out XRTexture? texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(samplerName))
                return false;

            lock (_bindingLock)
            {
                if (_samplersByName.TryGetValue(samplerName, out XRTexture? found))
                {
                    texture = found;
                    return true;
                }
            }

            return false;
        }

        internal bool TryGetBoundBuffer(uint binding, out XRDataBuffer? buffer)
        {
            lock (_bindingLock)
            {
                if (_buffersByBinding.TryGetValue(binding, out XRDataBuffer? found))
                {
                    buffer = found;
                    return true;
                }
            }

            buffer = null;
            return false;
        }

        /// <summary>
        /// Folds the program-bound named samplers into a descriptor resource fingerprint so
        /// descriptor sets are rewritten when an FBO/engine sampler binding changes.
        /// </summary>
        internal void AddSamplerResourceFingerprint(ref HashCode hash)
        {
            lock (_bindingLock)
            {
                hash.Add(_samplersByName.Count);
                ulong xor = 0;
                ulong sum = 0;
                foreach (KeyValuePair<string, XRTexture> pair in _samplersByName)
                    AddUnorderedFingerprintItem(ref xor, ref sum, ComputeSamplerResourceFingerprintItem(pair.Key, pair.Value));

                hash.Add(xor);
                hash.Add(sum);
            }
        }

        internal ulong ComputeSamplerResourceFingerprint()
        {
            HashCode hash = new();
            AddSamplerResourceFingerprint(ref hash);
            return unchecked((ulong)hash.ToHashCode());
        }

        internal void AddBoundBufferResourceFingerprint(ref HashCode hash)
        {
            lock (_bindingLock)
            {
                hash.Add(_buffersByBinding.Count);
                ulong xor = 0;
                ulong sum = 0;
                foreach (KeyValuePair<uint, XRDataBuffer> pair in _buffersByBinding)
                    AddUnorderedFingerprintItem(ref xor, ref sum, ComputeBoundBufferResourceFingerprintItem(pair.Key, pair.Value));

                hash.Add(xor);
                hash.Add(sum);
            }
        }

        internal ulong ComputeBoundBufferResourceFingerprint()
        {
            HashCode hash = new();
            AddBoundBufferResourceFingerprint(ref hash);
            return unchecked((ulong)hash.ToHashCode());
        }

        private ulong ComputeSamplerResourceFingerprintItem(string name, XRTexture? texture)
        {
            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add(texture?.GetHashCode() ?? 0);
            if (texture is not null && Renderer.GetOrCreateAPIRenderObject(texture, generateNow: false) is IVkImageDescriptorSource source)
            {
                item.Add(source.IsDescriptorReady);
                item.Add(source.DescriptorGeneration);
                item.Add(source.DescriptorImage.Handle);
                item.Add(source.DescriptorView.Handle);
                item.Add(source.DescriptorSampler.Handle);
                item.Add(source.DescriptorViewType);
                item.Add(source.DescriptorFormat);
                item.Add(source.DescriptorAspect);
                item.Add(source.DescriptorUsage);
                item.Add(source.DescriptorSamples);
                item.Add(source.DescriptorMipLevels);
                item.Add(source.DescriptorArrayLayers);
            }
            else
            {
                item.Add(0UL);
            }

            return unchecked((ulong)item.ToHashCode());
        }

        private ulong ComputeBoundBufferResourceFingerprintItem(uint binding, XRDataBuffer? buffer)
        {
            HashCode item = new();
            item.Add(binding);
            item.Add(buffer?.GetHashCode() ?? 0);
            if (buffer is null)
            {
                item.Add(0UL);
                return unchecked((ulong)item.ToHashCode());
            }

            item.Add(buffer.AttributeName, StringComparer.Ordinal);
            item.Add(buffer.Name, StringComparer.Ordinal);
            item.Add(buffer.Length);
            item.Add((int)buffer.Target);
            item.Add(buffer.BindingIndexOverride ?? uint.MaxValue);

            if (Renderer.GetOrCreateAPIRenderObject(buffer, generateNow: false) is VkDataBuffer vkBuffer)
            {
                item.Add(vkBuffer.BufferHandle?.Handle ?? 0UL);
                item.Add(vkBuffer.AllocatedByteSize);
            }
            else
            {
                item.Add(0UL);
            }

            return unchecked((ulong)item.ToHashCode());
        }

        private static void AddUnorderedFingerprintItem(ref ulong xor, ref ulong sum, ulong itemHash)
        {
            unchecked
            {
                xor ^= itemHash;
                sum += BitOperations.RotateLeft(itemHash, (int)(itemHash & 31));
            }
        }

        private void BuildDescriptorLayouts()
        {
            DestroyLayouts();

            IEnumerable<DescriptorBindingInfo> shaderBindings = EnumerateShaderDescriptorBindings();
            string programName = Data.Name ?? "UnnamedProgram";
            var result = BuildDescriptorLayoutsShared(Renderer, Device, shaderBindings, programName);

            _descriptorSetLayouts = result.Layouts;
            _programDescriptorBindings.Clear();
            _programDescriptorBindings.AddRange(result.Bindings);
            _descriptorSetUsesUpdateAfterBind = result.SetUsesUpdateAfterBind;
            _descriptorSetsRequireUpdateAfterBind = result.RequiresUpdateAfterBind;
            _descriptorSetsRequireVariableDescriptorCount = result.RequiresVariableDescriptorCount;
            _descriptorHeapLayout = null;
            if (Renderer.IsDescriptorHeapDrawBindingActive)
            {
                _descriptorHeapLayout = Renderer.CreateDescriptorHeapProgramLayout(
                    _programDescriptorBindings,
                    programName,
                    out string descriptorHeapReason);
                if (_descriptorHeapLayout is null)
                    throw new InvalidOperationException($"Failed to create Vulkan descriptor heap mapping for program '{programName}': {descriptorHeapReason}");
            }

            _autoUniformBlocks.Clear();
            foreach (VkShader shader in _shaderCache.Values)
            {
                if (shader.AutoUniformBlock is { } block)
                    _autoUniformBlocks[block.InstanceName] = block;
            }

            CreatePipelineLayout(_descriptorSetLayouts);
        }

        public bool TryGetAutoUniformBlock(string name, out AutoUniformBlockInfo block)
        {
            if (_autoUniformBlocks.TryGetValue(name, out AutoUniformBlockInfo? resolvedBlock) && resolvedBlock is not null)
            {
                block = resolvedBlock;
                return true;
            }

            block = null!;
            return false;
        }

        /// <summary>
        /// Searches for an auto-uniform block by block name (in addition to
        /// instance name) or by (set, binding) coordinates. This handles the
        /// common case where SPIR-V reflection produces the struct type name
        /// rather than the variable instance name.
        /// </summary>
        public bool TryGetAutoUniformBlockFuzzy(string name, uint set, uint binding, out AutoUniformBlockInfo block)
        {
            // 1. Try exact instance-name match first.
            if (!string.IsNullOrWhiteSpace(name)
                && _autoUniformBlocks.TryGetValue(name, out AutoUniformBlockInfo? resolvedBlock)
                && resolvedBlock is not null)
            {
                block = resolvedBlock;
                return true;
            }

            // 2. Try matching by block name (struct type name from SPIR-V).
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (AutoUniformBlockInfo candidate in _autoUniformBlocks.Values)
                {
                    if (string.Equals(candidate.BlockName, name, StringComparison.Ordinal))
                    {
                        block = candidate;
                        return true;
                    }
                }
            }

            // 3. Fall back to (set, binding) coordinates.
            foreach (AutoUniformBlockInfo candidate in _autoUniformBlocks.Values)
            {
                if (candidate.Set == set && candidate.Binding == binding)
                {
                    block = candidate;
                    return true;
                }
            }

            block = default!;
            return false;
        }

        private void CreatePipelineLayout(IReadOnlyList<DescriptorSetLayout> layouts)
        {
            if (Renderer.IsDeviceLost)
                return;

            DestroyPipelineLayout("VkRenderProgram.CreatePipelineLayout");

            if (layouts.Count == 0)
            {
                PushConstantRange pushRange = CreateCommonPushConstantRange();
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange
                };
                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
                Renderer.TrackLivePipelineLayout(_pipelineLayout, "VkRenderProgram.PipelineLayout");
                return;
            }

            DescriptorSetLayout[] layoutArray = layouts.ToArray();
            fixed (DescriptorSetLayout* layoutPtr = layoutArray)
            {
                PushConstantRange pushRange = CreateCommonPushConstantRange();
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layoutArray.Length,
                    PSetLayouts = layoutPtr,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange
                };

                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
                Renderer.TrackLivePipelineLayout(_pipelineLayout, "VkRenderProgram.PipelineLayout");
            }
        }

        private void DestroyLayouts()
        {
            DestroyComputeUniformBuffers();
            _reusableComputeDescriptorRefreshKeys.Clear();

            if (_computePipeline.Handle != 0)
            {
                Renderer.RetirePipeline(_computePipeline);
                _computePipeline = default;
            }

            if (_descriptorSetLayouts.Length > 0)
            {
                foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                    Renderer.ReleaseCachedDescriptorSetLayout(layout);

                _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
            }

            if (_pipelineLayout.Handle != 0)
                DestroyPipelineLayout("VkRenderProgram.DestroyLayouts");

            _programDescriptorBindings.Clear();
            _descriptorHeapLayout = null;
            _descriptorSetUsesUpdateAfterBind = Array.Empty<bool>();
            _descriptorSetsRequireUpdateAfterBind = false;
            _descriptorSetsRequireVariableDescriptorCount = false;
            IsLinked = false;
        }

        private void DestroyPipelineLayout(string owner)
        {
            if (_pipelineLayout.Handle == 0)
                return;

            PipelineLayout pipelineLayout = _pipelineLayout;
            _pipelineLayout = default;

            if (Renderer.TryBeginDestroyPipelineLayout(pipelineLayout, owner))
                Api!.DestroyPipelineLayout(Device, pipelineLayout, null);
        }

        private void DestroyComputeUniformBuffers()
        {
            foreach (ComputeUniformBuffer resource in _computeUniformBuffers.Values)
                ReleaseComputeUniformBuffer(resource);

            _computeUniformBuffers.Clear();
        }

        private void ReleaseComputeUniformBuffer(in ComputeUniformBuffer resource)
        {
            if (resource.Mapped != null)
                Renderer.UnmapBufferMemory(resource.Buffer, resource.Memory);

            if (resource.Buffer.Handle != 0 || resource.Memory.Handle != 0)
                Renderer.RetireBuffer(resource.Buffer, resource.Memory);
        }

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages()
            => GetShaderStages(EProgramStageMask.AllShaderBits);

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask flag in EnumerateStages(mask))
            {
                // Skip geometry shader stage if the device feature is not enabled.
                if (flag == EProgramStageMask.GeometryShaderBit && !Renderer.SupportsGeometryShader)
                    continue;

                if (_stageLookup.TryGetValue(flag, out VkShader? shader))
                    yield return shader.ShaderStageCreateInfo;
            }
        }

        internal string DescribeShaderStages()
        {
            if (_shaderCache.Count == 0)
                return "<none>";

            return string.Join(", ", _shaderCache.Values
                .OrderBy(static shader => GetShaderStageSortKey(shader.StageFlags))
                .Select(static shader => shader.StageDebugLabel));
        }

        internal void WriteShaderDiagnostics(string reason)
        {
            if (!RenderDiagnosticsFlags.VkDumpShaderOnError)
                return;

            string programName = Data.Name ?? "UnnamedProgram";
            string stageSummary = DescribeShaderStages();
            foreach (VkShader shader in _shaderCache.Values.OrderBy(static shader => GetShaderStageSortKey(shader.StageFlags)))
                shader.WriteRewrittenSourceDiagnostics($"program='{programName}' stages=[{stageSummary}] {reason}");
        }

        private static int GetShaderStageSortKey(ShaderStageFlags stage)
            => stage switch
            {
                ShaderStageFlags.VertexBit => 0,
                ShaderStageFlags.TessellationControlBit => 1,
                ShaderStageFlags.TessellationEvaluationBit => 2,
                ShaderStageFlags.GeometryBit => 3,
                ShaderStageFlags.FragmentBit => 4,
                ShaderStageFlags.ComputeBit => 5,
                ShaderStageFlags.TaskBitNV => 6,
                ShaderStageFlags.MeshBitNV => 7,
                _ => 100,
            };

        private IEnumerable<DescriptorBindingInfo> EnumerateShaderDescriptorBindings()
        {
            foreach (VkShader shader in _shaderCache.Values)
            {
                foreach (DescriptorBindingInfo binding in shader.DescriptorBindings)
                    yield return binding;
            }
        }

        public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
        {
            if (!Link())
                throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

            if (pipelineCache.Handle == 0)
                pipelineCache = Renderer.ActivePipelineCache;

            uint colorAttachmentCount = 0;
            if (pipelineInfo.PNext is not null)
            {
                var renderingInfo = (PipelineRenderingCreateInfo*)pipelineInfo.PNext;
                if (renderingInfo->SType == StructureType.PipelineRenderingCreateInfo)
                    colorAttachmentCount = renderingInfo->ColorAttachmentCount;
            }
            else if (pipelineInfo.RenderPass.Handle != 0)
            {
                colorAttachmentCount = Renderer.GetRenderPassColorAttachmentCount(pipelineInfo.RenderPass);
            }

            PipelineShaderStageCreateInfo[] stages = GetShaderStages(GraphicsStageMask).ToArray();
            if (colorAttachmentCount == 0)
                stages = stages.Where(static s => s.Stage != ShaderStageFlags.FragmentBit).ToArray();

            if (stages.Length == 0)
                throw new InvalidOperationException("Graphics pipeline creation requires at least one graphics shader stage.");

            // ── DIAGNOSTIC: optionally log stages when creating pipeline for dynamic rendering ──
            bool tracePipeCreate = XREngine.Rendering.RenderDiagnosticsFlags.VkTracePipeCreate;
            if (tracePipeCreate)
            {
                var stageNames = string.Join(", ", stages.Select(s => s.Stage.ToString()));
                var stageModules = string.Join(", ", stages.Select(s => $"{s.Stage}=0x{s.Module.Handle:X}"));

                string colorFormats = "<none>";
                Format depthFormat = Format.Undefined;
                Format stencilFormat = Format.Undefined;

                if (pipelineInfo.PNext is not null)
                {
                    var renderingInfo = (PipelineRenderingCreateInfo*)pipelineInfo.PNext;
                    if (renderingInfo->SType == StructureType.PipelineRenderingCreateInfo)
                    {
                        colorAttachmentCount = renderingInfo->ColorAttachmentCount;
                        depthFormat = renderingInfo->DepthAttachmentFormat;
                        stencilFormat = renderingInfo->StencilAttachmentFormat;

                        if (colorAttachmentCount > 0 && renderingInfo->PColorAttachmentFormats is not null)
                        {
                            var formats = new string[colorAttachmentCount];
                            for (int i = 0; i < colorAttachmentCount; i++)
                                formats[i] = renderingInfo->PColorAttachmentFormats[i].ToString();
                            colorFormats = string.Join(",", formats);
                        }
                    }
                }

                Debug.RenderingWarning("[PipeCreate] prog={0} renderPass=0x{1:X} stages={2} stageFlags=[{3}] stageModules=[{4}] colors={5} colorFormats=[{6}] depth={7} stencil={8}",
                    Data.Name ?? "?prog",
                    pipelineInfo.RenderPass.Handle,
                    stages.Length,
                    stageNames,
                    stageModules,
                    colorAttachmentCount,
                    colorFormats,
                    depthFormat,
                    stencilFormat);
                Debug.RenderingWarning("[PipeCreate] prog={0} stageLabels=[{1}]",
                    Data.Name ?? "?prog",
                    DescribeShaderStages());
            }
            // ── END DIAGNOSTIC ──

            fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
            {
                pipelineInfo.StageCount = (uint)stages.Length;
                pipelineInfo.PStages = stagesPtr;
                pipelineInfo.Layout = _pipelineLayout;

                Result result;
                DescriptorHeapProgramLayout? descriptorHeapLayout = _descriptorHeapLayout;
                if (Renderer.IsDescriptorHeapDrawBindingActive)
                {
                    void* originalPipelinePNext = pipelineInfo.PNext;
                    PipelineCreateFlags2CreateInfoNative flags2 = new()
                    {
                        SType = VulkanDescriptorHeapExt.PipelineCreateFlags2CreateInfoSType,
                        PNext = originalPipelinePNext,
                        Flags = unchecked((ulong)pipelineInfo.Flags) | VulkanDescriptorHeapExt.PipelineCreate2DescriptorHeapBit,
                    };
                    pipelineInfo.PNext = &flags2;

                    if (descriptorHeapLayout is { Mappings.Length: > 0 })
                    {
                        fixed (DescriptorSetAndBindingMappingEXTNative* mappingPtr = descriptorHeapLayout.Mappings)
                        {
                            void** originalStagePNext = stackalloc void*[stages.Length];
                            ShaderDescriptorSetAndBindingMappingInfoEXTNative* mappingInfos = stackalloc ShaderDescriptorSetAndBindingMappingInfoEXTNative[stages.Length];
                            for (int i = 0; i < stages.Length; i++)
                            {
                                originalStagePNext[i] = stagesPtr[i].PNext;
                                mappingInfos[i] = new ShaderDescriptorSetAndBindingMappingInfoEXTNative
                                {
                                    SType = VulkanDescriptorHeapExt.ShaderDescriptorSetAndBindingMappingInfoSType,
                                    PNext = originalStagePNext[i],
                                    MappingCount = (uint)descriptorHeapLayout.Mappings.Length,
                                    Mappings = mappingPtr,
                                };
                                stagesPtr[i].PNext = mappingInfos + i;
                            }

                            result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline mappedHeapPipeline);
                            for (int i = 0; i < stages.Length; i++)
                                stagesPtr[i].PNext = originalStagePNext[i];

                            pipelineInfo.PNext = originalPipelinePNext;

                            if (result != Result.Success)
                            {
                                WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                                throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
                            }

                            Renderer.RegisterVulkanPipeline(mappedHeapPipeline, "VkRenderProgram.GraphicsMappedHeap");
                            Renderer.NotifyVulkanPipelineCreated("graphics");
                            return mappedHeapPipeline;
                        }
                    }

                    result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline heapPipeline);
                    pipelineInfo.PNext = originalPipelinePNext;

                    if (result != Result.Success)
                    {
                        WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                        throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
                    }

                    Renderer.RegisterVulkanPipeline(heapPipeline, "VkRenderProgram.GraphicsHeap");
                    Renderer.NotifyVulkanPipelineCreated("graphics");
                    return heapPipeline;
                }

                result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
                if (result != Result.Success)
                {
                    WriteShaderDiagnostics($"vkCreateGraphicsPipelines failed result={result}");
                    throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
                }

                Renderer.RegisterVulkanPipeline(pipeline, "VkRenderProgram.Graphics");
                Renderer.NotifyVulkanPipelineCreated("graphics");
                return pipeline;
            }
        }

        public Pipeline CreateComputePipeline(ref ComputePipelineCreateInfo pipelineInfo, PipelineCache pipelineCache = default)
        {
            if (!Link())
                throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");

            if (pipelineCache.Handle == 0)
                pipelineCache = Renderer.ActivePipelineCache;

            PipelineShaderStageCreateInfo computeStage = GetShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
            if (computeStage.Module.Handle == 0)
                throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");

            pipelineInfo.Stage = computeStage;
            pipelineInfo.Layout = _pipelineLayout;

            Result result;
            DescriptorHeapProgramLayout? descriptorHeapLayout = _descriptorHeapLayout;
            if (Renderer.IsDescriptorHeapDrawBindingActive)
            {
                void* originalPipelinePNext = pipelineInfo.PNext;
                PipelineCreateFlags2CreateInfoNative flags2 = new()
                {
                    SType = VulkanDescriptorHeapExt.PipelineCreateFlags2CreateInfoSType,
                    PNext = originalPipelinePNext,
                    Flags = unchecked((ulong)pipelineInfo.Flags) | VulkanDescriptorHeapExt.PipelineCreate2DescriptorHeapBit,
                };
                pipelineInfo.PNext = &flags2;

                if (descriptorHeapLayout is { Mappings.Length: > 0 })
                {
                    fixed (DescriptorSetAndBindingMappingEXTNative* mappingPtr = descriptorHeapLayout.Mappings)
                    {
                        void* originalStagePNext = pipelineInfo.Stage.PNext;
                        ShaderDescriptorSetAndBindingMappingInfoEXTNative mappingInfo = new()
                        {
                            SType = VulkanDescriptorHeapExt.ShaderDescriptorSetAndBindingMappingInfoSType,
                            PNext = originalStagePNext,
                            MappingCount = (uint)descriptorHeapLayout.Mappings.Length,
                            Mappings = mappingPtr,
                        };
                        pipelineInfo.Stage.PNext = &mappingInfo;
                        result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline mappedHeapPipeline);
                        pipelineInfo.Stage.PNext = originalStagePNext;
                        pipelineInfo.PNext = originalPipelinePNext;
                        if (result != Result.Success)
                            throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

                        Renderer.RegisterVulkanPipeline(mappedHeapPipeline, "VkRenderProgram.ComputeMappedHeap");
                        Renderer.NotifyVulkanPipelineCreated("compute");
                        return mappedHeapPipeline;
                    }
                }

                result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline heapPipeline);
                pipelineInfo.PNext = originalPipelinePNext;
                if (result != Result.Success)
                    throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

                Renderer.RegisterVulkanPipeline(heapPipeline, "VkRenderProgram.ComputeHeap");
                Renderer.NotifyVulkanPipelineCreated("compute");
                return heapPipeline;
            }

            result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            Renderer.RegisterVulkanPipeline(pipeline, "VkRenderProgram.Compute");
            Renderer.NotifyVulkanPipelineCreated("compute");
            return pipeline;
        }

        public ulong ComputeGraphicsPipelineFingerprint()
        {
            VulkanStableHash64 hash = new(schemaVersion: 2);
            hash.Add(CommonPushConstantSize);

            for (int stageIndex = 0; stageIndex < StageOrder.Length; stageIndex++)
            {
                EProgramStageMask flag = StageOrder[stageIndex];
                if ((GraphicsStageMask & flag) == 0)
                    continue;

                if (flag == EProgramStageMask.GeometryShaderBit && !Renderer.SupportsGeometryShader)
                    continue;

                if (!_stageLookup.TryGetValue(flag, out VkShader? shader))
                    continue;

                hash.Add((int)shader.StageFlags);
                hash.Add(shader.LastArtifact?.Identity ?? shader.CompileStatus.ArtifactIdentity ?? shader.StageDebugLabel);
            }

            hash.Add(_descriptorSetLayouts.Length);
            hash.Add((int)Renderer.ActiveDescriptorBackend);
            hash.Add(_descriptorHeapLayout?.PushByteCount ?? 0u);
            // Descriptor layout construction publishes this list in set/binding order.
            for (int i = 0; i < _programDescriptorBindings.Count; i++)
            {
                DescriptorBindingInfo binding = _programDescriptorBindings[i];
                hash.Add(binding.Set);
                hash.Add(binding.Binding);
                hash.Add((int)binding.DescriptorType);
                hash.Add(binding.Count);
                hash.Add((int)binding.StageFlags);
            }

            return hash.Value;
        }

        public ulong ComputeComputePipelineFingerprint()
        {
            VulkanStableHash64 hash = new(schemaVersion: 2);
            hash.Add(CommonPushConstantSize);

            if (_stageLookup.TryGetValue(EProgramStageMask.ComputeShaderBit, out VkShader? shader))
            {
                hash.Add((int)shader.StageFlags);
                hash.Add(shader.LastArtifact?.Identity ?? shader.CompileStatus.ArtifactIdentity ?? shader.StageDebugLabel);
            }

            hash.Add(_descriptorSetLayouts.Length);
            hash.Add((int)Renderer.ActiveDescriptorBackend);
            hash.Add(_descriptorHeapLayout?.PushByteCount ?? 0u);
            // Descriptor layout construction publishes this list in set/binding order.
            for (int i = 0; i < _programDescriptorBindings.Count; i++)
            {
                DescriptorBindingInfo binding = _programDescriptorBindings[i];
                hash.Add(binding.Set);
                hash.Add(binding.Binding);
                hash.Add((int)binding.DescriptorType);
                hash.Add(binding.Count);
                hash.Add((int)binding.StageFlags);
            }

            return hash.Value;
        }

        private string ComputeProgramArtifactFingerprint()
            => $"VKPROG-{ComputeGraphicsPipelineFingerprint():X16}-{ComputeComputePipelineFingerprint():X16}";

        public Pipeline GetOrCreateComputePipeline(
            int passIndex = int.MinValue,
            IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
        {
            if (_computePipeline.Handle != 0)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
                return _computePipeline;
            }

            Renderer.RecordVulkanComputePipelineCacheMiss(
                passIndex,
                passMetadata,
                this,
                ComputeComputePipelineFingerprint());

            ComputePipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.ComputePipelineCreateInfo
            };

            _computePipeline = CreateComputePipeline(ref pipelineInfo, Renderer.ActivePipelineCache);
            return _computePipeline;
        }

        internal bool TryBuildAndBindComputeDescriptorSets(
            CommandBuffer commandBuffer,
            uint imageIndex,
            ComputeDispatchSnapshot snapshot,
            ulong reusableDescriptorBindingKey,
            out DescriptorPool descriptorPool,
            out List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> tempUniformBuffers)
        {
            descriptorPool = default;
            tempUniformBuffers = [];

            if (_descriptorSetLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
                return false;

            Dictionary<DescriptorType, uint> poolSizeCounts = new();
            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                uint count = Math.Max(binding.Count, 1u);
                if (poolSizeCounts.TryGetValue(binding.DescriptorType, out uint existing))
                    poolSizeCounts[binding.DescriptorType] = existing + count;
                else
                    poolSizeCounts[binding.DescriptorType] = count;
            }

            if (poolSizeCounts.Count == 0)
                return false;

            DescriptorPoolSize[] poolSizes = poolSizeCounts
                .Select(p => new DescriptorPoolSize { Type = p.Key, DescriptorCount = p.Value })
                .ToArray();

            List<PendingDescriptorWrite> pendingWrites = [];
            List<DescriptorBufferInfo> bufferInfos = [];
            List<DescriptorImageInfo> imageInfos = [];
            List<BufferView> texelBufferViews = [];
            bool hasUnresolvedBinding = false;

            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                if (binding.Set >= _descriptorSetLayouts.Length)
                    continue;

                uint descriptorCount = Math.Max(binding.Count, 1u);
                switch (binding.DescriptorType)
                {
                    case DescriptorType.UniformBuffer:
                    case DescriptorType.StorageBuffer:
                        if (!TryResolveComputeBuffer(binding, imageIndex, snapshot, out DescriptorBufferInfo bufferInfo))
                        {
                            hasUnresolvedBinding = true;
                            WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                            RecordComputeDescriptorFailure(binding, "buffer resolution failed", skippedDispatch: true);
                            continue;
                        }

                        int bufferStart = bufferInfos.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            bufferInfos.Add(bufferInfo);

                        pendingWrites.Add(PendingDescriptorWrite.Buffer(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, bufferStart));
                        break;

                    case DescriptorType.CombinedImageSampler:
                    case DescriptorType.SampledImage:
                    case DescriptorType.Sampler:
                    case DescriptorType.StorageImage:
                        if (!TryResolveComputeImage(binding, snapshot, out DescriptorImageInfo imageInfo))
                        {
                            hasUnresolvedBinding = true;
                            WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} image binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                            RecordComputeDescriptorFailure(binding, "image resolution failed", skippedDispatch: true);
                            continue;
                        }

                        int imageStart = imageInfos.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            imageInfos.Add(imageInfo);

                        pendingWrites.Add(PendingDescriptorWrite.Image(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, imageStart));
                        break;

                    case DescriptorType.UniformTexelBuffer:
                    case DescriptorType.StorageTexelBuffer:
                        if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView texelView))
                        {
                            hasUnresolvedBinding = true;
                            WarnComputeOnce($"Skipping unresolved {binding.DescriptorType} texel binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}). Compute dispatch will be skipped.");
                            RecordComputeDescriptorFailure(binding, "texel buffer resolution failed", skippedDispatch: true);
                            continue;
                        }

                        int texelStart = texelBufferViews.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            texelBufferViews.Add(texelView);

                        pendingWrites.Add(PendingDescriptorWrite.Texel(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, texelStart));
                        break;
                }
            }

            if (hasUnresolvedBinding)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                    Data.Name,
                    "descriptor-set",
                    "<compute-required-binding>",
                    0,
                    0,
                    skippedDraw: false,
                    skippedDispatch: true,
                    "compute descriptor build had unresolved required bindings");
                return false;
            }

            if (pendingWrites.Count == 0)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                    Data.Name,
                    "descriptor-set",
                    "<none>",
                    0,
                    0,
                    skippedDraw: false,
                    skippedDispatch: true,
                    "compute descriptor build produced no writes");
                return false;
            }

            PendingDescriptorWrite[] pendingWriteArray = pendingWrites.ToArray();
            DescriptorBufferInfo[] bufferArray = bufferInfos.ToArray();
            DescriptorImageInfo[] imageArray = imageInfos.ToArray();
            BufferView[] texelArray = texelBufferViews.ToArray();

            if (Renderer.IsDescriptorHeapDrawBindingActive)
            {
                DescriptorHeapPushDataPayload payload = Renderer.CreateDescriptorHeapPushDataPayload(_descriptorHeapLayout);
                fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
                fixed (DescriptorImageInfo* imagePtr = imageArray)
                fixed (BufferView* texelPtr = texelArray)
                {
                    for (int i = 0; i < pendingWriteArray.Length; i++)
                    {
                        PendingDescriptorWrite pending = pendingWriteArray[i];
                        DescriptorBindingInfo binding = FindDescriptorBinding(pending.Set, pending.Binding, pending.DescriptorType);
                        bool wrote;
                        string heapReason;
                        switch (pending.Source)
                        {
                            case PendingDescriptorSource.Buffer:
                                wrote = Renderer.TryWriteDescriptorHeapBinding(this, binding, payload, bufferPtr + pending.SourceStartIndex, null, null, pending.DescriptorCount, out heapReason);
                                break;
                            case PendingDescriptorSource.Image:
                                wrote = Renderer.TryWriteDescriptorHeapBinding(this, binding, payload, null, imagePtr + pending.SourceStartIndex, null, pending.DescriptorCount, out heapReason);
                                break;
                            case PendingDescriptorSource.TexelBuffer:
                                wrote = Renderer.TryWriteDescriptorHeapBinding(this, binding, payload, null, null, texelPtr + pending.SourceStartIndex, pending.DescriptorCount, out heapReason);
                                break;
                            default:
                                wrote = false;
                                heapReason = "unsupported compute descriptor source.";
                                break;
                        }

                        if (!wrote)
                        {
                            RecordComputeDescriptorFailure(binding, $"descriptor heap write failed: {heapReason}", skippedDispatch: true);
                            return false;
                        }
                    }

                    if (!Renderer.TryPushDescriptorHeapProgramData(commandBuffer, this, payload, out string pushReason))
                    {
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                            Data.Name,
                            "descriptor-heap",
                            "<compute-push>",
                            0,
                            0,
                            skippedDraw: false,
                            skippedDispatch: true,
                            pushReason);
                        return false;
                    }
                }

                return true;
            }

            bool cacheable = tempUniformBuffers.Count == 0;
            DescriptorSet[] descriptorSets;
            bool shouldUpdateDescriptorData = true;

            if (cacheable)
            {
                ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
                ulong bindingFingerprint = ComputeComputeDescriptorBindingFingerprint(pendingWriteArray, bufferArray, imageArray, texelArray);
                ulong cacheBindingFingerprint = reusableDescriptorBindingKey == 0UL ? bindingFingerprint : reusableDescriptorBindingKey;
                DescriptorSetLayout[] layoutArray = _descriptorSetLayouts.ToArray();

                if (!Renderer.TryGetOrCreateComputeDescriptorSets(
                    imageIndex,
                    schemaFingerprint,
                    cacheBindingFingerprint,
                    layoutArray,
                    poolSizes,
                    _descriptorSetsRequireUpdateAfterBind,
                    out descriptorSets,
                    out bool isNewAllocation))
                {
                    WarnComputeOnce("Failed to acquire cached Vulkan compute descriptor sets.");
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                        Data.Name,
                        "descriptor-set",
                        "<cached-compute>",
                        0,
                        0,
                        skippedDraw: false,
                        skippedDispatch: true,
                        "failed to acquire cached compute descriptor sets");
                    return false;
                }

                shouldUpdateDescriptorData = isNewAllocation || reusableDescriptorBindingKey != 0UL;
            }
            else
            {
                if (!Renderer.TryAllocateTransientComputeDescriptorSets(
                    imageIndex,
                    _descriptorSetLayouts,
                    poolSizes,
                    _descriptorSetsRequireUpdateAfterBind,
                    out descriptorSets))
                {
                    WarnComputeOnce("Failed to allocate transient Vulkan compute descriptor sets.");
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                        Data.Name,
                        "descriptor-set",
                        "<transient-compute>",
                        0,
                        0,
                        skippedDraw: false,
                        skippedDispatch: true,
                        "failed to allocate transient compute descriptor sets");
                    return false;
                }
            }

            if (shouldUpdateDescriptorData)
                UpdateComputeDescriptorSets(descriptorSets, pendingWriteArray, bufferArray, imageArray, texelArray);

            Renderer.BindDescriptorSetsTracked(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                descriptorSets);

            return true;
        }

        private void UpdateComputeDescriptorSets(
            DescriptorSet[] descriptorSets,
            PendingDescriptorWrite[] pendingWrites,
            DescriptorBufferInfo[] bufferArray,
            DescriptorImageInfo[] imageArray,
            BufferView[] texelArray)
        {
            WriteDescriptorSet[] writeArray = new WriteDescriptorSet[pendingWrites.Length];
            for (int i = 0; i < pendingWrites.Length; i++)
            {
                PendingDescriptorWrite pending = pendingWrites[i];
                writeArray[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSets[pending.Set],
                    DstBinding = pending.Binding,
                    DescriptorCount = pending.DescriptorCount,
                    DescriptorType = pending.DescriptorType
                };
            }

            fixed (WriteDescriptorSet* writePtr = writeArray)
            fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
            fixed (DescriptorImageInfo* imagePtr = imageArray)
            fixed (BufferView* texelPtr = texelArray)
            {
                for (int i = 0; i < pendingWrites.Length; i++)
                {
                    PendingDescriptorWrite pending = pendingWrites[i];
                    switch (pending.Source)
                    {
                        case PendingDescriptorSource.Buffer:
                            writePtr[i].PBufferInfo = bufferPtr + pending.SourceStartIndex;
                            break;
                        case PendingDescriptorSource.Image:
                            writePtr[i].PImageInfo = imagePtr + pending.SourceStartIndex;
                            break;
                        case PendingDescriptorSource.TexelBuffer:
                            writePtr[i].PTexelBufferView = texelPtr + pending.SourceStartIndex;
                            break;
                    }
                }

                if (!TryUpdateComputeDescriptorSetsWithTemplates(descriptorSets, writeArray))
                    Renderer.UpdateDescriptorSetsTracked((uint)writeArray.Length, writePtr);
                Renderer.RecordVulkanDescriptorTableGeneration("ComputeDescriptorSets.Update");
            }
        }

        private static PushConstantRange CreateCommonPushConstantRange()
            => new()
            {
                StageFlags = CommonPushConstantStageFlags,
                Offset = 0,
                Size = CommonPushConstantSize
            };

        private bool TryUpdateComputeDescriptorSetsWithTemplates(DescriptorSet[] descriptorSets, WriteDescriptorSet[] writeArray)
        {
            if (RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template)
                return false;

            if (_descriptorSetLayouts.Length < descriptorSets.Length)
                return false;

            for (int setIndex = 0; setIndex < descriptorSets.Length; setIndex++)
            {
                List<WriteDescriptorSet> setWrites = [];
                for (int i = 0; i < writeArray.Length; i++)
                {
                    if (writeArray[i].DstSet.Handle == descriptorSets[setIndex].Handle)
                        setWrites.Add(writeArray[i]);
                }

                if (setWrites.Count == 0)
                    continue;

                if (!Renderer.TryUpdateDescriptorSetWithTemplate(
                    descriptorSets[setIndex],
                    _descriptorSetLayouts[setIndex],
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    (uint)setIndex,
                    CollectionsMarshal.AsSpan(setWrites)))
                {
                    return false;
                }
            }

            return true;
        }

        private ulong ComputeComputeDescriptorSchemaFingerprint()
        {
            ulong hash = 1469598103934665603UL;

            static void Mix(ref ulong value, ulong part)
            {
                value ^= part;
                value *= 1099511628211UL;
            }

            foreach (DescriptorBindingInfo binding in _programDescriptorBindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
            {
                Mix(ref hash, binding.Set);
                Mix(ref hash, binding.Binding);
                Mix(ref hash, (ulong)binding.DescriptorType);
                Mix(ref hash, binding.Count);
                Mix(ref hash, (ulong)binding.StageFlags);
            }

            foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                Mix(ref hash, layout.Handle);

            return hash;
        }

        private static ulong ComputeComputeDescriptorBindingFingerprint(
            PendingDescriptorWrite[] writes,
            DescriptorBufferInfo[] buffers,
            DescriptorImageInfo[] images,
            BufferView[] texelViews)
        {
            ulong hash = 1469598103934665603UL;

            static void Mix(ref ulong value, ulong part)
            {
                value ^= part;
                value *= 1099511628211UL;
            }

            foreach (PendingDescriptorWrite write in writes)
            {
                Mix(ref hash, write.Set);
                Mix(ref hash, write.Binding);
                Mix(ref hash, (ulong)write.DescriptorType);
                Mix(ref hash, write.DescriptorCount);
                Mix(ref hash, (ulong)write.Source);

                for (uint i = 0; i < write.DescriptorCount; i++)
                {
                    int index = write.SourceStartIndex + (int)i;
                    switch (write.Source)
                    {
                        case PendingDescriptorSource.Buffer:
                        {
                            DescriptorBufferInfo info = buffers[index];
                            Mix(ref hash, info.Buffer.Handle);
                            Mix(ref hash, info.Offset);
                            Mix(ref hash, info.Range);
                            break;
                        }
                        case PendingDescriptorSource.Image:
                        {
                            DescriptorImageInfo info = images[index];
                            Mix(ref hash, info.ImageView.Handle);
                            Mix(ref hash, info.Sampler.Handle);
                            Mix(ref hash, (ulong)info.ImageLayout);
                            break;
                        }
                        case PendingDescriptorSource.TexelBuffer:
                        {
                            BufferView view = texelViews[index];
                            Mix(ref hash, view.Handle);
                            break;
                        }
                    }
                }
            }

            return hash;
        }

        private enum PendingDescriptorSource : byte
        {
            Buffer,
            Image,
            TexelBuffer
        }

        private readonly record struct PendingDescriptorWrite(
            uint Set,
            uint Binding,
            DescriptorType DescriptorType,
            uint DescriptorCount,
            PendingDescriptorSource Source,
            int SourceStartIndex)
        {
            public static PendingDescriptorWrite Buffer(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
                => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.Buffer, sourceStartIndex);

            public static PendingDescriptorWrite Image(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
                => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.Image, sourceStartIndex);

            public static PendingDescriptorWrite Texel(uint set, uint binding, DescriptorType descriptorType, uint descriptorCount, int sourceStartIndex)
                => new(set, binding, descriptorType, descriptorCount, PendingDescriptorSource.TexelBuffer, sourceStartIndex);
        }

        private DescriptorBindingInfo FindDescriptorBinding(uint set, uint binding, DescriptorType descriptorType)
        {
            for (int i = 0; i < _programDescriptorBindings.Count; i++)
            {
                DescriptorBindingInfo candidate = _programDescriptorBindings[i];
                if (candidate.Set == set && candidate.Binding == binding)
                    return candidate;
            }

            return new DescriptorBindingInfo(set, binding, descriptorType, ShaderStageFlags.ComputeBit, 1u, string.Empty);
        }

        private static string GetDescriptorBindingClass(DescriptorType descriptorType)
            => descriptorType switch
            {
                DescriptorType.StorageImage => "storage-image",
                DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => "uniform-buffer",
                DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => "storage-buffer",
                DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer => "texel-buffer",
                _ => "sampled-image",
            };

        private void RecordComputeDescriptorFallback(DescriptorBindingInfo binding, int count = 1)
            => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
                Data.Name,
                GetDescriptorBindingClass(binding.DescriptorType),
                binding.Name,
                binding.Set,
                binding.Binding,
                count);

        private void RecordComputeDescriptorFailure(DescriptorBindingInfo binding, string reason, bool skippedDispatch)
            => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
                Data.Name,
                GetDescriptorBindingClass(binding.DescriptorType),
                binding.Name,
                binding.Set,
                binding.Binding,
                skippedDraw: false,
                skippedDispatch,
                reason);

        private bool TryResolveComputeBuffer(
            DescriptorBindingInfo binding,
            uint imageIndex,
            ComputeDispatchSnapshot snapshot,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;

            if (snapshot.Buffers.TryGetValue(binding.Binding, out VulkanComputeBufferBinding boundBuffer))
                return TryCreateDescriptorBufferInfo(binding, boundBuffer, out bufferInfo);

            if (binding.DescriptorType == DescriptorType.UniformBuffer &&
                TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
            {
                if (TryGetOrUpdateComputeAutoUniformBuffer(imageIndex, binding, snapshot, block, out bufferInfo))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(binding.Name))
            {
                if (snapshot.BuffersByName.TryGetValue(binding.Name, out VulkanComputeBufferBinding namedBuffer) &&
                    TryCreateDescriptorBufferInfo(binding, namedBuffer, out bufferInfo))
                {
                    return true;
                }
            }

            if (binding.DescriptorType == DescriptorType.UniformBuffer &&
                TryGetOrUpdateComputeFallbackUniformBuffer(imageIndex, binding, out bufferInfo))
            {
                RecordComputeDescriptorFallback(binding);
                return true;
            }

            return false;
        }

        private bool TryGetOrUpdateComputeFallbackUniformBuffer(
            uint imageIndex,
            DescriptorBindingInfo binding,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;

            const uint fallbackSize = 4096u;
            ComputeUniformBufferKey key = new(
                EComputeUniformBufferKind.Fallback,
                imageIndex,
                binding.Set,
                binding.Binding,
                binding.Name ?? string.Empty);

            if (!TryGetOrCreateComputeUniformBuffer(key, fallbackSize, out ComputeUniformBuffer resource, out bool created))
                return false;

            if (created && !ClearComputeUniformBuffer(resource, fallbackSize))
            {
                _computeUniformBuffers.Remove(key);
                ReleaseComputeUniformBuffer(resource);
                return false;
            }

            bufferInfo = new DescriptorBufferInfo
            {
                Buffer = resource.Buffer,
                Offset = 0,
                Range = fallbackSize
            };

            WarnComputeOnce($"Using zero-filled cached fallback uniform buffer for unresolved binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
            return true;
        }

        private bool TryGetOrUpdateComputeAutoUniformBuffer(
            uint imageIndex,
            DescriptorBindingInfo binding,
            ComputeDispatchSnapshot snapshot,
            AutoUniformBlockInfo block,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;

            uint size = Math.Max(block.Size, 1u);
            ComputeUniformBufferKey key = new(
                EComputeUniformBufferKind.Auto,
                imageIndex,
                binding.Set,
                binding.Binding,
                block.InstanceName);

            if (!TryGetOrCreateComputeUniformBuffer(key, size, out ComputeUniformBuffer resource, out _))
                return false;

            if (!TryWriteComputeAutoUniformBuffer(resource, size, snapshot, block))
                return false;

            bufferInfo = new DescriptorBufferInfo
            {
                Buffer = resource.Buffer,
                Offset = 0,
                Range = size
            };

            return true;
        }

        internal bool TryRefreshReusableComputeDispatchFrameData(uint imageIndex, ComputeDispatchSnapshot snapshot, ulong reusableDescriptorBindingKey)
        {
            if (_descriptorSetLayouts.Length == 0 || _programDescriptorBindings.Count == 0)
                return true;

            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                if (binding.Set >= _descriptorSetLayouts.Length ||
                    binding.DescriptorType != DescriptorType.UniformBuffer)
                {
                    continue;
                }

                bool hasSnapshotBuffer = snapshot.Buffers.ContainsKey(binding.Binding);
                if (!hasSnapshotBuffer)
                    hasSnapshotBuffer = SnapshotContainsNamedBuffer(snapshot, binding.Name);
                if (hasSnapshotBuffer)
                    continue;

                if (TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block))
                {
                    if (!TryUpdateExistingComputeAutoUniformBuffer(imageIndex, binding, snapshot, block))
                        return false;
                    continue;
                }

                if (!HasExistingComputeFallbackUniformBuffer(imageIndex, binding))
                    return false;
            }

            return TryRefreshReusableComputeDescriptorSets(imageIndex, snapshot, reusableDescriptorBindingKey);
        }

        private bool TryRefreshReusableComputeDescriptorSets(uint imageIndex, ComputeDispatchSnapshot snapshot, ulong reusableDescriptorBindingKey)
        {
            if (reusableDescriptorBindingKey == 0UL)
                return true;

            (uint ImageIndex, ulong BindingKey) refreshKey = (imageIndex, reusableDescriptorBindingKey);
            if (_reusableComputeDescriptorRefreshKeys.Contains(refreshKey))
                return true;

            Dictionary<DescriptorType, uint> poolSizeCounts = new();
            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                uint count = Math.Max(binding.Count, 1u);
                if (poolSizeCounts.TryGetValue(binding.DescriptorType, out uint existing))
                    poolSizeCounts[binding.DescriptorType] = existing + count;
                else
                    poolSizeCounts[binding.DescriptorType] = count;
            }

            if (poolSizeCounts.Count == 0)
                return true;

            DescriptorPoolSize[] poolSizes = poolSizeCounts
                .Select(p => new DescriptorPoolSize { Type = p.Key, DescriptorCount = p.Value })
                .ToArray();

            List<PendingDescriptorWrite> pendingWrites = [];
            List<DescriptorBufferInfo> bufferInfos = [];
            List<DescriptorImageInfo> imageInfos = [];
            List<BufferView> texelBufferViews = [];
            bool hasUnresolvedBinding = false;

            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                if (binding.Set >= _descriptorSetLayouts.Length)
                    continue;

                uint descriptorCount = Math.Max(binding.Count, 1u);
                switch (binding.DescriptorType)
                {
                    case DescriptorType.UniformBuffer:
                    case DescriptorType.StorageBuffer:
                        if (!TryResolveComputeBuffer(binding, imageIndex, snapshot, out DescriptorBufferInfo bufferInfo))
                        {
                            hasUnresolvedBinding = true;
                            RecordComputeDescriptorFailure(binding, "buffer refresh failed", skippedDispatch: true);
                            continue;
                        }

                        int bufferStart = bufferInfos.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            bufferInfos.Add(bufferInfo);

                        pendingWrites.Add(PendingDescriptorWrite.Buffer(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, bufferStart));
                        break;

                    case DescriptorType.CombinedImageSampler:
                    case DescriptorType.SampledImage:
                    case DescriptorType.Sampler:
                    case DescriptorType.StorageImage:
                        if (!TryResolveComputeImage(binding, snapshot, out DescriptorImageInfo imageInfo))
                        {
                            hasUnresolvedBinding = true;
                            RecordComputeDescriptorFailure(binding, "image refresh failed", skippedDispatch: true);
                            continue;
                        }

                        int imageStart = imageInfos.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            imageInfos.Add(imageInfo);

                        pendingWrites.Add(PendingDescriptorWrite.Image(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, imageStart));
                        break;

                    case DescriptorType.UniformTexelBuffer:
                    case DescriptorType.StorageTexelBuffer:
                        if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView texelView))
                        {
                            hasUnresolvedBinding = true;
                            RecordComputeDescriptorFailure(binding, "texel refresh failed", skippedDispatch: true);
                            continue;
                        }

                        int texelStart = texelBufferViews.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            texelBufferViews.Add(texelView);

                        pendingWrites.Add(PendingDescriptorWrite.Texel(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, texelStart));
                        break;
                }
            }

            if (hasUnresolvedBinding || pendingWrites.Count == 0)
                return false;

            ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
            DescriptorSetLayout[] layoutArray = _descriptorSetLayouts.ToArray();
            if (!Renderer.TryGetOrCreateComputeDescriptorSets(
                imageIndex,
                schemaFingerprint,
                reusableDescriptorBindingKey,
                layoutArray,
                poolSizes,
                _descriptorSetsRequireUpdateAfterBind,
                out DescriptorSet[] descriptorSets,
                out _))
            {
                return false;
            }

            UpdateComputeDescriptorSets(
                descriptorSets,
                pendingWrites.ToArray(),
                bufferInfos.ToArray(),
                imageInfos.ToArray(),
                texelBufferViews.ToArray());
            _reusableComputeDescriptorRefreshKeys.Add(refreshKey);
            return true;
        }

        private static bool SnapshotContainsNamedBuffer(ComputeDispatchSnapshot snapshot, string? bindingName)
            => !string.IsNullOrWhiteSpace(bindingName) && snapshot.BuffersByName.ContainsKey(bindingName);

        private bool TryUpdateExistingComputeAutoUniformBuffer(
            uint imageIndex,
            DescriptorBindingInfo binding,
            ComputeDispatchSnapshot snapshot,
            AutoUniformBlockInfo block)
        {
            uint size = Math.Max(block.Size, 1u);
            ComputeUniformBufferKey key = new(
                EComputeUniformBufferKind.Auto,
                imageIndex,
                binding.Set,
                binding.Binding,
                block.InstanceName);

            if (!_computeUniformBuffers.TryGetValue(key, out ComputeUniformBuffer resource) ||
                resource.Buffer.Handle == 0 ||
                resource.Size < size)
            {
                return false;
            }

            return TryWriteComputeAutoUniformBuffer(resource, size, snapshot, block);
        }

        private bool HasExistingComputeFallbackUniformBuffer(uint imageIndex, DescriptorBindingInfo binding)
        {
            const uint fallbackSize = 4096u;
            ComputeUniformBufferKey key = new(
                EComputeUniformBufferKind.Fallback,
                imageIndex,
                binding.Set,
                binding.Binding,
                binding.Name ?? string.Empty);

            return _computeUniformBuffers.TryGetValue(key, out ComputeUniformBuffer resource) &&
                resource.Buffer.Handle != 0 &&
                resource.Size >= fallbackSize;
        }

        private bool TryWriteComputeAutoUniformBuffer(
            ComputeUniformBuffer resource,
            uint size,
            ComputeDispatchSnapshot snapshot,
            AutoUniformBlockInfo block)
        {
            if (resource.Mapped == null || resource.Size < size)
                return false;

            Span<byte> data = new(resource.Mapped, (int)size);
            data.Clear();

            IReadOnlyList<AutoUniformMember> members = block.Members;
            for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
                TryWriteAutoUniformMember(data, members[memberIndex], snapshot);

            return true;
        }

        private bool TryGetOrCreateComputeUniformBuffer(
            ComputeUniformBufferKey key,
            uint size,
            out ComputeUniformBuffer resource,
            out bool created)
        {
            created = false;
            size = Math.Max(size, 1u);

            if (_computeUniformBuffers.TryGetValue(key, out resource) &&
                resource.Buffer.Handle != 0 &&
                resource.Size >= size)
            {
                return true;
            }

            if (resource.Buffer.Handle != 0 || resource.Memory.Handle != 0)
                ReleaseComputeUniformBuffer(resource);

            (Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory) = Renderer.CreateBuffer(
                size,
                Renderer.IsDescriptorHeapDrawBindingActive
                    ? BufferUsageFlags.UniformBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
                    : BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null,
                Renderer.IsDescriptorHeapDrawBindingActive);

            if (buffer.Handle == 0 || memory.Handle == 0)
            {
                resource = default;
                return false;
            }

            if (!Renderer.TryMapBufferMemory(buffer, memory, 0, size, out void* mapped))
            {
                Renderer.RetireBuffer(buffer, memory);
                resource = default;
                return false;
            }

            resource = new ComputeUniformBuffer(buffer, memory, size, mapped);
            _computeUniformBuffers[key] = resource;
            created = true;
            return true;
        }

        private bool ClearComputeUniformBuffer(ComputeUniformBuffer resource, uint size)
        {
            if (resource.Mapped == null || resource.Size < size)
                return false;

            Span<byte> data = new(resource.Mapped, (int)size);
            data.Clear();
            return true;
        }

        private bool TryCreateDescriptorBufferInfo(
            DescriptorBindingInfo binding,
            XRDataBuffer dataBuffer,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;
            bool allowSynchronousBufferUpload = Renderer.AllowSynchronousResourceUploads;
            if (Renderer.GetOrCreateAPIRenderObject(dataBuffer, generateNow: allowSynchronousBufferUpload) is not VkDataBuffer vkBuffer)
                return false;

            if (!vkBuffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload))
                return false;

            if (vkBuffer.BufferHandle is not { } handle || handle.Handle == 0)
                return false;

            ulong requestedRange = Math.Max((ulong)dataBuffer.Length, 1UL);
            if (vkBuffer.AllocatedByteSize < requestedRange)
            {
                if (!allowSynchronousBufferUpload)
                    return false;

                vkBuffer.PushData();
                handle = vkBuffer.BufferHandle ?? default;
            }

            if (handle.Handle == 0 || vkBuffer.AllocatedByteSize < requestedRange)
                return false;

            if (!vkBuffer.SupportsDescriptorType(binding.DescriptorType))
            {
                WarnComputeOnce(
                    $"Skipping Vulkan compute binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}) because buffer '{dataBuffer.AttributeName}' was created for {dataBuffer.Target}/{vkBuffer.LastUsageFlags}, not {binding.DescriptorType}. Compute dispatch will be skipped.");
                return false;
            }

            bufferInfo = new DescriptorBufferInfo
            {
                Buffer = handle,
                Offset = 0,
                Range = requestedRange
            };
            return true;
        }

        private bool TryCreateDescriptorBufferInfo(
            DescriptorBindingInfo binding,
            VulkanComputeBufferBinding snapshot,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;
            if (snapshot.Buffer.Handle == 0 || snapshot.Range == 0)
                return false;

            if (!VkDataBuffer.SupportsDescriptorType(binding.DescriptorType, snapshot.UsageFlags))
                return false;

            bufferInfo = new DescriptorBufferInfo
            {
                Buffer = snapshot.Buffer,
                Offset = 0,
                Range = snapshot.Range
            };
            return true;
        }

        private bool TryResolveComputeImage(DescriptorBindingInfo binding, ComputeDispatchSnapshot snapshot, out DescriptorImageInfo imageInfo)
        {
            imageInfo = default;

            if (binding.DescriptorType == DescriptorType.StorageImage)
            {
                if (!snapshot.Images.TryGetValue(binding.Binding, out ProgramImageBinding imageBinding))
                    return false;

                if (!TryResolveTextureDescriptor(binding, imageBinding.Texture, includeSampler: false, requiresSampledUsage: false, requiresStorageUsage: true, ImageLayout.General, out imageInfo))
                    return false;

                return true;
            }

            if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
            {
                // Fallback for shaders that only bind a single sampler but use non-zero binding in source.
                texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
                if (texture is null)
                    return false;

                WarnComputeOnce($"Image binding {binding.Binding} ('{binding.Name}') not found in snapshot; using only available sampler '{texture.Name ?? "<unnamed>"}' as fallback.");
                RecordComputeDescriptorFallback(binding);
            }

            bool includeSampler = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;
            bool requiresSampledUsage = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage;
            return TryResolveTextureDescriptor(binding, texture, includeSampler, requiresSampledUsage, requiresStorageUsage: false, ImageLayout.ShaderReadOnlyOptimal, out imageInfo);
        }

        private bool TryResolveComputeTexelBuffer(DescriptorBindingInfo binding, ComputeDispatchSnapshot snapshot, out BufferView texelView)
        {
            texelView = default;

            if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
            {
                texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
                if (texture is null)
                    return false;

                WarnComputeOnce($"Texel binding {binding.Binding} ('{binding.Name}') not found in snapshot; using only available sampler '{texture.Name ?? "<unnamed>"}' as fallback.");
                RecordComputeDescriptorFallback(binding);
            }

            return TryResolveTexelBufferDescriptor(texture, out texelView);
        }

        private bool TryResolveTextureDescriptor(DescriptorBindingInfo binding, XRTexture texture, bool includeSampler, bool requiresSampledUsage, bool requiresStorageUsage, ImageLayout layout, out DescriptorImageInfo imageInfo)
        {
            imageInfo = default;
            if (texture is null)
                return false;

            bool allowSynchronousTextureUpload = Renderer.AllowSynchronousResourceUploads;
            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryEnsureDescriptorReadyForUse($"compute descriptor '{binding.Name}'", allowSynchronousTextureUpload))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Descriptor.TextureNotReady.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping descriptor bind for texture '{0}' because its Vulkan descriptor source is not ready.",
                    texture.Name ?? texture.GetDescribingName());
                return false;
            }

            if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Descriptor.NoSampledUsage.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping sampled descriptor bind for texture '{0}' (usage={1}) because VK_IMAGE_USAGE_SAMPLED_BIT is not set.",
                    texture.Name ?? texture.GetDescribingName(),
                    source.DescriptorUsage);
                return false;
            }

            if (requiresStorageUsage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Descriptor.NoStorageUsage.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping storage descriptor bind for texture '{0}' (usage={1}) because VK_IMAGE_USAGE_STORAGE_BIT is not set.",
                    texture.Name ?? texture.GetDescribingName(),
                    source.DescriptorUsage);
                return false;
            }

            ImageView descriptorView = source.DescriptorView;
            ImageAspectFlags descriptorAspect = source.DescriptorAspect;
            if (IsCombinedDepthStencilFormat(source.DescriptorFormat) &&
                (descriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
            {
                // Descriptor bindings for depth-stencil images must target a single aspect view.
                // Request a depth-only view instead of skipping the bind entirely.
                ImageView depthOnlyView = source.GetDepthOnlyDescriptorView();
                if (depthOnlyView.Handle != 0)
                {
                    descriptorView = depthOnlyView;
                    descriptorAspect = ImageAspectFlags.DepthBit;
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Descriptor.DepthStencilCombinedAspect.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Skipping descriptor bind for texture '{0}' because no depth-only view is available.",
                        texture.Name ?? texture.GetDescribingName());
                    return false;
                }
            }

            if (!Renderer.IsLiveImageViewBackedByLiveImage(descriptorView))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Descriptor.RetiredImageView.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping descriptor bind for texture '{0}' because its Vulkan image view has been retired.",
                    texture.Name ?? texture.GetDescribingName());
                return false;
            }

            if (!TryResolveComputeDescriptorSampler(includeSampler, binding, source, out Sampler sampler))
                return false;

            ImageLayout descriptorLayout = Renderer.ResolveDescriptorImageLayout(
                source,
                requiresStorageUsage ? DescriptorType.StorageImage : DescriptorType.SampledImage);

            imageInfo = new DescriptorImageInfo
            {
                ImageLayout = descriptorLayout,
                ImageView = descriptorView,
                Sampler = sampler
            };
            return imageInfo.ImageView.Handle != 0;
        }

        private bool TryResolveComputeDescriptorSampler(bool includeSampler, DescriptorBindingInfo binding, IVkImageDescriptorSource source, out Sampler sampler)
        {
            sampler = default;
            if (!includeSampler)
                return true;

            sampler = source.DescriptorSampler;
            if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
                return true;

            if (sampler.Handle != 0)
            {
                WarnComputeOnce($"Compute texture for binding '{binding.Name}' references a retired Vulkan sampler. Using placeholder sampler.");
                RecordComputeDescriptorFallback(binding);
            }

            sampler = Renderer.GetPlaceholderSampler();
            if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
            {
                WarnComputeOnce($"Compute texture for binding '{binding.Name}' has no Vulkan sampler. Using placeholder sampler.");
                RecordComputeDescriptorFallback(binding);
                return true;
            }

            WarnComputeOnce($"Compute texture for binding '{binding.Name}' has no Vulkan sampler and placeholder sampler is unavailable.");
            RecordComputeDescriptorFailure(binding, "texture sampler unavailable", skippedDispatch: false);
            return false;
        }

        private static bool IsCombinedDepthStencilFormat(Format format)
            => format is Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint;

        private bool TryResolveTexelBufferDescriptor(XRTexture texture, out BufferView texelView)
        {
            texelView = default;
            if (texture is null)
                return false;

            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
                return false;

            texelView = source.DescriptorBufferView;
            return texelView.Handle != 0;
        }

        private bool TryWriteAutoUniformMember(Span<byte> destination, AutoUniformMember member, ComputeDispatchSnapshot snapshot)
        {
            if (member.Offset >= (uint)destination.Length)
                return false;

            if (snapshot.Uniforms.TryGetValue(member.Name, out ProgramUniformValue value))
                return TryWriteUniformValue(destination, member, value);

            if (TryResolveEngineUniform(member.Name, out ProgramUniformValue engineValue))
                return TryWriteUniformValue(destination, member, engineValue);

            if (member.DefaultValue is AutoUniformDefaultValue defaultValue)
            {
                ProgramUniformValue val = new(defaultValue.Type, defaultValue.Value, false);
                return TryWriteUniformValue(destination, member, val);
            }

            if (member.DefaultArrayValues is { Count: > 0 } defaults)
            {
                if (!member.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0)
                    return false;

                int count = Math.Min(defaults.Count, (int)member.ArrayLength);
                for (int i = 0; i < count; i++)
                {
                    AutoUniformDefaultValue defaultElement = defaults[i];
                    uint offset = member.Offset + (uint)i * member.ArrayStride;
                    TryWriteSingleUniform(destination, offset, defaultElement.Type, defaultElement.Value);
                }

                return true;
            }

            return false;
        }

        private bool TryResolveEngineUniform(string name, out ProgramUniformValue value)
        {
            value = default;

            ReadOnlySpan<char> uniform = name.AsSpan();
            const string vertexStageSuffix = "_VTX";
            if (uniform.EndsWith(vertexStageSuffix, StringComparison.Ordinal))
                uniform = uniform[..^vertexStageSuffix.Length];

            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
            XRCamera? rightCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
            bool stereo = RuntimeEngine.Rendering.State.IsStereoPass;
            var area = RuntimeEngine.Rendering.State.RenderArea;

            switch (uniform)
            {
                case nameof(EEngineUniform.UpdateDelta):
                    value = new ProgramUniformValue(EShaderVarType._float, RuntimeEngine.Time.Timer.Update.Delta, false);
                    return true;
                case nameof(EEngineUniform.ViewMatrix):
                case nameof(EEngineUniform.LeftEyeViewMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.PrevViewMatrix):
                case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalViewData) && temporalViewData.HistoryReady
                            ? temporalViewData.PrevViewMatrix
                            : camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.RightEyeViewMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.PrevRightEyeViewMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightViewData) && temporalRightViewData.HistoryReady
                            ? temporalRightViewData.RightEyePrevViewMatrix
                            : rightCamera?.Transform.InverseRenderMatrix ?? camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.InverseViewMatrix):
                case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.RightEyeInverseViewMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.Transform.RenderMatrix ?? camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.InverseProjMatrix):
                case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        camera?.InverseProjectionMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.RightEyeInverseProjMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        rightCamera?.InverseProjectionMatrix ?? camera?.InverseProjectionMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.ViewProjectionMatrix):
                case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ViewProjectionMatrix ?? camera?.ViewProjectionMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.ProjMatrix):
                case nameof(EEngineUniform.LeftEyeProjMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.PrevProjMatrix):
                case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalProjectionData) && temporalProjectionData.HistoryReady
                            ? temporalProjectionData.PrevProjection
                            : camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.RightEyeProjMatrix):
                    value = new ProgramUniformValue(EShaderVarType._mat4, rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case nameof(EEngineUniform.PrevRightEyeProjMatrix):
                    value = new ProgramUniformValue(
                        EShaderVarType._mat4,
                        VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalRightProjectionData) && temporalRightProjectionData.HistoryReady
                            ? temporalRightProjectionData.RightEyePrevProjection
                            : rightCamera?.ProjectionMatrix ?? camera?.ProjectionMatrix ?? Matrix4x4.Identity,
                        false);
                    return true;
                case nameof(EEngineUniform.CameraPosition):
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderTranslation ?? Vector3.Zero, false);
                    return true;
                case nameof(EEngineUniform.CameraForward):
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderForward ?? Vector3.UnitZ, false);
                    return true;
                case nameof(EEngineUniform.CameraUp):
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderUp ?? Vector3.UnitY, false);
                    return true;
                case nameof(EEngineUniform.CameraRight):
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderRight ?? Vector3.UnitX, false);
                    return true;
                case nameof(EEngineUniform.CameraNearZ):
                    value = new ProgramUniformValue(EShaderVarType._float, camera?.NearZ ?? 0f, false);
                    return true;
                case nameof(EEngineUniform.CameraFarZ):
                    value = new ProgramUniformValue(EShaderVarType._float, camera?.FarZ ?? 0f, false);
                    return true;
                case nameof(EEngineUniform.ScreenWidth):
                    value = new ProgramUniformValue(EShaderVarType._float, (float)area.Width, false);
                    return true;
                case nameof(EEngineUniform.ScreenHeight):
                    value = new ProgramUniformValue(EShaderVarType._float, (float)area.Height, false);
                    return true;
                case nameof(EEngineUniform.ScreenOrigin):
                    value = new ProgramUniformValue(EShaderVarType._vec2, Vector2.Zero, false);
                    return true;
                case nameof(EEngineUniform.DepthMode):
                    value = new ProgramUniformValue(EShaderVarType._int, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal), false);
                    return true;
                case nameof(EEngineUniform.ClipSpaceYDirection):
                    value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection, false);
                    return true;
                case nameof(EEngineUniform.ClipDepthRange):
                    value = new ProgramUniformValue(EShaderVarType._int, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange, false);
                    return true;
                case nameof(EEngineUniform.FramebufferTextureYDirection):
                    value = new ProgramUniformValue(EShaderVarType._int, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan), false);
                    return true;
                case nameof(EEngineUniform.VRMode):
                    value = new ProgramUniformValue(EShaderVarType._int, stereo ? 1 : 0, false);
                    return true;
            }

            return false;
        }

        private static bool TryWriteUniformValue(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
            => member.IsArray
                ? TryWriteUniformArray(destination, member, value)
                : TryWriteSingleUniform(destination, member.Offset, value);

        private static bool TryWriteSingleUniform(Span<byte> destination, uint offset, in ProgramUniformValue value)
        {
            if (!value.HasInlineValue)
                return value.ReferenceValue is { } reference &&
                    TryWriteSingleUniform(destination, offset, value.Type, reference);

            if (offset >= (uint)destination.Length)
                return false;

            ref byte start = ref destination[(int)offset];
            switch (value.Type)
            {
                case EShaderVarType._float:
                    Unsafe.WriteUnaligned(ref start, value.Float);
                    return true;
                case EShaderVarType._int:
                    Unsafe.WriteUnaligned(ref start, value.Int);
                    return true;
                case EShaderVarType._uint:
                    Unsafe.WriteUnaligned(ref start, value.UInt);
                    return true;
                case EShaderVarType._bool:
                    Unsafe.WriteUnaligned(ref start, value.Int != 0 ? 1 : 0);
                    return true;
                case EShaderVarType._double:
                    Unsafe.WriteUnaligned(ref start, value.Double);
                    return true;
                case EShaderVarType._vec2:
                    Unsafe.WriteUnaligned(ref start, value.Vector2);
                    return true;
                case EShaderVarType._vec3:
                    Unsafe.WriteUnaligned(ref start, new Vector4(value.Vector3, 0f));
                    return true;
                case EShaderVarType._vec4:
                    Unsafe.WriteUnaligned(ref start, value.Vector4);
                    return true;
                case EShaderVarType._dvec2:
                    Unsafe.WriteUnaligned(ref start, new DVector2(value.DVector4.X, value.DVector4.Y));
                    return true;
                case EShaderVarType._dvec3:
                case EShaderVarType._dvec4:
                    Unsafe.WriteUnaligned(ref start, value.DVector4);
                    return true;
                case EShaderVarType._ivec2:
                    Unsafe.WriteUnaligned(ref start, new IVector2(value.IVector4.X, value.IVector4.Y));
                    return true;
                case EShaderVarType._ivec3:
                case EShaderVarType._ivec4:
                    Unsafe.WriteUnaligned(ref start, value.IVector4);
                    return true;
                case EShaderVarType._uvec2:
                    Unsafe.WriteUnaligned(ref start, new UVector2(value.UVector4.X, value.UVector4.Y));
                    return true;
                case EShaderVarType._uvec3:
                case EShaderVarType._uvec4:
                    Unsafe.WriteUnaligned(ref start, value.UVector4);
                    return true;
                case EShaderVarType._mat4:
                    Unsafe.WriteUnaligned(ref start, value.Matrix4x4);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryWriteUniformArray(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
        {
            if (!value.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0)
                return false;

            object? arrayValue = value.ReferenceValue;
            return arrayValue switch
            {
                float[] values when value.Type == EShaderVarType._float
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                int[] values when value.Type is EShaderVarType._int or EShaderVarType._bool
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                uint[] values when value.Type == EShaderVarType._uint
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                bool[] values when value.Type == EShaderVarType._bool
                    => TryWriteBooleanUniformArray(destination, member, values),
                double[] values when value.Type == EShaderVarType._double
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                Vector2[] values when value.Type == EShaderVarType._vec2
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                Vector3[] values when value.Type == EShaderVarType._vec3
                    => TryWriteVector3UniformArray(destination, member, values),
                Vector4[] values when value.Type == EShaderVarType._vec4
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                Matrix4x4[] values when value.Type == EShaderVarType._mat4
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                DVector2[] values when value.Type == EShaderVarType._dvec2
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                DVector3[] values when value.Type == EShaderVarType._dvec3
                    => TryWriteDVector3UniformArray(destination, member, values),
                DVector4[] values when value.Type == EShaderVarType._dvec4
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                IVector2[] values when value.Type == EShaderVarType._ivec2
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                IVector3[] values when value.Type == EShaderVarType._ivec3
                    => TryWriteIVector3UniformArray(destination, member, values),
                IVector4[] values when value.Type == EShaderVarType._ivec4
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                UVector2[] values when value.Type == EShaderVarType._uvec2
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                UVector3[] values when value.Type == EShaderVarType._uvec3
                    => TryWriteUVector3UniformArray(destination, member, values),
                UVector4[] values when value.Type == EShaderVarType._uvec4
                    => TryWriteUnmanagedUniformArray(destination, member, values),
                BoolVector2[] values when value.Type == EShaderVarType._bvec2
                    => TryWriteBoolVector2UniformArray(destination, member, values),
                BoolVector3[] values when value.Type == EShaderVarType._bvec3
                    => TryWriteBoolVector3UniformArray(destination, member, values),
                BoolVector4[] values when value.Type == EShaderVarType._bvec4
                    => TryWriteBoolVector4UniformArray(destination, member, values),
                object?[] values => TryWriteReferenceUniformArray(destination, member, value.Type, values),
                _ => false,
            };
        }

        private static bool TryWriteUnmanagedUniformArray<T>(Span<byte> destination, AutoUniformMember member, T[] values)
            where T : unmanaged
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                if (!TryWriteUniformArrayElement(destination, offset, values[i]))
                    return false;
            }

            return true;
        }

        private static bool TryWriteBooleanUniformArray(Span<byte> destination, AutoUniformMember member, bool[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                if (!TryWriteUniformArrayElement(destination, offset, values[i] ? 1 : 0))
                    return false;
            }

            return true;
        }

        private static bool TryWriteVector3UniformArray(Span<byte> destination, AutoUniformMember member, Vector3[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                if (!TryWriteUniformArrayElement(destination, offset, new Vector4(values[i], 0f)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteDVector3UniformArray(Span<byte> destination, AutoUniformMember member, DVector3[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                DVector3 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new DVector4(vector.X, vector.Y, vector.Z, 0.0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteIVector3UniformArray(Span<byte> destination, AutoUniformMember member, IVector3[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                IVector3 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X, vector.Y, vector.Z, 0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteUVector3UniformArray(Span<byte> destination, AutoUniformMember member, UVector3[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                UVector3 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new UVector4(vector.X, vector.Y, vector.Z, 0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteBoolVector2UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector2[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                BoolVector2 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new IVector2(vector.X ? 1 : 0, vector.Y ? 1 : 0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteBoolVector3UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector3[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                BoolVector3 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X ? 1 : 0, vector.Y ? 1 : 0, vector.Z ? 1 : 0, 0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteBoolVector4UniformArray(Span<byte> destination, AutoUniformMember member, BoolVector4[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                uint offset = member.Offset + (uint)i * member.ArrayStride;
                BoolVector4 vector = values[i];
                if (!TryWriteUniformArrayElement(destination, offset, new IVector4(vector.X ? 1 : 0, vector.Y ? 1 : 0, vector.Z ? 1 : 0, vector.W ? 1 : 0)))
                    return false;
            }

            return true;
        }

        private static bool TryWriteReferenceUniformArray(
            Span<byte> destination,
            AutoUniformMember member,
            EShaderVarType type,
            object?[] values)
        {
            int count = Math.Min(values.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                object? element = values[i];
                if (element is null)
                    continue;

                uint offset = member.Offset + (uint)i * member.ArrayStride;
                if (!TryWriteSingleUniform(destination, offset, type, element))
                    return false;
            }

            return true;
        }

        private static bool TryWriteUniformArrayElement<T>(Span<byte> destination, uint offset, T value)
            where T : unmanaged
        {
            if (offset > (uint)destination.Length || Unsafe.SizeOf<T>() > destination.Length - (int)offset)
                return false;

            Unsafe.WriteUnaligned(ref destination[(int)offset], value);
            return true;
        }

        private static bool TryWriteSingleUniform(Span<byte> destination, uint offset, EShaderVarType type, object value)
        {
            if (offset >= (uint)destination.Length)
                return false;

            ref byte start = ref destination[(int)offset];
            switch (type)
            {
                case EShaderVarType._float:
                    Unsafe.WriteUnaligned(ref start, Convert.ToSingle(value));
                    return true;
                case EShaderVarType._int:
                    Unsafe.WriteUnaligned(ref start, Convert.ToInt32(value));
                    return true;
                case EShaderVarType._uint:
                    Unsafe.WriteUnaligned(ref start, Convert.ToUInt32(value));
                    return true;
                case EShaderVarType._bool:
                    Unsafe.WriteUnaligned(ref start, Convert.ToBoolean(value) ? 1 : 0);
                    return true;
                case EShaderVarType._vec2:
                    if (value is Vector2 v2)
                    {
                        Unsafe.WriteUnaligned(ref start, v2);
                        return true;
                    }
                    break;
                case EShaderVarType._vec3:
                    if (value is Vector3 v3)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(v3, 0f));
                        return true;
                    }
                    if (value is Vector4 v3From4)
                    {
                        Unsafe.WriteUnaligned(ref start, v3From4);
                        return true;
                    }
                    if (value is ColorF3 c3)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(c3.R, c3.G, c3.B, 0f));
                        return true;
                    }
                    if (value is ColorF4 c3From4)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(c3From4.R, c3From4.G, c3From4.B, 0f));
                        return true;
                    }
                    break;
                case EShaderVarType._vec4:
                    if (value is Vector4 v4)
                    {
                        Unsafe.WriteUnaligned(ref start, v4);
                        return true;
                    }
                    if (value is Vector3 v4From3)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(v4From3, 0f));
                        return true;
                    }
                    if (value is ColorF4 c4)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4.R, c4.G, c4.B, c4.A));
                        return true;
                    }
                    if (value is ColorF3 c4From3)
                    {
                        Unsafe.WriteUnaligned(ref start, new Vector4(c4From3.R, c4From3.G, c4From3.B, 0f));
                        return true;
                    }
                    break;
                case EShaderVarType._ivec2:
                    if (value is IVector2 iv2)
                    {
                        Unsafe.WriteUnaligned(ref start, iv2);
                        return true;
                    }
                    break;
                case EShaderVarType._ivec3:
                    if (value is IVector3 iv3)
                    {
                        Unsafe.WriteUnaligned(ref start, new IVector4(iv3.X, iv3.Y, iv3.Z, 0));
                        return true;
                    }
                    break;
                case EShaderVarType._ivec4:
                    if (value is IVector4 iv4)
                    {
                        Unsafe.WriteUnaligned(ref start, iv4);
                        return true;
                    }
                    break;
                case EShaderVarType._uvec2:
                    if (value is UVector2 uv2)
                    {
                        Unsafe.WriteUnaligned(ref start, uv2);
                        return true;
                    }
                    break;
                case EShaderVarType._uvec3:
                    if (value is UVector3 uv3)
                    {
                        Unsafe.WriteUnaligned(ref start, new UVector4(uv3.X, uv3.Y, uv3.Z, 0));
                        return true;
                    }
                    break;
                case EShaderVarType._uvec4:
                    if (value is UVector4 uv4)
                    {
                        Unsafe.WriteUnaligned(ref start, uv4);
                        return true;
                    }
                    break;
                case EShaderVarType._mat4:
                    if (value is Matrix4x4 mat)
                    {
                        Unsafe.WriteUnaligned(ref start, mat);
                        return true;
                    }
                    break;
                case EShaderVarType._dvec2:
                    if (value is DVector2 dv2)
                    {
                        Unsafe.WriteUnaligned(ref start, dv2);
                        return true;
                    }
                    break;
                case EShaderVarType._dvec3:
                    if (value is DVector3 dv3)
                    {
                        Unsafe.WriteUnaligned(ref start, new DVector4(dv3.X, dv3.Y, dv3.Z, 0.0));
                        return true;
                    }
                    break;
                case EShaderVarType._dvec4:
                    if (value is DVector4 dv4)
                    {
                        Unsafe.WriteUnaligned(ref start, dv4);
                        return true;
                    }
                    break;
                case EShaderVarType._double:
                    Unsafe.WriteUnaligned(ref start, Convert.ToDouble(value));
                    return true;
            }

            return false;
        }

        private void WarnComputeOnce(string message)
        {
            if (_computeWarnings.TryAdd(message, 0))
                Debug.VulkanWarning($"[VkCompute:{Data.Name ?? "UnnamedProgram"}] {message}");
        }

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

        private static readonly EProgramStageMask[] StageOrder =
        {
            EProgramStageMask.TaskShaderBit,
            EProgramStageMask.MeshShaderBit,
            EProgramStageMask.VertexShaderBit,
            EProgramStageMask.TessControlShaderBit,
            EProgramStageMask.TessEvaluationShaderBit,
            EProgramStageMask.GeometryShaderBit,
            EProgramStageMask.FragmentShaderBit,
            EProgramStageMask.ComputeShaderBit,
        };

        private static IEnumerable<EProgramStageMask> EnumerateStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask stage in StageOrder)
                if (mask.HasFlag(stage))
                    yield return stage;
        }

    }
