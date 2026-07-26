namespace XREngine.Rendering.Compute;

/// <summary>
/// Version-derived writes required for one stable physics-chain arena slice.
/// </summary>
public readonly record struct GPUPhysicsChainUploadPlan(
    bool UploadParticleState,
    bool UploadStaticTemplate,
    bool UploadTransformInputs,
    bool UploadColliderData)
{
    public bool HasStaticUpload => UploadStaticTemplate;

    public static GPUPhysicsChainUploadPlan Create(
        bool allocationChanged,
        int uploadedParticleStateVersion,
        int particleStateVersion,
        int uploadedStaticVersion,
        int staticVersion,
        int uploadedTransformVersion,
        int transformVersion,
        int uploadedColliderVersion,
        int colliderVersion)
        => new(
            allocationChanged || uploadedParticleStateVersion != particleStateVersion,
            allocationChanged || uploadedStaticVersion != staticVersion,
            allocationChanged || uploadedTransformVersion != transformVersion,
            allocationChanged || uploadedColliderVersion != colliderVersion);
}
