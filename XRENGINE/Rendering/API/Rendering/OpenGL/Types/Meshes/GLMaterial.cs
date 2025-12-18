using Extensions;
using System.Numerics;
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
            public float SecondsLive
            {
                get => _secondsLive;
                set => SetField(ref _secondsLive, value);
            }

            public GLRenderProgram? SeparableProgram => Renderer.GenericToAPI<GLRenderProgram>(Data.ShaderPipelineProgram);

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
                //Apply special rendering parameters
                if (Data.RenderOptions != null)
                    Renderer.ApplyRenderParameters(Data.RenderOptions);

                if (Engine.Rendering.Settings.AllowShaderPipelines)
                    materialProgram ??= SeparableProgram;

                if (materialProgram is null)
                    return;

                materialProgram.BeginBindingBatch();

                bool forceUniformUpdate = !ReferenceEquals(_lastUniformProgram, materialProgram.Data) ||
                    _lastUniformProgramBindingId != materialProgram.BindingId;

                _lastUniformProgram = materialProgram.Data;
                _lastUniformProgramBindingId = materialProgram.BindingId;

                // Ensure uniforms are resident on every program variant that renders this material.
                foreach (ShaderVar param in Data.Parameters)
                    param.SetUniform(materialProgram.Data, forceUpdate: forceUniformUpdate);

                SetTextureUniforms(materialProgram);
                SetEngineUniforms(materialProgram);
                Data.OnSettingUniforms(materialProgram.Data);

                materialProgram.WarnIfNoUniformOrSamplerBindings(Data.Name);
            }

            private void SetEngineUniforms(GLRenderProgram program)
            {
                SecondsLive += Engine.Time.Timer.Update.Delta;

                var reqs = Data.RenderOptions.RequiredEngineUniforms;

                if (reqs.HasFlag(EUniformRequirements.Camera))
                {
                    Engine.Rendering.State.RenderingCamera?.SetUniforms(program.Data, true);
                    Engine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program.Data, false);
                }

                if (reqs.HasFlag(EUniformRequirements.Lights))
                {
                    Engine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program.Data);
                }

                if (reqs.HasFlag(EUniformRequirements.RenderTime))
                    program.Uniform(nameof(EUniformRequirements.RenderTime), SecondsLive);
                
                if (reqs.HasFlag(EUniformRequirements.ViewportDimensions))
                {
                    var area = Engine.Rendering.State.RenderArea;
                    program.Uniform(EEngineUniform.ScreenWidth.ToString(), (float)area.Width);
                    program.Uniform(EEngineUniform.ScreenHeight.ToString(), (float)area.Height);
                    //program.Uniform(EEngineUniform.ScreenOrigin.ToString(), new Vector2(0.0f, 0.0f));
                }

                if (reqs.HasFlag(EUniformRequirements.MousePosition))
                {
                    //Program?.Uniform(nameof(EUniformRequirements.MousePosition), mousePosition);
                }
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
                for (int i = 0; i < Data.Textures.Count; ++i)
                    SetTextureUniform(program, i);
            }
            public void SetTextureUniform(GLRenderProgram program, int textureIndex, string? samplerNameOverride = null)
            {
                if (!Data.Textures.IndexInRange(textureIndex))
                    return;
                
                var tex = Data.Textures[textureIndex];
                if (tex is null || Renderer.GetOrCreateAPIRenderObject(tex) is not IGLTexture texture)
                    return;

                program?.Sampler(texture.ResolveSamplerName(textureIndex, samplerNameOverride), texture, textureIndex);
            }
        }
    }
}