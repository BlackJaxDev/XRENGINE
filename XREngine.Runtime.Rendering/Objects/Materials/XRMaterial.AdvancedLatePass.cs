namespace XREngine.Rendering;

public partial class XRMaterial
{
    private AdvancedLatePassMetadata? _advancedLatePassMetadata;

    /// <summary>
    /// Explicit late-lane behavior consumed by the Advanced pipeline. Scene
    /// color dependencies must be authored here rather than inferred from
    /// shader names, parameters, or texture slots.
    /// </summary>
    public AdvancedLatePassMetadata? AdvancedLatePassMetadata
    {
        get => _advancedLatePassMetadata;
        set => SetField(ref _advancedLatePassMetadata, value);
    }

    public bool TryGetAdvancedLateTemporalMaterial(EAdvancedLateTemporalOutput output, out XRMaterial? material)
    {
        AdvancedLatePassMetadata? metadata = AdvancedLatePassMetadata;
        material = output switch
        {
            EAdvancedLateTemporalOutput.Velocity => metadata?.TemporalVelocityMaterial,
            EAdvancedLateTemporalOutput.ReactiveMask => metadata?.TemporalReactiveMaskMaterial,
            _ => null,
        };
        return TryValidateAdvancedLateTemporalSource(out _) && material is not null;
    }

    /// <summary>Checks that authored temporal coverage still matches the current source material.</summary>
    public bool TryValidateAdvancedLateTemporalSource(out string? reason)
    {
        AdvancedLatePassMetadata? metadata = AdvancedLatePassMetadata;
        if (metadata is not
               {
                   ParticipatesInMotionVectors: true,
                   UnsupportedReason: null,
                   TemporalUnsupportedReason: null,
               })
        {
            reason = metadata?.TemporalUnsupportedReason ?? metadata?.UnsupportedReason ?? "The material does not admit Advanced temporal replay.";
            return false;
        }
        if (metadata.TemporalSourceShaderRevision is long revision && revision != ShaderStateRevision)
        {
            reason = "The source shader changed after temporal variants were created; recreate coverage-matching variants.";
            return false;
        }
        if (metadata.TemporalCoverageParameter is { } coverage)
        {
            for (int i = 0; i < Parameters.Length; ++i)
                if (ReferenceEquals(Parameters[i], coverage))
                {
                    reason = null;
                    return true;
                }
            reason = "The shared temporal coverage parameter was replaced; recreate the temporal variants.";
            return false;
        }
        reason = null;
        return true;
    }

    public override void OnSettingUniforms(XRRenderProgram program)
    {
        base.OnSettingUniforms(program);
        AdvancedSceneColorContract.ApplyMaterialBinding(this, program);
    }
}
