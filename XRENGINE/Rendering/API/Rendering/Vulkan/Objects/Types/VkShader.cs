using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Diagnostics;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public class VkShader(VulkanRenderer api, XRShader data) : VkObject<XRShader>(api, data)
    {
        private ShaderModule _shaderModule;
        private TextFile? _attachedSource;
        private readonly List<DescriptorBindingInfo> _descriptorBindings = new();
        private string _entryPoint = "main";
        private PipelineShaderStageCreateInfo _shaderStageCreateInfo;
        private AutoUniformBlockInfo? _autoUniformBlock;

        public override VkObjectType Type => VkObjectType.ShaderModule;
        public override bool IsGenerated => _shaderModule.Handle != 0;

        public PipelineShaderStageCreateInfo ShaderStageCreateInfo => _shaderStageCreateInfo;
        public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _descriptorBindings;
        public ShaderStageFlags StageFlags { get; private set; }
        public AutoUniformBlockInfo? AutoUniformBlock => _autoUniformBlock;

        protected override uint CreateObjectInternal()
        {
            CompileAndCreateModule();
            return CacheObject(this);
        }

        private void CompileAndCreateModule()
        {
            DestroyShaderResources();

            try
            {
                byte[] spirv = VulkanShaderCompiler.Compile(Data, out _entryPoint, out _autoUniformBlock, out string? rewrittenSource);
                StageFlags = ToVulkan(Data.Type);
                _descriptorBindings.Clear();
                _descriptorBindings.AddRange(VulkanShaderReflection.ExtractBindings(spirv, StageFlags, rewrittenSource ?? Data.Source?.Text));

                ShaderModuleCreateInfo createInfo = new()
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                };

                fixed (byte* codePtr = spirv)
                {
                    createInfo.PCode = (uint*)codePtr;
                    if (Api!.CreateShaderModule(Device, ref createInfo, null, out _shaderModule) != Result.Success)
                        throw new InvalidOperationException($"Failed to create shader module for '{Data.Name ?? "UnnamedShader"}'.");
                }

                _shaderStageCreateInfo = new()
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = StageFlags,
                    Module = _shaderModule,
                    PName = (byte*)SilkMarshal.StringToPtr(_entryPoint)
                };
            }
            catch (Exception ex)
            {
                Debug.VulkanException(ex, $"Vulkan shader '{Data.Name ?? "UnnamedShader"}' failed to compile.");
                throw;
            }
        }

        private static ShaderStageFlags ToVulkan(EShaderType type)
            => type switch
            {
                EShaderType.Vertex => ShaderStageFlags.VertexBit,
                EShaderType.Fragment => ShaderStageFlags.FragmentBit,
                EShaderType.Geometry => ShaderStageFlags.GeometryBit,
                EShaderType.TessControl => ShaderStageFlags.TessellationControlBit,
                EShaderType.TessEvaluation => ShaderStageFlags.TessellationEvaluationBit,
                EShaderType.Compute => ShaderStageFlags.ComputeBit,
                EShaderType.Task => ShaderStageFlags.TaskBitNV,
                EShaderType.Mesh => ShaderStageFlags.MeshBitNV,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

        protected override void DeleteObjectInternal()
        {
            DestroyShaderResources();
            RemoveCachedObject(BindingId);
        }

        private void DestroyShaderResources()
        {
            if (_shaderModule.Handle != 0)
            {
                Api!.DestroyShaderModule(Device, _shaderModule, null);
                _shaderModule = default;
            }

            if (_shaderStageCreateInfo.PName is not null)
            {
                SilkMarshal.Free((nint)_shaderStageCreateInfo.PName);
                _shaderStageCreateInfo.PName = null;
            }

            _shaderStageCreateInfo = default;
            StageFlags = 0;
        }

        protected override void LinkData()
        {
            Data.PropertyChanged += OnShaderPropertyChanged;
            AttachToSource(Data.Source);
        }

        protected override void UnlinkData()
        {
            Data.PropertyChanged -= OnShaderPropertyChanged;
            AttachToSource(null);
            DestroyShaderResources();
        }

        private void OnShaderPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(XRShader.Source))
                return;

            AttachToSource(Data.Source);
            Invalidate();
        }

        private void AttachToSource(TextFile? source)
        {
            if (_attachedSource == source)
                return;

            _attachedSource?.TextChanged -= OnSourceTextChanged;
            _attachedSource = source;
            _attachedSource?.TextChanged += OnSourceTextChanged;
        }

        private void OnSourceTextChanged()
            => Invalidate();

        private void Invalidate()
        {
            DestroyShaderResources();
            _descriptorBindings.Clear();
            _entryPoint = "main";
            _autoUniformBlock = null;
            _bindingId = null;
        }
    }
}