using System.Runtime.InteropServices;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Resource-specific deformation-write transition for one consumer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedPreparationBarrier(
    EAdvancedPreparationConsumer Consumer,
    RenderGraphSyncState Source,
    RenderGraphSyncState Destination,
    EAdvancedOpenGlMemoryBarrier OpenGlMask);
