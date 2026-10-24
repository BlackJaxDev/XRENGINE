namespace XREngine.Editor;

/// <summary>Identifies the continuous owner of one clone construction receipt.</summary>
internal enum EHierarchyCloneCustodyState
{
    Reserved,
    Detached,
    Applied,
    Undoing,
    Redoing,
    Document,
    Retiring,
    RecoveryRequired,
    Released
}
