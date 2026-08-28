namespace XREngine.Editor.Importers;

/// <summary>Installs editor-owned third-party import reactions for Runtime.Core asset watchers.</summary>
public static class EditorThirdPartyAssetWatcher
{
    public static IDisposable Install(AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return RuntimeAssetAuthoringServices.Install(new EditorAssetAuthoringServices(assets));
    }

    private sealed class EditorAssetAuthoringServices(AssetManager assets) : IRuntimeAssetAuthoringServices
    {
        private readonly AssetManager _assets = assets;

        public void QueueAutoImport(string sourcePath, string reason)
            => _assets.QueueThirdPartyAutoImport(sourcePath, reason);

        public void HandleSourceDeleted(string sourcePath)
            => _assets.HandleThirdPartySourceDeleted(sourcePath);

        public void HandleSourceRenamed(string oldSourcePath, string newSourcePath)
            => _assets.HandleThirdPartySourceRenamed(oldSourcePath, newSourcePath);
    }
}
