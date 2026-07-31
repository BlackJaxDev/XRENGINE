using System.Numerics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Enqueue-time scalar values and content generations used by the
/// frequency-owned auto-uniform publication path. The render thread consumes
/// this snapshot without hashing every matrix again for each reflected block
/// or rereading mutable camera and mesh-renderer state.
/// </summary>
internal readonly record struct VulkanAutoUniformPublicationSnapshot(
    ulong FrameGeneration,
    ulong ViewGeneration,
    ulong PassGeneration,
    ulong ObjectGeneration,
    ulong InstanceGeneration,
    ulong RuntimeCallbackGeneration,
    VulkanBindingFrequencyGenerations TypedPublicationGenerations,
    float CameraNearZ,
    float CameraFarZ,
    float CameraFovX,
    float CameraFovY,
    float CameraAspect,
    XRCamera.EDepthMode CameraDepthMode,
    uint SkinPaletteBase,
    uint SkinPaletteCount,
    int SkinningInfluenceCap,
    int BlendshapeActiveCount,
    float BlendshapeWeightThreshold,
    bool HasValidPrecombinedBlendshapeDeltas)
{
    internal static VulkanAutoUniformPublicationSnapshot Capture(
        in PendingMeshDraw draw)
    {
        XRCamera? camera = draw.Camera;
        XRPerspectiveCameraParameters? perspective =
            camera?.Parameters as XRPerspectiveCameraParameters;
        XRMeshRenderer meshRenderer = draw.Renderer.MeshRenderer;

        float cameraNearZ = camera?.NearZ ?? 0f;
        float cameraFarZ = camera?.FarZ ?? 0f;
        float cameraFovX = perspective?.HorizontalFieldOfView ?? 0f;
        float cameraFovY = perspective?.VerticalFieldOfView ?? 0f;
        float cameraAspect = perspective?.AspectRatio ?? 0f;
        XRCamera.EDepthMode cameraDepthMode =
            camera?.DepthMode ?? XRCamera.EDepthMode.Normal;
        uint skinPaletteBase = meshRenderer.ActiveSkinPaletteBase;
        uint skinPaletteCount = meshRenderer.ActiveSkinPaletteCount;
        int skinningInfluenceCap = meshRenderer.ActiveSkinningInfluenceCap;
        int blendshapeActiveCount = meshRenderer.ActiveBlendshapeCount;
        float blendshapeWeightThreshold =
            meshRenderer.BlendshapeActiveWeightThreshold;
        bool hasValidPrecombinedBlendshapeDeltas =
            meshRenderer.HasValidPrecombinedBlendshapeDeltas;

        return new VulkanAutoUniformPublicationSnapshot(
            ComputeFrameGeneration(),
            ComputeViewGeneration(
                draw,
                cameraNearZ,
                cameraFarZ,
                cameraFovX,
                cameraFovY,
                cameraAspect,
                cameraDepthMode),
            ComputePassGeneration(draw),
            ComputeObjectGeneration(
                draw,
                skinPaletteBase,
                skinPaletteCount,
                skinningInfluenceCap,
                blendshapeActiveCount,
                blendshapeWeightThreshold,
                hasValidPrecombinedBlendshapeDeltas),
            ComputeInstanceGeneration(draw),
            ComputeRuntimeCallbackGeneration(draw.ProgramBindingSnapshot),
            draw.ProgramBindingSnapshot?.TypedPublicationGenerations ?? default,
            cameraNearZ,
            cameraFarZ,
            cameraFovX,
            cameraFovY,
            cameraAspect,
            cameraDepthMode,
            skinPaletteBase,
            skinPaletteCount,
            skinningInfluenceCap,
            blendshapeActiveCount,
            blendshapeWeightThreshold,
            hasValidPrecombinedBlendshapeDeltas);
    }

    internal ulong GetGeneration(
        EVulkanBindingFrequency frequency,
        ulong materialGeneration)
        => frequency switch
        {
            EVulkanBindingFrequency.Frame =>
                CombineGeneration(FrameGeneration, frequency),
            EVulkanBindingFrequency.View =>
                CombineGeneration(ViewGeneration, frequency),
            EVulkanBindingFrequency.Pass =>
                CombineGeneration(PassGeneration, frequency),
            EVulkanBindingFrequency.Material =>
                CombineGeneration(materialGeneration, frequency),
            EVulkanBindingFrequency.Object =>
                CombineGeneration(ObjectGeneration, frequency),
            EVulkanBindingFrequency.Instance =>
                CombineGeneration(InstanceGeneration, frequency),
            EVulkanBindingFrequency.RuntimeCallback =>
                CombineGeneration(RuntimeCallbackGeneration, frequency),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };

    private ulong CombineGeneration(
        ulong ownerGeneration,
        EVulkanBindingFrequency frequency)
    {
        ulong typedGeneration = TypedPublicationGenerations.Get(frequency);
        if (typedGeneration == 0)
            return ownerGeneration;

        FrameOpSignatureHasher hash = new();
        hash.Add(ownerGeneration);
        hash.Add(typedGeneration);
        return hash.ToHash();
    }

    private static ulong ComputeFrameGeneration()
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(RuntimeEngine.Rendering.State.RenderFrameId);
        return hash.ToHash();
    }

    private static ulong ComputeViewGeneration(
        in PendingMeshDraw draw,
        float cameraNearZ,
        float cameraFarZ,
        float cameraFovX,
        float cameraFovY,
        float cameraAspect,
        XRCamera.EDepthMode cameraDepthMode)
    {
        FrameOpSignatureHasher hash = new();
        AddMatrix(ref hash, draw.ViewMatrix);
        AddMatrix(ref hash, draw.InverseViewMatrix);
        AddMatrix(ref hash, draw.ProjectionMatrix);
        AddMatrix(ref hash, draw.InverseProjectionMatrix);
        AddMatrix(ref hash, draw.ViewProjectionMatrix);
        AddMatrix(ref hash, draw.ViewProjectionMatrixUnjittered);
        AddMatrix(ref hash, draw.PreviousViewMatrix);
        AddMatrix(ref hash, draw.PreviousProjectionMatrix);
        AddMatrix(ref hash, draw.PreviousViewProjectionMatrix);
        AddMatrix(ref hash, draw.PreviousViewProjectionMatrixUnjittered);
        AddMatrix(ref hash, draw.RightEyeViewMatrix);
        AddMatrix(ref hash, draw.RightEyeInverseViewMatrix);
        AddMatrix(ref hash, draw.RightEyeProjectionMatrix);
        AddMatrix(ref hash, draw.RightEyeInverseProjectionMatrix);
        AddMatrix(ref hash, draw.RightEyeViewProjectionMatrix);
        AddMatrix(ref hash, draw.RightEyeViewProjectionMatrixUnjittered);
        AddMatrix(ref hash, draw.PreviousRightEyeViewMatrix);
        AddMatrix(ref hash, draw.PreviousRightEyeProjectionMatrix);
        AddMatrix(ref hash, draw.PreviousRightEyeViewProjectionMatrix);
        AddMatrix(
            ref hash,
            draw.PreviousRightEyeViewProjectionMatrixUnjittered);
        AddVector(ref hash, draw.CameraPosition);
        AddVector(ref hash, draw.CameraForward);
        AddVector(ref hash, draw.CameraUp);
        AddVector(ref hash, draw.CameraRight);
        hash.Add(draw.IsStereoPass);
        hash.Add(draw.UseUnjitteredProjection);
        hash.Add(cameraNearZ);
        hash.Add(cameraFarZ);
        hash.Add(cameraFovX);
        hash.Add(cameraFovY);
        hash.Add(cameraAspect);
        hash.Add((int)cameraDepthMode);
        return hash.ToHash();
    }

    private static ulong ComputePassGeneration(in PendingMeshDraw draw)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(draw.RenderAreaWidth);
        hash.Add(draw.RenderAreaHeight);
        hash.Add(draw.Viewport.X);
        hash.Add(draw.Viewport.Y);
        hash.Add(draw.Viewport.Width);
        hash.Add(draw.Viewport.Height);
        hash.Add(draw.Scissor.Offset.X);
        hash.Add(draw.Scissor.Offset.Y);
        hash.Add(draw.Scissor.Extent.Width);
        hash.Add(draw.Scissor.Extent.Height);
        hash.Add(unchecked((uint)draw.ShadowUniformState.GetHashCode()));
        return hash.ToHash();
    }

    private static ulong ComputeObjectGeneration(
        in PendingMeshDraw draw,
        uint skinPaletteBase,
        uint skinPaletteCount,
        int skinningInfluenceCap,
        int blendshapeActiveCount,
        float blendshapeWeightThreshold,
        bool hasValidPrecombinedBlendshapeDeltas)
    {
        FrameOpSignatureHasher hash = new();
        AddMatrix(ref hash, draw.ModelMatrix);
        AddMatrix(ref hash, draw.PreviousModelMatrix);
        hash.Add(draw.TransformId);
        hash.Add((uint)draw.BillboardMode);
        hash.Add(skinPaletteBase);
        hash.Add(skinPaletteCount);
        hash.Add(skinningInfluenceCap);
        hash.Add(blendshapeActiveCount);
        hash.Add(blendshapeWeightThreshold);
        hash.Add(hasValidPrecombinedBlendshapeDeltas);
        return hash.ToHash();
    }

    private static ulong ComputeInstanceGeneration(in PendingMeshDraw draw)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(draw.Instances);
        return hash.ToHash();
    }

    private static ulong ComputeRuntimeCallbackGeneration(
        ComputeDispatchSnapshot? snapshot)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(snapshot?.RuntimeUniformNameSignature ?? 0UL);
        hash.Add(snapshot?.RuntimeUniformValueSignature ?? 0UL);
        return hash.ToHash();
    }

    private static void AddMatrix(
        ref FrameOpSignatureHasher hash,
        in Matrix4x4 matrix)
    {
        hash.Add(matrix.M11);
        hash.Add(matrix.M12);
        hash.Add(matrix.M13);
        hash.Add(matrix.M14);
        hash.Add(matrix.M21);
        hash.Add(matrix.M22);
        hash.Add(matrix.M23);
        hash.Add(matrix.M24);
        hash.Add(matrix.M31);
        hash.Add(matrix.M32);
        hash.Add(matrix.M33);
        hash.Add(matrix.M34);
        hash.Add(matrix.M41);
        hash.Add(matrix.M42);
        hash.Add(matrix.M43);
        hash.Add(matrix.M44);
    }

    private static void AddVector(
        ref FrameOpSignatureHasher hash,
        in Vector3 vector)
    {
        hash.Add(vector.X);
        hash.Add(vector.Y);
        hash.Add(vector.Z);
    }
}
