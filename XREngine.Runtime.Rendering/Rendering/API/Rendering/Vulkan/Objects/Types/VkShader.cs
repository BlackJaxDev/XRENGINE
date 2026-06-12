using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        bool UsesVulkanClipDepthRemap,
        bool LoadedFromDiskCache = false,
        string TransformFeedbackPlanIdentity = "");

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
        private string _compiledTransformFeedbackPlanIdentity = string.Empty;
        private bool? _requestedVulkanClipDepthRemap;
        private VulkanTransformFeedbackCompilePlan? _transformFeedbackPlan;
        private readonly object _asyncCompileLock = new();
        private Task<VulkanShaderArtifact>? _asyncCompileTask;
        private int _asyncCompileShaderConfigVersion = -1;
        private bool _asyncCompileUsesVulkanClipDepthRemap;
        private string _asyncCompileTransformFeedbackPlanIdentity = string.Empty;
        private int _failedShaderConfigVersion = -1;
        private bool _failedUsesVulkanClipDepthRemap;
        private string _failedTransformFeedbackPlanIdentity = string.Empty;

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
                VulkanTransformFeedbackCompilePlan? transformFeedbackPlan = _transformFeedbackPlan;
                VulkanShaderArtifact artifact = BuildCpuArtifact(shaderConfigVersion, usesVulkanClipDepthRemap, transformFeedbackPlan);
                artifactIdentity = artifact.Identity;
                rewrittenSource = artifact.RewrittenSource;
                failureKind = EShaderCompileFailureKind.ShaderModuleCreation;
                ApplyCompiledArtifact(artifact);
                RuntimeEngine.Rendering.Stats.RecordShaderVariant(
                    linked: true,
                    loadedFromDiskCache: artifact.LoadedFromDiskCache,
                    generatedThisRun: !artifact.LoadedFromDiskCache);
            }
            catch (Exception ex)
            {
                SetCompileFailure(ex, failureKind, shaderConfigVersion, usesVulkanClipDepthRemap, artifactIdentity, rewrittenSource ?? _rewrittenSource);
                Debug.VulkanException(ex, $"Vulkan shader '{Data.Name ?? "UnnamedShader"}' failed to compile.");
                throw;
            }
        }

        internal bool TryGenerateFromAsyncCompile(bool enableVulkanClipDepthRemap, out string reason)
        {
            reason = "Ready";

            SetVulkanClipDepthRemapEnabled(enableVulkanClipDepthRemap);
            EnsureCompilePolicyCurrent();
            if (IsGenerated && IsCompiled)
                return true;

            int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
            bool usesVulkanClipDepthRemap = UsesVulkanClipDepthRemap();
            if (LastCompileFailure is not null &&
                _failedShaderConfigVersion == shaderConfigVersion &&
                _failedUsesVulkanClipDepthRemap == usesVulkanClipDepthRemap &&
                string.Equals(_failedTransformFeedbackPlanIdentity, CurrentTransformFeedbackPlanIdentity, StringComparison.Ordinal))
            {
                reason = "ShaderCompileFailed";
                return false;
            }

            VulkanTransformFeedbackCompilePlan? transformFeedbackPlan = _transformFeedbackPlan;
            string transformFeedbackPlanIdentity = transformFeedbackPlan?.Identity ?? string.Empty;
            Task<VulkanShaderArtifact> task;
            lock (_asyncCompileLock)
            {
                if (_asyncCompileTask is null ||
                    _asyncCompileShaderConfigVersion != shaderConfigVersion ||
                    _asyncCompileUsesVulkanClipDepthRemap != usesVulkanClipDepthRemap ||
                    !string.Equals(_asyncCompileTransformFeedbackPlanIdentity, transformFeedbackPlanIdentity, StringComparison.Ordinal))
                {
                    _asyncCompileShaderConfigVersion = shaderConfigVersion;
                    _asyncCompileUsesVulkanClipDepthRemap = usesVulkanClipDepthRemap;
                    _asyncCompileTransformFeedbackPlanIdentity = transformFeedbackPlanIdentity;
                    IsCompilePending = true;
                    IsCompiled = false;
                    CompileStatus = ShaderCompileStatus.Pending("Vulkan", null);
                    LastCompileFailure = null;
                    LastArtifact = null;
                    _asyncCompileTask = Task.Run(() => BuildCpuArtifact(shaderConfigVersion, usesVulkanClipDepthRemap, transformFeedbackPlan));
                }

                task = _asyncCompileTask;
            }

            if (!task.IsCompleted)
            {
                reason = "ShaderCompilePending";
                return false;
            }

            VulkanShaderArtifact artifact;
            try
            {
                artifact = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                SetCompileFailure(
                    ex,
                    EShaderCompileFailureKind.SpirvCompilation,
                    shaderConfigVersion,
                    usesVulkanClipDepthRemap,
                    null,
                    _rewrittenSource);
                reason = "ShaderCompileFailed";
                return false;
            }
            finally
            {
                lock (_asyncCompileLock)
                {
                    if (ReferenceEquals(_asyncCompileTask, task))
                        _asyncCompileTask = null;
                }
            }

            try
            {
                bool wasActive = IsActive;
                if (!wasActive)
                    PreGenerated();

                DestroyShaderResources();
                ApplyCompiledArtifact(artifact);

                if (!wasActive)
                {
                    _bindingId = CacheObject(this);
                    PostGenerated();
                }

                RuntimeEngine.Rendering.Stats.RecordShaderVariant(
                    linked: true,
                    loadedFromDiskCache: artifact.LoadedFromDiskCache,
                    generatedThisRun: !artifact.LoadedFromDiskCache);
                return true;
            }
            catch (Exception ex)
            {
                SetCompileFailure(
                    ex,
                    EShaderCompileFailureKind.ShaderModuleCreation,
                    shaderConfigVersion,
                    usesVulkanClipDepthRemap,
                    artifact.Identity,
                    artifact.RewrittenSource);
                reason = "ShaderModuleCreateFailed";
                return false;
            }
        }

        private VulkanShaderArtifact BuildCpuArtifact(
            int shaderConfigVersion,
            bool usesVulkanClipDepthRemap,
            VulkanTransformFeedbackCompilePlan? transformFeedbackPlan)
        {
            string transformFeedbackPlanIdentity = transformFeedbackPlan?.Identity ?? string.Empty;
            VulkanShaderCompiler.PreparedSource prepared = VulkanShaderCompiler.Prepare(Data, usesVulkanClipDepthRemap, transformFeedbackPlan);
            string artifactIdentity = VulkanShaderCompiler.BuildArtifactIdentity(
                Data,
                shaderConfigVersion,
                usesVulkanClipDepthRemap,
                prepared.RewrittenSource);
            ShaderStageFlags stageFlags = ToVulkan(Data.Type);

            if (VulkanShaderArtifactCache.TryRead(
                artifactIdentity,
                Data,
                shaderConfigVersion,
                usesVulkanClipDepthRemap,
                prepared.RewrittenSource,
                prepared.AutoUniformBlock,
                stageFlags,
                transformFeedbackPlanIdentity,
                out VulkanShaderArtifact cachedArtifact))
            {
                RuntimeEngine.Rendering.Stats.RecordShaderVariant(warming: true, loadedFromDiskCache: true);
                return cachedArtifact;
            }

            byte[] spirv = VulkanShaderCompiler.CompilePrepared(Data, prepared);
            IReadOnlyList<DescriptorBindingInfo> bindings = VulkanShaderReflection.ExtractBindings(spirv, stageFlags, prepared.RewrittenSource);
            Dictionary<string, uint> vertexInputLocations = ParseVertexInputLocations(prepared.RewrittenSource, stageFlags);

            VulkanShaderArtifact artifact = new(
                artifactIdentity,
                Data.Type,
                prepared.EntryPoint,
                Data.Source?.FilePath ?? Data.FilePath,
                prepared.RewrittenSource,
                spirv,
                bindings,
                prepared.AutoUniformBlock,
                vertexInputLocations,
                stageFlags,
                shaderConfigVersion,
                usesVulkanClipDepthRemap);
            artifact = artifact with { TransformFeedbackPlanIdentity = transformFeedbackPlanIdentity };

            VulkanShaderArtifactCache.QueueWrite(artifact);
            Debug.Vulkan("[VulkanShaderCache] MISS key={0} stage={1} bytes={2}.", artifactIdentity, Data.Type, spirv.Length);
            return artifact;
        }

        private void ApplyCompiledArtifact(VulkanShaderArtifact artifact)
        {
            _entryPoint = artifact.EntryPoint;
            _autoUniformBlock = artifact.AutoUniformBlock;
            _rewrittenSource = artifact.RewrittenSource;
            StageFlags = artifact.StageFlags;
            _descriptorBindings.Clear();
            _descriptorBindings.AddRange(artifact.DescriptorBindings);
            _vertexInputLocations = new Dictionary<string, uint>(artifact.VertexInputLocations, StringComparer.Ordinal);

            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)artifact.SpirV.Length,
            };

            fixed (byte* codePtr = artifact.SpirV)
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

            _compiledShaderConfigVersion = artifact.ShaderConfigVersion;
            _compiledUsesVulkanClipDepthRemap = artifact.UsesVulkanClipDepthRemap;
            _compiledTransformFeedbackPlanIdentity = artifact.TransformFeedbackPlanIdentity;
            _failedShaderConfigVersion = -1;
            _failedUsesVulkanClipDepthRemap = false;
            _failedTransformFeedbackPlanIdentity = string.Empty;
            IsCompiled = true;
            IsCompilePending = false;
            CompileStatus = ShaderCompileStatus.Ready("Vulkan", artifact.Identity);
            LastArtifact = artifact;
            LogDeferredLightingShaderArtifact(artifact);
        }

        private void LogDeferredLightingShaderArtifact(VulkanShaderArtifact artifact)
        {
            if (!XREngine.Rendering.DeferredLightingDiagnostics.Enabled)
                return;

            string sourcePath = artifact.SourcePath ?? Data.Source?.FilePath ?? Data.FilePath ?? string.Empty;
            string fileName = Path.GetFileName(sourcePath);
            if (!fileName.StartsWith("DeferredLightCombine", StringComparison.OrdinalIgnoreCase))
                return;

            string rewrittenSource = artifact.RewrittenSource ?? string.Empty;
            string bindings = artifact.DescriptorBindings.Count == 0
                ? "<none>"
                : string.Join("; ", artifact.DescriptorBindings
                    .OrderBy(static binding => binding.Set)
                    .ThenBy(static binding => binding.Binding)
                    .Select(static binding => $"{binding.Set}:{binding.Binding}:{binding.Name}:{binding.DescriptorType}:count={binding.Count}"));
            bool hasMsaaDefine = HasShaderDefine(rewrittenSource, "XRENGINE_MSAA_DEFERRED");
            bool hasLightingAccumSymbol = rewrittenSource.Contains("LightingAccumTexture", StringComparison.Ordinal);
            bool hasLightingMsaaSymbol = rewrittenSource.Contains("LightingTextureMS", StringComparison.Ordinal);

            XREngine.Rendering.DeferredLightingDiagnostics.Write(
                "[VkShader.Artifact] " +
                $"shader='{Data.Name ?? "<unnamed>"}' shaderObj=0x{RuntimeHelpers.GetHashCode(Data):X8} " +
                $"type={Data.Type} sourcePath='{sourcePath}' identity='{artifact.Identity}' disk={artifact.LoadedFromDiskCache} " +
                $"rewrittenLen={rewrittenSource.Length} rewrittenHash={StringComparer.Ordinal.GetHashCode(rewrittenSource):X8} " +
                $"msaaDefine={hasMsaaDefine} " +
                $"lightingAccumSymbol={hasLightingAccumSymbol} " +
                $"lightingMsaaSymbol={hasLightingMsaaSymbol} " +
                $"bindings=[{bindings}]");
        }

        private static bool HasShaderDefine(string sourceText, string defineName)
            => !string.IsNullOrEmpty(sourceText) &&
               (sourceText.Contains($"#define {defineName}", StringComparison.Ordinal) ||
                sourceText.Contains($"# define {defineName}", StringComparison.Ordinal));

        private void SetCompileFailure(
            Exception exception,
            EShaderCompileFailureKind failureKind,
            int shaderConfigVersion,
            bool usesVulkanClipDepthRemap,
            string? artifactIdentity,
            string? rewrittenSource)
        {
            artifactIdentity ??= VulkanShaderCompiler.BuildArtifactIdentity(
                Data,
                shaderConfigVersion,
                usesVulkanClipDepthRemap,
                rewrittenSource ?? _rewrittenSource ?? Data.Source?.Text);
            string? diagnosticPath = WriteCompileFailureDiagnosticsFile(exception, failureKind, artifactIdentity, rewrittenSource ?? _rewrittenSource);
            IsCompilePending = false;
            IsCompiled = false;
            _failedShaderConfigVersion = shaderConfigVersion;
            _failedUsesVulkanClipDepthRemap = usesVulkanClipDepthRemap;
            _failedTransformFeedbackPlanIdentity = CurrentTransformFeedbackPlanIdentity;
            LastCompileFailure = new VulkanShaderCompileFailure(
                artifactIdentity,
                failureKind,
                exception.Message,
                diagnosticPath,
                rewrittenSource ?? _rewrittenSource);
            CompileStatus = ShaderCompileStatus.Failed("Vulkan", failureKind, exception.Message, artifactIdentity, diagnosticPath);
            RuntimeEngine.Rendering.Stats.RecordShaderVariant(failed: true);
        }

        private Dictionary<string, uint> ParseVertexInputLocations()
        {
            Dictionary<string, uint> map = new(StringComparer.Ordinal);
            if (StageFlags != ShaderStageFlags.VertexBit)
                return map;

            string? source = _rewrittenSource ?? Data.Source?.Text;
            return ParseVertexInputLocations(source, StageFlags);
        }

        private static Dictionary<string, uint> ParseVertexInputLocations(string? source, ShaderStageFlags stageFlags)
        {
            if (string.IsNullOrWhiteSpace(source))
                return new Dictionary<string, uint>(StringComparer.Ordinal);

            Dictionary<string, uint> map = new(StringComparer.Ordinal);
            if (stageFlags != ShaderStageFlags.VertexBit)
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
            string transformFeedbackPlanIdentity = CurrentTransformFeedbackPlanIdentity;
            if (_compiledShaderConfigVersion == shaderConfigVersion &&
                _compiledUsesVulkanClipDepthRemap == usesVulkanClipDepthRemap &&
                string.Equals(_compiledTransformFeedbackPlanIdentity, transformFeedbackPlanIdentity, StringComparison.Ordinal))
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

        internal void SetTransformFeedbackCompilePlan(VulkanTransformFeedbackCompilePlan? plan)
        {
            string currentIdentity = CurrentTransformFeedbackPlanIdentity;
            string nextIdentity = plan?.Identity ?? string.Empty;
            if (string.Equals(currentIdentity, nextIdentity, StringComparison.Ordinal))
                return;

            _transformFeedbackPlan = plan;
            Invalidate();
        }

        private string CurrentTransformFeedbackPlanIdentity => _transformFeedbackPlan?.Identity ?? string.Empty;

        private void Invalidate()
        {
            DestroyShaderResources();
            lock (_asyncCompileLock)
                _asyncCompileTask = null;
            _descriptorBindings.Clear();
            _entryPoint = "main";
            _autoUniformBlock = null;
            _rewrittenSource = null;
            _compiledShaderConfigVersion = -1;
            _compiledUsesVulkanClipDepthRemap = false;
            _compiledTransformFeedbackPlanIdentity = string.Empty;
            _failedShaderConfigVersion = -1;
            _failedUsesVulkanClipDepthRemap = false;
            _failedTransformFeedbackPlanIdentity = string.Empty;
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
