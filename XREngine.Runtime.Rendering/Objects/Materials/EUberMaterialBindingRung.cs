namespace XREngine.Rendering;

/// <summary>
/// Ordered binding strategies for sampler-heavy uber materials.
/// </summary>
public enum EUberMaterialBindingRung
{
    DirectSamplers,
    CompatibleTextureArrays,
    MaterialTextureTable,
    BindlessDescriptors,
    Unsupported,
}
