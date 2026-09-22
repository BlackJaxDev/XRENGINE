namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Immutable module input. Provider implementations receive only this neutral host/plan view.
/// </summary>
public readonly record struct GlobalIlluminationModuleContext(
    IGlobalIlluminationHostAdapter Host,
    GlobalIlluminationPlan Plan,
    EGlobalIlluminationExecutionAnchor Anchor,
    GlobalIlluminationHostResources? Resources = null);
