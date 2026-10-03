# In-session calibration values

`VrCalibrationSessionStore` keeps validated committed calibration values in memory independently of scene nodes and temporary VR rigs. It has no disk serialization and a new application/store starts empty.

Save only after a successful calibration transaction. An entry contains:

- Stable application player key and stable avatar asset identity, not a newly allocated rig-node ID
- Explicit provider, provider session generation, and reference-space version
- The selected body measurement mode/value and height-to-eye ratio when applicable
- Per-slot physical tracker identities and finite, invertible affine offsets

No source transform, scene node, runtime device index, or SteamVR role is retained. Invalid or duplicate entries do not overwrite the last committed entry. The store copies its input array.

On restoration, supply current usable source transforms correlated by exact physical identity within that provider. The store returns proposed `VrCalibrationTarget` values. The caller must publish the proposal through the same validated transaction boundary as fresh calibration. Missing or unusable devices leave their slots empty and return a reconnect/recalibrate notice; a different device never inherits a slot. Duplicate current identities are rejected rather than resolved by enumeration order.

Avatar, measurement, provider, generation, or reference-basis mismatch requires recalibration. Invalidate the player's entry on explicit avatar replacement/reset with `Remove`; do not silently relabel an entry for a new avatar or session.

## Current continuity limit

The strict restore contract supports temporary-rig recreation while the same provider generation and reference-space basis remain valid. Restarting OpenXR can change both. There is no inferred cross-generation transform or automatic rebasing API: a VR off/on cycle with unknown basis relationship must request recalibration even when the same tracker paths reappear. This helper is not proof that hardware VR toggles restore calibration. Such acceptance requires explicit rig wiring and a known, tested tracking-space relationship.

`VrCalibrationSessionStoreTests` cover exact-ID rebinding to a new rig, missing/replacement devices, scope and measurement rejection, invalid-save rollback, defensive copies, duplicate IDs, destroyed sources, and empty application sessions.
