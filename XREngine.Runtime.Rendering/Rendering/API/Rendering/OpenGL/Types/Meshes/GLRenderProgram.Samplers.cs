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
                if (location < 0 && Engine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texture.Name}', SamplerName: '{texture.SamplerName}'. " +
                            $"Using fixed sampler binding if the unit is free.");
                    }
                }

                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler into the fragment shader of this program by name.
            /// The name is cached so that retrieving the sampler's location is only required once.
            /// </summary>
            public void Sampler(string name, IGLTexture texture, int textureUnit)
            {
                RememberSamplerBindingName(name);
                int location = GetUniformLocation(name);

                if (location < 0 && Engine.Rendering.Settings.LogMissingShaderSamplers)
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

                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler value into the fragment shader of this program by location.
            /// </summary>
            public void Sampler(int location, IGLTexture texture, int textureUnit)
            {
                bool canBindUniform = MarkSamplerBinding(location);

                if (location >= 0)
                    _boundSamplerLocations.Add(location);

                texture.PreSampling();
                if (!TryResolveSamplerTextureUnit(location, texture, textureUnit, out int resolvedTextureUnit))
                {
                    texture.PostSampling();
                    return;
                }

                _boundSamplerUnits[resolvedTextureUnit] = new SamplerUnitBinding(texture.BindingId, texture.TextureTarget);
                Renderer.SetActiveTextureUnit(resolvedTextureUnit);

                // Unbind stale texture targets on this unit that conflict with the
                // new binding (e.g. a cubemap left from a previous pass when this
                // shader expects sampler2D).  Prevents NVIDIA GL_INVALID_OPERATION
                // "program texture usage" errors.
                Renderer.ClearConflictingTextureTargets(texture.TextureTarget);

                if (canBindUniform && location >= 0)
                    Uniform(location, resolvedTextureUnit);

                texture.Bind();
                texture.PostSampling();
            }

            private bool TryResolveSamplerTextureUnit(int location, IGLTexture texture, int requestedTextureUnit, out int resolvedTextureUnit)
            {
                int maxTextureUnits = Math.Max(1, Renderer.MaxFragmentTextureImageUnits);
                if (requestedTextureUnit >= 0 && requestedTextureUnit < maxTextureUnits)
                {
                    if (!_boundSamplerUnits.TryGetValue(requestedTextureUnit, out SamplerUnitBinding existing)
                        || IsSameSamplerBinding(existing, texture))
                    {
                        resolvedTextureUnit = requestedTextureUnit;
                        return true;
                    }

                    if (location < 0)
                    {
                        resolvedTextureUnit = -1;
                        return false;
                    }
                }

                if (location >= 0 && TryFindFreeSamplerTextureUnit(out resolvedTextureUnit))
                    return true;

                resolvedTextureUnit = requestedTextureUnit;
                return requestedTextureUnit >= 0 && requestedTextureUnit < maxTextureUnits;
            }

            private bool TryFindFreeSamplerTextureUnit(out int textureUnit)
            {
                int maxTextureUnits = Math.Max(1, Renderer.MaxFragmentTextureImageUnits);
                for (int candidate = maxTextureUnits - 1; candidate >= 0; candidate--)
                {
                    if (_boundSamplerUnits.ContainsKey(candidate))
                        continue;

                    textureUnit = candidate;
                    return true;
                }

                textureUnit = -1;
                return false;
            }

            private static bool IsSameSamplerBinding(SamplerUnitBinding binding, IGLTexture texture)
                => binding.TextureId == texture.BindingId && binding.Target == texture.TextureTarget;
            #endregion
        }
    }
}
