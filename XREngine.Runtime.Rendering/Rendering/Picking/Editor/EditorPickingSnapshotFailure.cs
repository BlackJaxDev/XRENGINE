namespace XREngine.Rendering;

/// <summary>Reports why a frozen editor picking source could not be retained.</summary>
public enum EditorPickingSnapshotFailure
{
    None = 0,
    InvalidPackageState,
    NoFreeSnapshot,
    LeaseCapacityExhausted,
    InvalidStableQueryKey,
    ConflictingOwner,
    RowCapacityExceeded,
    StaleLease,
}
