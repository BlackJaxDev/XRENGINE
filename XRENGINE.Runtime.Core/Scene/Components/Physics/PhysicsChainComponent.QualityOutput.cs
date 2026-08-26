using System.Numerics;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    /// <summary>
    /// Current presentation alpha between the two most recently simulated
    /// outputs. Reading it never advances simulation time.
    /// </summary>
    public float CpuRenderInterpolationAlpha => ComputeRenderAlpha();

    /// <summary>Copies an interpolated CPU palette without performing a simulation step.</summary>
    public bool TryCopyCpuInterpolatedPalette(Span<Matrix4x4> destination)
        => _cpuBackend is not null
            && _cpuBackend.TryCopyInterpolatedPalette(
                _cpuBackendHandle,
                destination,
                CpuRenderInterpolationAlpha);

    private void ApplyCpuQualityPolicy()
    {
        if (_cpuBackend is null || !_cpuBackendHandle.IsValid)
            return;
        PhysicsChainCpuOutputPolicy outputPolicy = PhysicsChainCpuOutputPolicy.FromQuality(EffectiveQualityPolicy);
        _cpuBackend.TryUpdateOutputPolicy(_cpuBackendHandle, outputPolicy);
    }
}
