using Silk.NET.OpenGL;
using System;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// One headset exposure for the two pipelines of a two-pass VR eye pair (OpenVR or OpenXR on
/// OpenGL). The left eye copies its adapted 1x1 exposure into one renderer-wide storage buffer
/// (publish). The right eye copies that value into its own 1x1 exposure texture (follow), so its
/// tonemap samples the same exposure. OpenGL runs the commands of one context in submission order,
/// and the left eye renders before the right eye, so the right eye reads the value of the same frame.
/// </summary>
public partial class OpenGLRenderer
{
    // Storage buffer binding of the headset exposure in both programs.
    private const uint HeadsetExposureBufferBinding = 1u;

    private XRRenderProgram? _headsetExposurePublishProgram;
    private XRRenderProgram? _headsetExposureFollowProgram;
    // One vec4: x = headset exposure, y = 1 after the first publish. Zero means no value yet.
    private uint _headsetExposureBuffer;
    private bool _headsetExposureInitialized;

    // Copies the left eye's exposure into the headset exposure buffer. An invalid exposure
    // (not finite or not positive) is not published.
    private const string HeadsetExposurePublishShaderSource = @"#version 430
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(r32f, binding = 0) uniform image2D ExposureOut;
layout(std430, binding = 1) buffer HeadsetExposureBlock
{
    vec4 HeadsetExposure;
};

void main()
{
    float exposure = imageLoad(ExposureOut, ivec2(0, 0)).r;
    if (isnan(exposure) || isinf(exposure) || !(exposure > 0.0))
        return;
    HeadsetExposure = vec4(exposure, 1.0, 0.0, 0.0);
}
";

    // Writes the headset exposure into the right eye's exposure. Before the first publish, the
    // image keeps its own valid value or gets the fallback exposure, with the same rules as the
    // auto exposure shaders.
    private const string HeadsetExposureFollowShaderSource = @"#version 430
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(r32f, binding = 0) uniform image2D ExposureOut;
layout(std430, binding = 1) buffer HeadsetExposureBlock
{
    vec4 HeadsetExposure;
};

uniform float MinExposure;
uniform float MaxExposure;
uniform float FallbackExposure;

void main()
{
    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    bool currentValid = !(isnan(current) || isinf(current)) && current >= MinExposure && current <= MaxExposure;
    float result = currentValid ? current : clamp(FallbackExposure, MinExposure, MaxExposure);

    vec4 headset = HeadsetExposure;
    if (headset.y > 0.5 && !(isnan(headset.x) || isinf(headset.x)) && headset.x > 0.0)
        result = clamp(headset.x, MinExposure, MaxExposure);

    imageStore(ExposureOut, ivec2(0, 0), vec4(result, 0.0, 0.0, 0.0));
}
";

    public override bool SupportsHeadsetSharedAutoExposure
        => SupportsGpuAutoExposure && EnsureHeadsetExposureResources();

