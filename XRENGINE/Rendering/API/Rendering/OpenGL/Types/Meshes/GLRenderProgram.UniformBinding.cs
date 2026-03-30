using Silk.NET.OpenGL;
using System.Text;
using XREngine;
using XREngine.Data.Rendering;

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
            }

            private bool TryRestoreCachedUniformMetadata(UniformMetadataEntry[]? metadata)
            {
                if (metadata is not { Length: > 0 })
                    return false;

                _uniformMetadata.Clear();
                foreach (var entry in metadata)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    _uniformMetadata[entry.Name] = new UniformInfo(entry.Type, entry.Size);
                }

                return _uniformMetadata.Count > 0;
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

                if (_boundSamplerNames.ContainsKey(name))
                    return true;

                return name.EndsWith("[0]", StringComparison.Ordinal)
                    && _boundSamplerNames.ContainsKey(name[..^3]);
            }

            private void SuppressFallbackSamplerWarning(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _suppressedFallbackSamplerNames[name] = 1;
                if (name.EndsWith("[0]", StringComparison.Ordinal))
                {
                    string baseName = name[..^3];
                    if (!string.IsNullOrWhiteSpace(baseName))
                        _suppressedFallbackSamplerNames[baseName] = 1;
                }
            }

            private bool IsFallbackSamplerWarningSuppressed(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                if (_suppressedFallbackSamplerNames.ContainsKey(name))
                    return true;

                return name.EndsWith("[0]", StringComparison.Ordinal)
                    && _suppressedFallbackSamplerNames.ContainsKey(name[..^3]);
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
                    if (_boundSamplerUnits.ContainsKey(candidate))
                        continue;

                    _boundSamplerUnits[candidate] = 1;
                    textureUnit = candidate;
                    return true;
                }

                textureUnit = -1;
                return false;
            }

            public void BindFallbackSamplers()
            {
                if (!IsLinked || _uniformMetadata.Count == 0)
                    return;

                foreach (var pair in _uniformMetadata)
                {
                    string name = pair.Key;
                    UniformInfo meta = pair.Value;
                    if (!IsSamplerType(meta.Type))
                        continue;

                    if (IsSamplerNameBound(name))
                        continue;

                    int location = GetUniformLocation(name);
                    if (location < 0 || _boundSamplerLocations.ContainsKey(location))
                        continue;

                    // layout(binding = N) samplers can already point at a unit that has a real
                    // texture bound even when no explicit glUniform1i call happened in this batch.
                    // Do not override those assignments with fallback textures.
                    Api.GetUniform(BindingId, location, out int assignedUnit);
                    if (assignedUnit >= 0 && _boundSamplerUnits.ContainsKey(assignedUnit))
                        continue;

                    var fallbackTexture = GetFallbackSamplerTexture(meta.Type);
                    if (fallbackTexture is null)
                        continue;

                    if (!TryReserveFallbackSamplerUnit(out int fallbackUnit))
                    {
                        string programName = Data.Name ?? BindingId.ToString();
                        Debug.OpenGLWarningEvery(
                            $"GLFallbackSamplerNoUnit:{programName}",
                            TimeSpan.FromSeconds(30),
                            $"[Shader Texture Binding] No free texture units remain for fallback samplers in program '{programName}'.");
                        break;
                    }

                    bool suppressWarning = IsFallbackSamplerWarningSuppressed(name);
                    Sampler(location, fallbackTexture, fallbackUnit);

                    if (!suppressWarning)
                    {
                        string programName = Data.Name ?? BindingId.ToString();
                        string warningKey = $"GLFallbackSampler:{programName}:{name}:{meta.Type}";
                        Debug.OpenGLWarningEvery(
                            warningKey,
                            TimeSpan.FromSeconds(30),
                            $"[Shader Texture Binding] Bound fallback sampler for '{name}' in program '{programName}'. SamplerType={meta.Type}, TextureUnit={fallbackUnit}.");
                    }
                }
            }

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
