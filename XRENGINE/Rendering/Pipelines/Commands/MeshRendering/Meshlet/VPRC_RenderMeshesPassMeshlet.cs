namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassMeshlet
{
    private static bool _warned;

    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        if (!_warned)
        {
            _warned = true;
            XREngine.Debug.LogWarning("Meshlet mesh rendering path is not implemented yet; falling back to Traditional path.");
        }

        VPRC_RenderMeshesPassTraditional.Execute(command);
    }
}
