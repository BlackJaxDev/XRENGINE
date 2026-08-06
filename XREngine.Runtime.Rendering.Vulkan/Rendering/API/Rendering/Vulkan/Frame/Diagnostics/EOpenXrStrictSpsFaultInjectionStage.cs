namespace XREngine.Rendering.Vulkan;

internal enum EOpenXrStrictSpsFaultInjectionStage : byte
{
    None = 0,
    Recording,
    LifetimeValidation,
    Submit,
}
