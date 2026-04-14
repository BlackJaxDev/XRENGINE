using Extensions;
using System.Numerics;
using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLMaterial(OpenGLRenderer renderer, XRMaterial material) : GLObject<XRMaterial>(renderer, material)
        {
            public override EGLObjectType Type => EGLObjectType.Material;

            private float _secondsLive = 0.0f;
            private uint _lastUniformProgramBindingId = uint.MaxValue;
            private XRRenderProgram? _lastUniformProgram;
            private ulong _lastSecondsLiveFrameId = ulong.MaxValue;
            private ulong _lastRenderTimeProgramFrameId = ulong.MaxValue;
            private uint _lastRenderTimeProgramBindingId = uint.MaxValue;
            private XRRenderProgram? _lastRenderTimeProgram;
            public float SecondsLive
            {
                get => _secondsLive;
                set => SetField(ref _secondsLive, value);
            }

            private GLRenderProgram? _separableProgram;
            public GLRenderProgram? SeparableProgram
                => _separableProgram ??= Renderer.GenericToAPI<GLRenderProgram>(Data.ShaderPipelineProgram);

            protected override void LinkData()
            {
                //foreach (var tex in Data.Textures)
                //    if (Renderer.TryGetAPIRenderObject(tex, out var apiObj))
                //        apiObj?.Generate();

                Data.Textures.PostAnythingAdded += TextureAdded;
                Data.Textures.PostAnythingRemoved += TextureRemoved;
            }

            protected override void UnlinkData()
            {
                foreach (var tex in Data.Textures)
                    if (tex is not null && Renderer.TryGetAPIRenderObject(tex, out var apiObj))
                        apiObj?.Destroy();
                
                Data.Textures.PostAnythingAdded -= TextureAdded;
                Data.Textures.PostAnythingRemoved -= TextureRemoved;
            }

            private void TextureRemoved(XRTexture? tex)
            {
                if (tex is not null && Renderer.TryGetAPIRenderObject(tex, out var apiObj))
                    apiObj?.Destroy();
            }
            
            private void TextureAdded(XRTexture? tex)
            {

            }

            public void SetUniforms(GLRenderProgram? materialProgram)
            {
                using var sample = Engine.Profiler.Start("GLMaterial.SetUniforms");

                var renderOptions = Data.RenderOptions;

                //Apply special rendering parameters
                if (renderOptions != null)
                {
                    using var renderOptionsProf = Engine.Profiler.Start("GLMaterial.SetUniforms.ApplyRenderParameters");
                    Renderer.ApplyRenderParameters(renderOptions);
                }

                bool usePipelines = Engine.Rendering.Settings.AllowShaderPipelines
                    || (Engine.Rendering.State.RenderingPipelineState?.ForceShaderPipelines ?? false);
                if (usePipelines)
                    materialProgram ??= SeparableProgram;

                if (materialProgram is null)
                    return;

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.BeginBindingBatch"))
                    materialProgram.BeginBindingBatch();

                bool forceUniformUpdate = !ReferenceEquals(_lastUniformProgram, materialProgram.Data) ||
                    _lastUniformProgramBindingId != materialProgram.BindingId;

                _lastUniformProgram = materialProgram.Data;
                _lastUniformProgramBindingId = materialProgram.BindingId;

                // Ensure uniforms are resident on every program variant that renders this material.
                using (Engine.Profiler.Start("GLMaterial.SetUniforms.Parameters"))
                {
                    foreach (ShaderVar param in Data.Parameters)
                        param.SetUniform(materialProgram.Data, forceUpdate: forceUniformUpdate);
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.Textures"))
                    SetTextureUniforms(materialProgram);

                EUniformRequirements requiredEngineUniforms = renderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None;
                if (RequiresEngineUniformBinding(materialProgram, requiredEngineUniforms))
                {
                    using (Engine.Profiler.Start("GLMaterial.SetUniforms.EngineUniforms"))
                        SetEngineUniforms(materialProgram, requiredEngineUniforms);
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.MaterialHook"))
                    Data.OnSettingUniforms(materialProgram.Data);

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.ScopedProgramBindings"))
                    Engine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(materialProgram.Data);
            }

            public void FinalizeUniformBindings(GLRenderProgram? materialProgram)
            {
                if (materialProgram is null)
                    return;

                using var sample = Engine.Profiler.Start("GLMaterial.FinalizeUniformBindings");

                // Mesh/FBO SettingUniforms hooks run after SetUniforms(); defer fallback binding until
                // the full binding batch is complete so late sampler binds are observed correctly.
                using (Engine.Profiler.Start("GLMaterial.FinalizeUniformBindings.BindFallbackSamplers"))
                    materialProgram.BindFallbackSamplers();

                using (Engine.Profiler.Start("GLMaterial.FinalizeUniformBindings.WarnIfNoBindings"))
                    materialProgram.WarnIfNoUniformOrSamplerBindings(Data.Name);
            }

            private bool RequiresEngineUniformBinding(GLRenderProgram program, EUniformRequirements requiredRequirements)
            {
                if (requiredRequirements == EUniformRequirements.None)
                    return false;

                EUniformRequirements cachedProgramRequirements = requiredRequirements & ~EUniformRequirements.RenderTime;
                if (program.GetMissingEngineUniformRequirements(cachedProgramRequirements) != EUniformRequirements.None)
                    return true;

                return requiredRequirements.HasFlag(EUniformRequirements.RenderTime) && ShouldApplyRenderTimeUniforms(program);
            }

            private void SetEngineUniforms(GLRenderProgram program, EUniformRequirements reqs)
            {
                if (reqs == EUniformRequirements.None)
                    return;

                UpdateSecondsLive();

                EUniformRequirements cachedProgramRequirements = reqs & ~EUniformRequirements.RenderTime;
                EUniformRequirements missingProgramRequirements = program.GetMissingEngineUniformRequirements(cachedProgramRequirements);

                if (missingProgramRequirements.HasFlag(EUniformRequirements.Camera))
                {
                    Engine.Rendering.State.RenderingCamera?.SetUniforms(program.Data, true);
                    Engine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program.Data, false);
                }

                if (missingProgramRequirements.HasFlag(EUniformRequirements.Lights))
                {
                    var world = Engine.Rendering.State.RenderingWorld;
                    var lights = world?.Lights;
                    if (lights != null)
                        lights.SetForwardLightingUniforms(program.Data);
                    else
                        Debug.OpenGL($"[ForwardLighting] Skipped: RenderingWorld={world != null}, Lights={lights != null}");
                }

                if (reqs.HasFlag(EUniformRequirements.RenderTime) && ShouldApplyRenderTimeUniforms(program))
                {
                    program.Uniform(EEngineUniform.RenderTime.ToStringFast(), SecondsLive);
                    program.Uniform(EEngineUniform.EngineTime.ToStringFast(), Engine.ElapsedTime);
                    program.Uniform(EEngineUniform.DeltaTime.ToStringFast(), Engine.Time.Timer.Render.Delta);
                    MarkRenderTimeUniformsApplied(program);
                }
                
                if (missingProgramRequirements.HasFlag(EUniformRequirements.ViewportDimensions))
                {
                    var area = Engine.Rendering.State.RenderArea;
                    program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)area.Width);
                    program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)area.Height);
                    program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), new Vector2(area.X, area.Y));
                }

                if (missingProgramRequirements.HasFlag(EUniformRequirements.MousePosition))
                {
                    //Program?.Uniform(nameof(EUniformRequirements.MousePosition), mousePosition);
                }

                program.MarkEngineUniformsApplied(missingProgramRequirements);
            }

            private void UpdateSecondsLive()
            {
                ulong frameId = Engine.Rendering.State.RenderFrameId;
                if (_lastSecondsLiveFrameId == frameId)
                    return;

                SecondsLive += Engine.Time.Timer.Update.Delta;
                _lastSecondsLiveFrameId = frameId;
            }

            private bool ShouldApplyRenderTimeUniforms(GLRenderProgram program)
            {
                ulong frameId = Engine.Rendering.State.RenderFrameId;
                return _lastRenderTimeProgramFrameId != frameId
                    || _lastRenderTimeProgramBindingId != program.BindingId
                    || !ReferenceEquals(_lastRenderTimeProgram, program.Data);
            }

            private void MarkRenderTimeUniformsApplied(GLRenderProgram program)
            {
                _lastRenderTimeProgramFrameId = Engine.Rendering.State.RenderFrameId;
                _lastRenderTimeProgramBindingId = program.BindingId;
                _lastRenderTimeProgram = program.Data;
            }

            public EDrawBuffersAttachment[] CollectFBOAttachments()
            {
                if (Data.Textures is null || Data.Textures.Count <= 0)
                    return [];

                List<EDrawBuffersAttachment> fboAttachments = [];
                foreach (XRTexture? tref in Data.Textures)
                {
                    if (tref is null || !tref.FrameBufferAttachment.HasValue)
                        continue;

                    switch (tref.FrameBufferAttachment.Value)
                    {
                        case EFrameBufferAttachment.DepthAttachment:
                        case EFrameBufferAttachment.DepthStencilAttachment:
                        case EFrameBufferAttachment.StencilAttachment:
                            continue;
                    }
                    fboAttachments.Add((EDrawBuffersAttachment)(int)tref.FrameBufferAttachment.Value);
                }

                return [.. fboAttachments];
            }

            public void SetTextureUniforms(GLRenderProgram program)
            {
                using var sample = Engine.Profiler.Start("GLMaterial.SetTextureUniforms");

                // Use textureIndex as textureUnit so that null entries preserve
                // the correspondence between array position and GL texture unit.
                // Shaders with layout(binding=X) rely on this stable mapping;
                // the previous compact-unit scheme shifted all bindings when a
                // texture slot was null, breaking deferred lighting in particular.
                for (int textureIndex = 0; textureIndex < Data.Textures.Count; ++textureIndex)
                {
                    if (!TryGetTexture(textureIndex, out IGLTexture texture))
                        continue;

                    SetTextureUniform(program, textureIndex, texture, textureIndex);
                }
            }

            public void SetTextureUniform(GLRenderProgram program, int textureIndex, string? samplerNameOverride = null)
            {
                if (!TryGetTextureBinding(textureIndex, out IGLTexture texture, out int textureUnit))
                    return;

                SetTextureUniform(program, textureIndex, texture, textureUnit, samplerNameOverride);
            }

            private void SetTextureUniform(GLRenderProgram program, int textureIndex, IGLTexture texture, int textureUnit, string? samplerNameOverride = null)
            {
                if (program is null)
                    return;

                string resolvedSamplerName = texture.ResolveSamplerName(textureIndex, samplerNameOverride);
                program.Sampler(resolvedSamplerName, texture, textureUnit);

                if (!string.IsNullOrWhiteSpace(samplerNameOverride))
                    return;

                string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);
                if (string.Equals(resolvedSamplerName, indexedSamplerName, StringComparison.Ordinal))
                    return;

                // Imported textures can carry explicit sampler names from the source asset
                // even when the stock engine shader expects Texture0/Texture1/... Bind the
                // indexed alias too when the shader declares it but not the explicit name.
                if (program.GetUniformLocation(resolvedSamplerName) >= 0)
                    return;

                if (program.GetUniformLocation(indexedSamplerName) >= 0)
                    program.Sampler(indexedSamplerName, texture, textureUnit);
            }

            private bool TryGetTextureBinding(int textureIndex, out IGLTexture texture, out int textureUnit)
            {
                texture = null!;
                textureUnit = textureIndex;

                if (!Data.Textures.IndexInRange(textureIndex))
                    return false;

                return TryGetTexture(textureIndex, out texture);
            }

            private bool TryGetTexture(int textureIndex, out IGLTexture texture)
            {
                texture = null!;
                if (!Data.Textures.IndexInRange(textureIndex))
                    return false;

                var tex = Data.Textures[textureIndex];
                if (tex is null || Renderer.GetOrCreateAPIRenderObject(tex) is not IGLTexture glTexture)
                    return false;

                texture = glTexture;
                return true;
            }
        }
    }
}