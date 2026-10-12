using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders;

public static partial class OutlinePassVariantFactory
{
    private static XRMaterial CreateCookedMaterialVariant(XRMaterial source, MaterialPassDefinition pass)
    {
        if (pass.ShaderBehavior != EngineMaterialSemanticIdentity.UberOutlineV1 ||
            ShaderProgramArtifactCatalog.ValidateIdentity(pass.CookedArtifactIdentity) is null ||
            source.CookedOutlineProfile is not { } profile)
            throw new NotSupportedException("UberOutline.CookedContractMissing: the outline pass requires its explicit modeled behavior, source profile and exact cooked descriptor.");
        if (!UberOutlineSurfaceBinding.TryCreate(source, profile, out UberOutlineSurfaceBinding? binding, out string? reason))
            throw new NotSupportedException(reason);

        ReadOnlySpan<UberOutlineTextureBinding> required = binding!.RequiredTextures;
        XRTexture?[] textures = new XRTexture?[required.Length];
        for (int index = 0; index < textures.Length; index++) textures[index] = required[index].Texture;
        // The constructor owns this compact list; its images and the eventual
        // parameter array remain borrowed from the authoritative source.
        XRMaterial variant = new(textures, Array.Empty<XRShader>());
        try
        {
            XRShader shader = new(EShaderType.Fragment) { CookedArtifactIdentity = pass.CookedArtifactIdentity };
            variant.OwnCookedOutlineShader(shader);
            variant.Shaders.Add(shader);
            variant.Name = string.Concat(source.Name, " [Outline]");
            variant.BillboardMode = source.BillboardMode;
            variant.AlphaCutoff = source.AlphaCutoff;
            variant.TransparencyMode = source.TransparencyMode;
            variant.TransparentTechniqueOverride = source.TransparentTechniqueOverride;
            variant.TransparentSortPriority = source.TransparentSortPriority;
            variant.RenderPass = pass.RenderPass;
            variant.RenderOptions = pass.RenderOptions;
            variant.Parameters = source.Parameters;
            variant.UberAuthoredState = source.UberAuthoredState;
            variant.UberOutlineSourceMaterial = source;
            variant.EngineSemantic = EngineMaterialSemanticIdentity.UberOutlineV1;
            return variant;
        }
        catch
        {
            variant.Parameters = [];
            variant.Destroy(now: true);
            throw;
        }
    }
}
