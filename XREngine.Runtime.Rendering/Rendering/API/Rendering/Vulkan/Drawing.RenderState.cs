using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Render Parameters ===========

        private float _materialUniformSecondsLive;
        private XRMaterial? _vulkanShadowBindingSourceMaterial;
        private XRRenderProgram? _vulkanShadowBindingProgram;
        private ulong _vulkanShadowBindingSourceLayoutVersion = ulong.MaxValue;
        private MaterialShadowBindingPlan? _vulkanShadowBindingPlan;

        public override void ApplyRenderParameters(RenderingParameters parameters)
        {
            if (parameters is null)
                return;

            // Apply color write mask
            _state.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);
            _state.SetCullMode(ToVulkanCullMode(ResolveCullMode(parameters.CullMode)));
            _state.SetFrontFace(ToVulkanFrontFace(ResolveWinding(parameters.Winding)));
            _state.SetAlphaToCoverageEnabled(parameters.AlphaToCoverage == ERenderParamUsage.Enabled);

            // Apply depth test settings
            var depthTest = parameters.DepthTest;
            if (depthTest.Enabled == ERenderParamUsage.Enabled)
            {
                _state.SetDepthTestEnabled(true);
                _state.SetDepthWriteEnabled(depthTest.UpdateDepth);
                _state.SetDepthCompare(ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(depthTest.Function)));
            }
            else if (depthTest.Enabled == ERenderParamUsage.Disabled)
            {
                _state.SetDepthTestEnabled(false);
                _state.SetDepthWriteEnabled(false);
            }

            var stencilTest = parameters.StencilTest;
            if (stencilTest.Enabled == ERenderParamUsage.Enabled)
            {
                _state.SetStencilEnabled(true);
                _state.SetStencilStates(ToVulkanStencilState(stencilTest.FrontFace), ToVulkanStencilState(stencilTest.BackFace));
                _state.SetStencilWriteMask(stencilTest.FrontFace.WriteMask);
            }
            else if (stencilTest.Enabled == ERenderParamUsage.Disabled)
            {
                _state.SetStencilEnabled(false);
                _state.SetStencilStates(default, default);
                _state.SetStencilWriteMask(0);
            }

            BlendMode? blend = ResolveBlendMode(parameters);
            if (blend is not null && blend.Enabled == ERenderParamUsage.Enabled)
            {
                _state.SetBlendState(
                    true,
                    ToVulkanBlendOp(blend.RgbEquation),
                    ToVulkanBlendOp(blend.AlphaEquation),
                    ToVulkanBlendFactor(blend.RgbSrcFactor),
                    ToVulkanBlendFactor(blend.RgbDstFactor),
                    ToVulkanBlendFactor(blend.AlphaSrcFactor),
                    ToVulkanBlendFactor(blend.AlphaDstFactor));
            }
            else if (blend is not null && blend.Enabled == ERenderParamUsage.Disabled)
            {
                _state.SetBlendState(false, BlendOp.Add, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero);
            }
            else if (blend is null)
            {
                _state.SetBlendState(false, BlendOp.Add, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero);
            }
        }

        // =========== Engine & Material Uniforms ===========

        public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
        {
            if (program is null)
                return;

            bool stereoPass = RuntimeEngine.Rendering.State.IsStereoPass;
            if (stereoPass)
            {
                var rightCam = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
                PassCameraUniforms(program, camera, EEngineUniform.LeftEyeViewMatrix, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeInverseProjMatrix, EEngineUniform.LeftEyeProjMatrix, EEngineUniform.LeftEyeViewProjectionMatrix);
                PassCameraUniforms(program, rightCam, EEngineUniform.RightEyeViewMatrix, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeInverseProjMatrix, EEngineUniform.RightEyeProjMatrix, EEngineUniform.RightEyeViewProjectionMatrix);
            }
            else
            {
                PassCameraUniforms(program, camera, EEngineUniform.ViewMatrix, EEngineUniform.InverseViewMatrix, EEngineUniform.InverseProjMatrix, EEngineUniform.ProjMatrix, EEngineUniform.ViewProjectionMatrix);
            }
        }

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
            => SetMaterialUniforms(material, program, LayeredShadowUniformState.CaptureFromCurrentRenderingState());

        private void SetMaterialUniforms(XRMaterial material, XRRenderProgram program, in LayeredShadowUniformState shadowState)
        {
            if (material is null || program is null)
                return;

            if (material.RenderOptions is not null)
                ApplyRenderParameters(material.RenderOptions);

            XRMaterial? shadowBindingSource = null;
            MaterialShadowBindingPlan? shadowBindingPlan = null;
            if (shadowState.IsShadowPass)
            {
                shadowBindingSource = material.ShadowBindingSourceMaterial;
                if (shadowBindingSource is not null)
                    shadowBindingPlan = GetOrCreateVulkanShadowBindingPlan(program, shadowBindingSource);
            }

            XRMaterialBase uniformSource = shadowBindingPlan is not null ? shadowBindingSource! : material;
            if (shadowBindingPlan is not null)
            {
                foreach (ShaderVar param in shadowBindingPlan.Parameters)
                    param.SetUniform(program, forceUpdate: true);
            }
            else
            {
                foreach (ShaderVar param in uniformSource.Parameters)
                    param.SetUniform(program, forceUpdate: true);
            }

            if (shadowBindingPlan is not null)
                SetTextureUniforms(program, shadowBindingSource!, shadowBindingPlan.TextureIndices);
            else
                SetTextureUniforms(program, uniformSource);

            _materialUniformSecondsLive += RuntimeEngine.Time.Timer.Update.Delta;
            EUniformRequirements reqs =
                (material.RenderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None) |
                program.GetActiveEngineUniformRequirements();

            if (reqs.HasFlag(EUniformRequirements.Camera))
            {
                RuntimeEngine.Rendering.State.RenderingCamera?.SetUniforms(program, true);
                RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program, false);
            }

            bool lightingUniformsBound = false;
            if (reqs.HasFlag(EUniformRequirements.Lights))
            {
                RuntimeEngine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program);
                lightingUniformsBound = RuntimeEngine.Rendering.State.RenderingWorld?.Lights is not null;
            }

            if (reqs.HasFlag(EUniformRequirements.AmbientOcclusion) && !lightingUniformsBound)
                Lights3DCollection.SetForwardAmbientOcclusionUniforms(program);

            if (reqs.HasFlag(EUniformRequirements.RenderTime))
            {
                program.Uniform(EEngineUniform.RenderTime.ToStringFast(), _materialUniformSecondsLive);
                program.Uniform(EEngineUniform.EngineTime.ToStringFast(), RuntimeEngine.ElapsedTime);
                program.Uniform(EEngineUniform.DeltaTime.ToStringFast(), RuntimeEngine.Time.Timer.Render.Delta);
            }

            if (reqs.HasFlag(EUniformRequirements.ViewportDimensions))
            {
                var area = RuntimeEngine.Rendering.State.RenderArea;
                float screenWidth = area.Width;
                float screenHeight = area.Height;
                // The render-region stack can already be popped (RenderArea Empty) for
                // deferred draws such as the debug line/point primitives, leaving the
                // ScreenWidth/ScreenHeight geometry-shader uniforms at 0 and collapsing
                // their pixel→NDC viewport to (1,1). Fall back to the bound draw target's
                // actual dimensions, which are valid during command recording.
                if (screenWidth <= 0f || screenHeight <= 0f)
                {
                    XRFrameBuffer? drawTarget = GetCurrentDrawFrameBuffer();
                    if (drawTarget is not null)
                    {
                        screenWidth = drawTarget.Width;
                        screenHeight = drawTarget.Height;
                    }
                    else
                    {
                        Extent2D targetExtent = GetCurrentTargetExtent();
                        screenWidth = targetExtent.Width;
                        screenHeight = targetExtent.Height;
                    }
                }
                program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), screenWidth);
                program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), screenHeight);
                program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), new Vector2(area.X, area.Y));
            }

            if (reqs.HasFlag(EUniformRequirements.ClipSpacePolicy))
            {
                program.Uniform(EEngineUniform.ClipSpaceYDirection.ToStringFast(), (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
                program.Uniform(EEngineUniform.ClipDepthRange.ToStringFast(), (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
                program.Uniform(EEngineUniform.FramebufferTextureYDirection.ToStringFast(), (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan));
            }

            if (!shadowState.IsShadowPass)
            {
                material.OnSettingUniforms(program);
                RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);
            }
        }

        private MaterialShadowBindingPlan GetOrCreateVulkanShadowBindingPlan(XRRenderProgram program, XRMaterial sourceMaterial)
        {
            ulong bindingLayoutVersion = sourceMaterial.BindingLayoutVersion;
            if (_vulkanShadowBindingPlan is not null
                && ReferenceEquals(_vulkanShadowBindingSourceMaterial, sourceMaterial)
                && ReferenceEquals(_vulkanShadowBindingProgram, program)
                && _vulkanShadowBindingSourceLayoutVersion == bindingLayoutVersion)
                return _vulkanShadowBindingPlan;

            _vulkanShadowBindingPlan = MaterialTextureBindingResolver.BuildShadowBindingPlan(program, sourceMaterial);
            _vulkanShadowBindingSourceMaterial = sourceMaterial;
            _vulkanShadowBindingProgram = program;
            _vulkanShadowBindingSourceLayoutVersion = bindingLayoutVersion;
            return _vulkanShadowBindingPlan;
        }

        private static void SetTextureUniforms(XRRenderProgram program, XRMaterialBase material)
        {
            for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
                SetTextureUniform(program, material, textureIndex);
        }

        private static void SetTextureUniforms(XRRenderProgram program, XRMaterialBase material, int[] textureIndices)
        {
            foreach (int textureIndex in textureIndices)
                SetTextureUniform(program, material, textureIndex);
        }

        private static void SetTextureUniform(XRRenderProgram program, XRMaterialBase material, int textureIndex)
        {
            if ((uint)textureIndex >= (uint)material.Textures.Count)
                return;

            XRTexture? texture = material.Textures[textureIndex];
            if (texture is null)
                return;

            string resolvedSamplerName = texture.ResolveSamplerName(textureIndex, null);
            program.Sampler(resolvedSamplerName, texture, textureIndex);

            string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);
            if (string.Equals(resolvedSamplerName, indexedSamplerName, StringComparison.Ordinal))
                return;

            if (!program.HasUniform(resolvedSamplerName) && program.HasUniform(indexedSamplerName))
                program.Sampler(indexedSamplerName, texture, textureIndex);
        }

        private static void PassCameraUniforms(XRRenderProgram program, XRCamera? camera, EEngineUniform viewName, EEngineUniform inverseViewName, EEngineUniform inverseProjectionName, EEngineUniform projectionName, EEngineUniform viewProjectionName)
        {
            Matrix4x4 viewMatrix;
            Matrix4x4 inverseViewMatrix;
            Matrix4x4 inverseProjectionMatrix;
            Matrix4x4 projectionMatrix;
            Matrix4x4 viewProjectionMatrix;
            if (camera is not null)
            {
                viewMatrix = camera.Transform.InverseRenderMatrix;
                inverseViewMatrix = camera.Transform.RenderMatrix;
                bool useUnjittered = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                projectionMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
                inverseProjectionMatrix = useUnjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
                viewProjectionMatrix = useUnjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
            }
            else
            {
                viewMatrix = Matrix4x4.Identity;
                inverseViewMatrix = Matrix4x4.Identity;
                inverseProjectionMatrix = Matrix4x4.Identity;
                projectionMatrix = Matrix4x4.Identity;
                viewProjectionMatrix = Matrix4x4.Identity;
            }

            program.Uniform(viewName.ToStringFast(), viewMatrix);
            program.Uniform(inverseViewName.ToStringFast(), inverseViewMatrix);
            program.Uniform(inverseProjectionName.ToStringFast(), inverseProjectionMatrix);
            program.Uniform(projectionName.ToStringFast(), projectionMatrix);
            program.Uniform(viewProjectionName.ToStringFast(), viewProjectionMatrix);

            program.Uniform(viewName.ToVertexUniformName(), viewMatrix);
            program.Uniform(inverseViewName.ToVertexUniformName(), inverseViewMatrix);
            program.Uniform(inverseProjectionName.ToVertexUniformName(), inverseProjectionMatrix);
            program.Uniform(projectionName.ToVertexUniformName(), projectionMatrix);
            program.Uniform(viewProjectionName.ToVertexUniformName(), viewProjectionMatrix);
        }

        // =========== Vulkan State Conversion Helpers ===========

        private static ECullMode ResolveCullMode(ECullMode mode)
        {
            if (!RuntimeEngine.Rendering.State.ReverseCulling)
                return mode;

            return mode switch
            {
                ECullMode.Front => ECullMode.Back,
                ECullMode.Back => ECullMode.Front,
                _ => mode
            };
        }

        private static EWinding ResolveWinding(EWinding winding)
        {
            if (!RuntimeEngine.Rendering.State.ReverseWinding)
                return winding;

            return winding == EWinding.Clockwise ? EWinding.CounterClockwise : EWinding.Clockwise;
        }

        private static BlendMode? ResolveBlendMode(RenderingParameters parameters)
        {
            if (parameters.BlendModeAllDrawBuffers is not null)
                return parameters.BlendModeAllDrawBuffers;

            if (parameters.BlendModesPerDrawBuffer is not null && parameters.BlendModesPerDrawBuffer.Count > 0)
            {
                if (parameters.BlendModesPerDrawBuffer.TryGetValue(0u, out BlendMode? primary))
                    return primary;

                return parameters.BlendModesPerDrawBuffer.Values.FirstOrDefault();
            }

            return null;
        }

        private static ColorComponentFlags ToVulkanColorWriteMask(RenderingParameters parameters)
        {
            ColorComponentFlags mask = 0;
            if (parameters.WriteRed)
                mask |= ColorComponentFlags.RBit;
            if (parameters.WriteGreen)
                mask |= ColorComponentFlags.GBit;
            if (parameters.WriteBlue)
                mask |= ColorComponentFlags.BBit;
            if (parameters.WriteAlpha)
                mask |= ColorComponentFlags.ABit;
            return mask;
        }

        private static CullModeFlags ToVulkanCullMode(ECullMode mode)
            => mode switch
            {
                ECullMode.None => CullModeFlags.None,
                ECullMode.Back => CullModeFlags.BackBit,
                ECullMode.Front => CullModeFlags.FrontBit,
                ECullMode.Both => CullModeFlags.FrontAndBack,
                _ => CullModeFlags.BackBit
            };

        private static FrontFace ToVulkanFrontFace(EWinding winding)
            => winding switch
            {
                EWinding.Clockwise => FrontFace.Clockwise,
                EWinding.CounterClockwise => FrontFace.CounterClockwise,
                _ => FrontFace.CounterClockwise
            };

        private static StencilOpState ToVulkanStencilState(StencilTestFace face)
            => new()
            {
                FailOp = ToVulkanStencilOp(face.BothFailOp),
                PassOp = ToVulkanStencilOp(face.BothPassOp),
                DepthFailOp = ToVulkanStencilOp(face.StencilPassDepthFailOp),
                CompareOp = ToVulkanCompareOp(face.Function),
                CompareMask = face.ReadMask,
                WriteMask = face.WriteMask,
                Reference = (uint)Math.Max(face.Reference, 0)
            };

        private static Silk.NET.Vulkan.StencilOp ToVulkanStencilOp(EStencilOp op)
            => op switch
            {
                EStencilOp.Zero => Silk.NET.Vulkan.StencilOp.Zero,
                EStencilOp.Invert => Silk.NET.Vulkan.StencilOp.Invert,
                EStencilOp.Keep => Silk.NET.Vulkan.StencilOp.Keep,
                EStencilOp.Replace => Silk.NET.Vulkan.StencilOp.Replace,
                EStencilOp.Incr => Silk.NET.Vulkan.StencilOp.IncrementAndClamp,
                EStencilOp.Decr => Silk.NET.Vulkan.StencilOp.DecrementAndClamp,
                EStencilOp.IncrWrap => Silk.NET.Vulkan.StencilOp.IncrementAndWrap,
                EStencilOp.DecrWrap => Silk.NET.Vulkan.StencilOp.DecrementAndWrap,
                _ => Silk.NET.Vulkan.StencilOp.Keep
            };

        private static BlendOp ToVulkanBlendOp(EBlendEquationMode mode)
            => mode switch
            {
                EBlendEquationMode.FuncAdd => BlendOp.Add,
                EBlendEquationMode.FuncSubtract => BlendOp.Subtract,
                EBlendEquationMode.FuncReverseSubtract => BlendOp.ReverseSubtract,
                EBlendEquationMode.Min => BlendOp.Min,
                EBlendEquationMode.Max => BlendOp.Max,
                _ => BlendOp.Add
            };

        private static BlendFactor ToVulkanBlendFactor(EBlendingFactor factor)
            => factor switch
            {
                EBlendingFactor.Zero => BlendFactor.Zero,
                EBlendingFactor.One => BlendFactor.One,
                EBlendingFactor.SrcColor => BlendFactor.SrcColor,
                EBlendingFactor.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
                EBlendingFactor.DstColor => BlendFactor.DstColor,
                EBlendingFactor.OneMinusDstColor => BlendFactor.OneMinusDstColor,
                EBlendingFactor.SrcAlpha => BlendFactor.SrcAlpha,
                EBlendingFactor.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
                EBlendingFactor.DstAlpha => BlendFactor.DstAlpha,
                EBlendingFactor.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
                EBlendingFactor.SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
                EBlendingFactor.ConstantColor => BlendFactor.ConstantColor,
                EBlendingFactor.OneMinusConstantColor => BlendFactor.OneMinusConstantColor,
                EBlendingFactor.ConstantAlpha => BlendFactor.ConstantAlpha,
                EBlendingFactor.OneMinusConstantAlpha => BlendFactor.OneMinusConstantAlpha,
                EBlendingFactor.Src1Color => BlendFactor.Src1Color,
                EBlendingFactor.OneMinusSrc1Color => BlendFactor.OneMinusSrc1Color,
                EBlendingFactor.Src1Alpha => BlendFactor.Src1Alpha,
                EBlendingFactor.OneMinusSrc1Alpha => BlendFactor.OneMinusSrc1Alpha,
                _ => BlendFactor.One
            };
    }
}
