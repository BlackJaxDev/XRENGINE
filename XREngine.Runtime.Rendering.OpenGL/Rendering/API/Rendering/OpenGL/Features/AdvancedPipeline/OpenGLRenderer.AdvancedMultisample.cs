using Silk.NET.OpenGL;
using XREngine.Core.Files;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Generator;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private XRRenderProgram? _advancedMsaaShadeProgram;
    private XRRenderProgram? _advancedMsaaLateProgram;
    private XRRenderProgram? _advancedMsaaResolveProgram;
    private uint _advancedMsaaResolveVao;
    private static readonly string[] AdvancedMultisampleNames =
    [
        AdvancedVisibilityResourceNames.IdentityMultisample,
        AdvancedVisibilityResourceNames.MetadataMultisample,
        AdvancedVisibilityResourceNames.SelectionMultisample,
        AdvancedVisibilityResourceNames.DepthStencilMultisample,
        AdvancedVisibilityResourceNames.SamplePositionMultisample,
    ];

    private bool TryEnsureAdvancedMultisamplePrograms(out string reason)
    {
        if (RawGL.GetInteger(GLEnum.MaxComputeTextureImageUnits) < 10)
        {
            reason = "Advanced MSAA requires ten compute texture units.";
            return false;
        }
        EAdvancedTextureIndirectionMode mode = EAdvancedTextureIndirectionMode.OpenGlBindlessHandles;
        _advancedMsaaShadeProgram ??= CreateAdvancedComputeProgram("Advanced.Shading.NativeOpaqueMsaa",
            "Advanced/Shading/ShadeNativeOpaqueMsaa.comp", mode);
        _advancedMsaaLateProgram ??= CreateAdvancedComputeProgram("Advanced.Preparation.LateVisibilityMsaa",
            "Advanced/Preparation/LateVisibility.comp", mode, "\n#define XR_ADV_DISABLE_HZB_OCCLUSION 1\n");
        if (_advancedMsaaResolveProgram is null)
        {
            XRShader fragmentTemplate = ShaderHelper.LoadEngineShader("Advanced/Visibility/ResolveAdvancedMsaaVisibility.frag", EShaderType.Fragment);
            XRShader fragment = new(EShaderType.Fragment, new TextFile(fragmentTemplate.Source.FilePath ?? "Advanced/Visibility/ResolveAdvancedMsaaVisibility.frag")
            {
                Text = InjectAdvancedPreamble(ResolveAdvancedShaderSource(fragmentTemplate),
                    AdvancedShaderAccessLibrary.BuildPreamble(RuntimeGraphicsApiKind.OpenGL, mode)),
            });
            XRShader vertex = new(EShaderType.Vertex, new TextFile
            {
                Text = "#version 450 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0.0,1.0);}",
            });
            _advancedMsaaResolveProgram = new XRRenderProgram(true, false, vertex, fragment) { Name = "Advanced.Visibility.MultisampleResolve" };
        }
        bool ready = IsLinked(_advancedMsaaShadeProgram) && IsLinked(_advancedMsaaLateProgram) && IsLinked(_advancedMsaaResolveProgram);
        reason = ready ? "Ready" : "Advanced MSAA programs are compiling or failed to link.";
        return ready;
    }

    private bool TryGetAdvancedMultisampleTextures(uint samples, Span<uint> ids, out string reason)
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        for (int index = 0; index < AdvancedMultisampleNames.Length; index++)
        {
            if (pipeline is null || !pipeline.Resources.TryGetTexture(AdvancedMultisampleNames[index], out XRTexture? texture) ||
                texture is not XRTexture2DArray array || !array.MultiSample || array.Textures.Length == 0 ||
                array.Textures[0].MultiSampleCount != samples ||
                GetOrCreateAPIRenderObject(array, generateNow: true) is not GLTexture2DArray gl)
            {
                reason = $"Advanced MSAA is missing its {samples}-sample array '{AdvancedMultisampleNames[index]}'.";
                return false;
            }
            gl.Bind();
            if (!gl.TryGetBindingId(out ids[index]))
            {
                reason = "Advanced MSAA texture storage has not been realized.";
                return false;
            }
            RawGL.GetTextureLevelParameter(ids[index], 0, GLEnum.TextureSamples, out int actualSamples);
            if (actualSamples != samples)
            {
                reason = $"Advanced MSAA requested {samples} samples, but '{AdvancedMultisampleNames[index]}' has {actualSamples}.";
                return false;
            }
        }
        reason = "Ready";
        return true;
    }

    private unsafe bool TryResolveAdvancedMultisampleVisibility(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        if (!TryEnsureAdvancedMultisamplePrograms(out reason) ||
            !TryCaptureAdvancedVisibilityOutputClosure(in request, out var target, out reason))
            return false;
        Span<uint> raw = stackalloc uint[5];
        if (!TryGetAdvancedMultisampleTextures(request.MsaaSampleCount, raw, out reason) ||
            GenericToAPI<GLRenderProgram>(_advancedMsaaResolveProgram) is not { } program || !program.Use())
            return false;
        int oldFramebuffer = RawGL.GetInteger(GLEnum.DrawFramebufferBinding);
        int oldVao = RawGL.GetInteger(GLEnum.VertexArrayBinding);
        int oldDepthFunction = RawGL.GetInteger(GLEnum.DepthFunc);
        bool oldDepthWrite = RawGL.GetBoolean(GLEnum.DepthWritemask);
        bool oldDepth = RawGL.IsEnabled(GLEnum.DepthTest), oldBlend = RawGL.IsEnabled(GLEnum.Blend);
        bool oldCull = RawGL.IsEnabled(GLEnum.CullFace), oldScissor = RawGL.IsEnabled(GLEnum.ScissorTest);
        bool oldStencil = RawGL.IsEnabled(GLEnum.StencilTest);
        int* viewport = stackalloc int[4];
        RawGL.GetInteger(GLEnum.Viewport, viewport);
        int* masks = stackalloc int[12];
        Span<uint> previousSamplers = stackalloc uint[4];
        for (uint index = 0; index < 4; index++)
            previousSamplers[(int)index] = unchecked((uint)RawGL.GetInteger(GLEnum.SamplerBinding, 5u + index));
        for (uint attachment = 0; attachment < 3; attachment++)
            RawGL.GetInteger(GLEnum.ColorWritemask, attachment, masks + attachment * 4);
        if (_advancedRasterFramebuffer == 0u) _advancedRasterFramebuffer = RawGL.GenFramebuffer();
        if (_advancedMsaaResolveVao == 0u) _advancedMsaaResolveVao = RawGL.GenVertexArray();
        try
        {
            RawGL.MemoryBarrier(MemoryBarrierMask.FramebufferBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
            RawGL.BindFramebuffer(GLEnum.DrawFramebuffer, _advancedRasterFramebuffer);
            RawGL.BindVertexArray(_advancedMsaaResolveVao);
            RawGL.Enable(GLEnum.DepthTest);
            RawGL.DepthFunc(DepthFunction.Always);
            RawGL.DepthMask(true);
            RawGL.Disable(GLEnum.Blend);
            RawGL.Disable(GLEnum.CullFace);
            RawGL.Disable(GLEnum.ScissorTest);
            RawGL.Disable(GLEnum.StencilTest);
            RawGL.Viewport(0, 0, target.Width, target.Height);
            uint* outputs = stackalloc uint[3] { (uint)GLEnum.ColorAttachment0, (uint)GLEnum.ColorAttachment1, (uint)GLEnum.ColorAttachment2 };
            RawGL.DrawBuffers(3, (GLEnum*)outputs);
            RawGL.NamedFramebufferTexture(_advancedRasterFramebuffer, GLEnum.ColorAttachment3, 0, 0);
            for (uint index = 0; index < 4; index++)
            {
                RawGL.BindTextureUnit(5u + index, raw[(int)index]);
                // Multisample textures have no sampling state.
                RawGL.BindSampler(5u + index, 0u);
            }
            for (uint attachment = 0; attachment < 3; attachment++)
                RawGL.ColorMask(attachment, true, true, true, true);
            for (uint view = 0; view < request.Views.ViewCount; view++)
            {
                RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment0, target.IdentityId, 0, (int)view);
                RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment1, target.MetadataId, 0, (int)view);
                RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.ColorAttachment2, target.SelectionId, 0, (int)view);
                RawGL.NamedFramebufferTextureLayer(_advancedRasterFramebuffer, GLEnum.DepthAttachment, target.DepthId, 0, (int)view);
                if (RawGL.CheckNamedFramebufferStatus(_advancedRasterFramebuffer, GLEnum.DrawFramebuffer) != GLEnum.FramebufferComplete)
                {
                    reason = "Advanced MSAA canonical resolve framebuffer is incomplete.";
                    return false;
                }
                program.Uniform("MultisampleViewIndex", view);
                program.Uniform("MultisampleReversedDepth", request.Views.GetView((int)view).ReversedDepth);
                RawGL.DrawArrays(GLEnum.Triangles, 0, 3);
            }
            RawGL.MemoryBarrier(MemoryBarrierMask.FramebufferBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit | MemoryBarrierMask.ShaderImageAccessBarrierBit);
            reason = "Ready";
            return true;
        }
        finally
        {
            RestoreAdvancedNativeSamplers(5u, previousSamplers);
            RawGL.BindFramebuffer(GLEnum.DrawFramebuffer, (uint)oldFramebuffer);
            RawGL.BindVertexArray((uint)oldVao);
            RawGL.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
            RawGL.DepthFunc((DepthFunction)oldDepthFunction);
            RawGL.DepthMask(oldDepthWrite);
            RestoreAdvancedEnable(GLEnum.DepthTest, oldDepth);
            RestoreAdvancedEnable(GLEnum.Blend, oldBlend);
            RestoreAdvancedEnable(GLEnum.CullFace, oldCull);
            RestoreAdvancedEnable(GLEnum.ScissorTest, oldScissor);
            RestoreAdvancedEnable(GLEnum.StencilTest, oldStencil);
            for (uint attachment = 0; attachment < 3; attachment++)
            {
                int* mask = masks + attachment * 4;
                RawGL.ColorMask(attachment, mask[0] != 0, mask[1] != 0, mask[2] != 0, mask[3] != 0);
            }
        }
    }
}
