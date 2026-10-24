using System.Threading;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    private readonly object _directWorldChildrenGate = new();
    private readonly HashSet<RenderCommandMesh3D> _directWorldChildren =
        new(ReferenceEqualityComparer.Instance);
    private long _directWorldChildrenRevision = 1;

    internal long DirectWorldChildrenRevision =>
        Volatile.Read(ref _directWorldChildrenRevision);

    internal void RegisterDirectWorldChild(RenderCommandMesh3D command)
    {
        lock (_directWorldChildrenGate)
            if (_directWorldChildren.Add(command))
                Interlocked.Increment(ref _directWorldChildrenRevision);
    }

    internal void UnregisterDirectWorldChild(RenderCommandMesh3D command)
    {
        lock (_directWorldChildrenGate)
            if (_directWorldChildren.Remove(command))
                Interlocked.Increment(ref _directWorldChildrenRevision);
    }

    internal void NotifyDirectWorldChildChanged()
        => Interlocked.Increment(ref _directWorldChildrenRevision);

    /// <summary>Checks every live direct child against one frozen draw set.</summary>
    internal bool MatchesDirectWorldChildren(UIPublication publication,
        IRuntimeWorldContext world, long expectedRevision)
    {
        lock (_directWorldChildrenGate)
        {
            if (expectedRevision == 0 ||
                expectedRevision != _directWorldChildrenRevision ||
                publication.WorldCollectionRevision != expectedRevision ||
                !_enableRetainedWorldRendering || !IsActiveInHierarchy ||
                WorldCollectionUnavailable ||
                CanvasDrawSpaceOrDefault == ECanvasDrawSpace.Screen ||
                UseOffscreenRenderingForNonScreenSpaces() ||
                publication.AuthoringOffscreen ||
                publication.AuthoringDrawSpace != CanvasDrawSpaceOrDefault ||
                publication.AuthoringCanvasWorldMatrix != CanvasTransform.WorldMatrix ||
                publication.HasVisibleBackdrop || publication.HasVisiblePreRenderDraw ||
                !ReferenceEquals(SceneNode?.World, world) ||
                RuntimeRenderWorldRegistry.Get(world) is not RuntimeWorldRenderer owner)
                return false;
            var scene = owner.VisualScene;
            ReadOnlySpan<UIPublishedWorldMeshCommand> frozen = publication.WorldCommands;
            if (frozen.Length == 0 || frozen.Length != _directWorldChildren.Count)
                return false;
            for (int index = 0; index < frozen.Length; index++)
            {
                UIPublishedWorldMeshCommand command = frozen[index];
                RenderCommandMesh3D? source = command.SourceWorldCommand;
                if (source is null || !_directWorldChildren.Contains(source) ||
                    command.SourceWorldRenderInfo is not { } info ||
                    info.WorldRegistrationIncarnation != command.WorldRegistrationIncarnation ||
                    command.WorldRegistrationIncarnation == 0 ||
                    !info.IsRegisteredInWorld(world) ||
                    !scene.ContainsRegisteredDrawable(info) ||
                    info.HasCommittedWorldBoundsProvider ||
                    command.WorldCrop is not null ||
                    !info.IsVisible || !info.SupportsCanonicalGpuCollection ||
                    info.Owner is not UIRenderableComponent renderable ||
                    !renderable.HasCanonicalWorldPreCollect(info) ||
                    !info.HasDefaultCommandCallbacks(source, scene.RenderableSwapHandler) ||
                    !ReferenceEquals(info.UserInterfaceCanvas, this) ||
                    info.CullingIntersectionOverride is not null ||
                    info.RenderCullingVolumeDebugOverride is not null ||
                    info.RenderCommands.Count != 1 ||
                    !ReferenceEquals(info.RenderCommands[0], source) ||
                    !source.Enabled || source.Instances == 0 ||
                    !command.Enabled || command.Instances == 0 ||
                    command.CastsShadows || info.CastsShadows ||
                    command.RenderPass != source.RenderPass ||
                    command.Layer != info.Layer ||
                    !Equals(command.FrozenCullingVolume, info.CaptureWorldCullingVolume()))
                    return false;
                for (int prior = 0; prior < index; prior++)
                    if (ReferenceEquals(frozen[prior].SourceWorldCommand, source))
                        return false;
            }
            return expectedRevision == _directWorldChildrenRevision;
        }
    }
}
