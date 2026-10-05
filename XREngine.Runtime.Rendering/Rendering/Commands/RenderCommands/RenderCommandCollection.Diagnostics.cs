using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    /// <summary>
    /// Copies pass metadata and published command diagnostics while the rendering
    /// buffer is stable. The returned rows contain no borrowed command collections.
    /// </summary>
    public RenderCommandPassDiagnostic[] CaptureRenderingPassDiagnostics()
    {
        using var readScope = EnterRenderingBufferReadScope();
        RenderCommandPassDiagnostic[] rows = new RenderCommandPassDiagnostic[_passMetadata.Count];
        int rowIndex = 0;
        foreach (KeyValuePair<int, RenderPassMetadata> pair in _passMetadata)
        {
            int commandCount = 0;
            int meshCommandCount = 0;
            int enabledCommandCount = 0;
            int enabledMeshCommandCount = 0;
            string? firstCommandType = null;
            string? firstMeshName = null;
            string? firstMaterialName = null;

            if (TryGetPublishedPassNoLock(pair.Key, out BackendReadyRenderPass pass))
            {
                commandCount = pass.Commands.Count;
                foreach (RenderCommand command in pass.Commands)
                {
                    if (command is null)
                        continue;

                    firstCommandType ??= command.GetType().Name;
                    bool enabled = command.RenderEnabled;
                    if (enabled)
                        enabledCommandCount++;

                    if (command is IRenderCommandMesh meshCommand)
                    {
                        meshCommandCount++;
                        if (enabled)
                            enabledMeshCommandCount++;

                        firstMeshName ??= meshCommand.Mesh?.Mesh?.Name;
                        firstMaterialName ??= (meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material)?.Name;
                    }
                }
            }

            rows[rowIndex++] = new RenderCommandPassDiagnostic(
                pair.Key,
                pair.Value.Name,
                pair.Value.Stage,
                commandCount,
                meshCommandCount,
                enabledCommandCount,
                enabledMeshCommandCount,
                firstCommandType,
                firstMeshName,
                firstMaterialName);
        }

        return rows;
    }
}
