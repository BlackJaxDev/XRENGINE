using XREngine.Extensions;
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

            private uint _lastUniformProgramBindingId = uint.MaxValue;
            private XRRenderProgram? _lastUniformProgram;
            private XRMaterial? _shadowBindingSourceMaterial;
            private XRRenderProgram? _shadowBindingProgram;
            private uint _shadowBindingProgramBindingId = uint.MaxValue;
            private ulong _shadowBindingSourceLayoutVersion = ulong.MaxValue;
            private ShadowBindingPlan? _shadowBindingPlan;

            private sealed class ShadowBindingPlan(ShaderVar[] parameters, int[] textureIndices)
            {
                public ShaderVar[] Parameters { get; } = parameters;
                public int[] TextureIndices { get; } = textureIndices;
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

                XRMaterial? shadowBindingSource = null;
                ShadowBindingPlan? shadowBindingPlan = null;
                if (Engine.Rendering.State.IsShadowPass)
                {
                    shadowBindingSource = Data.ShadowBindingSourceMaterial;
                    if (shadowBindingSource is not null)
                    {
                        using var shadowPlanProf = Engine.Profiler.Start("GLMaterial.SetUniforms.ShadowBindingPlan");
                        shadowBindingPlan = GetOrCreateShadowBindingPlan(materialProgram, shadowBindingSource);
                    }
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.BeginBindingBatch"))
                    materialProgram.BeginBindingBatch();

                bool forceUniformUpdate = !ReferenceEquals(_lastUniformProgram, materialProgram.Data) ||
                    _lastUniformProgramBindingId != materialProgram.BindingId;

                _lastUniformProgram = materialProgram.Data;
                _lastUniformProgramBindingId = materialProgram.BindingId;

                // Ensure uniforms are resident on every program variant that renders this material.
                using (Engine.Profiler.Start("GLMaterial.SetUniforms.Parameters"))
                {
                    if (shadowBindingPlan is not null)
                    {
                        foreach (ShaderVar param in shadowBindingPlan.Parameters)
                            param.SetUniform(materialProgram.Data, forceUpdate: forceUniformUpdate);
                    }
                    else
                    {
                        foreach (ShaderVar param in Data.Parameters)
                            param.SetUniform(materialProgram.Data, forceUpdate: forceUniformUpdate);
                    }
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.Textures"))
                {
                    if (shadowBindingPlan is not null)
                        SetTextureUniforms(materialProgram, shadowBindingSource!, shadowBindingPlan.TextureIndices);
                    else
                        SetTextureUniforms(materialProgram);
                }

                EUniformRequirements requiredEngineUniforms = renderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None;
                if (RequiresEngineUniformBinding(materialProgram, requiredEngineUniforms))
                {
                    using (Engine.Profiler.Start("GLMaterial.SetUniforms.EngineUniforms"))
                        SetEngineUniforms(materialProgram, requiredEngineUniforms);
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.MaterialHook"))
                {
                    XRMaterial? shadowUniformSource = Engine.Rendering.State.IsShadowPass
                        ? Data.ShadowUniformSourceMaterial
                        : null;
                    if (shadowUniformSource?.HasSettingShadowUniformHandlers == true)
                        shadowUniformSource.OnSettingShadowUniforms(materialProgram.Data);

                    if (shadowBindingSource is not null)
                    {
                        if (shadowBindingSource.HasSettingShadowUniformHandlers)
                            shadowBindingSource.OnSettingShadowUniforms(materialProgram.Data);
                        else if (shadowBindingSource.HasSettingUniformsHandlers)
                            shadowBindingSource.OnSettingUniforms(materialProgram.Data);
                    }
                    else
                    {
                        Data.OnSettingUniforms(materialProgram.Data);
                    }
                }

                using (Engine.Profiler.Start("GLMaterial.SetUniforms.ScopedProgramBindings"))
                    Engine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(materialProgram.Data);
            }

            private ShadowBindingPlan GetOrCreateShadowBindingPlan(GLRenderProgram materialProgram, XRMaterial sourceMaterial)
            {
                ulong bindingLayoutVersion = sourceMaterial.BindingLayoutVersion;
                if (_shadowBindingPlan is not null
                    && ReferenceEquals(_shadowBindingSourceMaterial, sourceMaterial)
                    && ReferenceEquals(_shadowBindingProgram, materialProgram.Data)
                    && _shadowBindingProgramBindingId == materialProgram.BindingId
                    && _shadowBindingSourceLayoutVersion == bindingLayoutVersion)
                    return _shadowBindingPlan;

                _shadowBindingPlan = BuildShadowBindingPlan(materialProgram, sourceMaterial);
                _shadowBindingSourceMaterial = sourceMaterial;
                _shadowBindingProgram = materialProgram.Data;
                _shadowBindingProgramBindingId = materialProgram.BindingId;
                _shadowBindingSourceLayoutVersion = bindingLayoutVersion;
                return _shadowBindingPlan;
            }

            private static ShadowBindingPlan BuildShadowBindingPlan(GLRenderProgram materialProgram, XRMaterial sourceMaterial)
            {
                List<ShaderVar> parameterBindings = [];
                foreach (ShaderVar parameter in sourceMaterial.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Name))
                        continue;

                    if (materialProgram.HasUniform(parameter.Name))
                        parameterBindings.Add(parameter);
                }

                List<int> textureBindings = [];
                for (int textureIndex = 0; textureIndex < sourceMaterial.Textures.Count; ++textureIndex)
                {
                    XRTexture? texture = sourceMaterial.Textures[textureIndex];
                    if (texture is null)
                        continue;

                    string resolvedSamplerName = texture.ResolveSamplerName(textureIndex, null);
                    string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);
                    if (materialProgram.HasUniform(resolvedSamplerName)
                        || (!string.Equals(resolvedSamplerName, indexedSamplerName, StringComparison.Ordinal)
                            && materialProgram.HasUniform(indexedSamplerName)))
                        textureBindings.Add(textureIndex);
                }

                return new([.. parameterBindings], [.. textureBindings]);
            }

            public void FinalizeUniformBindings(GLRenderProgram? materialProgram)
            {
                if (materialProgram is null)
                    return;

                using var sample = Engine.Profiler.Start("GLMaterial.FinalizeUniformBindings");

                bool isSamplerFreeShadowBindingPath = Engine.Rendering.State.IsShadowPass
                    && !materialProgram.HasActiveSamplerUniforms();

                if (isSamplerFreeShadowBindingPath)
                    return;

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

                // Light bindings include shadow-map samplers. Each material draw starts a new
                // binding batch, so they must be rebound even when scalar light uniforms were
                // already cached for the current frame/context.
                if (requiredRequirements.HasFlag(EUniformRequirements.Lights))
                    return true;

                return program.GetMissingEngineUniformRequirements(requiredRequirements) != EUniformRequirements.None;
            }

            private void SetEngineUniforms(GLRenderProgram program, EUniformRequirements reqs)
            {
                if (reqs == EUniformRequirements.None)
                    return;

                EUniformRequirements missingProgramRequirements = program.GetMissingEngineUniformRequirements(reqs);
                if (reqs.HasFlag(EUniformRequirements.Lights))
                    missingProgramRequirements |= EUniformRequirements.Lights;

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

                if (missingProgramRequirements.HasFlag(EUniformRequirements.RenderTime))
                {
                    program.Uniform(EEngineUniform.RenderTime.ToStringFast(), Engine.ElapsedTime);
                    program.Uniform(EEngineUniform.EngineTime.ToStringFast(), Engine.ElapsedTime);
                    program.Uniform(EEngineUniform.DeltaTime.ToStringFast(), Engine.Time.Timer.Render.Delta);
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
                => SetTextureUniforms(program, Data);

            private void SetTextureUniforms(GLRenderProgram program, XRMaterialBase material)
            {
                using var sample = Engine.Profiler.Start("GLMaterial.SetTextureUniforms");

                // Use textureIndex as textureUnit so that null entries preserve
                // the correspondence between array position and GL texture unit.
                // Shaders with layout(binding=X) rely on this stable mapping;
                // the previous compact-unit scheme shifted all bindings when a
                // texture slot was null, breaking deferred lighting in particular.
                for (int textureIndex = 0; textureIndex < material.Textures.Count; ++textureIndex)
                {
                    if (!TryGetTexture(material, textureIndex, out IGLTexture texture))
                        continue;

                    SetTextureUniform(program, material, textureIndex, texture, textureIndex);
                }
            }

            private void SetTextureUniforms(GLRenderProgram program, XRMaterialBase material, IReadOnlyList<int> textureIndices)
            {
                using var sample = Engine.Profiler.Start("GLMaterial.SetTextureUniforms");

                for (int index = 0; index < textureIndices.Count; ++index)
                {
                    int textureIndex = textureIndices[index];
                    if (!TryGetTexture(material, textureIndex, out IGLTexture texture))
                        continue;

                    SetTextureUniform(program, material, textureIndex, texture, textureIndex);
                }
            }

            public void SetTextureUniform(GLRenderProgram program, int textureIndex, string? samplerNameOverride = null)
            {
                if (!TryGetTextureBinding(Data, textureIndex, out IGLTexture texture, out int textureUnit))
                    return;

                SetTextureUniform(program, Data, textureIndex, texture, textureUnit, samplerNameOverride);
            }

            private void SetTextureUniform(GLRenderProgram program, XRMaterialBase material, int textureIndex, IGLTexture texture, int textureUnit, string? samplerNameOverride = null)
            {
                if (program is null)
                    return;

                if (Engine.Rendering.Settings.LogMaterialTextureBindings)
                {
                    // Opt-in diagnostic: record every material → texture → unit mapping so the exact
                    // cross-material texture-bleed culprit (e.g. lion diffuse rendering on sponza roof)
                    // can be identified post-hoc from the log.
                    string matName = material?.Name ?? Data?.Name ?? "<unnamed-material>";
                    string texName = texture.Data?.Name ?? "<unnamed-texture>";
                    uint texId = texture is GLObjectBase glob && glob.TryGetBindingId(out uint id)
                        ? id
                        : 0;
                    Debug.OpenGL(
                        $"[GLMaterial.Bind] material='{matName}' slot={textureIndex} unit={textureUnit} " +
                        $"texture='{texName}' glId={texId} target={texture.TextureTarget} program='{program.Data?.Name}'.");
                }

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

            private bool TryGetTextureBinding(XRMaterialBase material, int textureIndex, out IGLTexture texture, out int textureUnit)
            {
                texture = null!;
                textureUnit = textureIndex;

                if (!material.Textures.IndexInRange(textureIndex))
                    return false;

                return TryGetTexture(material, textureIndex, out texture);
            }

            private bool TryGetTexture(XRMaterialBase material, int textureIndex, out IGLTexture texture)
            {
                texture = null!;
                if (!material.Textures.IndexInRange(textureIndex))
                    return false;

                var tex = material.Textures[textureIndex];
                if (tex is null || Renderer.GetOrCreateAPIRenderObject(tex) is not IGLTexture glTexture)
                    return false;

                texture = glTexture;
                return true;
            }
        }
    }
}
