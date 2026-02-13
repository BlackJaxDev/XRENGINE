using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Models.Materials;

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

    public class VkRenderProgram(VulkanRenderer renderer, XRRenderProgram data) : VkObject<XRRenderProgram>(renderer, data)
    {
        private readonly Dictionary<XRShader, VkShader> _shaderCache = new();
        private readonly Dictionary<EProgramStageMask, VkShader> _stageLookup = new();
        private DescriptorSetLayout[] _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private readonly List<DescriptorBindingInfo> _programDescriptorBindings = new();
        private readonly Dictionary<string, AutoUniformBlockInfo> _autoUniformBlocks = new(StringComparer.Ordinal);
        private readonly object _bindingLock = new();
        private readonly Dictionary<string, ProgramUniformValue> _uniformValues = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, XRTexture> _samplersByUnit = new();
        private readonly Dictionary<uint, ProgramImageBinding> _imagesByUnit = new();
        private readonly Dictionary<uint, XRDataBuffer> _buffersByBinding = new();
        private readonly HashSet<string> _computeWarnings = new(StringComparer.Ordinal);
        private Pipeline _computePipeline;
        private bool _descriptorSetsRequireUpdateAfterBind;

        public override VkObjectType Type => VkObjectType.Program;
        public override bool IsGenerated => true;
        public bool IsLinked { get; private set; }
        public PipelineLayout PipelineLayout => _pipelineLayout;
        public IReadOnlyList<DescriptorSetLayout> DescriptorSetLayouts => _descriptorSetLayouts;
        public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _programDescriptorBindings;
        public IReadOnlyDictionary<string, AutoUniformBlockInfo> AutoUniformBlocks => _autoUniformBlocks;
        public bool DescriptorSetsRequireUpdateAfterBind => _descriptorSetsRequireUpdateAfterBind;

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
            IsLinked = false;
        }

        private void ShaderRemoved(XRShader shader)
        {
            if (_shaderCache.Remove(shader, out VkShader? vkShader) && vkShader is not null)
                vkShader.Destroy();

            IsLinked = false;
        }

        private void OnLinkRequested(XRRenderProgram program)
        {
            if (Engine.InvokeOnMainThread(() => OnLinkRequested(program), "VkRenderProgram.LinkRequested"))
                return;

            if (!Link())
                Debug.VulkanWarning($"Failed to link Vulkan program '{Data.Name ?? "UnnamedProgram"}'.");
        }

        private void OnUseRequested(XRRenderProgram program)
        {
            if (Engine.InvokeOnMainThread(() => OnUseRequested(program), "VkRenderProgram.UseRequested"))
                return;

            if (!IsLinked)
                Link();
        }

        private void ClearBindings()
        {
            lock (_bindingLock)
            {
                _uniformValues.Clear();
                _samplersByUnit.Clear();
                _imagesByUnit.Clear();
                _buffersByBinding.Clear();
            }
        }

        private void SetUniformValue(string name, EShaderVarType type, object value, bool isArray = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            lock (_bindingLock)
                _uniformValues[name] = new ProgramUniformValue(type, value, isArray);
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
                    string stripped = name[..^4];
                    if (_uniformValues.TryGetValue(stripped, out value))
                        return true;
                }
                else if (_uniformValues.TryGetValue(name + "_VTX", out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        internal ComputeDispatchSnapshot CaptureComputeSnapshot()
        {
            lock (_bindingLock)
            {
                return new ComputeDispatchSnapshot(
                    new Dictionary<string, ProgramUniformValue>(_uniformValues, StringComparer.Ordinal),
                    new Dictionary<uint, XRTexture>(_samplersByUnit),
                    new Dictionary<uint, ProgramImageBinding>(_imagesByUnit),
                    new Dictionary<uint, XRDataBuffer>(_buffersByBinding));
            }
        }

        private void Uniform(string name, Matrix4x4 value) => SetUniformValue(name, EShaderVarType._mat4, value);
        private void Uniform(string name, Quaternion value) => SetUniformValue(name, EShaderVarType._vec4, new Vector4(value.X, value.Y, value.Z, value.W));
        private void Uniform(string name, Matrix4x4[] value) => SetUniformValue(name, EShaderVarType._mat4, value.ToArray(), true);
        private void Uniform(string name, Quaternion[] value)
        {
            Vector4[] converted = value.Select(q => new Vector4(q.X, q.Y, q.Z, q.W)).ToArray();
            SetUniformValue(name, EShaderVarType._vec4, converted, true);
        }

        private void Uniform(string name, bool value) => SetUniformValue(name, EShaderVarType._bool, value);
        private void Uniform(string name, BoolVector2 value) => SetUniformValue(name, EShaderVarType._bvec2, value);
        private void Uniform(string name, BoolVector3 value) => SetUniformValue(name, EShaderVarType._bvec3, value);
        private void Uniform(string name, BoolVector4 value) => SetUniformValue(name, EShaderVarType._bvec4, value);
        private void Uniform(string name, bool[] value) => SetUniformValue(name, EShaderVarType._bool, value.ToArray(), true);
        private void Uniform(string name, BoolVector2[] value) => SetUniformValue(name, EShaderVarType._bvec2, value.ToArray(), true);
        private void Uniform(string name, BoolVector3[] value) => SetUniformValue(name, EShaderVarType._bvec3, value.ToArray(), true);
        private void Uniform(string name, BoolVector4[] value) => SetUniformValue(name, EShaderVarType._bvec4, value.ToArray(), true);

        private void Uniform(string name, float value) => SetUniformValue(name, EShaderVarType._float, value);
        private void Uniform(string name, Vector2 value) => SetUniformValue(name, EShaderVarType._vec2, value);
        private void Uniform(string name, Vector3 value) => SetUniformValue(name, EShaderVarType._vec3, value);
        private void Uniform(string name, Vector4 value) => SetUniformValue(name, EShaderVarType._vec4, value);
        private void Uniform(string name, float[] value) => SetUniformValue(name, EShaderVarType._float, value.ToArray(), true);
        private void Uniform(string name, Span<float> value) => SetUniformValue(name, EShaderVarType._float, value.ToArray(), true);
        private void Uniform(string name, Vector2[] value) => SetUniformValue(name, EShaderVarType._vec2, value.ToArray(), true);
        private void Uniform(string name, Vector3[] value) => SetUniformValue(name, EShaderVarType._vec3, value.ToArray(), true);
        private void Uniform(string name, Vector4[] value) => SetUniformValue(name, EShaderVarType._vec4, value.ToArray(), true);

        private void Uniform(string name, double value) => SetUniformValue(name, EShaderVarType._double, value);
        private void Uniform(string name, DVector2 value) => SetUniformValue(name, EShaderVarType._dvec2, value);
        private void Uniform(string name, DVector3 value) => SetUniformValue(name, EShaderVarType._dvec3, value);
        private void Uniform(string name, DVector4 value) => SetUniformValue(name, EShaderVarType._dvec4, value);
        private void Uniform(string name, double[] value) => SetUniformValue(name, EShaderVarType._double, value.ToArray(), true);
        private void Uniform(string name, DVector2[] value) => SetUniformValue(name, EShaderVarType._dvec2, value.ToArray(), true);
        private void Uniform(string name, DVector3[] value) => SetUniformValue(name, EShaderVarType._dvec3, value.ToArray(), true);
        private void Uniform(string name, DVector4[] value) => SetUniformValue(name, EShaderVarType._dvec4, value.ToArray(), true);

        private void Uniform(string name, int value) => SetUniformValue(name, EShaderVarType._int, value);
        private void Uniform(string name, IVector2 value) => SetUniformValue(name, EShaderVarType._ivec2, value);
        private void Uniform(string name, IVector3 value) => SetUniformValue(name, EShaderVarType._ivec3, value);
        private void Uniform(string name, IVector4 value) => SetUniformValue(name, EShaderVarType._ivec4, value);
        private void Uniform(string name, int[] value) => SetUniformValue(name, EShaderVarType._int, value.ToArray(), true);
        private void Uniform(string name, IVector2[] value) => SetUniformValue(name, EShaderVarType._ivec2, value.ToArray(), true);
        private void Uniform(string name, IVector3[] value) => SetUniformValue(name, EShaderVarType._ivec3, value.ToArray(), true);
        private void Uniform(string name, IVector4[] value) => SetUniformValue(name, EShaderVarType._ivec4, value.ToArray(), true);

        private void Uniform(string name, uint value) => SetUniformValue(name, EShaderVarType._uint, value);
        private void Uniform(string name, UVector2 value) => SetUniformValue(name, EShaderVarType._uvec2, value);
        private void Uniform(string name, UVector3 value) => SetUniformValue(name, EShaderVarType._uvec3, value);
        private void Uniform(string name, UVector4 value) => SetUniformValue(name, EShaderVarType._uvec4, value);
        private void Uniform(string name, uint[] value) => SetUniformValue(name, EShaderVarType._uint, value.ToArray(), true);
        private void Uniform(string name, UVector2[] value) => SetUniformValue(name, EShaderVarType._uvec2, value.ToArray(), true);
        private void Uniform(string name, UVector3[] value) => SetUniformValue(name, EShaderVarType._uvec3, value.ToArray(), true);
        private void Uniform(string name, UVector4[] value) => SetUniformValue(name, EShaderVarType._uvec4, value.ToArray(), true);

        private void Sampler(string name, XRTexture texture, int textureUnit)
        {
            if (texture is null)
                return;

            uint unit = textureUnit < 0 ? 0u : (uint)textureUnit;
            lock (_bindingLock)
                _samplersByUnit[unit] = texture;
        }

        private void Sampler(int location, XRTexture texture, int textureUnit)
            => Sampler(location.ToString(), texture, textureUnit);

        private void BindImageTexture(uint unit, XRTexture texture, int level, bool layered, int layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)
        {
            if (texture is null)
                return;

            lock (_bindingLock)
                _imagesByUnit[unit] = new ProgramImageBinding(texture, level, layered, layer, access, format);
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
            IEnumerable<(uint unit, XRTexture texture, int level, int? layer, XRRenderProgram.EImageAccess access, XRRenderProgram.EImageFormat format)>? textures = null)
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

        public bool Link()
        {
            if (IsLinked)
                return true;

            if (!Data.LinkReady)
                return false;

            if (_shaderCache.Count == 0)
            {
                Debug.VulkanWarning($"Cannot link Vulkan program '{Data.Name ?? "UnnamedProgram"}' because it contains no shaders.");
                return false;
            }

            foreach (VkShader shader in _shaderCache.Values)
                shader.Generate();

            BuildStageLookup();
            BuildDescriptorLayouts();

            IsLinked = true;
            return true;
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

        private void BuildDescriptorLayouts()
        {
            DestroyLayouts();

            IEnumerable<DescriptorBindingInfo> shaderBindings = EnumerateShaderDescriptorBindings();
            string programName = Data.Name ?? "UnnamedProgram";
            var result = BuildDescriptorLayoutsShared(Renderer, Device, shaderBindings, programName);

            _descriptorSetLayouts = result.Layouts;
            _programDescriptorBindings.Clear();
            _programDescriptorBindings.AddRange(result.Bindings);
            _descriptorSetsRequireUpdateAfterBind = result.RequiresUpdateAfterBind;
            _autoUniformBlocks.Clear();
            foreach (VkShader shader in _shaderCache.Values)
            {
                if (shader.AutoUniformBlock is { } block)
                    _autoUniformBlocks[block.InstanceName] = block;
            }

            CreatePipelineLayout(_descriptorSetLayouts);
        }

        public bool TryGetAutoUniformBlock(string name, out AutoUniformBlockInfo block)
            => _autoUniformBlocks.TryGetValue(name, out block);

        private void CreatePipelineLayout(IReadOnlyList<DescriptorSetLayout> layouts)
        {
            if (_pipelineLayout.Handle != 0)
            {
                Api!.DestroyPipelineLayout(Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (layouts.Count == 0)
            {
                PipelineLayoutCreateInfo info = new() { SType = StructureType.PipelineLayoutCreateInfo };
                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
                return;
            }

            DescriptorSetLayout[] layoutArray = layouts.ToArray();
            fixed (DescriptorSetLayout* layoutPtr = layoutArray)
            {
                PipelineLayoutCreateInfo info = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)layoutArray.Length,
                    PSetLayouts = layoutPtr,
                };

                if (Api!.CreatePipelineLayout(Device, ref info, null, out _pipelineLayout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
            }
        }

        private void DestroyLayouts()
        {
            if (_computePipeline.Handle != 0)
            {
                Api!.DestroyPipeline(Device, _computePipeline, null);
                _computePipeline = default;
            }

            if (_descriptorSetLayouts.Length > 0)
            {
                foreach (DescriptorSetLayout layout in _descriptorSetLayouts)
                    Renderer.ReleaseCachedDescriptorSetLayout(layout);

                _descriptorSetLayouts = Array.Empty<DescriptorSetLayout>();
            }

            if (_pipelineLayout.Handle != 0)
            {
                Api!.DestroyPipelineLayout(Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            _programDescriptorBindings.Clear();
            _descriptorSetsRequireUpdateAfterBind = false;
            IsLinked = false;
        }

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages()
            => GetShaderStages(EProgramStageMask.AllShaderBits);

        public IEnumerable<PipelineShaderStageCreateInfo> GetShaderStages(EProgramStageMask mask)
        {
            foreach (EProgramStageMask flag in EnumerateStages(mask))
            {
                if (_stageLookup.TryGetValue(flag, out VkShader? shader))
                    yield return shader.ShaderStageCreateInfo;
            }
        }

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

            PipelineShaderStageCreateInfo[] stages = GetShaderStages(GraphicsStageMask).ToArray();
            if (stages.Length == 0)
                throw new InvalidOperationException("Graphics pipeline creation requires at least one graphics shader stage.");

            fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
            {
                pipelineInfo.StageCount = (uint)stages.Length;
                pipelineInfo.PStages = stagesPtr;
                pipelineInfo.Layout = _pipelineLayout;

                Result result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
                if (result != Result.Success)
                    throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");

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

            Result result = Api!.CreateComputePipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");

            return pipeline;
        }

        public ulong ComputeGraphicsPipelineFingerprint()
        {
            HashCode hash = new();
            hash.Add(_pipelineLayout.Handle);

            foreach (PipelineShaderStageCreateInfo stage in GetShaderStages(GraphicsStageMask))
            {
                hash.Add((int)stage.Stage);
                hash.Add(stage.Module.Handle);
            }

            DescriptorBindingInfo[] bindings = _programDescriptorBindings
                .OrderBy(static binding => binding.Set)
                .ThenBy(static binding => binding.Binding)
                .ToArray();

            for (int i = 0; i < bindings.Length; i++)
            {
                DescriptorBindingInfo binding = bindings[i];
                hash.Add(binding.Set);
                hash.Add(binding.Binding);
                hash.Add((int)binding.DescriptorType);
                hash.Add(binding.Count);
                hash.Add((int)binding.StageFlags);
            }

            return unchecked((ulong)hash.ToHashCode());
        }

        public ulong ComputeComputePipelineFingerprint()
        {
            HashCode hash = new();
            hash.Add(_pipelineLayout.Handle);

            PipelineShaderStageCreateInfo computeStage = GetShaderStages(EProgramStageMask.ComputeShaderBit).SingleOrDefault();
            hash.Add((int)computeStage.Stage);
            hash.Add(computeStage.Module.Handle);

            return unchecked((ulong)hash.ToHashCode());
        }

        public Pipeline GetOrCreateComputePipeline()
        {
            if (_computePipeline.Handle != 0)
            {
                Engine.Rendering.Stats.RecordVulkanPipelineCacheLookup(cacheHit: true);
                return _computePipeline;
            }

            Engine.Rendering.Stats.RecordVulkanPipelineCacheLookup(cacheHit: false);

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

            foreach (DescriptorBindingInfo binding in _programDescriptorBindings)
            {
                if (binding.Set >= _descriptorSetLayouts.Length)
                    continue;

                uint descriptorCount = Math.Max(binding.Count, 1u);
                switch (binding.DescriptorType)
                {
                    case DescriptorType.UniformBuffer:
                    case DescriptorType.StorageBuffer:
                        if (!TryResolveComputeBuffer(binding, snapshot, tempUniformBuffers, out DescriptorBufferInfo bufferInfo))
                            continue;

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
                            continue;

                        int imageStart = imageInfos.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            imageInfos.Add(imageInfo);

                        pendingWrites.Add(PendingDescriptorWrite.Image(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, imageStart));
                        break;

                    case DescriptorType.UniformTexelBuffer:
                    case DescriptorType.StorageTexelBuffer:
                        if (!TryResolveComputeTexelBuffer(binding, snapshot, out BufferView texelView))
                            continue;

                        int texelStart = texelBufferViews.Count;
                        for (int i = 0; i < descriptorCount; i++)
                            texelBufferViews.Add(texelView);

                        pendingWrites.Add(PendingDescriptorWrite.Texel(binding.Set, binding.Binding, binding.DescriptorType, descriptorCount, texelStart));
                        break;
                }
            }

            if (pendingWrites.Count == 0)
                return false;

            PendingDescriptorWrite[] pendingWriteArray = pendingWrites.ToArray();
            DescriptorBufferInfo[] bufferArray = bufferInfos.ToArray();
            DescriptorImageInfo[] imageArray = imageInfos.ToArray();
            BufferView[] texelArray = texelBufferViews.ToArray();

            bool cacheable = tempUniformBuffers.Count == 0;
            DescriptorSet[] descriptorSets;
            bool shouldUpdateDescriptorData = true;

            if (cacheable)
            {
                ulong schemaFingerprint = ComputeComputeDescriptorSchemaFingerprint();
                ulong bindingFingerprint = ComputeComputeDescriptorBindingFingerprint(pendingWriteArray, bufferArray, imageArray, texelArray);
                DescriptorSetLayout[] layoutArray = _descriptorSetLayouts.ToArray();

                if (!Renderer.TryGetOrCreateComputeDescriptorSets(
                    imageIndex,
                    schemaFingerprint,
                    bindingFingerprint,
                    layoutArray,
                    poolSizes,
                    _descriptorSetsRequireUpdateAfterBind,
                    out descriptorSets,
                    out bool isNewAllocation))
                {
                    WarnComputeOnce("Failed to acquire cached Vulkan compute descriptor sets.");
                    return false;
                }

                shouldUpdateDescriptorData = isNewAllocation;
            }
            else
            {
                if (!TryAllocateTransientComputeDescriptorSets(poolSizes, out descriptorPool, out descriptorSets))
                    return false;
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

        private bool TryAllocateTransientComputeDescriptorSets(
            DescriptorPoolSize[] poolSizes,
            out DescriptorPool descriptorPool,
            out DescriptorSet[] descriptorSets)
        {
            descriptorPool = default;
            descriptorSets = Array.Empty<DescriptorSet>();

            fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
            {
                DescriptorPoolCreateInfo poolInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    Flags = _descriptorSetsRequireUpdateAfterBind
                        ? DescriptorPoolCreateFlags.FreeDescriptorSetBit | DescriptorPoolCreateFlags.UpdateAfterBindBit
                        : DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                    MaxSets = (uint)_descriptorSetLayouts.Length,
                    PoolSizeCount = (uint)poolSizes.Length,
                    PPoolSizes = poolSizesPtr,
                };

                if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out descriptorPool) != Result.Success)
                {
                    WarnComputeOnce("Failed to create Vulkan compute descriptor pool.");
                    return false;
                }
            }

            descriptorSets = new DescriptorSet[_descriptorSetLayouts.Length];
            fixed (DescriptorSetLayout* layoutPtr = _descriptorSetLayouts)
            fixed (DescriptorSet* setPtr = descriptorSets)
            {
                DescriptorSetAllocateInfo allocInfo = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptorPool,
                    DescriptorSetCount = (uint)_descriptorSetLayouts.Length,
                    PSetLayouts = layoutPtr,
                };

                if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
                {
                    WarnComputeOnce("Failed to allocate Vulkan compute descriptor sets.");
                    Api!.DestroyDescriptorPool(Device, descriptorPool, null);
                    descriptorPool = default;
                    descriptorSets = Array.Empty<DescriptorSet>();
                    return false;
                }
            }

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

                Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
            }
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

        private bool TryResolveComputeBuffer(
            DescriptorBindingInfo binding,
            ComputeDispatchSnapshot snapshot,
            List<(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)> tempUniformBuffers,
            out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;

            if (snapshot.Buffers.TryGetValue(binding.Binding, out XRDataBuffer? boundBuffer))
                return TryCreateDescriptorBufferInfo(boundBuffer, out bufferInfo);

            if (binding.DescriptorType == DescriptorType.UniformBuffer &&
                !string.IsNullOrWhiteSpace(binding.Name) &&
                _autoUniformBlocks.TryGetValue(binding.Name, out AutoUniformBlockInfo block))
            {
                if (TryCreateAutoUniformBuffer(snapshot, block, out Silk.NET.Vulkan.Buffer autoBuffer, out DeviceMemory autoMemory))
                {
                    tempUniformBuffers.Add((autoBuffer, autoMemory));
                    bufferInfo = new DescriptorBufferInfo
                    {
                        Buffer = autoBuffer,
                        Offset = 0,
                        Range = Math.Max(block.Size, 1u)
                    };
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(binding.Name))
            {
                XRDataBuffer? namedBuffer = snapshot.Buffers.Values.FirstOrDefault(
                    b => string.Equals(b.AttributeName, binding.Name, StringComparison.Ordinal));
                if (namedBuffer is not null && TryCreateDescriptorBufferInfo(namedBuffer, out bufferInfo))
                    return true;
            }

            return false;
        }

        private bool TryCreateDescriptorBufferInfo(XRDataBuffer dataBuffer, out DescriptorBufferInfo bufferInfo)
        {
            bufferInfo = default;
            if (Renderer.GetOrCreateAPIRenderObject(dataBuffer) is not VkDataBuffer vkBuffer)
                return false;

            vkBuffer.Generate();
            if (vkBuffer.BufferHandle is not { } handle || handle.Handle == 0)
                return false;

            bufferInfo = new DescriptorBufferInfo
            {
                Buffer = handle,
                Offset = 0,
                Range = Math.Max(dataBuffer.Length, 1u)
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

                if (!TryResolveTextureDescriptor(imageBinding.Texture, includeSampler: false, requiresSampledUsage: false, ImageLayout.General, out imageInfo))
                    return false;

                return true;
            }

            if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
            {
                // Fallback for shaders that only bind a single sampler but use non-zero binding in source.
                texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
                if (texture is null)
                    return false;
            }

            bool includeSampler = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;
            bool requiresSampledUsage = binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler or DescriptorType.SampledImage;
            return TryResolveTextureDescriptor(texture, includeSampler, requiresSampledUsage, ImageLayout.ShaderReadOnlyOptimal, out imageInfo);
        }

        private bool TryResolveComputeTexelBuffer(DescriptorBindingInfo binding, ComputeDispatchSnapshot snapshot, out BufferView texelView)
        {
            texelView = default;

            if (!snapshot.Samplers.TryGetValue(binding.Binding, out XRTexture? texture))
            {
                texture = snapshot.Samplers.Count == 1 ? snapshot.Samplers.Values.First() : null;
                if (texture is null)
                    return false;
            }

            return TryResolveTexelBufferDescriptor(texture, out texelView);
        }

        private bool TryResolveTextureDescriptor(XRTexture texture, bool includeSampler, bool requiresSampledUsage, ImageLayout layout, out DescriptorImageInfo imageInfo)
        {
            imageInfo = default;
            if (texture is null)
                return false;

            if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

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

            imageInfo = new DescriptorImageInfo
            {
                ImageLayout = layout,
                ImageView = source.DescriptorView,
                Sampler = includeSampler ? source.DescriptorSampler : default
            };
            return imageInfo.ImageView.Handle != 0;
        }

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

        private bool TryCreateAutoUniformBuffer(
            ComputeDispatchSnapshot snapshot,
            AutoUniformBlockInfo block,
            out Silk.NET.Vulkan.Buffer buffer,
            out DeviceMemory memory)
        {
            buffer = default;
            memory = default;

            uint size = Math.Max(block.Size, 1u);
            (buffer, memory) = Renderer.CreateBuffer(
                size,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            void* mapped;
            if (Api!.MapMemory(Device, memory, 0, size, 0, &mapped) != Result.Success)
                return false;

            try
            {
                Span<byte> data = new(mapped, (int)size);
                data.Clear();

                foreach (AutoUniformMember member in block.Members)
                    TryWriteAutoUniformMember(data, member, snapshot);
            }
            finally
            {
                Api.UnmapMemory(Device, memory);
            }

            return true;
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
                ProgramUniformValue val = new(member.EngineType ?? EShaderVarType._float, defaults.Select(d => d.Value).ToArray(), true);
                return TryWriteUniformValue(destination, member, val);
            }

            return false;
        }

        private bool TryResolveEngineUniform(string name, out ProgramUniformValue value)
        {
            value = default;

            if (!Enum.TryParse(name, ignoreCase: false, out EEngineUniform uniform))
                return false;

            XRCamera? camera = Engine.Rendering.State.RenderingCamera;
            XRCamera? rightCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
            bool stereo = Engine.Rendering.State.IsStereoPass;
            var area = Engine.Rendering.State.RenderArea;

            switch (uniform)
            {
                case EEngineUniform.UpdateDelta:
                    value = new ProgramUniformValue(EShaderVarType._float, Engine.Time.Timer.Update.Delta, false);
                    return true;
                case EEngineUniform.ViewMatrix:
                case EEngineUniform.PrevViewMatrix:
                case EEngineUniform.PrevLeftEyeViewMatrix:
                case EEngineUniform.PrevRightEyeViewMatrix:
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case EEngineUniform.InverseViewMatrix:
                case EEngineUniform.LeftEyeInverseViewMatrix:
                case EEngineUniform.RightEyeInverseViewMatrix:
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.Transform.RenderMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case EEngineUniform.ProjMatrix:
                case EEngineUniform.PrevProjMatrix:
                case EEngineUniform.LeftEyeProjMatrix:
                case EEngineUniform.RightEyeProjMatrix:
                case EEngineUniform.PrevLeftEyeProjMatrix:
                case EEngineUniform.PrevRightEyeProjMatrix:
                    value = new ProgramUniformValue(EShaderVarType._mat4, camera?.ProjectionMatrix ?? Matrix4x4.Identity, false);
                    return true;
                case EEngineUniform.CameraPosition:
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderTranslation ?? Vector3.Zero, false);
                    return true;
                case EEngineUniform.CameraForward:
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderForward ?? Vector3.UnitZ, false);
                    return true;
                case EEngineUniform.CameraUp:
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderUp ?? Vector3.UnitY, false);
                    return true;
                case EEngineUniform.CameraRight:
                    value = new ProgramUniformValue(EShaderVarType._vec3, camera?.Transform.RenderRight ?? Vector3.UnitX, false);
                    return true;
                case EEngineUniform.CameraNearZ:
                    value = new ProgramUniformValue(EShaderVarType._float, camera?.NearZ ?? 0f, false);
                    return true;
                case EEngineUniform.CameraFarZ:
                    value = new ProgramUniformValue(EShaderVarType._float, camera?.FarZ ?? 0f, false);
                    return true;
                case EEngineUniform.ScreenWidth:
                    value = new ProgramUniformValue(EShaderVarType._float, (float)area.Width, false);
                    return true;
                case EEngineUniform.ScreenHeight:
                    value = new ProgramUniformValue(EShaderVarType._float, (float)area.Height, false);
                    return true;
                case EEngineUniform.ScreenOrigin:
                    value = new ProgramUniformValue(EShaderVarType._vec2, Vector2.Zero, false);
                    return true;
                case EEngineUniform.DepthMode:
                    value = new ProgramUniformValue(EShaderVarType._int, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal), false);
                    return true;
                case EEngineUniform.VRMode:
                    value = new ProgramUniformValue(EShaderVarType._int, stereo ? 1 : 0, false);
                    return true;
            }

            _ = rightCamera;
            return false;
        }

        private static bool TryWriteUniformValue(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
        {
            if (member.IsArray)
                return TryWriteUniformArray(destination, member, value);

            return TryWriteSingleUniform(destination, member.Offset, value.Type, value.Value);
        }

        private static bool TryWriteUniformArray(Span<byte> destination, AutoUniformMember member, ProgramUniformValue value)
        {
            if (!value.IsArray || member.ArrayLength == 0 || member.ArrayStride == 0)
                return false;

            if (value.Value is not Array array)
                return false;

            int count = Math.Min(array.Length, (int)member.ArrayLength);
            for (int i = 0; i < count; i++)
            {
                object? element = array.GetValue(i);
                if (element is null)
                    continue;

                uint offset = member.Offset + (uint)i * member.ArrayStride;
                TryWriteSingleUniform(destination, offset, value.Type, element);
            }

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
                    break;
                case EShaderVarType._vec4:
                    if (value is Vector4 v4)
                    {
                        Unsafe.WriteUnaligned(ref start, v4);
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
            if (_computeWarnings.Add(message))
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
            List<DescriptorBindingInfo> reflectedBindings = bindings.ToList();
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
                return new DescriptorLayoutBuildResult(Array.Empty<DescriptorSetLayout>(), new List<DescriptorBindingInfo>(), false);

            List<DescriptorSetLayout> layouts = new();
            bool requiresUpdateAfterBind = false;
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

                if (!renderer.TryAcquireCachedDescriptorSetLayout(vkBindings, out DescriptorSetLayout layout, out bool usesUpdateAfterBind))
                    throw new InvalidOperationException($"Failed to create descriptor set layout for program '{programName}'.");

                requiresUpdateAfterBind |= usesUpdateAfterBind;
                layouts.Add(layout);
            }

            List<DescriptorBindingInfo> mergedBindings = builders.Values
                .OrderBy(b => b.Set)
                .ThenBy(b => b.Binding)
                .Select(b => b.ToDescriptorBindingInfo())
                .ToList();

            return new DescriptorLayoutBuildResult(layouts.ToArray(), mergedBindings, requiresUpdateAfterBind);
        }

        private sealed class DescriptorSetLayoutBindingBuilder
        {
            public uint Set { get; }
            public uint Binding { get; }
            public DescriptorType DescriptorType { get; }
            public uint Count { get; }
            public ShaderStageFlags StageFlags { get; private set; }

            public DescriptorSetLayoutBindingBuilder(DescriptorBindingInfo info)
            {
                Set = info.Set;
                Binding = info.Binding;
                DescriptorType = info.DescriptorType;
                Count = Math.Max(info.Count, 1u);
                StageFlags = info.StageFlags;
            }

            public void Merge(DescriptorBindingInfo info)
            {
                if (info.DescriptorType != DescriptorType || Math.Max(info.Count, 1u) != Count)
                    throw new InvalidOperationException($"Conflicting descriptor definitions detected for set {Set}, binding {Binding}.");

                StageFlags |= info.StageFlags;
            }

            public DescriptorSetLayoutBinding ToBinding()
                => new()
                {
                    Binding = Binding,
                    DescriptorType = DescriptorType,
                    DescriptorCount = Count,
                    StageFlags = StageFlags,
                };

            public DescriptorBindingInfo ToDescriptorBindingInfo()
                => new(Set, Binding, DescriptorType, StageFlags, Count, string.Empty);
        }

        private readonly record struct DescriptorLayoutBuildResult(DescriptorSetLayout[] Layouts, List<DescriptorBindingInfo> Bindings, bool RequiresUpdateAfterBind);

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
            {
                if (mask.HasFlag(stage))
                    yield return stage;
            }
        }

    }
