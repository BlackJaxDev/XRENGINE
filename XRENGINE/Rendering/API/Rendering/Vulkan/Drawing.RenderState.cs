using System;
using System.Linq;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Render Parameters ===========

        private float _materialUniformSecondsLive;

        public override void ApplyRenderParameters(RenderingParameters parameters)
        {
            if (parameters is null)
                return;

            // Apply color write mask
            _state.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);
            _state.SetCullMode(ToVulkanCullMode(ResolveCullMode(parameters.CullMode)));
            _state.SetFrontFace(ToVulkanFrontFace(ResolveWinding(parameters.Winding)));

            // Apply depth test settings
            var depthTest = parameters.DepthTest;
            if (depthTest.Enabled == ERenderParamUsage.Enabled)
            {
                _state.SetDepthTestEnabled(true);
                _state.SetDepthWriteEnabled(depthTest.UpdateDepth);
                _state.SetDepthCompare(ToVulkanCompareOp(Engine.Rendering.State.MapDepthComparison(depthTest.Function)));
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

            MarkCommandBuffersDirty();
        }

        // =========== Engine & Material Uniforms ===========

        public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
        {
            if (program is null)
                return;

            bool stereoPass = Engine.Rendering.State.IsStereoPass;
            if (stereoPass)
            {
                var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                PassCameraUniforms(program, camera, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeProjMatrix);
                PassCameraUniforms(program, rightCam, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeProjMatrix);
            }
            else
            {
                PassCameraUniforms(program, camera, EEngineUniform.InverseViewMatrix, EEngineUniform.ProjMatrix);
            }
        }

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
        {
            if (material is null || program is null)
                return;

            if (material.RenderOptions is not null)
                ApplyRenderParameters(material.RenderOptions);

            foreach (ShaderVar param in material.Parameters)
                param.SetUniform(program, forceUpdate: true);

            for (int i = 0; i < material.Textures.Count; i++)
            {
                XRTexture? texture = material.Textures[i];
                if (texture is null)
                    continue;

                string samplerName = texture.ResolveSamplerName(i, null);
                program.Sampler(samplerName, texture, i);
            }

            _materialUniformSecondsLive += Engine.Time.Timer.Update.Delta;
            var reqs = material.RenderOptions?.RequiredEngineUniforms ?? EUniformRequirements.None;

            if (reqs.HasFlag(EUniformRequirements.Camera))
            {
                Engine.Rendering.State.RenderingCamera?.SetUniforms(program, true);
                Engine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program, false);
            }

            if (reqs.HasFlag(EUniformRequirements.Lights))
                Engine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program);

            if (reqs.HasFlag(EUniformRequirements.RenderTime))
                program.Uniform(nameof(EUniformRequirements.RenderTime), _materialUniformSecondsLive);

            if (reqs.HasFlag(EUniformRequirements.ViewportDimensions))
            {
                var area = Engine.Rendering.State.RenderArea;
                program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)area.Width);
                program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)area.Height);
            }

            material.OnSettingUniforms(program);
        }

        private static void PassCameraUniforms(XRRenderProgram program, XRCamera? camera, EEngineUniform inverseViewName, EEngineUniform projectionName)
        {
            Matrix4x4 viewMatrix;
            Matrix4x4 inverseViewMatrix;
            Matrix4x4 projectionMatrix;
            if (camera is not null)
            {
                viewMatrix = camera.Transform.InverseRenderMatrix;
                inverseViewMatrix = camera.Transform.RenderMatrix;
                bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                projectionMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            }
            else
            {
                viewMatrix = Matrix4x4.Identity;
                inverseViewMatrix = Matrix4x4.Identity;
                projectionMatrix = Matrix4x4.Identity;
            }

            program.Uniform(EEngineUniform.ViewMatrix.ToStringFast(), viewMatrix);
            program.Uniform(inverseViewName.ToStringFast(), inverseViewMatrix);
            program.Uniform(projectionName.ToStringFast(), projectionMatrix);

            program.Uniform(EEngineUniform.ViewMatrix.ToVertexUniformName(), viewMatrix);
            program.Uniform(inverseViewName.ToVertexUniformName(), inverseViewMatrix);
            program.Uniform(projectionName.ToVertexUniformName(), projectionMatrix);
        }

        // =========== Vulkan State Conversion Helpers ===========

        private static ECullMode ResolveCullMode(ECullMode mode)
        {
            if (!Engine.Rendering.State.ReverseCulling)
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
            if (!Engine.Rendering.State.ReverseWinding)
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

        private static StencilOp ToVulkanStencilOp(EStencilOp op)
            => op switch
            {
                EStencilOp.Zero => StencilOp.Zero,
                EStencilOp.Invert => StencilOp.Invert,
                EStencilOp.Keep => StencilOp.Keep,
                EStencilOp.Replace => StencilOp.Replace,
                EStencilOp.Incr => StencilOp.IncrementAndClamp,
                EStencilOp.Decr => StencilOp.DecrementAndClamp,
                EStencilOp.IncrWrap => StencilOp.IncrementAndWrap,
                EStencilOp.DecrWrap => StencilOp.DecrementAndWrap,
                _ => StencilOp.Keep
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
