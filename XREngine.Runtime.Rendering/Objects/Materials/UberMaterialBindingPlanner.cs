namespace XREngine.Rendering;

/// <summary>
/// Selects the first faithful rung in the documented uber binding ladder.
/// </summary>
public static class UberMaterialBindingPlanner
{
    public static UberMaterialBindingPlan Plan(
        int samplerCount,
        int sampledImageCount,
        int uniformBytes,
        UberMaterialBindingLimits limits,
        bool textureArraysCompatible,
        bool materialTextureTableAvailable,
        bool bindlessDescriptorsAvailable)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegative(samplerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(sampledImageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(uniformBytes);

        if (uniformBytes > limits.MaxUniformBytes)
        {
            return Unsupported(
                samplerCount,
                sampledImageCount,
                uniformBytes,
                $"Uber material requires {uniformBytes} uniform bytes; {limits.BackendName} permits {limits.MaxUniformBytes}.");
        }

        if (samplerCount <= limits.MaxFragmentSamplers && sampledImageCount <= limits.MaxSampledImages)
            return Supported(EUberMaterialBindingRung.DirectSamplers, samplerCount, sampledImageCount, uniformBytes);
        if (textureArraysCompatible)
            return Supported(EUberMaterialBindingRung.CompatibleTextureArrays, samplerCount, sampledImageCount, uniformBytes);
        if (materialTextureTableAvailable)
            return Supported(EUberMaterialBindingRung.MaterialTextureTable, samplerCount, sampledImageCount, uniformBytes);
        if (bindlessDescriptorsAvailable)
            return Supported(EUberMaterialBindingRung.BindlessDescriptors, samplerCount, sampledImageCount, uniformBytes);

        return Unsupported(
            samplerCount,
            sampledImageCount,
            uniformBytes,
            $"Uber material requires {samplerCount} fragment samplers and {sampledImageCount} sampled images, exceeding " +
            $"{limits.BackendName} direct limits ({limits.MaxFragmentSamplers}/{limits.MaxSampledImages}); textures are not " +
            "array-compatible and neither a material texture table nor bindless descriptors are available.");
    }

    private static UberMaterialBindingPlan Supported(
        EUberMaterialBindingRung rung,
        int samplerCount,
        int sampledImageCount,
        int uniformBytes)
        => new()
        {
            Rung = rung,
            SamplerCount = samplerCount,
            SampledImageCount = sampledImageCount,
            UniformBytes = uniformBytes,
        };

    private static UberMaterialBindingPlan Unsupported(
        int samplerCount,
        int sampledImageCount,
        int uniformBytes,
        string reason)
        => new()
        {
            Rung = EUberMaterialBindingRung.Unsupported,
            SamplerCount = samplerCount,
            SampledImageCount = sampledImageCount,
            UniformBytes = uniformBytes,
            FailureReason = reason,
        };
}
