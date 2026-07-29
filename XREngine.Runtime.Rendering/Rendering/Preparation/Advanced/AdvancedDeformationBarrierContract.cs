using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral deformation barriers lowered by OpenGL and Vulkan after the
/// bounded aggregate dispatches.
/// </summary>
public static class AdvancedDeformationBarrierContract
{
    private static readonly RenderGraphSyncState Source =
        new(
            RenderGraphStageMask.ComputeShader,
            RenderGraphAccessMask.ShaderWrite,
            Layout: null);

    public static AdvancedPreparationBarrier Get(
        EAdvancedPreparationConsumer consumer)
        => consumer switch
        {
            EAdvancedPreparationConsumer.Visibility or
            EAdvancedPreparationConsumer.Depth or
            EAdvancedPreparationConsumer.Velocity or
            EAdvancedPreparationConsumer.DirectionalShadow or
            EAdvancedPreparationConsumer.PointShadow or
            EAdvancedPreparationConsumer.SpotShadow or
            EAdvancedPreparationConsumer.Probe or
            EAdvancedPreparationConsumer.Capture
                => GraphicsBarrier(consumer),
            EAdvancedPreparationConsumer.MaterialReconstruction
                => new AdvancedPreparationBarrier(
                    consumer,
                    Source,
                    new RenderGraphSyncState(
                        RenderGraphStageMask.ComputeShader,
                        RenderGraphAccessMask.ShaderRead,
                        Layout: null),
                    EAdvancedOpenGlMemoryBarrier.ShaderStorage),
            _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
        };

    public static bool TryWriteRequired(
        EAdvancedPreparationConsumer consumers,
        Span<AdvancedPreparationBarrier> destination,
        out int count)
    {
        count = 0;
        for (uint bit = 0u; bit < 9u; bit++)
        {
            EAdvancedPreparationConsumer consumer =
                (EAdvancedPreparationConsumer)(1u << (int)bit);
            if ((consumers & consumer) == 0)
                continue;
            if (count >= destination.Length)
                return false;
            destination[count++] = Get(consumer);
        }

        return true;
    }

    private static AdvancedPreparationBarrier GraphicsBarrier(
        EAdvancedPreparationConsumer consumer)
        => new(
            consumer,
            Source,
            new RenderGraphSyncState(
                RenderGraphStageMask.DrawIndirect |
                RenderGraphStageMask.VertexInput |
                RenderGraphStageMask.VertexShader,
                RenderGraphAccessMask.IndirectCommandRead |
                RenderGraphAccessMask.VertexAttributeRead |
                RenderGraphAccessMask.ShaderRead,
                Layout: null),
            EAdvancedOpenGlMemoryBarrier.Command |
            EAdvancedOpenGlMemoryBarrier.VertexAttributeArray |
            EAdvancedOpenGlMemoryBarrier.ShaderStorage);
}
