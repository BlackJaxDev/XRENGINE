using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    internal bool TryAcquireFrozenWorldCollectPacket(IRuntimeWorldContext expectedWorld,
        out UIPublication? publication)
    {
        lock (_publicationGate)
        {
            publication = null;
            if (!ReferenceEquals(SceneNode?.World, expectedWorld))
                return false;
            if (!IsActive || WorldCollectionUnavailable ||
                CanvasDrawSpaceOrDefault == ECanvasDrawSpace.Screen ||
                UseOffscreenRenderingForNonScreenSpaces())
                return true;
            UIPublication? selected = _currentPublication;
            if (selected is null || selected.AuthoringOffscreen ||
                selected.AuthoringDrawSpace == ECanvasDrawSpace.Screen ||
                selected.CanvasLifetime != (ulong)Volatile.Read(ref _canvasLifetime))
                return false;
            selected.Retain();
            publication = selected;
            return true;
        }
    }

    internal bool HasFrozenWorldShadowCasters(UIWorldCollectPacketSet? packetSet)
    {
        if (packetSet is null || !packetSet.TryGet(this, out UIPublication? publication) ||
            publication is null)
            return false;
        ReadOnlySpan<UIPublishedWorldMeshCommand> commands = publication.WorldCommands;
        for (int index = 0; index < commands.Length; index++)
            if (commands[index].CastsShadows && commands[index].Enabled)
                return true;
        return false;
    }

    internal ERenderInfoCollectResult CollectFrozenWorldCommands(
        RenderCommandCollection passes,
        IRuntimeCullingCamera? camera,
        IVolume? collectionVolume,
        bool containsOnly, UIWorldCollectPacketSet? packetSet)
    {
        if (packetSet is null || !packetSet.TryGet(this, out UIPublication? publication) ||
            publication is null)
            return ERenderInfoCollectResult.RejectedForEveryCamera;
        try
        {
            ReadOnlySpan<UIPublishedWorldMeshCommand> commands = publication.WorldCommands;
            for (int index = 0; index < commands.Length; index++)
            {
                UIPublishedWorldMeshCommand command = commands[index];
                if (!command.Enabled || passes.IsShadowPass && !command.CastsShadows ||
                    camera is not null && !camera.RendersLayer(command.Layer))
                    continue;

                if (passes.IsShadowPass &&
                    (command.IndexedDrawIntents.IsEmpty ||
                     command.IndexedDrawIntents[0].Topology != EPrimitiveType.Triangles))
                    continue;

                if (command.FrozenCullingVolume is Box bounds &&
                    !RenderInfo3D.FrozenWorldVolumeIntersects(collectionVolume, bounds, containsOnly))
                    continue;

                command.CollectedForRender(camera);
                if (!passes.TryAddCPU(command, camera))
                {
                    packetSet.MarkCollectionFailed();
                    return ERenderInfoCollectResult.CallbackRejected;
                }
            }

            return ERenderInfoCollectResult.Collected;
        }
        catch
        {
            packetSet.MarkCollectionFailed();
            throw;
        }
    }
}
