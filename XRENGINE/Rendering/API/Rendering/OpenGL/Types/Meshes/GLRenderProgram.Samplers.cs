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

                _boundSamplerNames[name] = 1;
                if (name.EndsWith("[0]", StringComparison.Ordinal))
                {
                    string baseName = name[..^3];
                    if (!string.IsNullOrWhiteSpace(baseName))
                        _boundSamplerNames[baseName] = 1;
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

                // If the uniform is optimized out (e.g., layout(binding=) samplers), still bind the texture to the unit
                // so fixed-binding samplers can sample correctly. Log once when the uniform is missing.
                if (location < 0 && Engine.Rendering.Settings.LogMissingShaderSamplers)
                {
                    string key = $"{Data.Name ?? BindingId.ToString()}:{name}:{textureUnit}";
                    if (_loggedUniformMismatches.TryAdd(key, 1))
                    {
                        Debug.OpenGLWarning($"[Shader Texture Binding] Sampler '{name}' not found in program '{Data.Name ?? BindingId.ToString()}' for texture unit {textureUnit}. " +
                            $"Texture: '{texture.Name}', SamplerName: '{texture.SamplerName}'. " +
                            $"Binding anyway due to fixed sampler binding.");
                    }
                }

                // Always bind to the requested unit; Uniform() will be a no-op if location < 0.
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
                            $"Binding anyway due to fixed sampler binding.");
                    }
                }

                Sampler(location, texture, textureUnit);
            }

            /// <summary>
            /// Passes a texture sampler value into the fragment shader of this program by location.
            /// </summary>
            public void Sampler(int location, IGLTexture texture, int textureUnit)
            {
                // Even if the uniform location is invalid (-1), we still want the GL state
                // to have the texture bound at the requested unit for layout(binding=) samplers.
                _boundSamplerUnits[textureUnit] = 1;
                bool canBindUniform = MarkSamplerBinding(location);

                if (location >= 0)
                    _boundSamplerLocations[location] = 1;

                texture.PreSampling();
                Renderer.SetActiveTextureUnit(textureUnit);

                // Unbind stale texture targets on this unit that conflict with the
                // new binding (e.g. a cubemap left from a previous pass when this
                // shader expects sampler2D).  Prevents NVIDIA GL_INVALID_OPERATION
                // "program texture usage" errors.
                Renderer.ClearConflictingTextureTargets(texture.TextureTarget);

                if (canBindUniform && location >= 0)
                    Uniform(location, textureUnit);

                texture.Bind();
                texture.PostSampling();
            }
            #endregion
        }
    }
}
