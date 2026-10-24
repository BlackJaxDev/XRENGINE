using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Rendering.Shaders;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UICanvasPublicationCaptureTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void NestedShaderScopeRestoresOnlyItsOwnSnippetRegistration()
    {
        const string prior = "// prior registered owner\n";
        bool hadOuter = UITestShaderScope.TryGetRegisteredSnippet(
            "NormalEncoding", out string? outer);
        ShaderSnippets.Register("NormalEncoding", prior);
        try
        {
            using (_shaderScope.RegisterCanonicalCropSnippets())
            {
                ShaderSnippets.TryGet("NormalEncoding", out string? nested)
                    .ShouldBeTrue();
                nested.ShouldNotBe(prior);
            }
            ShaderSnippets.TryGet("NormalEncoding", out string? restored)
                .ShouldBeTrue();
            restored.ShouldBe(prior);
        }
        finally
        {
            if (hadOuter)
                ShaderSnippets.Register("NormalEncoding", outer!);
            else
                ShaderSnippets.Unregister("NormalEncoding");
        }
    }

    [Test]
    public void CanonicalImageBatchRemainsEligibleWithResolvedSnippets()
    {
        using IDisposable snippets = _shaderScope.RegisterCanonicalCropSnippets();
        using IDisposable target =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        XRTexture2D texture = new()
        {
            Mipmaps = [new Mipmap2D(2, 2, new byte[16])],
            SizedInternalFormat = ESizedInternalFormat.Rgba8,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
        XRMaterial material = UIMaterialComponent.CreateImageMaterial(texture,
            Vector4.One);
        UIMaterialComponent component = new(material);
        try
        {
            ShaderVector4 color = material.Parameter<ShaderVector4>("MatColor")!;
            color.Value = new Vector4(0.25f, 0.5f, 0.75f, 1.0f);
            UIMaterialComponent.HasCanonicalImageShader(material).ShouldBeTrue();
            UIMaterialComponent.TryGetDesktopImageProfile(texture, false,
                out _).ShouldBeTrue();
            material.Parameters.Length.ShouldBe(1);
            component.SupportsBatchedRendering.ShouldBeTrue();
            material.ShadersChanged();
            material.Parameters.Length.ShouldBe(1);
            material.Parameters[0].ShouldBeSameAs(color);
            color.Value.ShouldBe(new Vector4(0.25f, 0.5f, 0.75f, 1.0f));
            component.SupportsBatchedRendering.ShouldBeTrue();
        }
        finally
        {
            component.Destroy(true);
            material.Destroy(true);
            texture.Destroy(true);
        }
    }

    [Test]
    public void UnrelatedUserUniformRemainsAnAuthoredMaterialParameter()
    {
        ShaderFloat authored = new(7.0f, "UserAmount");
        XRShader shader = new(EShaderType.Fragment,
            "uniform float UserAmount;\nvoid main() {}\n");
        XRMaterial material = new([authored], shader);
        try
        {
            material.ShadersChanged();
            material.Parameters.Length.ShouldBe(1);
            material.Parameters[0].ShouldBeSameAs(authored);
            authored.Value.ShouldBe(7.0f);
        }
        finally
        {
            material.Destroy(true);
            shader.Destroy(true);
        }
    }

    [Test]
    public void WorldCropShaderVariantKeepsCanonicalFragmentAuthority()
    {
        using IDisposable snippets = _shaderScope.RegisterCanonicalCropSnippets();
        XRShader canonical = XRShader.EngineShader(
            Path.Combine("Common", "UiTexturedForward.fs"),
            EShaderType.Fragment);
        XRShader custom = new(EShaderType.Fragment,
            "#version 460\nvoid main() {}\n");
        XRShader? clipped = null;
        try
        {
            XRMaterial material = new(Array.Empty<ShaderVar>(), canonical);
            try
            {
                UIWorldCropShaderContract.TrySelectFragment(material,
                    out int index).ShouldBeTrue();
                index.ShouldBe(0);
                UIWorldCropShaderContract.TryCreateFragmentVariant(canonical,
                    out clipped).ShouldBeTrue();
                clipped.ShouldNotBeNull();
                UIWorldCropShaderContract.IsCanonicalClippedFragment(clipped!)
                    .ShouldBeTrue();
                clipped!.GetResolvedSource().ShouldContain("UiWorldModelToCanvas");
                clipped.GetResolvedSource().ShouldContain("XRENGINE_ApplyUiWorldCrop();");
                clipped.Source.Text += "\n// Mutated after capture\n";
                UIWorldCropShaderContract.IsCanonicalClippedFragment(clipped)
                    .ShouldBeFalse();
                UIWorldCropShaderContract.TryCreateFragmentVariant(custom,
                    out _).ShouldBeFalse();

                XRShader textVertex = XRShader.EngineShader(
                    Path.Combine("Common", "Text.vs"), EShaderType.Vertex);
                XRShader textStereo = XRShader.EngineShader(
                    Path.Combine("Common", "TextStereo.vs"), EShaderType.Vertex);
                XRShader textFragment = XRShader.EngineShader(
                    Path.Combine("Common", "TextMsdf.fs"), EShaderType.Fragment);
                XRMaterial text = new(Array.Empty<ShaderVar>(),
                    textVertex, textStereo, textFragment);
                try
                {
                    UIWorldCropShaderContract.TrySelectFragment(text,
                        out int textIndex).ShouldBeTrue();
                    textIndex.ShouldBe(2);
                    text.Shaders[1] = textVertex;
                    UIWorldCropShaderContract.TrySelectFragment(text,
                        out _).ShouldBeFalse();
                }
                finally { text.Destroy(true); }
            }
            finally { material.Destroy(true); }
        }
        finally
        {
            clipped?.Destroy(true);
            custom.Destroy(true);
            canonical.Destroy(true);
        }
    }

    [Test]
    public void DirectWorldChildFreezesNestedCropAndCanonicalShader()
    {
        using IDisposable snippets = _shaderScope.RegisterCanonicalCropSnippets();
        using IDisposable materialTarget =
            RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
                EngineMaterialConstructionTarget.DesktopGlsl);
        UICanvasTransform canvasTransform = new()
        {
            Width = 100, Height = 100,
            DrawSpace = ECanvasDrawSpace.World
        };
        SceneNode root = new("Clipped world publication", canvasTransform)
        {
            IsActiveSelf = false
        };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            canvas.PreferOffscreenRenderingForNonScreenSpaces = false;
            SceneNode parent = new(root) { Name = "Clip parent" };
            UIBoundableTransform parentTransform =
                parent.SetTransform<UIBoundableTransform>();
            parent.AddComponent<UIMaterialComponent>()!.ClipToBounds = true;
            UIMaterialComponent child = Quad(parent, "World child", 1);
            child.RenderInfo3D.IsVisible = true;
            child.RenderInfo3D.CastsShadows = false;
            child.RenderPass = (int)EDefaultRenderPass.TransparentForward;
            root.IsActiveSelf = true;
            Apply(canvasTransform, new Vector2(100), Vector2.Zero);
            Apply(parentTransform, new Vector2(40), new Vector2(20));
            Apply(child.BoundableTransform, new Vector2(24), new Vector2(30));
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            parentTransform.RefreshAxisAlignedRegion();
            UIRenderRecord record = child.CapturePublicationRenderRecord(
                new UILayoutNodeOutput
                {
                    ActualSize = new Vector2(24), Visible = true
                });
            record.CaptureFailure.ShouldBeNull();
            record.FrozenWorldCommand.ShouldNotBeNull();
            record.Crop.ShouldNotBeNull();
            record.Payload.ShouldNotBeNull();
            record.Payload!.WorldCrop!.Value.Region.ShouldBe(record.Crop.Value);
            record.Payload.WorldCropFragmentShader.ShouldNotBeNull();
            UIWorldCropShaderContract.IsCanonicalClippedFragment(
                record.Payload.WorldCropFragmentShader!).ShouldBeTrue();
            record.Payload.WorldCrop!.Value.ModelToCanvas.M41.ShouldBe(
                record.WorldMatrix.M41);

            XRShader unsupportedShader = new(EShaderType.Fragment,
                "#version 460\nlayout(location=0) out vec4 OutColor;\n" +
                "void main() { OutColor = vec4(1.0); }\n");
            XRMaterial unsupported = new(Array.Empty<ShaderVar>(),
                unsupportedShader);
            try
            {
                child.Material = unsupported;
                UIRenderRecord rejected = child.CapturePublicationRenderRecord(
                    new UILayoutNodeOutput
                    {
                        ActualSize = new Vector2(24), Visible = true
                    });
                rejected.CaptureFailure.ShouldNotBeNull();
                rejected.CaptureFailure!.ShouldContain(
                    "WorldCropShaderRequired");
                rejected.FrozenWorldCommand.ShouldBeNull();
                child.RenderInfo3D.IsVisible.ShouldBeTrue();
                child.RenderInfo3D.CastsShadows.ShouldBeFalse();
            }
            finally
            {
                child.Material = null;
                unsupported.Destroy(true);
                unsupportedShader.Destroy(true);
            }
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void PaintChangesKeepHitGenerationButEligibilityChangesReplaceIt()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication hit revisions", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            var button = root.NewChild("Button").AddComponent<UIVirtualButtonComponent>()!;
            button.BoundableTransform.Width = 20;
            button.BoundableTransform.Height = 20;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            ulong hitGeneration = first.HitGeneration;
            first.Hits.Length.ShouldBe(1);

            button.IsSelected = true;
            Capture(canvas);
            UIPublication painted = Candidate(canvas).ShouldNotBeNull();
            painted.Generation.ShouldBeGreaterThan(first.Generation);
            painted.HitGeneration.ShouldBe(hitGeneration);

            button.IsEnabled = false;
            Capture(canvas);
            UIPublication disabled = Candidate(canvas).ShouldNotBeNull();
            disabled.HitGeneration.ShouldBeGreaterThan(hitGeneration);
            disabled.Hits.Length.ShouldBe(1);
            disabled.Hits[0].InteractionRevision.ShouldBe(button.InteractionRevision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ThrowingObserversCannotHideCommittedHitAndTransformChanges()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication invalidation after notification failure", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            root.AddComponent<UICanvasComponent>();
            UIVirtualButtonComponent button = root.NewChild("Button")
                .AddComponent<UIVirtualButtonComponent>()!;
            long before = transform.ChangeJournal.Revision;
            button.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UIInteractableComponent.IsTabStop))
                    throw new InvalidOperationException("Observer failed after commit.");
            };
            Should.Throw<InvalidOperationException>(() => button.IsTabStop = false);
            button.IsTabStop.ShouldBeFalse();
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(before);

            before = transform.ChangeJournal.Revision;
            button.RenderInfo2D.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(button.RenderInfo2D.LayerIndex))
                    throw new InvalidOperationException("Observer failed after commit.");
            };
            Should.Throw<InvalidOperationException>(() => button.RenderInfo2D.LayerIndex = 3);
            button.RenderInfo2D.LayerIndex.ShouldBe(3);
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(before);

            before = transform.ChangeJournal.Revision;
            button.BoundableTransform.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITransform.Scale))
                    throw new InvalidOperationException("Observer failed after commit.");
            };
            Matrix4x4 oldWorld = button.BoundableTransform.WorldMatrix;
            Matrix4x4 oldInverse = button.BoundableTransform.InverseWorldMatrix;
            Should.Throw<InvalidOperationException>(() =>
                button.BoundableTransform.Scale = new Vector3(2));
            button.BoundableTransform.Scale.ShouldBe(new Vector3(2));
            button.BoundableTransform.IsLocalMatrixDirty.ShouldBeTrue();
            button.BoundableTransform.IsWorldMatrixDirty.ShouldBeTrue();
            transform.IsLayoutInvalidated.ShouldBeTrue();
            transform.RecalculateMatrixHierarchyImmediate();
            button.BoundableTransform.WorldMatrix.ShouldNotBe(oldWorld);
            button.BoundableTransform.InverseWorldMatrix.ShouldNotBe(oldInverse);
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(before);

            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            transform.IsLayoutInvalidated.ShouldBeFalse();
            button.BoundableTransform.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UITransform.Visibility))
                    throw new InvalidOperationException("Observer failed after commit.");
            };
            Should.Throw<InvalidOperationException>(() =>
                button.BoundableTransform.Visibility = EVisibility.Hidden);
            transform.IsLayoutInvalidated.ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void OpenTransactionCannotPublishItsPartialHitState()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Open publication transaction", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPointerMetadataProbe probe = root.NewChild("Pointer")
                .AddComponent<UIPointerMetadataProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            int captures = 0;
            probe.OnCapture = () => captures++;
            object completed = new();
            using (transform.ChangeJournal.BeginTransaction())
            {
                probe.Metadata = completed;
                probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
                Capture(canvas);
                Candidate(canvas).ShouldBeNull();
                captures.ShouldBe(0);
            }
            Capture(canvas);
            Candidate(canvas).ShouldNotBeNull().Hits[0].PointerGeometry.Metadata
                .ShouldBeSameAs(completed);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CaptureCallbackThatOpensTransactionCannotReplaceCommittedPacket()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication transaction callback", transform);
        root.SetWorldContext(new TestWorldContext());
        IDisposable? pending = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPointerMetadataProbe probe = root.NewChild("Pointer")
                .AddComponent<UIPointerMetadataProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            probe.Metadata = new object();
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
            probe.OnCapture = () =>
            {
                pending = transform.ChangeJournal.BeginTransaction();
                probe.Metadata = new object();
                probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
            };
            Capture(canvas);
            pending.ShouldNotBeNull();
            Candidate(canvas).ShouldBeSameAs(first);
            probe.OnCapture = null;
            pending!.Dispose();
            pending = null;
            Capture(canvas);
            Candidate(canvas).ShouldNotBeNull().ShouldNotBeSameAs(first);
        }
        finally { pending?.Dispose(); root.Destroy(true); }
    }

    [Test]
    public void HeldHitPacketKeepsItsPointerMetadataAfterNewCapture()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Pointer metadata publication", transform);
        root.SetWorldContext(new TestWorldContext());
        UIPublication? held = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPointerMetadataProbe probe = root.NewChild("Pointer")
                .AddComponent<UIPointerMetadataProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 30;
            probe.BoundableTransform.MinAnchor = Vector2.Zero;
            probe.BoundableTransform.MaxAnchor = Vector2.Zero;
            object original = new();
            probe.Metadata = original;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            Capture(canvas);
            held = Candidate(canvas).ShouldNotBeNull();
            held.Retain();
            probe.AcceptedSize.ShouldBe(new Vector2(20, 30));
            held.Hits[0].PointerGeometry.Metadata.ShouldBeSameAs(original);

            object replacement = new();
            probe.Metadata = replacement;
            probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
            Capture(canvas);
            UIPublication next = Candidate(canvas).ShouldNotBeNull();
            next.ShouldNotBeSameAs(held);
            next.Hits[0].PointerGeometry.Metadata.ShouldBeSameAs(replacement);
            held.Hits[0].PointerGeometry.Metadata.ShouldBeSameAs(original);
            held.Hits[0].PointerGeometry.LocalSize.ShouldBe(new Vector2(20, 30));
        }
        finally { held?.Release(); root.Destroy(true); }
    }

    [Test]
    public void PointerMetadataCallbackCannotPublishAMixedGeneration()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Pointer metadata reentry", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPointerMetadataProbe probe = root.NewChild("Pointer")
                .AddComponent<UIPointerMetadataProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            probe.Metadata = new object();
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
            probe.OnCapture = () => probe.BoundableTransform.RecordUIChange(UIChangeDomain.HitTest);
            Capture(canvas);
            Candidate(canvas).ShouldBeSameAs(first);

            probe.OnCapture = null;
            Capture(canvas);
            UIPublication next = Candidate(canvas).ShouldNotBeNull();
            next.ShouldNotBeSameAs(first);
            next.ChangeRevision.ShouldBe(transform.ChangeJournal.Revision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CaptureMutationRejectsMixedCandidateAndCleanRetrySucceeds()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication mutation", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPublicationMutationProbe probe = root.NewChild("Probe")
                .AddComponent<UIPublicationMutationProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();

            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            probe.CaptureCount.ShouldBeGreaterThan(0);

            probe.BoundableTransform.RecordUIChange(UIChangeDomain.Visual);
            probe.MutateDuringCapture = true;
            Capture(canvas);

            Candidate(canvas).ShouldBeSameAs(first);
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(first.ChangeRevision);

            probe.MutateDuringCapture = false;
            Capture(canvas);
            UIPublication accepted = Candidate(canvas).ShouldNotBeNull();
            accepted.ShouldNotBeSameAs(first);
            accepted.ChangeRevision.ShouldBe(transform.ChangeJournal.Revision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CaptureInheritsCompositionOwnerAndNestedLayerOverridesIt()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Layer inheritance", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            root.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.StaticChrome;
            UIPublicationMutationProbe chrome = root.NewChild("Chrome").AddComponent<UIPublicationMutationProbe>()!;
            chrome.BoundableTransform.Width = 20;
            chrome.BoundableTransform.Height = 20;
            UIPublicationMutationProbe overlay = root.NewChild("Overlay").AddComponent<UIPublicationMutationProbe>()!;
            overlay.BoundableTransform.Width = 20;
            overlay.BoundableTransform.Height = 20;
            overlay.SceneNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            Capture(canvas);
            UIPublication publication = Candidate(canvas).ShouldNotBeNull();
            publication.Renders.Length.ShouldBe(2);
            foreach (ref readonly UIRenderRecord record in publication.Renders)
                record.CompositionOwner.ShouldBe(record.Identity == chrome.UITransform.ElementIdentity
                    ? new UICompositionOwner(transform.ElementIdentity, EUICompositionLayerKind.StaticChrome)
                    : new UICompositionOwner(overlay.UITransform.ElementIdentity, EUICompositionLayerKind.Overlay));
            publication.CompositionSegments.Length.ShouldBe(2);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedClipsStayFrozenAcrossBatchedAndFallbackPainterOrder(bool offscreen)
    {
        using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        UICanvasTransform canvasTransform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Clipped publication", canvasTransform) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
        SceneNode outerNode = new(root) { Name = "Outer" };
        UIBoundableTransform outer = outerNode.SetTransform<UIBoundableTransform>();
        outerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;
        SceneNode innerNode = new(outerNode) { Name = "Inner" };
        UIBoundableTransform inner = innerNode.SetTransform<UIBoundableTransform>();
        innerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;
        UIMaterialComponent first = Quad(innerNode, "First batch", 1);
        UIMaterialComponent fallback = Quad(innerNode, "Fallback", 2);
        UIMaterialComponent third = Quad(innerNode, "Third batch", 3);
        UIMaterialComponent outerLast = Quad(outerNode, "Outer batch", 4);
        XRMaterial? fallbackMaterial = null;
        UIPublication? publication = null;
        try
        {
            if (offscreen)
            {
                canvasTransform.DrawSpace = ECanvasDrawSpace.World;
                canvas.PreferOffscreenRenderingForNonScreenSpaces = true;
            }
            root.IsActiveSelf = true;
            foreach (UIMaterialComponent component in new[] { first, fallback, third, outerLast })
                component.SetBatchSolidColor(Vector4.One);
            // The repository's default legacy shader uses host-file snippets. A self-contained
            // custom material exercises the actual frozen fallback draw in headless tests.
            fallbackMaterial = new XRMaterial(Array.Empty<ShaderVar>(), new XRShader(EShaderType.Fragment,
                "#version 450\nlayout(location=0) out vec4 FragColor;\nvoid main() { FragColor = vec4(1.0); }"));
            fallback.Material = fallbackMaterial;
            fallback.DisableBatching = true;
            Apply(canvasTransform, new Vector2(100), Vector2.Zero);
            Apply(outer, new Vector2(80), new Vector2(10));
            Apply(inner, new Vector2(40), new Vector2(20));
            foreach (UIMaterialComponent component in new[] { first, fallback, third, outerLast })
                Apply(component.BoundableTransform, new Vector2(24), Vector2.Zero);
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            outer.RefreshAxisAlignedRegion();
            inner.RefreshAxisAlignedRegion();

            UIRenderRecord[] records = new[] { first, fallback, third, outerLast }
                .Select(component => component.CapturePublicationRenderRecord(new UILayoutNodeOutput
                {
                    ActualSize = new Vector2(24), Visible = true
                })).ToArray();
            Assert.That(records.All(record => record.Visible && record.CaptureFailure is null), Is.True,
                string.Join("; ", records.Select((record, index) =>
                    $"{index}: visible={record.Visible}, active={new[] { first, fallback, third, outerLast }[index].IsActiveInHierarchy}, renderInfo={record.RenderInfo.IsVisible}, " +
                    $"batch={record.SupportsBatch}, failure={record.CaptureFailure ?? "<none>"}")));
            records[0].SupportsBatch.ShouldBeTrue();
            records[1].SupportsBatch.ShouldBeFalse();
            records[1].FrozenCommand.ShouldNotBeNull();
            records[2].SupportsBatch.ShouldBeTrue();
            records[3].SupportsBatch.ShouldBeTrue();
            BoundingRectangle? resolvedInner = UIClipRegion.ResolveCrop(first.BoundableTransform, false);
            BoundingRectangle? resolvedOuter = UIClipRegion.ResolveCrop(outerLast.BoundableTransform, false);
            resolvedInner.ShouldNotBeNull();
            resolvedOuter.ShouldNotBeNull();
            BoundingRectangle innerCrop = resolvedInner.Value;
            BoundingRectangle outerCrop = resolvedOuter.Value;
            innerCrop.ShouldNotBe(outerCrop);
            for (int index = 0; index < 3; index++) records[index].Crop.ShouldBe(innerCrop);
            records[3].Crop.ShouldBe(outerCrop);
            records[1].FrozenCommand!.WorldCropRegion.ShouldBe(innerCrop);
            records[1].FrozenCommand!.UICanvasCropSpace.ShouldBe(canvasTransform.GetActualBounds());

            publication = new UIPublication(1, 1, 1, 1, 1,
                ReadOnlyMemory<UILayoutNodeOutput>.Empty, ReadOnlyMemory<UIElementIdentity>.Empty,
                [], records, canvasTransform.GetActualBounds(), Matrix4x4.Identity, Matrix4x4.Identity,
                authoringDrawSpace: offscreen ? ECanvasDrawSpace.World : ECanvasDrawSpace.Screen,
                authoringOffscreen: offscreen);
            Apply(inner, new Vector2(40), new Vector2(25));
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            inner.RefreshAxisAlignedRegion();
            UIClipRegion.ResolveCrop(first.BoundableTransform, false).ShouldNotBe(innerCrop);
            publication.Renders[0].Crop.ShouldBe(innerCrop);
            publication.Renders[1].FrozenCommand!.WorldCropRegion.ShouldBe(innerCrop);
            const int pass = (int)EDefaultRenderPass.TransparentForward;
            RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
            {
                [pass] = new NearToFarRenderCommandSorter()
            });
            using UIBatchCollector collector = new();
            List<UIElementIdentity> fallbackOrder = [];
            collector.CollectPublishedRuns(publication, commands, record =>
            {
                fallbackOrder.Add(record.Identity);
                commands.AddCPU(record.FrozenCommand!);
            });
            fallbackOrder.ShouldBe([fallback.UITransform.ElementIdentity]);
            commands.GetUpdatingPassCommandCount(pass).ShouldBe(4);
            var updating = (Dictionary<int, ICollection<RenderCommand>>)typeof(RenderCommandCollection)
                .GetField("_updatingPasses", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(commands)!;
            updating[pass].Cast<RenderCommand2D>().Select(command => command.ZIndex)
                .ShouldBe([1, 2, 3, 4]);
            UIBatchCollector.MapCanvasCropToRaster(innerCrop, canvasTransform.GetActualBounds(),
                    new BoundingRectangle(200, 300, 200, 200))
                .ShouldNotBe(UIBatchCollector.MapCanvasCropToRaster(outerCrop,
                    canvasTransform.GetActualBounds(), new BoundingRectangle(200, 300, 200, 200)));
        }
        finally
        {
            publication?.Release();
            root.Destroy(true);
            fallbackMaterial?.Destroy(true);
        }
    }

    private static UIMaterialComponent Quad(SceneNode parent, string name, int zIndex)
    {
        SceneNode node = new(parent) { Name = name };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        component.RenderCommand2D.ZIndex = zIndex;
        return component;
    }

    private static void Apply(UIBoundableTransform transform, Vector2 size, Vector2 translation)
    {
        transform.ApplyCalculatedLayout(new UILayoutNodeOutput
        {
            ActualSize = size, LocalTranslation = translation, Visible = true
        }, Vector2.Zero);
        transform.RefreshAxisAlignedRegion();
    }

    private static void Capture(UICanvasComponent canvas)
        => typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
}
