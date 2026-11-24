namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Identifies how the engine uses a translated pass.
/// </summary>
public enum EnginePassKind
{
    /// <summary>A forward color pass with no light data (<c>LightMode</c> absent or <c>Always</c>).</summary>
    Unlit,

    /// <summary>The forward base pass: main directional light and ambient.</summary>
    ForwardBase,

    /// <summary>The forward additive pass for one more light.</summary>
    ForwardAdd,

    /// <summary>The depth pass for shadow maps.</summary>
    ShadowCaster,
}
