namespace XREngine;

public readonly record struct RvcVisibilitySourceRecord(
    uint InstanceId,
    uint DrawOrMeshletId,
    uint PrimitiveId,
    uint MaterialRowId,
    uint TransformId,
    uint EditorSelectionId,
    uint DeformationVersion,
    uint MaterialResourceGeneration);
