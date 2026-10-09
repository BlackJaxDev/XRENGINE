namespace XREngine.Components;

/// <summary>Identifies why GPU inputs need the hierarchy compatibility route.</summary>
public enum PhysicsChainGpuRestInputCompatibilityReason : byte
{
    None,
    InvalidRuntimeHandle,
    CpuBoneMirror,
    CustomTransform,
    ChangedParent,
    SharedResetNode,
    ExternalParentDependency,
    AuthoredRestResetRequired,
    OpaqueTransformDependency,
    ManualHierarchyRefresh,
    HierarchyInputDependency,
    PrerequisiteMatrixRefresh,
    ColliderDependency,
}
