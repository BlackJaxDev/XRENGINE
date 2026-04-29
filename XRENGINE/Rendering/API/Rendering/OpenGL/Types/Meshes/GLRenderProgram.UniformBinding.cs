using Silk.NET.OpenGL;
using System.Collections.Generic;
using System.Text;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            private void CacheActiveUniforms()
            {
                if (!IsLinked)
                    return;

                _uniformMetadata.Clear();
                _activeSamplerUniforms = [];

                Api.GetProgram(BindingId, GLEnum.ActiveUniforms, out int uniformCount);
                if (uniformCount <= 0)
                    return;

                Api.GetProgram(BindingId, GLEnum.ActiveUniformMaxLength, out int maxLength);
                if (maxLength <= 0)
                    maxLength = 256;

                byte[] nameBuffer = new byte[maxLength];

                fixed (byte* namePtr = nameBuffer)
                {
                    for (uint index = 0; index < (uint)uniformCount; index++)
                    {
                        uint length;
                        int size;
                        GLEnum type;

                        Api.GetActiveUniform(BindingId, index, (uint)nameBuffer.Length, out length, out size, out type, namePtr);
                        if (length == 0 || length > nameBuffer.Length)
                            continue;

                        string name = Encoding.UTF8.GetString(nameBuffer, 0, (int)length);
                        _uniformMetadata[name] = new UniformInfo(type, size);
                        if (name.EndsWith("[0]", StringComparison.Ordinal))
                        {
                            string baseName = name[..^3];
                            _uniformMetadata[baseName] = new UniformInfo(type, size);
                        }
                    }
                }

                RebuildActiveSamplerUniforms();
            }

            private bool TryRestoreCachedUniformMetadata(UniformMetadataEntry[]? metadata)
            {
                if (metadata is not { Length: > 0 })
                {
                    _activeSamplerUniforms = [];
                    return false;
                }

                _uniformMetadata.Clear();
                foreach (var entry in metadata)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    _uniformMetadata[entry.Name] = new UniformInfo(entry.Type, entry.Size);
                }

                RebuildActiveSamplerUniforms();
                return _uniformMetadata.Count > 0;
            }

            private void RebuildActiveSamplerUniforms()
            {
                if (_uniformMetadata.Count == 0)
                {
                    _activeSamplerUniforms = [];
                    return;
                }

                List<SamplerUniformInfo> samplerUniforms = [];
                foreach (var pair in _uniformMetadata)
                {
                    if (IsSamplerType(pair.Value.Type))
                        samplerUniforms.Add(new SamplerUniformInfo(pair.Key, pair.Value.Type));
                }

                _activeSamplerUniforms = [.. samplerUniforms];
            }

            private UniformMetadataEntry[] SnapshotUniformMetadata()
            {
                if (_uniformMetadata.Count == 0)
                    return [];

                var snapshot = new UniformMetadataEntry[_uniformMetadata.Count];
                int index = 0;
                foreach (var pair in _uniformMetadata)
                    snapshot[index++] = new UniformMetadataEntry(pair.Key, pair.Value.Type, pair.Value.Size);
                return snapshot;
            }

            private void PromoteCurrentUniformMetadataToCachedProgram()
            {
                if (_cachedProgram is not BinaryProgram cachedProgram)
                    return;

                if (cachedProgram.Uniforms is { Length: > 0 })
                    return;

                var metadata = SnapshotUniformMetadata();
                if (metadata.Length == 0)
                    return;

                var updated = cachedProgram with { Uniforms = metadata };
                _cachedProgram = updated;
                if (BinaryCache is not null)
                    BinaryCache[Hash] = updated;
                WriteToBinaryShaderCache(updated);
            }

            public void BeginBindingBatch()
            {
                _uniformBindingAttempts = 0;
                _uniformBindings = 0;
                _samplerBindingAttempts = 0;
                _samplerBindings = 0;
                _boundSamplerLocations.Clear();
                _boundSamplerUnits.Clear();
                _boundSamplerNames.Clear();
                _suppressedFallbackSamplerNames.Clear();
            }

            private bool IsSamplerNameBound(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                if (_boundSamplerNames.Contains(name))
                    return true;

                return name.EndsWith("[0]", StringComparison.Ordinal)
                    && _boundSamplerNames.Contains(name[..^3]);
            }

            private void SuppressFallbackSamplerWarning(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _suppressedFallbackSamplerNames.Add(name);
                if (name.EndsWith("[0]", StringComparison.Ordinal))
                {
                    string baseName = name[..^3];
                    if (!string.IsNullOrWhiteSpace(baseName))
                        _suppressedFallbackSamplerNames.Add(baseName);
                }
            }

            private bool IsFallbackSamplerWarningSuppressed(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                if (_suppressedFallbackSamplerNames.Contains(name))
                    return true;

                return name.EndsWith("[0]", StringComparison.Ordinal)
                    && _suppressedFallbackSamplerNames.Contains(name[..^3]);
            }

            private static bool IsSamplerType(GLEnum type)
                => type is GLEnum.Sampler1D or GLEnum.Sampler1DArray or GLEnum.Sampler1DArrayShadow or GLEnum.Sampler1DShadow
                    or GLEnum.Sampler2D or GLEnum.Sampler2DArray or GLEnum.Sampler2DArrayShadow or GLEnum.Sampler2DMultisample
                    or GLEnum.Sampler2DMultisampleArray or GLEnum.Sampler2DRect or GLEnum.Sampler2DRectShadow or GLEnum.Sampler2DShadow
                    or GLEnum.Sampler3D or GLEnum.SamplerBuffer or GLEnum.SamplerCube or GLEnum.SamplerCubeShadow
                    or GLEnum.IntSampler1D or GLEnum.IntSampler1DArray or GLEnum.IntSampler2D or GLEnum.IntSampler2DArray
                    or GLEnum.IntSampler2DMultisample or GLEnum.IntSampler2DMultisampleArray or GLEnum.IntSampler2DRect
                    or GLEnum.IntSampler3D or GLEnum.IntSamplerBuffer or GLEnum.IntSamplerCube
                    or GLEnum.UnsignedIntSampler1D or GLEnum.UnsignedIntSampler1DArray or GLEnum.UnsignedIntSampler2D
                    or GLEnum.UnsignedIntSampler2DArray or GLEnum.UnsignedIntSampler2DMultisample or GLEnum.UnsignedIntSampler2DMultisampleArray
                    or GLEnum.UnsignedIntSampler2DRect or GLEnum.UnsignedIntSampler3D or GLEnum.UnsignedIntSamplerBuffer or GLEnum.UnsignedIntSamplerCube;

            private static XRTexture2D FallbackTexture2D
                => _fallbackTexture2D ??= new XRTexture2D(1u, 1u, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
                {
                    Resizable = false,
                };

            private static XRTexture2DArray FallbackTexture2DArray
                => _fallbackTexture2DArray ??= new XRTexture2DArray(1u, 1u, 1u, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
                {
                    Resizable = false,
                };

            private static XRTextureCube FallbackTextureCube
                => _fallbackTextureCube ??= new XRTextureCube(1u, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true, 1)
                {
                    Resizable = false,
                };

            private IGLTexture? GetFallbackSamplerTexture(GLEnum samplerType)
            {
                XRTexture texture = samplerType switch
                {
                    GLEnum.SamplerCube or GLEnum.SamplerCubeShadow or GLEnum.IntSamplerCube or GLEnum.UnsignedIntSamplerCube
                        => FallbackTextureCube,
                    GLEnum.Sampler2DArray or GLEnum.Sampler2DArrayShadow or GLEnum.Sampler2DMultisampleArray
                        or GLEnum.IntSampler2DArray or GLEnum.IntSampler2DMultisampleArray
                        or GLEnum.UnsignedIntSampler2DArray or GLEnum.UnsignedIntSampler2DMultisampleArray
                        => FallbackTexture2DArray,
                    _ => FallbackTexture2D,
                };

                return Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) as IGLTexture;
            }

            private bool TryReserveFallbackSamplerUnit(out int textureUnit)
            {
                int maxTextureUnits = Math.Max(1, Renderer.MaxFragmentTextureImageUnits);
                for (int candidate = maxTextureUnits - 1; candidate >= 0; candidate--)
                {
                    if (_boundSamplerUnits.Contains(candidate))
                        continue;

                    _boundSamplerUnits.Add(candidate);
                    textureUnit = candidate;
                    return true;
                }

                textureUnit = -1;
                return false;
            }

            public void BindFallbackSamplers()
            {
                if (!IsLinked || _activeSamplerUniforms.Length == 0)
                    return;

                foreach (SamplerUniformInfo samplerUniform in _activeSamplerUniforms)
                {
                    string name = samplerUniform.Name;

                    if (IsSamplerNameBound(name))
                        continue;

                    int location = GetUniformLocation(name);
                    if (location < 0 || _boundSamplerLocations.Contains(location))
                        continue;

                    // layout(binding = N) samplers can already point at a unit that has a real
                    // texture bound even when no explicit glUniform1i call happened in this batch.
                    // Do not override those assignments with fallback textures.
                    if (_boundSamplerUnits.Count > 0)
                    {
                        Api.GetUniform(BindingId, location, out int assignedUnit);
                        if (assignedUnit >= 0 && _boundSamplerUnits.Contains(assignedUnit))
                            continue;
                    }

                    var fallbackTexture = GetFallbackSamplerTexture(samplerUniform.Type);
                    if (fallbackTexture is null)
                        continue;

                    if (!TryReserveFallbackSamplerUnit(out int fallbackUnit))
                    {
                        string programName = Data.Name ?? BindingId.ToString();
                        Debug.OpenGLErrorEvery(
                            $"GLFallbackSamplerNoUnit:{programName}",
                            TimeSpan.FromSeconds(30),
                            $"[Shader Texture Binding] No free texture units remain for fallback samplers in program '{programName}'. " +
                            "One or more sampler uniforms are unbound — check that all material textures are loaded and assigned.");
                        break;
                    }

                    bool suppressWarning = IsFallbackSamplerWarningSuppressed(name);
                    Sampler(location, fallbackTexture, fallbackUnit);

                    if (!suppressWarning)
                    {
                        string programName = Data.Name ?? BindingId.ToString();
                        string errorKey = $"GLFallbackSampler:{programName}:{name}:{samplerUniform.Type}";
                        Debug.OpenGLErrorEvery(
                            errorKey,
                            TimeSpan.FromSeconds(30),
                            $"[Shader Texture Binding] Sampler '{name}' in program '{programName}' was not bound by any material texture. " +
                            $"A fallback texture was substituted (SamplerType={samplerUniform.Type}, TextureUnit={fallbackUnit}). " +
                            "This likely means a texture failed to load or the material is missing a required texture slot.");
                    }
                }
            }

            public bool HasActiveSamplerUniforms()
                => IsLinked && _activeSamplerUniforms.Length > 0;

            private bool MarkUniformBinding(int location)
            {
                if (location < 0)
                    return false;

                _uniformBindingAttempts++;
                _uniformBindings++;
                
                return true;
            }

            private bool MarkSamplerBinding(int location)
            {
                if (location < 0)
                    return false;

                _samplerBindingAttempts++;
                _samplerBindings++;
                
                return true;
            }

            public void WarnIfNoUniformOrSamplerBindings(string? materialName)
            {
                int attempts = _uniformBindingAttempts + _samplerBindingAttempts;
                int successes = _uniformBindings + _samplerBindings;
                if (attempts == 0 || successes > 0)
                    return;

                string programName = Data.Name ?? BindingId.ToString();
                string matName = string.IsNullOrWhiteSpace(materialName) ? "<unnamed material>" : materialName!;
                string key = $"{programName}:{matName}";

                if (_loggedEmptyBindingBatches.TryAdd(key, 1))
                {
                    Debug.OpenGLError($"[Shader Binding] Program '{programName}' rendered material '{matName}' with no uniforms or samplers bound. " +
                        "Check that material parameter and sampler names match the shader.");
                }
            }

            public EUniformRequirements GetMissingEngineUniformRequirements(EUniformRequirements requestedRequirements)
            {
                if (requestedRequirements == EUniformRequirements.None)
                    return EUniformRequirements.None;

                if (!MatchesCurrentEngineUniformContext())
                    return requestedRequirements;

                return requestedRequirements & ~_engineUniformRequirements;
            }

            public void MarkEngineUniformsApplied(EUniformRequirements appliedRequirements)
            {
                if (appliedRequirements == EUniformRequirements.None)
                    return;

                ulong frameId = Engine.Rendering.State.RenderFrameId;
                XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
                XRCamera? camera = Engine.Rendering.State.RenderingCamera;
                XRCamera? stereoRightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                XRWorldInstance? world = Engine.Rendering.State.RenderingWorld;
                bool stereoPass = Engine.Rendering.State.IsStereoPass;
                bool useUnjitteredProjection = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                var renderArea = Engine.Rendering.State.RenderArea;

                if (MatchesEngineUniformContext(frameId, pipeline, camera, stereoRightEyeCamera, world, stereoPass, useUnjitteredProjection, renderArea))
                {
                    _engineUniformRequirements |= appliedRequirements;
                    return;
                }

                _engineUniformFrameId = frameId;
                _engineUniformRequirements = appliedRequirements;
                _engineUniformPipeline = pipeline;
                _engineUniformCamera = camera;
                _engineUniformStereoRightEyeCamera = stereoRightEyeCamera;
                _engineUniformWorld = world;
                _engineUniformStereoPass = stereoPass;
                _engineUniformUseUnjitteredProjection = useUnjitteredProjection;
                _engineUniformRenderAreaX = renderArea.X;
                _engineUniformRenderAreaY = renderArea.Y;
                _engineUniformRenderAreaWidth = renderArea.Width;
                _engineUniformRenderAreaHeight = renderArea.Height;
            }

            public void ResetEngineUniformBindingState()
            {
                _engineUniformFrameId = ulong.MaxValue;
                _engineUniformRequirements = EUniformRequirements.None;
                _engineUniformPipeline = null;
                _engineUniformCamera = null;
                _engineUniformStereoRightEyeCamera = null;
                _engineUniformWorld = null;
                _engineUniformStereoPass = false;
                _engineUniformUseUnjitteredProjection = false;
                _engineUniformRenderAreaX = 0;
                _engineUniformRenderAreaY = 0;
                _engineUniformRenderAreaWidth = 0;
                _engineUniformRenderAreaHeight = 0;
            }

            private bool MatchesCurrentEngineUniformContext()
            {
                ulong frameId = Engine.Rendering.State.RenderFrameId;
                XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
                XRCamera? camera = Engine.Rendering.State.RenderingCamera;
                XRCamera? stereoRightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                XRWorldInstance? world = Engine.Rendering.State.RenderingWorld;
                bool stereoPass = Engine.Rendering.State.IsStereoPass;
                bool useUnjitteredProjection = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                var renderArea = Engine.Rendering.State.RenderArea;

                return MatchesEngineUniformContext(frameId, pipeline, camera, stereoRightEyeCamera, world, stereoPass, useUnjitteredProjection, renderArea);
            }

            private bool MatchesEngineUniformContext(
                ulong frameId,
                XRRenderPipelineInstance? pipeline,
                XRCamera? camera,
                XRCamera? stereoRightEyeCamera,
                XRWorldInstance? world,
                bool stereoPass,
                bool useUnjitteredProjection,
                XREngine.Data.Geometry.BoundingRectangle renderArea)
                => _engineUniformFrameId == frameId
                && ReferenceEquals(_engineUniformPipeline, pipeline)
                && ReferenceEquals(_engineUniformCamera, camera)
                && ReferenceEquals(_engineUniformStereoRightEyeCamera, stereoRightEyeCamera)
                && ReferenceEquals(_engineUniformWorld, world)
                && _engineUniformStereoPass == stereoPass
                && _engineUniformUseUnjitteredProjection == useUnjitteredProjection
                && _engineUniformRenderAreaX == renderArea.X
                && _engineUniformRenderAreaY == renderArea.Y
                && _engineUniformRenderAreaWidth == renderArea.Width
                && _engineUniformRenderAreaHeight == renderArea.Height;

            private bool ValidateUniformType(int location, params GLEnum[] expectedTypes)
            {
                /*
                using var sample = Engine.Profiler.Start("GLRenderProgram.ValidateUniformType (by location)");

                if (location < 0)
                    return false;

                if (_locationNameCache.TryGetValue(location, out string? name))
                    return ValidateUniformType(name, location, expectedTypes);
                */
                return true;
            }

            private bool ValidateUniformType(string name, int? location, params GLEnum[] expectedTypes)
            {
                /*
                using var sample = Engine.Profiler.Start("GLRenderProgram.ValidateUniformType (by name)");

                if (!_uniformMetadata.TryGetValue(name, out var meta))
                    return true;

                foreach (var expected in expectedTypes)
                    if (meta.Type == expected)
                        return true;

                if (_loggedUniformMismatches.TryAdd(name, 0))
                {
                    string expectedDesc = string.Join(", ", expectedTypes);
                    string locDesc = location.HasValue ? location.Value.ToString() : "unknown";
                    Debug.LogWarning($"Uniform '{name}' (location {locDesc}) expects GL type {meta.Type} (size {meta.Size}) but received upload for {expectedDesc}. Skipping to avoid GL_INVALID_OPERATION.");
                }
                */
                //return false;
                return true;
            }
        }
    }
}
