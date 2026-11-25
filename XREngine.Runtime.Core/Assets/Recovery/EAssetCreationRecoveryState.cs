namespace XREngine.Assets.Recovery;

/// <summary>States of one journaled asset creation. The journal stores the state names.</summary>
public enum EAssetCreationRecoveryState
{
    /// <summary>The operation directory is being written. The public paths are not touched yet.</summary>
    Preparing,
    /// <summary>The manifest, immutable originals, and working copies are written and verified.</summary>
    Prepared,
    /// <summary>The working copies are being moved to their public paths.</summary>
    PublishPending,
    /// <summary>All public files exist with the recorded bytes.</summary>
    Published,
    /// <summary>The public files are being moved back to the working copies.</summary>
    RetainPending,
    /// <summary>The public files were moved back to the working copies. The public paths are free.</summary>
    Retained,
    /// <summary>Reserved for an explicit restore of the immutable originals.</summary>
    RestorePending,
    /// <summary>The recorded files are ambiguous. The operation keeps its files until explicit recovery.</summary>
    RecoveryRequired,
    /// <summary>The Undo receipt is invalid. The operation keeps its files until explicit cleanup.</summary>
    Released,
}
