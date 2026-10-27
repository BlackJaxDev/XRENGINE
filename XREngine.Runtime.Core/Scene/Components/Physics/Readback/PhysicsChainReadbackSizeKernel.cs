namespace XREngine.Components;

/// <summary>Calculates packed readback sizes from the selected field bits.</summary>
internal static class PhysicsChainReadbackSizeKernel
{
    /// <summary>Calculates element and byte counts without temporary field storage.</summary>
    [global::Engine.Diagnostics.HotPath]
    internal static bool TryCalculate(
        PhysicsChainReadbackFields fields,
        int selectedElementCount,
        out int elementCount,
        out int byteCount)
    {
        elementCount = 0;
        byteCount = 0;
        if (selectedElementCount <= 0)
            return false;

        int kindCount = 0;
        int bytesPerSelection = 0;
        if ((fields & PhysicsChainReadbackFields.Particles) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.ParticleByteCount;
        }
        if ((fields & PhysicsChainReadbackFields.Bones) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.AffineTransformByteCount;
        }
        if ((fields & PhysicsChainReadbackFields.Sockets) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.AffineTransformByteCount;
        }
        if ((fields & PhysicsChainReadbackFields.Bounds) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.BoundsByteCount;
        }
        if ((fields & PhysicsChainReadbackFields.CollisionEvents) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.CollisionEventByteCount;
        }
        if ((fields & PhysicsChainReadbackFields.FullTransformMirror) != 0)
        {
            ++kindCount;
            bytesPerSelection += PhysicsChainReadbackLayout.AffineTransformByteCount;
        }

        long totalElements = (long)kindCount * selectedElementCount;
        long totalBytes = (long)bytesPerSelection * selectedElementCount;
        if (totalElements == 0L || totalElements > int.MaxValue || totalBytes > int.MaxValue)
            return false;

        elementCount = (int)totalElements;
        byteCount = (int)totalBytes;
        return true;
    }
}
