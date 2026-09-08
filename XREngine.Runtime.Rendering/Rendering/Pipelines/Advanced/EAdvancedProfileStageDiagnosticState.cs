namespace XREngine.Rendering;

/// <summary>Latest directly observed authoring state for one Advanced stage phase.</summary>
public enum EAdvancedProfileStageDiagnosticState
{
    NotObserved,
    CommandScopeReached,
    BackendEnqueueAccepted,
    RejectedPrerequisite,
    RejectedAdmission,
    RejectedCapability,
    BackendEnqueueRejected,
}
