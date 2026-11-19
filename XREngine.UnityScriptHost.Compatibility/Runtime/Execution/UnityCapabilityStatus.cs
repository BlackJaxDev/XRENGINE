namespace XREngine.UnityScriptHost.Execution;

/// <summary>
/// Classifies one exact compatibility member identifier.
/// </summary>
public enum UnityCapabilityStatus
{
    /// <summary>The member is not supported.</summary>
    Unsupported,
    /// <summary>The member has executable behavior.</summary>
    Implemented,
    /// <summary>The member can compile but cannot execute.</summary>
    CompileOnly
}
