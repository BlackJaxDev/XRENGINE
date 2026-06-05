using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private string? _rewrittenSource;

        public override VkObjectType Type => VkObjectType.ShaderModule;
        public override bool IsGenerated => _shaderModule.Handle != 0;

        public PipelineShaderStageCreateInfo ShaderStageCreateInfo => _shaderStageCreateInfo;
        public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _descriptorBindings;
        public ShaderStageFlags StageFlags { get; private set; }
        public AutoUniformBlockInfo? AutoUniformBlock => _autoUniformBlock;
        internal string SourceLabel => ResolveShaderSourceLabel(Data);
        internal string StageDebugLabel => $"{Data.Type}:{SourceLabel}";

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
                _rewrittenSource = rewrittenSource;

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

        internal void WriteRewrittenSourceDiagnostics(string reason)
        {
            if (!RenderDiagnosticsFlags.VkDumpShaderOnError)
                return;

            string source = _rewrittenSource ?? Data.Source?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
                return;

            StringBuilder builder = new();
            builder.AppendLine($"[Vulkan] Shader diagnostics: reason='{reason}'");
            builder.AppendLine($"[Vulkan]   Stage={Data.Type} Entry='{_entryPoint}' Shader='{Data.Name ?? "UnnamedShader"}'");
            builder.AppendLine($"[Vulkan]   File='{Data.Source?.FilePath ?? Data.FilePath ?? "<embedded>"}' SourceLines={CountLines(Data.Source?.Text)} RewrittenLines={CountLines(source)}");
            builder.AppendLine($"[Vulkan]   AutoUniformBlock={DescribeAutoUniformBlock(_autoUniformBlock)}");
            AppendSourcePreview(builder, source, 160);

            Debug.WriteAuxiliaryLog("vulkan-shader-diagnostics.log", builder.ToString().TrimEnd());
        }

        private static string ResolveShaderSourceLabel(XRShader shader)
        {
            if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
                return Path.GetFileName(shader.Source.FilePath!);
            if (!string.IsNullOrWhiteSpace(shader.FilePath))
                return Path.GetFileName(shader.FilePath!);
            if (!string.IsNullOrWhiteSpace(shader.Name))
                return shader.Name!;

            return shader.Type.ToString();
        }

        private static int CountLines(string? source)
        {
            if (string.IsNullOrEmpty(source))
                return 0;

            int count = 1;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                    count++;
            }

            return count;
        }

        private static string DescribeAutoUniformBlock(AutoUniformBlockInfo? autoUniformBlock)
        {
            if (autoUniformBlock is null)
                return "<none>";

            IReadOnlyList<AutoUniformMember> members = autoUniformBlock.Members;
            int previewCount = Math.Min(members.Count, 8);
            string[] previews = new string[previewCount];
            for (int i = 0; i < previewCount; i++)
            {
                AutoUniformMember member = members[i];
                previews[i] = member.IsArray && member.ArrayLength > 0
                    ? $"{member.GlslType} {member.Name}[{member.ArrayLength}]"
                    : $"{member.GlslType} {member.Name}";
            }

            string suffix = members.Count > previewCount ? ", ..." : string.Empty;
            return $"name='{autoUniformBlock.BlockName}' set={autoUniformBlock.Set} binding={autoUniformBlock.Binding} size={autoUniformBlock.Size} members={members.Count} [{string.Join(", ", previews)}{suffix}]";
        }

        private static void AppendSourcePreview(StringBuilder builder, string source, int maxLines)
        {
            builder.AppendLine("--- Rewritten GLSL preview ---");

            using StringReader reader = new(source.Replace("\r\n", "\n", StringComparison.Ordinal));
            int lineNumber = 0;
            string? line;
            while (lineNumber < maxLines && (line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                builder.AppendLine($"{lineNumber,4}: {line}");
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
            _rewrittenSource = null;
            _bindingId = null;
            ResetGenerationFailure();
        }
    }
}
