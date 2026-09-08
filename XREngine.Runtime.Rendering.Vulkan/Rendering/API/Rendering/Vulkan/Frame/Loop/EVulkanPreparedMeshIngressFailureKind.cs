namespace XREngine.Rendering.Vulkan;

/// <summary>Exact stage that rejected prepared mesh ingress stable-bin construction.</summary>
internal enum EVulkanPreparedMeshIngressFailureKind : byte
{
    None = 0,
    PackageExceptionUnmatchedHandle = 1,
    PackageExceptionAppendFailed = 2,
    StreamExceptionAppendFailed = 3,
    NonOpaqueComputedOrdering = 4,
    StableRecordAppendFailed = 5,
}