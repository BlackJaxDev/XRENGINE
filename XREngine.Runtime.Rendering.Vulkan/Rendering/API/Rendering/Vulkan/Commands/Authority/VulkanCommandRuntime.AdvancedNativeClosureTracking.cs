using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Pins every raw native resource consumed by Final after queue-overlap
    /// recording begins. Exact generations reject ABA replacement before the
    /// indirect shade dispatch can be admitted.
    /// </summary>
    private void TrackAdvancedNativeShadeClosure(
        CommandBuffer commandBuffer,
        in VulkanAdvancedNativeComputeClosure closure)
    {
        TrackAdvancedNativeImageClosure(commandBuffer, closure.IdentityResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.MetadataResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.DepthResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.HdrResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.VelocityResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.ReactiveResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.ShadingDiagnosticsResource);
        TrackAdvancedNativeImageClosure(commandBuffer, closure.AmbientOcclusionResource);
        TrackCommandBufferResource(commandBuffer, new(ObjectType.Sampler, closure.Sampler.Handle),
            "AdvancedNativeShade.Sampler", closure.SamplerGeneration);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.ActiveTiles);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.KernelTiles);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.ClassificationCounters);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.DispatchArguments);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.KernelCounts);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.FroxelGrid);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.LightIndices);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.LightingCounters);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.FroxelDecalGrid);
        TrackAdvancedNativeBufferClosure(commandBuffer, closure.DecalIndices);
    }

    private void TrackAdvancedNativeImageClosure(CommandBuffer commandBuffer,
        in VulkanAdvancedNativeImageClosure resource)
    {
        TrackCommandBufferResource(commandBuffer, new(ObjectType.Image, resource.Image.Handle),
            "AdvancedNativeShade.Image", resource.ImageGeneration);
        TrackCommandBufferResource(commandBuffer, new(ObjectType.ImageView, resource.View.Handle),
            "AdvancedNativeShade.ImageView", resource.ViewGeneration);
    }

    private void TrackAdvancedNativeBufferClosure(CommandBuffer commandBuffer,
        in VulkanFrozenBufferBarrier buffer)
        => TrackCommandBufferResource(commandBuffer, new(ObjectType.Buffer, buffer.NativeBuffer.Handle),
            "AdvancedNativeShade.Buffer", buffer.NativeGeneration);
}
