using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Diagnostics;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public sealed record VulkanShaderArtifact(
        string Identity,
        EShaderType ShaderType,
        string EntryPoint,
        string? SourcePath,
        string? RewrittenSource,
        byte[] SpirV,
        IReadOnlyList<DescriptorBindingInfo> DescriptorBindings,
        AutoUniformBlockInfo? AutoUniformBlock,
        IReadOnlyDictionary<string, uint> VertexInputLocations,
        ShaderStageFlags StageFlags,
        int ShaderConfigVersion,
        bool UsesVulkanClipDepthRemap);

    public sealed record VulkanShaderCompileFailure(
        string? ArtifactIdentity,
        EShaderCompileFailureKind FailureKind,
        string FailureReason,
        string? DiagnosticPath,
        string? RewrittenSource);

    public class VkShader(VulkanRenderer api, XRShader data) : VkObject<XRShader>(api, data)
    {
        private ShaderModule _shaderModule;
        private TextFile? _attachedSource;
        private readonly List<DescriptorBindingInfo> _descriptorBindings = new();
        private string _entryPoint = "main";
        private PipelineShaderStageCreateInfo _shaderStageCreateInfo;
        private AutoUniformBlockInfo? _autoUniformBlock;
        private string? _rewrittenSource;
        private Dictionary<string, uint>? _vertexInputLocations;
        private int _compiledShaderConfigVersion = -1;
        private bool _compiledUsesVulkanClipDepthRemap;
        private bool? _requestedVulkanClipDepthRemap;

        /// <summary>
        /// Matches a vertex shader input attribute declaration and captures its explicit
        /// location and variable name, e.g. <c>layout(location = 1) in vec3 Normal;</c>.
        /// Allows extra layout qualifiers and interpolation/precision qualifiers.
        /// </summary>
        private static readonly Regex VertexInputRegex = new(
            @"layout\s*\([^)]*\blocation\s*=\s*(?<loc>\d+)[^)]*\)\s*(?:(?:flat|noperspective|smooth|centroid)\s+)*in\s+(?:(?:highp|mediump|lowp)\s+)?\w+\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public override VkObjectType Type => VkObjectType.ShaderModule;
        public override bool IsGenerated => _shaderModule.Handle != 0;

        public PipelineShaderStageCreateInfo ShaderStageCreateInfo => _shaderStageCreateInfo;
        public IReadOnlyList<DescriptorBindingInfo> DescriptorBindings => _descriptorBindings;
        public ShaderStageFlags StageFlags { get; private set; }
        public AutoUniformBlockInfo? AutoUniformBlock => _autoUniformBlock;
        public bool IsCompiled { get; private set; }
        public bool IsCompilePending { get; private set; }
        public ShaderCompileStatus CompileStatus { get; private set; } = ShaderCompileStatus.Empty;
        public VulkanShaderArtifact? LastArtifact { get; private set; }
        public VulkanShaderCompileFailure? LastCompileFailure { get; private set; }
        internal event Action<VkShader>? ShaderInvalidated;

        /// <summary>
        /// Vertex stage input attribute name &#8594; location map parsed from the shader
        /// source. Empty for non-vertex stages or sources without explicit locations.
        /// Mirrors the OpenGL path, which binds vertex buffers to attributes by name.
        /// </summary>
        internal IReadOnlyDictionary<string, uint> VertexInputLocations
            => _vertexInputLocations ??= ParseVertexInputLocations();
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
            _vertexInputLocations = null;
            int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
            bool usesVulkanClipDepthRemap = UsesVulkanClipDepthRemap();
            string? artifactIdentity = null;
            string? rewrittenSource = null;
            IsCompilePending = true;
            IsCompiled = false;
            LastArtifact = null;
            LastCompileFailure = null;
            CompileStatus = ShaderCompileStatus.Pending("Vulkan", null);
            EShaderCompileFailureKind failureKind = EShaderCompileFailureKind.SpirvCompilation;

            try
            {
                byte[] spirv = VulkanShaderCompiler.Compile(
                    Data,
                    usesVulkanClipDepthRemap,
                    out _entryPoint,
                    out _autoUniformBlock,
                    out rewrittenSource);
                _rewrittenSource = rewrittenSource;
                artifactIdentity = VulkanShaderCompiler.BuildArtifactIdentity(
                    Data,
                    shaderConfigVersion,
                    usesVulkanClipDepthRemap,
                    rewrittenSource);

                StageFlags = ToVulkan(Data.Type);
                failureKind = EShaderCompileFailureKind.Reflection;
                _descriptorBindings.Clear();
                _descriptorBindings.AddRange(VulkanShaderReflection.ExtractBindings(spirv, StageFlags, rewrittenSource ?? Data.Source?.Text));

                failureKind = EShaderCompileFailureKind.ShaderModuleCreation;
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

                _compiledShaderConfigVersion = shaderConfigVersion;
                _compiledUsesVulkanClipDepthRemap = usesVulkanClipDepthRemap;
                IsCompiled = true;
                IsCompilePending = false;
                CompileStatus = ShaderCompileStatus.Ready("Vulkan", artifactIdentity);
                LastArtifact = new VulkanShaderArtifact(
                    artifactIdentity,
                    Data.Type,
                    _entryPoint,
                    Data.Source?.FilePath ?? Data.FilePath,
                    rewrittenSource,
                    spirv,
                    _descriptorBindings.ToArray(),
                    _autoUniformBlock,
                    VertexInputLocations.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                    StageFlags,
                    shaderConfigVersion,
                    usesVulkanClipDepthRemap);
            }
            catch (Exception ex)
            {
                artifactIdentity ??= VulkanShaderCompiler.BuildArtifactIdentity(
                    Data,
                    shaderConfigVersion,
                    usesVulkanClipDepthRemap,
                    rewrittenSource ?? _rewrittenSource ?? Data.Source?.Text);
                string? diagnosticPath = WriteCompileFailureDiagnosticsFile(ex, failureKind, artifactIdentity, rewrittenSource ?? _rewrittenSource);
                IsCompilePending = false;
                IsCompiled = false;
                LastCompileFailure = new VulkanShaderCompileFailure(
                    artifactIdentity,
                    failureKind,
                    ex.Message,
                    diagnosticPath,
                    rewrittenSource ?? _rewrittenSource);
                CompileStatus = ShaderCompileStatus.Failed("Vulkan", failureKind, ex.Message, artifactIdentity, diagnosticPath);
                Debug.VulkanException(ex, $"Vulkan shader '{Data.Name ?? "UnnamedShader"}' failed to compile.");
                throw;
            }
        }

        private Dictionary<string, uint> ParseVertexInputLocations()
        {
            Dictionary<string, uint> map = new(StringComparer.Ordinal);
            if (StageFlags != ShaderStageFlags.VertexBit)
                return map;

            string? source = _rewrittenSource ?? Data.Source?.Text;
            if (string.IsNullOrWhiteSpace(source))
                return map;

            foreach (Match match in VertexInputRegex.Matches(source))
            {
                if (!uint.TryParse(match.Groups["loc"].Value, out uint location))
                    continue;

                string name = match.Groups["name"].Value;
                if (!string.IsNullOrEmpty(name))
                    map[name] = location;
            }

            return map;
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

        private string? WriteCompileFailureDiagnosticsFile(
            Exception exception,
            EShaderCompileFailureKind failureKind,
            string? artifactIdentity,
            string? rewrittenSource)
        {
            if (!RenderDiagnosticsFlags.VkDumpShaderOnError)
                return null;

            try
            {
                string directory = Path.Combine("Build", "Logs", "vulkan-shader-failures");
                Directory.CreateDirectory(directory);
                string identitySuffix = string.IsNullOrWhiteSpace(artifactIdentity) ? "noidentity" : artifactIdentity;
                string fileName = SanitizeDiagnosticFileName($"{Data.Name ?? "UnnamedShader"}-{Data.Type}-{identitySuffix}.glsl.txt");
                string path = Path.Combine(directory, fileName);

                StringBuilder builder = new();
                builder.AppendLine($"[Vulkan] Shader compile failure: kind={failureKind}");
                builder.AppendLine($"[Vulkan]   Shader='{Data.Name ?? "UnnamedShader"}' Stage={Data.Type} ArtifactIdentity={artifactIdentity ?? "<none>"}");
                builder.AppendLine($"[Vulkan]   File='{Data.Source?.FilePath ?? Data.FilePath ?? "<embedded>"}'");
                builder.AppendLine($"[Vulkan]   Failure={exception.Message}");
                builder.AppendLine($"[Vulkan]   AutoUniformBlock={DescribeAutoUniformBlock(_autoUniformBlock)}");
                AppendSourcePreview(builder, rewrittenSource ?? _rewrittenSource ?? Data.Source?.Text ?? string.Empty, 220);

                File.WriteAllText(path, builder.ToString().TrimEnd(), Encoding.UTF8);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeDiagnosticFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder builder = new(fileName.Length);
            foreach (char ch in fileName)
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            return builder.ToString();
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
            bool sourceChanged = e.PropertyName == nameof(XRShader.Source);
            bool typeChanged = e.PropertyName == nameof(XRShader.Type);
            if (!sourceChanged && !typeChanged)
                return;

            if (sourceChanged)
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

        internal void EnsureCompilePolicyCurrent()
        {
            if (!IsGenerated)
                return;

            int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
            bool usesVulkanClipDepthRemap = UsesVulkanClipDepthRemap();
            if (_compiledShaderConfigVersion == shaderConfigVersion &&
                _compiledUsesVulkanClipDepthRemap == usesVulkanClipDepthRemap)
                return;

            Invalidate();
        }

        private bool UsesVulkanClipDepthRemap()
            => RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap &&
               (_requestedVulkanClipDepthRemap ?? Data.Type == EShaderType.Vertex);

        internal void SetVulkanClipDepthRemapEnabled(bool enabled)
        {
            if (_requestedVulkanClipDepthRemap == enabled)
                return;

            _requestedVulkanClipDepthRemap = enabled;
            Invalidate();
        }

        private void Invalidate()
        {
            DestroyShaderResources();
            _descriptorBindings.Clear();
            _entryPoint = "main";
            _autoUniformBlock = null;
            _rewrittenSource = null;
            _compiledShaderConfigVersion = -1;
            _compiledUsesVulkanClipDepthRemap = false;
            IsCompiled = false;
            IsCompilePending = false;
            CompileStatus = ShaderCompileStatus.Empty;
            LastArtifact = null;
            LastCompileFailure = null;
            _vertexInputLocations = null;
            _bindingId = null;
            ResetGenerationFailure();
            ShaderInvalidated?.Invoke(this);
        }
    }
}