    public override bool PublishHeadsetAutoExposureGpu(XRTexture2D exposureTex)
    {
        if (!EnsureHeadsetExposureResources() ||
            exposureTex is null ||
            !TryResolveExposureImage(exposureTex, out uint exposureBindingId) ||
            GenericToAPI<GLRenderProgram>(_headsetExposurePublishProgram) is not { } glProgram ||
            !glProgram.Use())
        {
            return false;
        }

        // The exposure dispatch wrote the image with imageStore; make that write visible to this load.
        Api.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit);
        Api.BindImageTexture(0, exposureBindingId, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R32f);
        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, HeadsetExposureBufferBinding, _headsetExposureBuffer);
        try
        {
            Api.DispatchCompute(1, 1, 1);
            MarkImmediateHistoryGpuWriteUnproven();
            Api.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        }
        finally
        {
            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, HeadsetExposureBufferBinding, 0);
        }
        return true;
    }

    public override bool FollowHeadsetAutoExposureGpu(XRTexture2D exposureTex, ColorGradingSettings settings)
    {
        if (!EnsureHeadsetExposureResources() ||
            exposureTex is null || settings is null ||
            !TryResolveExposureImage(exposureTex, out uint exposureBindingId) ||
            GenericToAPI<GLRenderProgram>(_headsetExposureFollowProgram) is not { } glProgram ||
            !glProgram.Use())
        {
            return false;
        }

        settings.GetResolvedExposureBounds(out float minExposure, out float maxExposure);
        float fallbackExposure = settings.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
            ? settings.ComputePhysicalExposureMultiplier()
            : settings.Exposure;
        glProgram.Uniform("MinExposure", minExposure);
        glProgram.Uniform("MaxExposure", maxExposure);
        glProgram.Uniform("FallbackExposure", Math.Clamp(fallbackExposure, minExposure, maxExposure));

        // Make the left eye's publish visible to this read.
        Api.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
        Api.BindImageTexture(0, exposureBindingId, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R32f);
        Api.BindBufferBase(GLEnum.ShaderStorageBuffer, HeadsetExposureBufferBinding, _headsetExposureBuffer);
        try
        {
            Api.DispatchCompute(1, 1, 1);
            MarkImmediateHistoryGpuWriteUnproven();
            Api.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        }
        finally
        {
            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, HeadsetExposureBufferBinding, 0);
        }
        return true;
    }

    /// <summary>
    /// Creates the two headset exposure programs and the zeroed storage buffer once. Returns
    /// false when GPU auto exposure is not available or creation failed; the eyes then adapt on
    /// their own.
    /// </summary>
    private unsafe bool EnsureHeadsetExposureResources()
    {
        if (_headsetExposureInitialized)
            return true;
        if (_supportsGpuAutoExposure != true)
            return false;

        try
        {
            _headsetExposurePublishProgram = new XRRenderProgram(
                true, false, new XRShader(EShaderType.Compute, HeadsetExposurePublishShaderSource));
            _headsetExposureFollowProgram = new XRRenderProgram(
                true, false, new XRShader(EShaderType.Compute, HeadsetExposureFollowShaderSource));

            _headsetExposureBuffer = Api.GenBuffer();
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, _headsetExposureBuffer);
            System.Numerics.Vector4 zero = System.Numerics.Vector4.Zero;
            Api.BufferData(GLEnum.ShaderStorageBuffer, 16u, &zero, GLEnum.DynamicCopy);
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            _headsetExposureInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.OpenGLWarning($"Failed to initialize headset exposure compute shaders: {ex.Message}");
            DestroyHeadsetExposureResources(orphanGLHandles: false);
        }
        return _headsetExposureInitialized;
    }

    /// <summary>Deletes the headset exposure programs and buffer.</summary>
    private void DestroyHeadsetExposureResources(bool orphanGLHandles)
    {
        if (_headsetExposureBuffer != 0 && !orphanGLHandles)
            Api.DeleteBuffer(_headsetExposureBuffer);
        _headsetExposureBuffer = 0;
        _headsetExposurePublishProgram?.Destroy();
        _headsetExposureFollowProgram?.Destroy();
        _headsetExposurePublishProgram = null;
        _headsetExposureFollowProgram = null;
        _headsetExposureInitialized = false;
    }

    /// <summary>
    /// Gets the GL name of a 1x1 exposure texture for image load/store. A framebuffer-only
    /// texture may still be only a generated name, so it is bound once to make the driver create
    /// the texture object and its storage. Returns false (with a rate-limited warning) when the
    /// texture is not ready; an invalid image binding causes expensive GL debug-driver errors.
    /// </summary>
    private bool TryResolveExposureImage(XRTexture2D exposureTex, out uint exposureBindingId)
    {
        exposureBindingId = 0;
        var glExposure = GetOrCreateAPIRenderObject(exposureTex, generateNow: true) as GLTexture2D;
        if (glExposure is not null)
        {
            IGLTexture? previousBoundTexture = BoundTexture;
            glExposure.Bind();
            if (previousBoundTexture is null || ReferenceEquals(previousBoundTexture, glExposure))
                glExposure.Unbind();
            else
                previousBoundTexture.Bind();

            exposureBindingId = glExposure.BindingId;
            if (exposureBindingId != GLObjectBase.InvalidBindingId && Api.IsTexture(exposureBindingId))
                return true;
        }

        string exposureTextureName = exposureTex.Name ?? "<unnamed>";
        Debug.OpenGLWarningEvery(
            $"AutoExposure.InvalidExposure.{exposureTextureName}",
            TimeSpan.FromSeconds(1),
            "[AutoExposure] Exposure texture is not ready for image load/store. Texture='{0}', BindingId={1}.",
            exposureTextureName,
            glExposure?.BindingId ?? 0);
        exposureBindingId = 0;
        return false;
    }
}
