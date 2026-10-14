namespace XREngine.Rendering.Compute;

/// <summary>Checks a drawn material against the padding in one committed bounds slot.</summary>
public static class PhysicsChainMaterialBoundsCertificate
{
    /// <summary>True when the slot contains the material's post-skinning displacement.</summary>
    public static bool Covers(float certifiedPadding, bool producerSupported, XRMaterial? material)
    {
        if (!AcceptsPadding(certifiedPadding, producerSupported))
            return false;

        PhysicsChainMaterialBoundsContract current = PhysicsChainMaterialBoundsEvaluator.Evaluate(material);
        return Covers(certifiedPadding, producerSupported, in current);
    }

    /// <summary>True when the slot padding contains the displacement of an evaluated contract.</summary>
    public static bool Covers(float certifiedPadding, bool producerSupported,
        in PhysicsChainMaterialBoundsContract contract)
        => AcceptsPadding(certifiedPadding, producerSupported) &&
            contract.IsSupported && float.IsFinite(contract.Padding) &&
            contract.Padding <= certifiedPadding;

    /// <summary>True when the producer supports the slot and its padding is a finite, non-negative value.</summary>
    public static bool AcceptsPadding(float certifiedPadding, bool producerSupported)
        => producerSupported && float.IsFinite(certifiedPadding) && certifiedPadding >= 0.0f;

    /// <summary>Checks the material state sealed with a draw publication.</summary>
    public static bool Covers(float certifiedPadding, bool producerSupported,
        in PhysicsChainDrawMaterialSnapshot material)
        => material.IsCurrent && Covers(certifiedPadding, producerSupported, material.Material) &&
            material.IsCurrent;
}
