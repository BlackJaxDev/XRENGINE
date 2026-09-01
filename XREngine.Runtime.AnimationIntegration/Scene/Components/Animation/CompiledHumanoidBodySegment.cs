namespace XREngine.Components.Animation;

/// <summary>Immutable role-indexed body-center contribution.</summary>
internal readonly struct CompiledHumanoidBodySegment
{
    public CompiledHumanoidBodySegment(
        CompiledHumanoidBodyPoint start,
        CompiledHumanoidBodyPoint end,
        float centerFraction,
        float massFraction)
    {
        Start = start;
        End = end;
        CenterFraction = centerFraction;
        MassFraction = massFraction;
    }

    public CompiledHumanoidBodyPoint Start { get; }
    public CompiledHumanoidBodyPoint End { get; }
    public float CenterFraction { get; }
    public float MassFraction { get; }
}
