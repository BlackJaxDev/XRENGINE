namespace XREngine.Core.Files;

/// <summary>Physical path checks and root replacement for guarded authoring operations.</summary>
public interface IAssetAuthoringPathBackend : IAssetMetadataFileBackend
{
    /// <summary>Returns true when an existing path component is a link or reparse point.</summary>
    bool HasLinkedComponent(string path);

    /// <summary>Replaces a generated root with its staged sibling.</summary>
    void ReplaceFile(string stagedPath, string destinationPath);
}
