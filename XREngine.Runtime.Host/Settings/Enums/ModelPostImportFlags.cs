namespace XREngine.Runtime.Bootstrap;

[Flags]
public enum ModelPostImportFlags
{
    None = 0,
    GenerateCoacdCollidersPerSubmesh = 1 << 0,
    SplitSubmeshesIntoSeparateModelComponents = 1 << 1,
    SeparateMeshIslands = 1 << 2,
    GenerateIndividualSceneNodesPerSubmesh = 1 << 3,
    PutAllCoacdCollidersIntoOneStaticRigidBodyComponent = 1 << 4,
    SpatiallyPartitionMeshesForOcclusion = 1 << 5,
}
