namespace XREngine.Data.Runtime.AotParity;

/// <summary>
/// The runtime activities that constitute the player path. Editor import, inspector editing,
/// YAML editing, and cooking never enter it.
/// </summary>
public enum EAotParityPlayerPathKind
{
    /// <summary>Loading an asset from a published content archive.</summary>
    PublishedContentLoad = 0,
    /// <summary>World begin-play, component construction during play, and the play-mode update loop.</summary>
    PlayMode = 1,
    /// <summary>Loading a cooked world or scene snapshot.</summary>
    CookedSnapshotLoad = 2,
}
