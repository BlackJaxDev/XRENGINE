namespace XREngine.Rendering.Compute;

/// <summary>
/// Keeps the material bounds contracts of a few published material states for one preparation
/// pass. Covered draws that share a material then evaluate its contract once, not once for each draw.
/// </summary>
/// <remarks>
/// The key is the full published snapshot: the material reference and its five state versions.
/// A hit still requires the material to be current. Call <see cref="Clear"/> once for each pass,
/// so that a shader change that does not advance a material version is seen on the next pass.
/// The owner thread is the only user.
/// </remarks>
internal sealed class PhysicsChainMaterialBoundsContractMemo
{
    private const int Capacity = 4;

    private readonly PhysicsChainDrawMaterialSnapshot[] _snapshots = new PhysicsChainDrawMaterialSnapshot[Capacity];
    private readonly PhysicsChainMaterialBoundsContract[] _contracts = new PhysicsChainMaterialBoundsContract[Capacity];
    private int _count;
    private int _next;

    /// <summary>The number of contracts evaluated since the last <see cref="Clear"/>.</summary>
    public int EvaluationCount { get; private set; }

    /// <summary>Removes every entry and its material reference.</summary>
    public void Clear()
    {
        Array.Clear(_snapshots);
        Array.Clear(_contracts);
        _count = 0;
        _next = 0;
        EvaluationCount = 0;
    }

    /// <summary>
    /// Gives the same result as
    /// <see cref="PhysicsChainMaterialBoundsCertificate.Covers(float, bool, in PhysicsChainDrawMaterialSnapshot)"/>,
    /// and evaluates each published material state at most once until the next <see cref="Clear"/>.
    /// </summary>
    public bool Covers(float certifiedPadding, bool producerSupported,
        in PhysicsChainDrawMaterialSnapshot material)
    {
        if (!PhysicsChainMaterialBoundsCertificate.AcceptsPadding(certifiedPadding, producerSupported) ||
            !material.IsCurrent)
            return false;

        for (int index = 0; index < _count; ++index)
        {
            if (Same(in _snapshots[index], in material))
                return PhysicsChainMaterialBoundsCertificate.Covers(certifiedPadding, producerSupported,
                    in _contracts[index]);
        }

        PhysicsChainMaterialBoundsContract contract = PhysicsChainMaterialBoundsEvaluator.Evaluate(material.Material);
        ++EvaluationCount;
        // A material that changed during the evaluation fails the check and is not kept.
        if (!material.IsCurrent)
            return false;

        int slot = _count < Capacity ? _count++ : _next;
        _next = (slot + 1) % Capacity;
        _snapshots[slot] = material;
        _contracts[slot] = contract;
        return PhysicsChainMaterialBoundsCertificate.Covers(certifiedPadding, producerSupported, in contract);
    }

    private static bool Same(in PhysicsChainDrawMaterialSnapshot left, in PhysicsChainDrawMaterialSnapshot right)
        => ReferenceEquals(left.Material, right.Material) &&
            left.BindingLayoutVersion == right.BindingLayoutVersion &&
            left.BindingValueVersion == right.BindingValueVersion &&
            left.BindingResourceVersion == right.BindingResourceVersion &&
            left.ShaderStateRevision == right.ShaderStateRevision &&
            left.UberStateRevision == right.UberStateRevision;
}
