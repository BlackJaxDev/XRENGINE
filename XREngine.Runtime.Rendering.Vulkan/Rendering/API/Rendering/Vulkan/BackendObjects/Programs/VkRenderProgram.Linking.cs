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
    public partial class VkRenderProgram
    {
        public bool Link(bool allowAsyncShaderCompile = false)
        {
            if (Renderer.IsDeviceLost)
                return false;

            int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
            bool usesVulkanClipDepthRemap = RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap;
            EShaderType? vulkanClipDepthRemapStage = ResolveVulkanClipDepthRemapStage();
            if (IsLinked &&
                _linkedShaderConfigVersion == shaderConfigVersion &&
                _linkedUsesVulkanClipDepthRemap == usesVulkanClipDepthRemap &&
                _linkedVulkanClipDepthRemapStage == vulkanClipDepthRemapStage &&
                _linkedTransformFeedbackLayoutVersion == Data.TransformFeedbackLayoutVersion)
                return true;

            global::System.Diagnostics.Stopwatch buildWatch = global::System.Diagnostics.Stopwatch.StartNew();
            double compileMilliseconds = 0.0;
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

                BuildProgramInterface();
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

            if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            {
                if (!capture.SamplersByName.TryGetValue(samplerName, out texture))
                    return false;

                return true;
            }

            lock (_bindingLock)
            {
                Dictionary<string, XRTexture> samplers = _appliedBindingSnapshot?.SamplersByName ?? _samplersByName;
                if (samplers.TryGetValue(samplerName, out XRTexture? found))
                {
                    texture = found;
                    return true;
                }
            }

            return false;
        }

        internal bool TryGetBoundBuffer(uint binding, out XRDataBuffer? buffer)
        {
            if (TryGetActiveBindingCaptureState(out BindingCaptureState capture))
            {
                buffer = capture.BuffersByBinding.GetValueOrDefault(binding);
                return buffer is not null;
            }

            lock (_bindingLock)
            {
                if (_appliedBindingSnapshot is { } snapshot &&
                    snapshot.Buffers.TryGetValue(binding, out VulkanComputeBufferBinding captured))
                {
                    buffer = captured.Data;
                    return true;
                }

                if (_appliedBindingSnapshot is null &&
                    _buffersByBinding.TryGetValue(binding, out XRDataBuffer? found))
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
                Dictionary<string, XRTexture> samplers = _appliedBindingSnapshot?.SamplersByName ?? _samplersByName;
                hash.Add(samplers.Count);
                ulong xor = 0;
                ulong sum = 0;
                foreach (KeyValuePair<string, XRTexture> pair in samplers)
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
                ulong xor = 0;
                ulong sum = 0;
                if (_appliedBindingSnapshot is { } snapshot)
                {
                    hash.Add(snapshot.Buffers.Count);
                    foreach (KeyValuePair<uint, VulkanComputeBufferBinding> pair in snapshot.Buffers)
                    {
                        AddUnorderedFingerprintItem(
                            ref xor,
                            ref sum,
                            ComputeBoundBufferResourceFingerprintItem(pair.Key, pair.Value.Data));
                    }
                }
                else
                {
                    hash.Add(_buffersByBinding.Count);
                    foreach (KeyValuePair<uint, XRDataBuffer> pair in _buffersByBinding)
                        AddUnorderedFingerprintItem(ref xor, ref sum, ComputeBoundBufferResourceFingerprintItem(pair.Key, pair.Value));
                }

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

    }
}
