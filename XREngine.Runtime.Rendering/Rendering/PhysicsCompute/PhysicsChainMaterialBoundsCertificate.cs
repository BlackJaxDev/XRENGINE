namespace XREngine.Rendering.Compute;

/// <summary>Checks a drawn material against the padding in one committed bounds slot.</summary>
public static class PhysicsChainMaterialBoundsCertificate
{
    /// <summary>True when the slot contains the material's post-skinning displacement.</summary>
    public static bool Covers(float certifiedPadding, bool producerSupported, XRMaterial? material)
    {
        if (!producerSupported || !float.IsFinite(certifiedPadding) || certifiedPadding < 0.0f)
            return false;

        PhysicsChainMaterialBoundsContract current = PhysicsChainMaterialBoundsEvaluator.Evaluate(material);
        return current.IsSupported && float.IsFinite(current.Padding) &&
            current.Padding <= certifiedPadding;
    }

    /// <summary>Checks the material state sealed with a draw publication.</summary>
    public static bool Covers(float certifiedPadding, bool producerSupported,
        in PhysicsChainDrawMaterialSnapshot material)
        => material.IsCurrent && Covers(certifiedPadding, producerSupported, material.Material) &&
            material.IsCurrent;
}
