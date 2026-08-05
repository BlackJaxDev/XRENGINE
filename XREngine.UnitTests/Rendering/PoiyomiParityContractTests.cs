using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiParityContractTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void UnityYamlAndTextureMetadata_ParsesOldModernAndInlineLayouts()
    {
        string root = Path.GetDirectoryName(PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.UnitTests", "TestData", "Poiyomi", "README.md"))!;
        UnityMaterialDocument old = UnityMaterialDocumentParser.ParseFile(
            Path.Combine(root, "material-old-unity.yaml"));
        UnityMaterialDocument modern = UnityMaterialDocumentParser.ParseFile(
            Path.Combine(root, "material-new-unity.yaml"));
        UnityTextureImportDocument texture = UnityTextureImportDocumentParser.ParseFile(
            Path.Combine(root, "texture-import-modern.meta")).ShouldNotBeNull();
        UnityMaterialDocument inline = UnityMaterialDocumentParser.Parse(
            """
            Material:
              m_Name: Inline
              m_Shader: {fileID: 4800000, guid: 9444ce77bf4418748b1e8591b9d97f85, type: 3}
              m_CustomRenderQueue: 2450
              m_SavedProperties:
                m_TexEnvs: []
                m_Ints: [{_Mode: 1}]
                m_Floats: [{_Cutoff: 0.5}]
                m_Colors: [{_Color: {r: 1, g: 0.5, b: 0.25, a: 1}}]
            """);

        old.Name.ShouldNotBeNullOrWhiteSpace();
        modern.Name.ShouldNotBeNullOrWhiteSpace();
        texture.SourcePath.ShouldNotBeNullOrWhiteSpace();
        inline.CustomRenderQueue.ShouldBe(2450);
        inline.TryGetFloat("_Cutoff", out float cutoff).ShouldBeTrue();
        cutoff.ShouldBe(0.5f);
        inline.Vectors.ShouldContainKey("_Color");
    }

    [Test]
    public void UberSamplerTextures_ScheduleRealPreviewDecodeInsteadOfLeavingFallbackPixels()
    {
        string importerPath = PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.Runtime.ModelingBridge", "Importing", "ModelImporter.cs");
        string importerSource = File.ReadAllText(importerPath);
        int methodStart = importerSource.IndexOf(
            "public static XRTexture2D GetOrCreateUberSamplerTexture",
            StringComparison.Ordinal);
        int methodEnd = importerSource.IndexOf(
            "private static XRTexture2D GetOrCreateDefaultUberSamplerTexture",
            methodStart,
            StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = importerSource[methodStart..methodEnd];
        method.ShouldContain("ScheduleImportedTexturePreviewJob");
        method.ShouldNotContain("RegisterImportedTextureStreamingPlaceholder");
        method.ShouldContain("loaded.SamplerName = key.samplerName");
        method.ShouldContain("onError:");
    }

    [Test]
    public void OpenGlImportedPreview_PublishesDenseBeforeSparseEligibilityIsKnown()
    {
        string managerPath = PoiyomiParityCorpusTests.FindRepositoryFile(
            "XREngine.Runtime.Rendering", "Objects", "Textures", "2D", "ImportedTextureStreamingManager.cs");
        string managerSource = File.ReadAllText(managerPath);
        int methodStart = managerSource.IndexOf(
            "private ITextureResidencyBackend ResolvePreviewBackendCandidate",
            StringComparison.Ordinal);
        int methodEnd = managerSource.IndexOf(
            "private ITextureResidencyBackend ResolveBackendForTexture",
            methodStart,
            StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = managerSource[methodStart..methodEnd];
        method.ShouldContain("return _tieredBackend;");
        method.ShouldNotContain("GetSparseTextureStreamingSupport");
        method.ShouldNotContain("return _sparseBackend;");
    }
    [Test]
    public void ShaderMatcher_DetectsPinnedUnlockedLockedAndRejectsOtherVersions()
    {
        HashSet<string> signature = new(
            ["shader_master_label", "shader_is_using_thry_editor", "_ShaderOptimizerEnabled", "_MainTex", "_ShadingEnabled"],
            StringComparer.Ordinal);
        PoiyomiShaderMatchResult unlocked = PoiyomiShaderMatcher.Match(new()
        {
            ShaderPath = "Assets/_PoiyomiShaders/Shaders/9.3/Toon/Poiyomi Toon.shader",
            ShaderSource = "Shader \".poiyomi/Poiyomi Toon\" { // Poiyomi 9.3.64 }",
            PropertyNames = signature,
        });
        PoiyomiShaderMatchResult locked = PoiyomiShaderMatcher.Match(new()
        {
            ShaderPath = "Assets/OptimizedShaders/Avatar.shader",
            ShaderSource = "Shader \"Hidden/Locked/Avatar\" { // Poiyomi 9.3.64 OPTIMIZER_ENABLED }",
            PropertyNames = signature,
            OverrideTags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OriginalShaderGUID"] = PoiyomiToon93Catalog.ShaderGuid,
            },
        });
        PoiyomiShaderMatchResult rejected = PoiyomiShaderMatcher.Match(new()
        {
            ShaderPath = "Assets/_PoiyomiShaders/Shaders/10/Poiyomi Toon.shader",
            ShaderSource = "Shader \".poiyomi/Poiyomi Toon\" { // Poiyomi 10.0.0 }",
            PropertyNames = signature,
        });

        unlocked.IsAccepted.ShouldBeTrue();
        unlocked.IsLocked.ShouldBeFalse();
        locked.IsAccepted.ShouldBeTrue();
        locked.IsLocked.ShouldBeTrue();
        rejected.IsAccepted.ShouldBeFalse();
        rejected.Diagnostics.Single().Code.ShouldBe(MaterialConversionDiagnosticCodes.UnknownVersion);
    }

    [Test]
    public void ImportedValueSerialization_CoversTextureScalarVectorColorEnumAndRenderState()
    {
        ShaderVar[] parameters =
        [
            new ShaderFloat(1.25f, "_Float"),
            new ShaderInt(3, "_Enum"),
            new ShaderUInt(7, "_UInt"),
            new ShaderBool(true, "_Bool"),
            new ShaderVector2(new(1, 2), "_Vector2"),
            new ShaderVector3(new(1, 2, 3), "_Vector3"),
            new ShaderVector4(new(1, 0.5f, 0.25f, 1), "_Color"),
        ];
        foreach (ShaderVar source in parameters)
        {
            MaterialSerializedParameter serialized =
                MaterialSerializedParameter.Capture(source).ShouldNotBeNull();
            ShaderVar target = CreateZeroValue(source);
            serialized.Apply(target).ShouldBeTrue();
            target.GenericValue.ShouldBe(source.GenericValue);
        }

        XRMaterial material = new()
        {
            Parameters = parameters,
            Textures = [new XRTexture2D { SamplerName = "_MainTex", FilePath = "color.png" }],
            RenderPass = 42,
            TransparencyMode = ETransparencyMode.AlphaBlend,
            AlphaCutoff = 0.37f,
            TransparentSortPriority = 5,
        };
        MaterialConversionReport report = new() { SourceContentSha256 = "A" };
        MaterialImportedStateSnapshot snapshot = MaterialImportedStateSnapshot.Capture(material, report);
        snapshot.Textures[0].SamplerName.ShouldBe("_MainTex");
        snapshot.RenderPass.ShouldBe(material.RenderPass);
        snapshot.TransparencyMode.ShouldBe(ETransparencyMode.AlphaBlend);
        snapshot.AlphaCutoff.ShouldBe(0.37f);
        snapshot.TransparentSortPriority.ShouldBe(5);
    }

    [Test]
    public void UnknownValuesAndDiagnostics_AreStableAndNeverSilentlyDropped()
    {
        MaterialConversionDiagnostic[] diagnostics =
        [
            new("POI9999", MaterialConversionDiagnosticSeverity.Warning, "Unknown value retained.", "_UnknownZ"),
            new("POI9998", MaterialConversionDiagnosticSeverity.Warning, "Unknown value retained.", "_UnknownA"),
        ];
        XRMaterial material = new() { Name = "Unknowns" };
        string source = Path.GetTempFileName();
        try
        {
            File.WriteAllText(source, "synthetic");
            MaterialConversionReport first = MaterialConversionReportBuilder.Create(
                source,
                null,
                material,
                null,
                ["z warning", "a warning", "a warning"],
                diagnostics,
                EMaterialConversionOutcome.Converted);
            MaterialConversionReport second = MaterialConversionReportBuilder.Create(
                source,
                null,
                material,
                null,
                ["z warning", "a warning", "a warning"],
                diagnostics,
                EMaterialConversionOutcome.Converted);
            first.ToJson().ShouldBe(second.ToJson());
            first.Warnings.ShouldBe(["a warning", "z warning"]);
            first.DiagnosticGroups.SelectMany(static group => group.Diagnostics).Count().ShouldBe(2);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Test]
    public void PassStateVariantAndDependencyContracts_AreDeterministicAndIsolated()
    {
        XRMaterial first = CreateUberMaterial();
        first.SetUberFeatureEnabled("emission", true);
        first.SetUberFeatureEnabled("outline", true);
        first.SetUberPropertyMode("_EmissionStrength", EShaderUiPropertyMode.Animated);
        first.PrepareUberVariantImmediately().ShouldBeTrue();

        XRMaterial second = CreateUberMaterial();
        second.SetUberFeatureEnabled("emission", true);
        second.SetUberFeatureEnabled("outline", true);
        second.SetUberPropertyMode("_EmissionStrength", EShaderUiPropertyMode.Animated);
        second.PrepareUberVariantImmediately().ShouldBeTrue();

        first.RequestedUberVariant.VariantHash.ShouldBe(second.RequestedUberVariant.VariantHash);
        first.RequestedUberVariant.AnimatedProperties.ShouldContain("_EmissionStrength");
        first.PassSet.Passes.Select(static pass => pass.Identity).ShouldBeUnique();
        first.PassSet.Passes.ShouldAllBe(static pass => pass.RenderOptions != null);
        first.SetUberFeatureEnabled("outline", false);
        first.RequestedUberVariant.VariantHash.ShouldNotBe(second.RequestedUberVariant.VariantHash);
    }

    [Test]
    public void SamplerLimitPlanner_UsesEveryFaithfulRungAndFailsExplicitly()
    {
        UberMaterialBindingLimits limits = UberMaterialBindingLimits.Vulkan10Minimum;
        UberMaterialBindingPlanner.Plan(16, 16, 1024, limits, false, false, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.DirectSamplers);
        UberMaterialBindingPlanner.Plan(24, 24, 1024, limits, true, false, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.CompatibleTextureArrays);
        UberMaterialBindingPlanner.Plan(24, 24, 1024, limits, false, true, false)
            .Rung.ShouldBe(EUberMaterialBindingRung.MaterialTextureTable);
        UberMaterialBindingPlanner.Plan(24, 24, 1024, limits, false, false, true)
            .Rung.ShouldBe(EUberMaterialBindingRung.BindlessDescriptors);
        UberMaterialBindingPlan failure =
            UberMaterialBindingPlanner.Plan(33, 33, 1024, limits, false, false, false);
        failure.Rung.ShouldBe(EUberMaterialBindingRung.Unsupported);
        failure.FailureReason.ShouldNotBeNull().ShouldContain("33 fragment samplers");
    }

    [Test]
    public void SchemaHierarchyConditionsAndWidgets_HaveStableCompleteContracts()
    {
        ShaderAuthoringSchema first = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
            ShaderHelper.UberFragForward().GetUiManifest());
        ShaderAuthoringSchema second = PoiyomiAuthoringSchemaCatalog.GetOrCreate(
            ShaderHelper.UberFragForward().GetUiManifest());
        first.Fingerprint.ShouldBe(second.Fingerprint);
        first.Version.ShouldBeGreaterThan(0);
        first.DeclarationOrder.Select(static node => node.SemanticId).ShouldBeUnique();
        first.DeclarationOrder.Select(static node => node.DeclarationOrder)
            .ShouldBeInOrder(SortDirection.Ascending);
        first.DeclarationOrder.ShouldAllBe(static node =>
            node.Kind == EShaderAuthoringNodeKind.Root || node.Parent != null);
        first.DeclarationOrder.Where(static node => node.Options.PersistentExpand)
            .ShouldAllBe(static node => node.Kind == EShaderAuthoringNodeKind.Section || node.Kind == EShaderAuthoringNodeKind.Subsection);

        foreach (ShaderAuthoringWidgetDescriptor widget in ShaderAuthoringWidgetRegistry.All)
        {
            ShaderAuthoringWidgetRegistry.TryResolve(widget.Id, out ShaderAuthoringWidgetDescriptor resolved)
                .ShouldBeTrue();
            resolved.SupportsReset.ShouldBe(widget.SupportsReset);
            if (widget.IsTool)
                ShaderAuthoringWidgetRegistry.IsAllowlistedTool(widget.Id).ShouldBeTrue();
        }
    }

    [Test]
    public void Expressions_HandlePrecedenceCoercionUnknownCyclesAndMalformedInput()
    {
        DictionaryExpressionContext context = new(new()
        {
            ["a"] = 2.0,
            ["b"] = 3.0,
            ["texture:_MainTex"] = true,
            ["render_queue"] = 2450,
            ["animated:_Color"] = true,
        });
        ShaderAuthoringExpression.TryCompile(
            "a + b * 4 == 14 && texture:_MainTex && render_queue >= 2000 && animated:_Color",
            out ShaderAuthoringExpression? expression,
            out _).ShouldBeTrue();
        expression.ShouldNotBeNull().EvaluateBoolean(context).ShouldBeTrue();
        ShaderAuthoringExpression.TryCompile("unknown + 1", out ShaderAuthoringExpression? unknown, out _)
            .ShouldBeTrue();
        unknown.ShouldNotBeNull().Evaluate(context).AsNumber().ShouldBe(1.0);
        ShaderAuthoringExpression.TryCompile("(a +", out _, out string? malformed).ShouldBeFalse();
        malformed.ShouldNotBeNullOrWhiteSpace();

        ShaderAuthoringNode a = Node("_A", "_B > 0");
        ShaderAuthoringNode b = Node("_B", "_A > 0");
        ShaderAuthoringNode root = new()
        {
            SemanticId = "root",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = "Root",
        };
        root.Children.Add(a);
        root.Children.Add(b);
        a.Parent = root;
        b.Parent = root;
        ShaderAuthoringSchema cyclic = new("cycle", 1, "synthetic", "cycle", root, []);
        cyclic.Issues.ShouldContain(issue => issue.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ActionsAreClosedAtomicUndoableAndRequireExplicitRemotePolicy()
    {
        IReadOnlyList<MaterialAuthoringAction> actions = MaterialAuthoringActionParser.Parse(
            "SET_PROPERTY:_Mode=2;SET_TAG:RenderType=Transparent;SET_SHADER:Poiyomi;URL:https://example.com;" +
            "OPEN_EDITOR:not-registered;SET_RENDER_STATE:_ZWrite=0;SHELL:cmd.exe");
        actions.Count.ShouldBe(6);
        actions.ShouldNotContain(action => action.Target == "cmd.exe");

        XRMaterial material = CreateUberMaterial();
        float before = material.Parameter<ShaderFloat>("_EmissionStrength").ShouldNotBeNull().Value;
        MaterialAuthoringTransaction transaction = new("Atomic failure");
        transaction.AddStructural(
            material,
            "value",
            () => material.Parameter<ShaderFloat>("_EmissionStrength")!.Value = 9.0f,
            () => material.Parameter<ShaderFloat>("_EmissionStrength")!.Value = before,
            true);
        transaction.AddStructural(
            material,
            "fail",
            () => throw new InvalidOperationException("rollback"),
            static () => { });
        transaction.TryExecute(out MaterialAuthoringTransactionReport report).ShouldBeFalse();
        report.Diagnostics.ShouldContain(diagnostic => diagnostic.Contains("rollback", StringComparison.Ordinal));
        material.Parameter<ShaderFloat>("_EmissionStrength")!.Value.ShouldBe(before);

        MaterialAuthoringCommandRegistry.RequestSafeLink("http://example.com", out _).ShouldBeFalse();
        MaterialAuthoringCommandRegistry.RequestSafeLink("https://example.com", out string? noHandler).ShouldBeFalse();
        noHandler.ShouldNotBeNull().ShouldContain("confirmation");
        RemoteAuthoringPolicy disabled = new(false, new HashSet<string>(StringComparer.Ordinal), 1024, TimeSpan.Zero);
        disabled.Validate(new("https://example.com")).ShouldBe("Remote authoring content is disabled.");
    }

    [Test]
    public void ClipboardPresetLayersLinksAndGeneratedTools_AreDeterministicAndSafe()
    {
        MaterialAuthoringClipboardPayload payload = new()
        {
            SchemaId = "poiyomi",
            ScopeSemanticId = "surface",
            Values = [new("color", "Vector4", "[1,1,1,1]", null, EShaderUiPropertyMode.Animated)],
        };
        string serialized = payload.Serialize();
        MaterialAuthoringClipboardPayload.TryDeserialize(
            serialized,
            out MaterialAuthoringClipboardPayload? roundTrip).ShouldBeTrue();
        roundTrip.ShouldNotBeNull().Values.Single().SemanticId.ShouldBe("color");
        MaterialAuthoringClipboardPayload.TryDeserialize(
            MaterialAuthoringClipboardPayload.Prefix + "{\"Version\":999}",
            out _).ShouldBeFalse();

        MaterialAuthoringLayeredValue<float> layers = new();
        layers.Apply(EMaterialAuthoringValueLayer.Imported, 1);
        layers.Apply(EMaterialAuthoringValueLayer.Preset, 2);
        layers.Apply(EMaterialAuthoringValueLayer.Local, 3);
        layers.TryResolve(out float value, out EMaterialAuthoringValueLayer layer).ShouldBeTrue();
        value.ShouldBe(3);
        layer.ShouldBe(EMaterialAuthoringValueLayer.Local);
        layers.Revert(EMaterialAuthoringValueLayer.Local).ShouldBeTrue();
        layers.TryResolve(out value, out layer).ShouldBeTrue();
        value.ShouldBe(2);

        TexturePackingRecipe recipe = new()
        {
            Width = 2,
            Height = 2,
            Channels =
            [
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.1f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.2f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 0.3f },
                new() { Kind = ETextureChannelSourceKind.Constant, Constant = 1.0f },
            ],
        };
        Vector4[] first = MaterialTexturePacker.Pack(recipe, new Dictionary<string, TexturePixelSource>());
        Vector4[] second = MaterialTexturePacker.Pack(recipe, new Dictionary<string, TexturePixelSource>());
        first.ShouldBe(second);
        first.ShouldAllBe(pixel => pixel == new Vector4(0.1f, 0.2f, 0.3f, 1.0f));
        Should.Throw<UnauthorizedAccessException>(() =>
            MaterialTexturePacker.ValidateOutputPath(
                Path.GetFullPath("Assets"),
                Path.GetFullPath(Path.Combine("..", "escape.png"))));
    }

    [Test]
    public void FuzzedUntrustedPayloadsNeverExecuteEscapeHangOrPartiallyMutate()
    {
        Random random = new(130964);
        XRMaterial material = CreateUberMaterial();
        float original = material.Parameter<ShaderFloat>("_EmissionStrength")!.Value;
        for (int iteration = 0; iteration < 500; iteration++)
        {
            string fuzz = new(Enumerable.Range(0, random.Next(0, 96))
                .Select(_ => (char)random.Next(1, 127))
                .ToArray());
            _ = ShaderAuthoringExpression.TryCompile(fuzz, out _, out _);
            _ = MaterialAuthoringActionParser.Parse(fuzz);
            _ = MaterialAuthoringClipboardPayload.TryDeserialize(fuzz, out _);
            _ = MaterialAuthoringCommandRegistry.RequestSafeLink(fuzz, out _);
            material.Parameter<ShaderFloat>("_EmissionStrength")!.Value.ShouldBe(original);
        }
    }

    private static XRMaterial CreateUberMaterial()
    {
        XRMaterial material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Add(ShaderHelper.UberFragForward());
        material.EnsureUberStateInitialized();
        return material;
    }

    private static ShaderVar CreateZeroValue(ShaderVar source)
        => source switch
        {
            ShaderFloat => new ShaderFloat(0, source.Name),
            ShaderInt => new ShaderInt(0, source.Name),
            ShaderUInt => new ShaderUInt(0, source.Name),
            ShaderBool => new ShaderBool(false, source.Name),
            ShaderVector2 => new ShaderVector2(Vector2.Zero, source.Name),
            ShaderVector3 => new ShaderVector3(Vector3.Zero, source.Name),
            ShaderVector4 => new ShaderVector4(Vector4.Zero, source.Name),
            _ => throw new NotSupportedException(),
        };

    private static ShaderAuthoringNode Node(string name, string condition)
    {
        ShaderAuthoringExpression.TryCompile(condition, out ShaderAuthoringExpression? expression, out _)
            .ShouldBeTrue();
        return new()
        {
            SemanticId = name.TrimStart('_'),
            Kind = EShaderAuthoringNodeKind.Property,
            DisplayName = name,
            SourcePropertyName = name,
            VisibilityExpression = expression,
        };
    }

    private sealed class DictionaryExpressionContext(Dictionary<string, object?> values)
        : IShaderAuthoringExpressionContext
    {
        public bool TryResolve(string operand, out ShaderAuthoringValue value)
        {
            if (values.TryGetValue(operand, out object? found))
            {
                value = new(found);
                return true;
            }
            value = default;
            return false;
        }
    }
}
