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

        public void QueueAutoImport(AssetAuthoringTicket ticket, string sourcePath, string reason)
            => _assets.QueueThirdPartyAutoImport(ticket, sourcePath, reason);

        public void HandleSourceDeleted(AssetAuthoringTicket ticket, string sourcePath)
            => _assets.HandleThirdPartySourceDeleted(ticket, sourcePath);

        public void HandleSourceRenamed(AssetAuthoringTicket ticket, string oldSourcePath, string newSourcePath)
            => _assets.HandleThirdPartySourceRenamed(ticket, oldSourcePath, newSourcePath);
    }
}
