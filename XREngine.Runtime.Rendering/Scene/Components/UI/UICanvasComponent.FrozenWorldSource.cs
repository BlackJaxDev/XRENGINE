using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    internal bool HasFrozenWorldShadowCasters
    {
        get
        {
            lock (_publicationGate)
            {
                UIPublication? publication = _currentPublication;
                if (!IsActive || publication is null || publication.AuthoringOffscreen ||
                    publication.AuthoringDrawSpace == ECanvasDrawSpace.Screen ||
                    publication.CanvasLifetime != (ulong)Volatile.Read(ref _canvasLifetime))
                    return false;

                ReadOnlySpan<UIPublishedWorldMeshCommand> commands = publication.WorldCommands;
                for (int index = 0; index < commands.Length; index++)
                    if (commands[index].CastsShadows && commands[index].Enabled)
                        return true;
                return false;
            }
        }
    }

    internal ERenderInfoCollectResult CollectFrozenWorldCommands(
        RenderCommandCollection passes,
        IRuntimeCullingCamera? camera,
        IVolume? collectionVolume,
        bool containsOnly)
    {
        if (!IsActive || AcquireCurrentPublication() is not { } publication)
            return ERenderInfoCollectResult.RejectedForEveryCamera;

        try
        {
            if (publication.AuthoringOffscreen ||
                publication.AuthoringDrawSpace == ECanvasDrawSpace.Screen ||
                publication.CanvasLifetime != (ulong)Volatile.Read(ref _canvasLifetime))
                return ERenderInfoCollectResult.RejectedForEveryCamera;

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
                passes.AddCPU(command, camera);
            }

            return ERenderInfoCollectResult.Collected;
        }
        finally { publication.Release(); }
    }
}
