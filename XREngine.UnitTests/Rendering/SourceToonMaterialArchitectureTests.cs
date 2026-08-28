using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SourceToonMaterialArchitectureTests
{
    [TestCase(0, SourceToonRenderPreset.Opaque, 2000, ETransparencyMode.Opaque, EDefaultRenderPass.OpaqueForward, EBlendingFactor.One, EBlendingFactor.Zero, true)]
    [TestCase(1, SourceToonRenderPreset.Cutout, 2450, ETransparencyMode.Masked, EDefaultRenderPass.MaskedForward, EBlendingFactor.One, EBlendingFactor.Zero, true)]
    [TestCase(9, SourceToonRenderPreset.TransClipping, 2460, ETransparencyMode.Masked, EDefaultRenderPass.MaskedForward, EBlendingFactor.SrcAlpha, EBlendingFactor.OneMinusSrcAlpha, true)]
    [TestCase(2, SourceToonRenderPreset.Fade, 3000, ETransparencyMode.AlphaBlend, EDefaultRenderPass.TransparentForward, EBlendingFactor.SrcAlpha, EBlendingFactor.OneMinusSrcAlpha, false)]
    [TestCase(3, SourceToonRenderPreset.Transparent, 3000, ETransparencyMode.PremultipliedAlpha, EDefaultRenderPass.TransparentForward, EBlendingFactor.One, EBlendingFactor.OneMinusSrcAlpha, false)]
    [TestCase(4, SourceToonRenderPreset.Additive, 3000, ETransparencyMode.Additive, EDefaultRenderPass.TransparentForward, EBlendingFactor.One, EBlendingFactor.One, false)]
    [TestCase(5, SourceToonRenderPreset.SoftAdditive, 3000, ETransparencyMode.AlphaBlend, EDefaultRenderPass.TransparentForward, EBlendingFactor.OneMinusDstColor, EBlendingFactor.One, false)]
    [TestCase(6, SourceToonRenderPreset.Multiplicative, 3000, ETransparencyMode.AlphaBlend, EDefaultRenderPass.TransparentForward, EBlendingFactor.DstColor, EBlendingFactor.Zero, false)]
    [TestCase(7, SourceToonRenderPreset.Multiplicative2X, 3000, ETransparencyMode.AlphaBlend, EDefaultRenderPass.TransparentForward, EBlendingFactor.DstColor, EBlendingFactor.SrcColor, false)]
    public void PresetMapping_IsExact(
        int mode,
        SourceToonRenderPreset expectedPreset,
        int expectedQueue,
        ETransparencyMode expectedTransparency,
        EDefaultRenderPass expectedRenderPass,
        EBlendingFactor expectedSource,
        EBlendingFactor expectedDestination,
        bool expectedDepthWrite)
    {
        SourceToonRenderStateConversion conversion = Convert(CreateDocument(("_Mode", mode)));

        conversion.Preset.ShouldBe(expectedPreset);
        conversion.TransparencyMode.ShouldBe(expectedTransparency);
        conversion.PassSet.SourceRenderQueue.ShouldBe(expectedQueue);
        conversion.PassSet.QueuePriority.ShouldBe(0);
        MaterialPassDefinition basePass = GetPass(conversion, EMaterialPassIdentity.Base);
        basePass.RenderPass.ShouldBe((int)expectedRenderPass);
        BlendMode baseBlend = basePass.RenderOptions.BlendModeAllDrawBuffers.ShouldNotBeNull();
        baseBlend.RgbSrcFactor.ShouldBe(expectedSource);
        baseBlend.RgbDstFactor.ShouldBe(expectedDestination);
        basePass.RenderOptions.DepthTest.UpdateDepth.ShouldBe(expectedDepthWrite);
    }

    [Test]
    public void PassSet_PreservesQueuePassEnablesAndDeterministicOrder()
    {
        SerializedMaterialDocument document = CreateDocument(
            ("_Mode", 0),
            ("_RenderingEarlyZEnabled", 1),
            ("_EnableOutlines", 1));
        document = WithQueue(document, 2075);
        document.DisabledShaderPasses.Add("ShadowCaster");
        document.DisabledShaderPasses.Add("Add");

        SourceToonRenderStateConversion conversion = Convert(document);

        conversion.PassSet.QueuePriority.ShouldBe(75);
        conversion.PassSet.ForwardAddPolicy.ShouldBe(EMaterialForwardAddPolicy.Disabled);
        GetPass(conversion, EMaterialPassIdentity.EarlyDepth).Enabled.ShouldBeTrue();
        GetPass(conversion, EMaterialPassIdentity.Outline).Enabled.ShouldBeTrue();
        GetPass(conversion, EMaterialPassIdentity.Shadow).Enabled.ShouldBeFalse();
        conversion.PassSet.Passes.Select(static pass => pass.Order)
            .ShouldBeInOrder(SortDirection.Ascending);
    }

    [Test]
    public void AuthoredFixedState_MapsRgbAlphaDepthCullMaskOffsetFogAndStencil()
    {
        SerializedMaterialDocument document = CreateDocument(
            ("_Mode", 2),
            ("_SrcBlend", 3),
            ("_DstBlend", 4),
            ("_SrcBlendAlpha", 7),
            ("_DstBlendAlpha", 8),
            ("_BlendOp", 1),
            ("_BlendOpAlpha", 2),
            ("_ZTest", 0),
            ("_ZWrite", 1),
            ("_Cull", 1),
            ("_ColorMask", 5),
            ("_AlphaToCoverage", 1),
            ("_StencilRef", 7),
            ("_StencilReadMask", 63),
            ("_StencilWriteMask", 31),
            ("_StencilCompareFunction", 3),
            ("_StencilPassOp", 2),
            ("_StencilFailOp", 5),
            ("_StencilZFailOp", 6),
            ("_OffsetFactor", -1),
            ("_OffsetUnits", 2),
            ("_IgnoreFog", 1));

        MaterialPassDefinition pass = GetPass(Convert(document), EMaterialPassIdentity.Base);
        BlendMode blend = pass.RenderOptions.BlendModeAllDrawBuffers.ShouldNotBeNull();

        blend.RgbSrcFactor.ShouldBe(EBlendingFactor.SrcColor);
        blend.RgbDstFactor.ShouldBe(EBlendingFactor.OneMinusDstColor);
        blend.AlphaSrcFactor.ShouldBe(EBlendingFactor.DstAlpha);
        blend.AlphaDstFactor.ShouldBe(EBlendingFactor.OneMinusDstAlpha);
        blend.RgbEquation.ShouldBe(EBlendEquationMode.FuncSubtract);
        blend.AlphaEquation.ShouldBe(EBlendEquationMode.FuncReverseSubtract);
        pass.RenderOptions.DepthTest.Enabled.ShouldBe(ERenderParamUsage.Disabled);
        pass.RenderOptions.DepthTest.UpdateDepth.ShouldBeTrue();
        pass.RenderOptions.CullMode.ShouldBe(ECullMode.Front);
        pass.RenderOptions.WriteRed.ShouldBeTrue();
        pass.RenderOptions.WriteGreen.ShouldBeFalse();
        pass.RenderOptions.WriteBlue.ShouldBeTrue();
        pass.RenderOptions.WriteAlpha.ShouldBeFalse();
        pass.RenderOptions.AlphaToCoverage.ShouldBe(ERenderParamUsage.Enabled);
        pass.RenderOptions.StencilTest.FrontFace.Reference.ShouldBe(7);
        pass.RenderOptions.StencilTest.FrontFace.ReadMask.ShouldBe(63u);
        pass.RenderOptions.StencilTest.FrontFace.WriteMask.ShouldBe(31u);
        pass.RenderOptions.StencilTest.FrontFace.Function.ShouldBe(EComparison.Equal);
        pass.RenderOptions.StencilTest.FrontFace.BothPassOp.ShouldBe(EStencilOp.Replace);
        pass.RenderOptions.StencilTest.FrontFace.BothFailOp.ShouldBe(EStencilOp.Invert);
        pass.RenderOptions.StencilTest.FrontFace.StencilPassDepthFailOp.ShouldBe(EStencilOp.IncrWrap);
        pass.PolygonOffsetFactor.ShouldBe(-1.0f);
        pass.PolygonOffsetUnits.ShouldBe(2.0f);
        pass.IgnoreFog.ShouldBeTrue();
    }

    [Test]
    public void CompanionPasses_SharePositionOpacityCoverageAndOutlineHasIndependentState()
    {
        SerializedMaterialDocument document = CreateDocument(
            ("_Mode", 1),
            ("_Cutoff", 0.37f),
            ("_EnableDissolve", 1),
            ("_VertexManipulationEnabled", 1),
            ("_EnableOutlines", 1),
            ("_OutlineCull", 0),
            ("_OutlineZWrite", 0),
            ("_OutlineZTest", 5));

        SourceToonRenderStateConversion conversion = Convert(document);
        ulong expectedHash = GetPass(conversion, EMaterialPassIdentity.Base).PositionOpacityStateHash;
        foreach (MaterialPassDefinition pass in conversion.PassSet.Passes)
        {
            pass.CoverageRules.ShouldBe(EMaterialPassCoverageRules.All);
            pass.PositionOpacityStateHash.ShouldBe(expectedHash);
        }

        MaterialPassDefinition outline = GetPass(conversion, EMaterialPassIdentity.Outline);
        outline.RenderOptions.CullMode.ShouldBe(ECullMode.None);
        outline.RenderOptions.DepthTest.UpdateDepth.ShouldBeFalse();
        outline.RenderOptions.DepthTest.Function.ShouldBe(EComparison.Greater);
        outline.VertexShaderPath.ShouldEndWith("UberShader.vert");
        outline.FragmentShaderPath.ShouldEndWith("UberShader.frag");

        const EUniformRequirements expectedRequirements =
            EUniformRequirements.Camera |
            EUniformRequirements.Lights |
            EUniformRequirements.AmbientOcclusion |
            EUniformRequirements.ViewportDimensions |
            EUniformRequirements.ClipSpacePolicy |
            EUniformRequirements.RenderTime;
        GetPass(conversion, EMaterialPassIdentity.Base).RenderOptions.RequiredEngineUniforms
            .ShouldBe(expectedRequirements);
        outline.RenderOptions.RequiredEngineUniforms.ShouldBe(expectedRequirements);
    }

    [Test]
    public void CopyEnabledPasses_IsAllocationFreeAfterWarmup()
    {
        MaterialPassSet passSet = Convert(CreateDocument(("_Mode", 0))).PassSet;
        MaterialPassDefinition[] destination = new MaterialPassDefinition[passSet.Passes.Length];
        passSet.CopyEnabledPasses(destination);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = passSet.CopyEnabledPasses(destination);
        long after = GC.GetAllocatedBytesForCurrentThread();

        count.ShouldBeGreaterThan(0);
        (after - before).ShouldBe(0);
    }

    [Test]
    public void SlotSchemas_AreAuthoritativeAndSpecialized()
    {
        UberMaterialSlotSchemas.Decals.SlotCount.ShouldBe(4);
        UberMaterialSlotSchemas.Matcaps.SlotCount.ShouldBe(4);
        UberMaterialSlotSchemas.Emissions.SlotCount.ShouldBe(4);
        UberMaterialSlotSchemas.Rims.SlotCount.ShouldBe(2);
        UberMaterialSlotSchemas.All.Select(static schema => schema.Id).Distinct().Count().ShouldBe(4);
        UberMaterialSlotSchemas.All.ShouldAllBe(static schema =>
            schema.FieldSuffixes.Length > 0 && schema.SamplerRoles.Length > 0);
    }

    [Test]
    public void FeatureDependencies_AreClosedTransitivelyAndSorted()
    {
        ShaderUiManifest manifest = new(
        [
            Feature("base"),
            Feature("normal", "base"),
            Feature("parallax", "normal"),
        ], [], []);

        UberFeatureDependencyResolver.Resolve(manifest, ["parallax"])
            .ShouldBe(["base", "normal", "parallax"]);
    }

    [Test]
    public void BindingPlanner_UsesOrderedLadderAndReportsPreciseFailure()
    {
        UberMaterialBindingLimits limits = UberMaterialBindingLimits.OpenGl46Minimum;

        UberMaterialBindingPlanner.Plan(8, 8, 256, limits, false, false, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.DirectSamplers);
        UberMaterialBindingPlanner.Plan(24, 24, 256, limits, true, false, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.CompatibleTextureArrays);
        UberMaterialBindingPlanner.Plan(24, 24, 256, limits, false, true, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.MaterialTextureTable);
        UberMaterialBindingPlanner.Plan(24, 24, 256, limits, false, false, true)
            .Rung.ShouldBe(EUberMaterialBindingRung.BindlessDescriptors);

        UberMaterialBindingPlan unsupported =
            UberMaterialBindingPlanner.Plan(24, 24, 256, limits, false, false, false);
        unsupported.IsSupported.ShouldBeFalse();
        unsupported.FailureReason.ShouldNotBeNull().ShouldContain("24 fragment samplers");

        UberMaterialBindingPlan tooManyUniforms =
            UberMaterialBindingPlanner.Plan(1, 1, limits.MaxUniformBytes + 1, limits, false, false, false);
        tooManyUniforms.IsSupported.ShouldBeFalse();
        tooManyUniforms.FailureReason.ShouldNotBeNull().ShouldContain("uniform bytes");
    }

    [Test]
    public void HelperAudit_AccountsForEveryModuleAndNeverMarksAnUnreachableModuleActive()
    {
        UberHelperModuleAudit.Entries.Count.ShouldBe(17);
        UberHelperModuleAudit.Entries.Select(static entry => entry.FileName)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(17);
        UberHelperModuleAudit.Entries
            .Where(static entry => entry.Status == EUberHelperModuleStatus.Active)
            .ShouldAllBe(static entry => entry.ReachableFromCanonicalPass);
        UberHelperModuleAudit.Entries.Single(static entry => entry.FileName == "outlines.glsl")
            .Status.ShouldBe(EUberHelperModuleStatus.Obsolete);
        UberHelperModuleAudit.Entries.Single(static entry => entry.FileName == "decals.glsl")
            .Status.ShouldBe(EUberHelperModuleStatus.Dormant);
    }

    [Test]
    public void SamplerFallbacks_AreSemanticAndDeterministic()
    {
        UberSamplerFallbacks.Get(EUberSamplerRole.Normal).Value.Z.ShouldBe(1.0f);
        UberSamplerFallbacks.Get(EUberSamplerRole.MaskWhite).Value.ShouldBe(System.Numerics.Vector4.One);
        UberSamplerFallbacks.Get(EUberSamplerRole.EmissionBlack).Value.X.ShouldBe(0.0f);
        UberSamplerFallbacks.Get(EUberSamplerRole.HeightNeutral).Value.X.ShouldBe(0.5f);
    }

    private static ShaderUiFeature Feature(string id, params string[] dependencies)
        => new(
            id,
            id,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            EShaderUiFeatureCost.Low,
            true,
            dependencies,
            []);

    private static SourceToonRenderStateConversion Convert(SerializedMaterialDocument document)
        => SourceToonRenderStateConverter.Convert(document, new List<MaterialConversionDiagnostic>());

    private static MaterialPassDefinition GetPass(
        SourceToonRenderStateConversion conversion,
        EMaterialPassIdentity identity)
    {
        conversion.PassSet.TryGetPass(identity, out MaterialPassDefinition pass).ShouldBeTrue();
        return pass;
    }

    private static SerializedMaterialDocument CreateDocument(params (string Name, float Value)[] properties)
    {
        SerializedMaterialDocument document = new() { Name = "MaterialArchitecture" };
        foreach ((string name, float value) in properties)
            document.Floats.Add(name, value);
        return document;
    }

    private static SerializedMaterialDocument WithQueue(SerializedMaterialDocument source, int queue)
    {
        SerializedMaterialDocument result = new()
        {
            Name = source.Name,
            CustomRenderQueue = queue,
        };
        foreach ((string name, float value) in source.Floats)
            result.Floats.Add(name, value);
        foreach (string pass in source.DisabledShaderPasses)
            result.DisabledShaderPasses.Add(pass);
        return result;
    }
}
