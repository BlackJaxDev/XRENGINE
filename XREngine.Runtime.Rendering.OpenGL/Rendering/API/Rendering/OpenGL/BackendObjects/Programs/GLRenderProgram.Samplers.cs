using Silk.NET.OpenGL;
using XREngine;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            #region Samplers
            private static readonly string[] s_directionalShadowMapNames =
            [
                "DirectionalShadowMaps[0]",
                "DirectionalShadowMaps[1]",
            ];

            private static readonly string[] s_directionalShadowMapArrayNames =
            [
                "DirectionalShadowMapArrays[0]",
                "DirectionalShadowMapArrays[1]",
            ];

            private static readonly string[] s_pointLightShadowMapNames =
            [
                "PointLightShadowMaps[0]",
                "PointLightShadowMaps[1]",
                "PointLightShadowMaps[2]",
                "PointLightShadowMaps[3]",
            ];

            private static readonly string[] s_spotLightShadowMapNames =
            [
                "SpotLightShadowMaps[0]",
                "SpotLightShadowMaps[1]",
                "SpotLightShadowMaps[2]",
                "SpotLightShadowMaps[3]",
            ];

            private void RememberSamplerBindingName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                _boundSamplerNames.Add(name);
                if (name.EndsWith("[0]", StringComparison.Ordinal))
                {
                    string baseName = name[..^3];
                    if (!string.IsNullOrWhiteSpace(baseName))
                        _boundSamplerNames.Add(baseName);
                }
            }

            public void Sampler(int location, IRenderTextureResource texture, int textureUnit)
            {
                if (texture is XRTexture xrTexture)
                    Sampler(location, xrTexture, textureUnit);
            }

            public void Sampler(int location, XRTexture texture, int textureUnit)
            {
                var glObj = Renderer.GetOrCreateAPIRenderObject(texture);
                if (glObj is not IGLTexture glTex)
                    return;

                Sampler(location, glTex, textureUnit);
            }

            public void Sampler(string name, IRenderTextureResource texture, int textureUnit)
            {
                if (texture is XRTexture xrTexture)
                    Sampler(name, xrTexture, textureUnit);
            }

            public void Sampler(string name, XRTexture texture, int textureUnit)
            {
                RememberSamplerBindingName(name);
                int location = GetUniformLocation(name);

                // If the uniform is optimized out (e.g. a layout(binding=) sampler),
                // fixed-unit binding below can still make the sampler usable when
                // the requested unit is free.
                if (location < 0 && RuntimeEngine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texture.Name}', SamplerName: '{texture.SamplerName}'. " +
                            $"Using fixed sampler binding if the unit is free.");
                    }
                }

                var glObj = Renderer.GetOrCreateAPIRenderObject(texture);
                if (glObj is IGLTexture glTex)
                    Sampler(location, glTex, textureUnit, name);
            }

            /// <summary>
            /// Passes a texture sampler into the fragment shader of this program by name.
            /// The name is cached so that retrieving the sampler's location is only required once.
            /// </summary>
            public void Sampler(string name, IGLTexture texture, int textureUnit)
            {
                RememberSamplerBindingName(name);
                int location = GetUniformLocation(name);

                if (location < 0 && RuntimeEngine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        string texName = texture.Data?.Name ?? "unknown";
                        string samplerName = (texture.Data as XRTexture)?.SamplerName ?? "null";
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texName}', SamplerName: '{samplerName}'. " +
                            $"Using fixed sampler binding if the unit is free.");
                    }
                }

                Sampler(location, texture, textureUnit, name);
            }

            /// <summary>
            /// Passes a texture sampler value into the fragment shader of this program by location.
            /// </summary>
            public void Sampler(int location, IGLTexture texture, int textureUnit)
                => Sampler(location, texture, textureUnit, samplerName: null);

            private void Sampler(int location, IGLTexture texture, int textureUnit, string? samplerName)
            {
                bool canBindUniform = MarkSamplerBinding(location);

                texture.PreSampling();
                if (!TryResolveSamplerTextureUnit(location, texture, textureUnit, samplerName, out int resolvedTextureUnit, out bool reusedExistingUnit))
                {
                    texture.PostSampling();
                    if (location >= 0)
                    {
                        string programName = Data.Name ?? BindingId.ToString();
                        string resolvedName = samplerName ?? "<unnamed>";
                        Debug.OpenGLErrorEvery(
                            $"GLSamplerNoUnit:{programName}:{resolvedName}",
                            TimeSpan.FromSeconds(30),
                            $"[Shader Texture Binding] No fragment texture unit available for active sampler '{resolvedName}' in program '{programName}' (maxUnits={Renderer.MaxFragmentTextureImageUnits}, boundUnits={_boundSamplerUnits.Count}).");
                    }

                    return;
                }

                if (location >= 0)
                    _boundSamplerLocations.Add(location);

                if (reusedExistingUnit)
                {
                    if (canBindUniform && location >= 0)
                        Uniform(location, resolvedTextureUnit);
                    texture.PostSampling();
                    return;
                }

                Renderer.SetActiveTextureUnit(resolvedTextureUnit);

                // Unbind stale texture targets on this unit that conflict with the
                // new binding (e.g. a cubemap left from a previous pass when this
                // shader expects sampler2D). Prevents NVIDIA GL_INVALID_OPERATION
                // "program texture usage" errors.
                Renderer.ClearConflictingTextureTargets(texture.TextureTarget);

                if (canBindUniform && location >= 0)
                    Uniform(location, resolvedTextureUnit);

                texture.Bind();
                _boundSamplerUnits[resolvedTextureUnit] = new SamplerUnitBinding(texture.BindingId, texture.TextureTarget, texture);
                texture.PostSampling();
            }

            private bool TryResolveSamplerTextureUnit(
                int location,
                IGLTexture texture,
                int requestedTextureUnit,
                string? samplerName,
                out int resolvedTextureUnit,
                out bool reusedExistingUnit)
            {
                int maxTextureUnits = Math.Max(1, Renderer.MaxFragmentTextureImageUnits);
                reusedExistingUnit = false;

                // Multiple uniforms frequently reference the same engine placeholder (shadow
                // arrays, environment maps, or material fallback textures) or distinct material
                // slots that hold identical 1x1 defaults or cooked image data. OpenGL permits
                // compatible sampler uniforms to share a unit, so reuse an existing compatible
                // binding before consuming another fragment texture unit.
                if (location >= 0 && TryFindExistingSamplerTextureUnit(texture, out resolvedTextureUnit))
                {
                    reusedExistingUnit = true;
                    return true;
                }

                bool requestedTextureUnitAvailable = requestedTextureUnit >= 0
                    && requestedTextureUnit < maxTextureUnits
                    && !IsTextureUnitReservedForDifferentActiveSampler(requestedTextureUnit, samplerName);

                if (requestedTextureUnitAvailable)
                {
                    if (!_boundSamplerUnits.TryGetValue(requestedTextureUnit, out SamplerUnitBinding existing))
                    {
                        resolvedTextureUnit = requestedTextureUnit;
                        return true;
                    }

                    if (IsSameSamplerBinding(existing, texture))
                    {
                        resolvedTextureUnit = requestedTextureUnit;
                        reusedExistingUnit = true;
                        return true;
                    }

                    if (location < 0)
                    {
                        resolvedTextureUnit = -1;
                        return false;
                    }
                }

                if (location >= 0 && TryFindFreeSamplerTextureUnit(samplerName, out resolvedTextureUnit))
                    return true;

                resolvedTextureUnit = requestedTextureUnit;
                return requestedTextureUnitAvailable;
            }

            private bool TryFindExistingSamplerTextureUnit(IGLTexture texture, out int textureUnit)
            {
                foreach ((int candidate, SamplerUnitBinding binding) in _boundSamplerUnits)
                {
                    if (!IsSameSamplerBinding(binding, texture))
                        continue;

                    textureUnit = candidate;
                    return true;
                }

                textureUnit = -1;
                return false;
            }

            private bool TryFindFreeSamplerTextureUnit(string? samplerName, out int textureUnit)
            {
                int maxTextureUnits = Math.Max(1, Renderer.MaxFragmentTextureImageUnits);
                for (int candidate = maxTextureUnits - 1; candidate >= 0; candidate--)
                {
                    if (_boundSamplerUnits.ContainsKey(candidate))
                        continue;

                    if (IsTextureUnitReservedForDifferentActiveSampler(candidate, samplerName))
                        continue;

                    textureUnit = candidate;
                    return true;
                }

                textureUnit = -1;
                return false;
            }

            private static bool IsSameSamplerBinding(SamplerUnitBinding binding, IGLTexture texture)
            {
                if (binding.Target != texture.TextureTarget)
                    return false;

                if (binding.TextureId != GLObjectBase.InvalidBindingId && binding.TextureId == texture.BindingId)
                    return true;

                return binding.TextureId != GLObjectBase.InvalidBindingId
                    && binding.Target == ETextureTarget.Texture2D
                    && binding.Texture is GLTexture2D existingTex2D
                    && texture is GLTexture2D candidateTex2D
                    && existingTex2D.IsSamplingEquivalentTo(candidateTex2D);
            }

            private bool IsTextureUnitReservedForDifferentActiveSampler(int textureUnit, string? samplerName)
            {
                switch (textureUnit)
                {
                    case 6:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.BRDF);
                    case 7:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.IrradianceArray);
                    case 8:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.PrefilterArray);
                    case 9:
                        return IsReservedForDifferentActiveSampler(samplerName, "DirectionalShadowAtlas");
                    case 12:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.EnvironmentMap);
                    case 13:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.PbrReflectionCube);
                    case 15:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ShadowMap, "DirectionalShadowMaps", s_directionalShadowMapNames, 0);
                    case 16:
                        return IsReservedForDifferentActiveSampler(samplerName, "DirectionalShadowMaps", s_directionalShadowMapNames, 1);
                    case 17:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ShadowMapArray, "DirectionalShadowMapArrays", s_directionalShadowMapArrayNames, 0);
                    case 18:
                        return IsReservedForDifferentActiveSampler(samplerName, "DirectionalShadowMapArrays", s_directionalShadowMapArrayNames, 1);
                    case >= 19 and <= 22:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.PointLightShadowMaps, s_pointLightShadowMapNames, textureUnit - 19);
                    case >= 23 and <= 26:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.SpotLightShadowMaps, s_spotLightShadowMapNames, textureUnit - 23);
                    case 27:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.AmbientOcclusionTexture);
                    case 28:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ForwardContactDepthView);
                    case 29:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ForwardContactNormalView);
                    case 30:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ForwardContactDepthViewArray);
                    case 31:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.ForwardContactNormalViewArray);
                    case 32:
                        return IsReservedForDifferentActiveSampler(samplerName, "SpotLightShadowAtlas");
                    case 33:
                        return IsReservedForDifferentActiveSampler(samplerName, EngineShaderBindingNames.Samplers.AmbientOcclusionTextureArray);
                    case 34:
                        return IsReservedForDifferentActiveSampler(samplerName, "PointLightShadowAtlas");
                    default:
                        return false;
                }
            }

            private bool IsReservedForDifferentActiveSampler(string? samplerName, string reservedSamplerName)
            {
                if (!IsUnboundActiveSamplerUniform(reservedSamplerName))
                    return false;

                return !string.Equals(samplerName, reservedSamplerName, StringComparison.Ordinal);
            }

            private bool IsReservedForDifferentActiveSampler(
                string? samplerName,
                string reservedSamplerName,
                string reservedArraySamplerName,
                string[] reservedArrayElementNames,
                int reservedArrayElementIndex)
            {
                if (string.Equals(samplerName, reservedSamplerName, StringComparison.Ordinal)
                    || IsSamplerArrayElementName(samplerName, reservedArraySamplerName, reservedArrayElementIndex))
                {
                    return false;
                }

                return IsUnboundActiveSamplerUniform(reservedSamplerName)
                    || IsUnboundActiveSamplerArrayElement(reservedArraySamplerName, reservedArrayElementNames, reservedArrayElementIndex);
            }

            private bool IsReservedForDifferentActiveSampler(
                string? samplerName,
                string reservedArraySamplerName,
                string[] reservedArrayElementNames,
                int reservedArrayElementIndex)
            {
                if (IsSamplerArrayElementName(samplerName, reservedArraySamplerName, reservedArrayElementIndex))
                    return false;

                return IsUnboundActiveSamplerArrayElement(reservedArraySamplerName, reservedArrayElementNames, reservedArrayElementIndex);
            }

            private bool IsUnboundActiveSamplerUniform(string name)
            {
                if (!HasActiveSamplerUniform(name))
                    return false;

                int location = GetUniformLocation(name);
                return location >= 0 && !_boundSamplerLocations.Contains(location);
            }

            private bool IsUnboundActiveSamplerArrayElement(string arrayName, string[] elementNames, int elementIndex)
            {
                if (!HasActiveSamplerUniform(arrayName)
                    || (uint)elementIndex >= (uint)elementNames.Length)
                {
                    return false;
                }

                int location = GetUniformLocation(elementNames[elementIndex]);
                return location >= 0 && !_boundSamplerLocations.Contains(location);
            }

            private bool HasActiveSamplerUniform(string name)
                => _uniformMetadata.TryGetValue(name, out UniformInfo info) && IsSamplerType(info.Type);

            private static bool IsSamplerArrayElementName(string? samplerName, string arrayName, int elementIndex)
            {
                if (string.IsNullOrEmpty(samplerName))
                    return false;

                if (string.Equals(samplerName, arrayName, StringComparison.Ordinal))
                    return elementIndex == 0;

                if (!samplerName.StartsWith(arrayName, StringComparison.Ordinal))
                    return false;

                int arrayNameLength = arrayName.Length;
                if (samplerName.Length <= arrayNameLength + 2
                    || samplerName[arrayNameLength] != '['
                    || samplerName[^1] != ']')
                    return false;

                ReadOnlySpan<char> indexSpan = samplerName.AsSpan(arrayNameLength + 1, samplerName.Length - arrayNameLength - 2);
                if (indexSpan.Length == 0)
                    return false;

                int parsedIndex = 0;
                for (int i = 0; i < indexSpan.Length; i++)
                {
                    char ch = indexSpan[i];
                    if (ch < '0' || ch > '9')
                        return false;

                    parsedIndex = checked((parsedIndex * 10) + (ch - '0'));
                }

                return parsedIndex == elementIndex;
            }
            #endregion
        }
    }
}
