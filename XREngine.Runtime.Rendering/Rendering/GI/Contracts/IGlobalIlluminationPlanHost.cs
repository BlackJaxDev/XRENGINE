using XREngine;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Exposes the serialized GI selection and its resolved immutable plan to
/// settings, reusable commands, and provider modules. It deliberately carries
/// no algorithm flags, pass factories, or algorithm resource names.
/// </summary>
public interface IGlobalIlluminationPlanHost
{
    EGlobalIlluminationMode GlobalIlluminationMode { get; set; }
    GlobalIlluminationPlan GlobalIlluminationPlan { get; }
}
