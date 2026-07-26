using System.Diagnostics;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    /// <summary>
    /// Prepares the base uber variant and all enabled companion pass sources as
    /// one material operation. The generated source cache owns the results, so
    /// steady-state submission only consumes immutable pass definitions.
    /// </summary>
    public MaterialPassPrewarmReport PrewarmUberPassSetImmediately()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        PrepareUberVariantImmediately();

        MaterialPassDefinition[] passes = PassSet.Passes;
        MaterialPassPrewarmEntry[] entries = new MaterialPassPrewarmEntry[passes.Length];
        int requested = 0;
        int prepared = 0;
        XRShader? baseFragment = GetShader(EShaderType.Fragment);

        for (int index = 0; index < passes.Length; index++)
        {
            MaterialPassDefinition pass = passes[index];
            bool sourcePrepared = false;
            string? failureReason = null;
            if (pass.Enabled)
            {
                requested++;
                try
                {
                    sourcePrepared = PreparePassSource(pass, baseFragment);
                    if (!sourcePrepared)
                        failureReason = $"No shader source could be prepared for the enabled {pass.Identity} pass.";
                    else
                        prepared++;
                }
                catch (Exception ex)
                {
                    failureReason = ex.GetBaseException().Message;
                }
            }

            entries[index] = new MaterialPassPrewarmEntry(
                pass.Identity,
                ComputePassVariantKey(pass),
                pass.Enabled,
                sourcePrepared,
                failureReason);
        }

        stopwatch.Stop();
        UberMaterialVariantStatus status = UberVariantStatus;
        int estimatedUniformBytes = checked(status.UniformCount * 16);
        UberMaterialBindingPlan openGlPlan = UberMaterialBindingPlanner.Plan(
            status.SamplerCount,
            status.SamplerCount,
            estimatedUniformBytes,
            UberMaterialBindingLimits.OpenGl46Minimum,
            textureArraysCompatible: false,
            materialTextureTableAvailable: false,
            bindlessDescriptorsAvailable: false);
        UberMaterialBindingPlan vulkanPlan = UberMaterialBindingPlanner.Plan(
            status.SamplerCount,
            status.SamplerCount,
            estimatedUniformBytes,
            UberMaterialBindingLimits.Vulkan10Minimum,
            textureArraysCompatible: false,
            materialTextureTableAvailable: false,
            bindlessDescriptorsAvailable: false);

        return new MaterialPassPrewarmReport
        {
            Entries = entries,
            RequestedPassCount = requested,
            PreparedPassCount = prepared,
            FeatureCount = RequestedUberVariant.EnabledFeatures.Length,
            SamplerCount = UberVariantStatus.SamplerCount,
            GeneratedSourceLength = UberVariantStatus.GeneratedSourceLength,
            PreparationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            CompileMilliseconds = status.CompileMilliseconds,
            LinkMilliseconds = status.LinkMilliseconds,
            OpenGlMinimumBindingPlan = openGlPlan,
            VulkanMinimumBindingPlan = vulkanPlan,
        };
    }

    private bool PreparePassSource(MaterialPassDefinition pass, XRShader? baseFragment)
    {
        switch (pass.Identity)
        {
            case EMaterialPassIdentity.Base:
                return baseFragment is not null;
            case EMaterialPassIdentity.DepthNormal:
            case EMaterialPassIdentity.EarlyDepth:
                return DepthNormalPrePassVariant is not null;
            case EMaterialPassIdentity.Shadow:
                return ShadowCasterVariant is not null;
            case EMaterialPassIdentity.Outline:
                return OutlinePassVariant is not null;
            default:
                if (baseFragment is null)
                    return false;

                XRShader? variant = baseFragment;
                foreach (string macro in pass.VariantMacros)
                    variant = ShaderHelper.CreateDefinedShaderVariant(variant, macro);
                return variant is not null;
        }
    }

    private ulong ComputePassVariantKey(MaterialPassDefinition pass)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        Add(ref hash, RequestedUberVariant.VariantHash, prime);
        Add(ref hash, (ulong)pass.Identity, prime);
        Add(ref hash, unchecked((ulong)pass.RenderPass), prime);
        Add(ref hash, pass.PositionOpacityStateHash, prime);
        foreach (string macro in pass.VariantMacros)
        {
            foreach (char character in macro)
                Add(ref hash, character, prime);
        }
        return hash;
    }

    private static void Add(ref ulong hash, ulong value, ulong prime)
    {
        hash ^= value;
        hash *= prime;
    }
}
