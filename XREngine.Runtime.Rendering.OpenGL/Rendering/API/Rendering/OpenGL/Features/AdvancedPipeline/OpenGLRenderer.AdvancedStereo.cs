using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private XRRenderProgram? _advancedStereoIndirectProgram;
    private XRRenderProgram? _advancedStereoRasterProgram;
    private XRRenderProgram? _advancedStereoMaskedRasterProgram;
    private bool? _advancedMultiviewSupported;
    private bool _advancedStereoReady;
    private string? _advancedStereoProgramFailure;

    private bool SupportsAdvancedMultiview
        => _advancedMultiviewSupported ??= OVRMultiView is not null &&
            RawGL.IsExtensionPresent("GL_OVR_multiview2") && RawGL.GetInteger((GLEnum)0x9631) >= 2;

    private bool TryEnsureAdvancedStereoPrograms(out string reason)
    {
        _advancedStereoReady = false;
        if (!SupportsAdvancedMultiview)
        {
            reason = "OpenGL Advanced stereo requires GL_OVR_multiview2 and at least two hardware views.";
            return false;
        }
        if (_advancedStereoProgramFailure is not null)
        {
            reason = _advancedStereoProgramFailure;
            return false;
        }
        if (RawGL.GetInteger(GLEnum.MaxVertexShaderStorageBlocks) < 9)
        {
            reason = "OpenGL Advanced stereo requires nine vertex-stage storage blocks.";
            return false;
        }
        try
        {
            const EAdvancedTextureIndirectionMode mode = EAdvancedTextureIndirectionMode.OpenGlBindlessHandles;
            _advancedStereoIndirectProgram ??= CreateAdvancedComputeProgram(
                "Advanced.Preparation.StereoIndirect", "Advanced/Preparation/BuildOpenGlStereoIndirect.comp", mode);
            _advancedStereoRasterProgram ??= CreateAdvancedRasterProgram(mode, stereo: true);
            _advancedStereoMaskedRasterProgram ??= CreateAdvancedRasterProgram(mode, masked: true, stereo: true);
        }
        catch (Exception exception)
        {
            reason = _advancedStereoProgramFailure = $"OpenGL Advanced stereo program creation failed: {exception.Message}";
            return false;
        }
        if (!IsLinked(_advancedStereoIndirectProgram) || !IsLinked(_advancedStereoRasterProgram) || !IsLinked(_advancedStereoMaskedRasterProgram))
        {
            reason = "OpenGL Advanced single-pass stereo programs are compiling or linking.";
            return false;
        }
        _advancedStereoReady = true;
        reason = "Ready";
        return true;
    }

    /// <summary>Compacts the union of two independently culled view streams,
    /// preserving a per-payload eye mask for hardware multiview raster.</summary>
    private bool TryBuildAdvancedStereoRasterStream(OpenGLAdvancedVisibilitySlot slot, bool late, out string reason)
    {
        if (!TryEnsureAdvancedStereoPrograms(out reason)) return false;
        uint payloads = checked((uint)_advancedInputStorage!.Payloads.Length);
        uint ranges = checked((uint)_advancedInputStorage.IndirectRanges.Length);
        slot.EnsureStorage(this, 75u, checked(payloads * 20u), clear: false);
        slot.EnsureStorage(this, 76u, checked(ranges * sizeof(uint)), clear: true);
        slot.EnsureStorage(this, 77u, checked(payloads * sizeof(uint)), clear: false);
        for (uint binding = 75u; binding <= 77u; binding++)
            RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, binding, slot.Buffer(binding));
        Span<uint> push = stackalloc uint[4] { 2u, payloads, late ? 1u : 0u, 0u };
        slot.UploadUniform(this, 0u, push);
        if (GenericToAPI<GLRenderProgram>(_advancedStereoIndirectProgram) is not { } program || !program.Use())
        {
            reason = "OpenGL Advanced stereo compaction program is unavailable.";
            return false;
        }
        RawGL.DispatchCompute(Math.Max(1u, (payloads + 255u) / 256u), 1u, 1u);
        RawGL.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.CommandBarrierBit);
        reason = "Ready";
        return true;
    }
}
