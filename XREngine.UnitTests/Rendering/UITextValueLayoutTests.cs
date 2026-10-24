using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextValueLayoutTests
{
    private UITestShaderScope _scope = null!;
    [SetUp] public void InstallHost() => _scope = new UITestShaderScope();
    [TearDown] public void RestoreHost() => _scope.Dispose();

    private static FontGlyphSet CreateFont()
        => new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8), Vector2.Zero),
            },
            Atlas = new XRTexture2D(8, 8, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true),
            AtlasType = EFontAtlasType.Mtsdf,
            LayoutEmSize = 8,
        };

    [Test]
    public void ShapedInputReusesExactSettingsAndInvalidatesLanguageFeaturesSizeAndDpi()
    {
        FontGlyphSet font = CreateFont();
        UIRuneMetrics metric = new(Vector2.Zero, new(8, 8), 9, new(0, 0, 8, 8));
        font.InstallRuntimeShapingFace(new UIFontFaceSnapshot([1], 0, 8,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric }));
        XREngine.Scene.SceneNode root = new("Shaping key", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        XREngine.Scene.SceneNode child = new(root) { Name = "Label" };
        child.SetTransform<UIBoundableTransform>();
        IUITextShapingBackend? original = UITextShapingBackendRegistry.Current;
        try
        {
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ => { });
            UITextComponent text = child.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            text.UseShapedText = true;
            UITextLayoutInput first = text.CaptureLayoutInput().ShouldNotBeNull();
            text.CaptureLayoutInput().ShouldBeSameAs(first);

            text.ShapeLanguage = "fr-FR";
            UITextLayoutInput language = text.CaptureLayoutInput().ShouldNotBeNull();
            language.ShouldNotBeSameAs(first);
            language.ShapingRequest!.Language.ShouldBe("fr-FR");
            text.OpenTypeFeatures = "liga=0";
            UITextLayoutInput features = text.CaptureLayoutInput().ShouldNotBeNull();
            features.ShouldNotBeSameAs(language);
            features.ShapingRequest!.Features.ShouldContain("liga=0");
            text.FontSize = 10;
            UITextLayoutInput size = text.CaptureLayoutInput().ShouldNotBeNull();
            size.ShouldNotBeSameAs(features);
            ((UICanvasTransform)root.Transform).DpiScale = 2;
            UITextLayoutInput dpi = text.CaptureLayoutInput().ShouldNotBeNull();
            dpi.ShouldNotBeSameAs(size);
            dpi.DpiScale.ShouldBe(2);
            text.CaptureLayoutInput().ShouldBeSameAs(dpi);
            text.Text = ".";
            UITextLayoutInput neutral = text.CaptureLayoutInput().ShouldNotBeNull();
            text.ShapeDirection = UITextDirection.RightToLeft;
            UITextLayoutInput direction = text.CaptureLayoutInput().ShouldNotBeNull();
            direction.ShouldNotBeSameAs(neutral);
            direction.ShapingRequest!.Direction.ShouldBe(UITextDirection.RightToLeft);
        }
        finally
        {
            UITextShapingBackendRegistry.Current = original;
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void AcceptedLayoutNotificationWaitsForCommitAndRejectsStaleResults()
    {
        FontGlyphSet font = CreateFont();
        XREngine.Scene.SceneNode root = new("Accepted text", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 40;
        XREngine.Scene.SceneNode child = new(root) { Name = "Label" };
        UIBoundableTransform bounds = child.SetTransform<UIBoundableTransform>();
        bounds.Width = 80;
        bounds.Height = 20;
        bounds.MaxAnchor = Vector2.Zero;
        try
        {
            UITextComponent text = child.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            int notifications = 0;
            UITextLayoutResult? seen = null;
            text.AcceptedLayoutChanged += (_, result, _) =>
            {
                notifications++;
                seen = result;
            };
            text.TryGetAcceptedLayoutResult(out _, out _).ShouldBeFalse();

            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            notifications.ShouldBe(1);
            text.TryGetAcceptedLayoutResult(out UITextLayoutResult current, out Vector2 actualSize)
                .ShouldBeTrue();
            current.ShouldBeSameAs(seen);
            text.NotifyAcceptedLayout(current, actualSize);
            notifications.ShouldBe(1);
            text.OpenTypeFeatures = "liga=0";
            text.TryGetAcceptedLayoutResult(out UITextLayoutResult unshaped, out _).ShouldBeTrue();
            unshaped.ShouldBeSameAs(current);

            bool rejected = false;
            canvas.LayoutingFinished += InvalidateBeforeCommit;
            text.Text = "AA";
            bounds.InvalidateMeasure();
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() =>
            {
                canvas.PumpLayoutResults();
                return rejected;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            canvas.LayoutingFinished -= InvalidateBeforeCommit;
            notifications.ShouldBe(1);
            text.TryGetAcceptedLayoutResult(out _, out _).ShouldBeFalse();

            SpinWait.SpinUntil(() =>
            {
                canvas.UpdateLayoutAsync();
                return canvas.PumpLayoutResults();
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            notifications.ShouldBe(2);
            text.TryGetAcceptedLayoutResult(out UITextLayoutResult latest, out Vector2 latestSize).ShouldBeTrue();
            latest.ShouldNotBeSameAs(current);

            bool canceled = false;
            int lateNotifications = 0;
            text.AcceptedLayoutChanged += (_, _, _) =>
            {
                canceled = true;
                canvas.CancelLayoutJob();
            };
            text.AcceptedLayoutChanged += (_, _, _) => lateNotifications++;
            text.Text = "AAAA";
            bounds.InvalidateMeasure();
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() =>
            {
                canvas.PumpLayoutResults();
                return canceled;
            }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
            lateNotifications.ShouldBe(0);
            text.TryGetAcceptedLayoutResult(out _, out _).ShouldBeFalse();

            child.Destroy(true);
            text.NotifyAcceptedLayout(latest, latestSize);
            notifications.ShouldBe(3);
            text.TryGetAcceptedLayoutResult(out _, out _).ShouldBeFalse();

            void InvalidateBeforeCommit(UICanvasTransform _)
            {
                if (rejected)
                    return;
                rejected = true;
                text.Text = "AAA";
                bounds.InvalidateMeasure();
            }
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void OneAcceptedTextListenerCanInvalidateCanvasWithoutStarvingAnother()
    {
        FontGlyphSet font = CreateFont();
        XREngine.Scene.SceneNode root = new("Two accepted labels", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 40;
        try
        {
            XREngine.Scene.SceneNode firstNode = new(root) { Name = "First" };
            UIBoundableTransform firstBounds = firstNode.SetTransform<UIBoundableTransform>();
            firstBounds.Width = 80;
            firstBounds.Height = 20;
            firstBounds.MaxAnchor = Vector2.Zero;
            UITextComponent first = firstNode.AddComponent<UITextComponent>()!;
            first.Font = font;
            first.FontSize = 8;
            first.Text = "A";

            XREngine.Scene.SceneNode secondNode = new(root) { Name = "Second" };
            UIBoundableTransform secondBounds = secondNode.SetTransform<UIBoundableTransform>();
            secondBounds.Width = 80;
            secondBounds.Height = 20;
            secondBounds.MaxAnchor = Vector2.Zero;
            UITextComponent second = secondNode.AddComponent<UITextComponent>()!;
            second.Font = font;
            second.FontSize = 8;
            second.Text = "A";

            int secondNotifications = 0;
            first.AcceptedLayoutChanged += (_, _, _) => firstBounds.Height = 21;
            second.AcceptedLayoutChanged += (sender, result, size) =>
            {
                canvas.IsLayoutInvalidated.ShouldBeTrue();
                sender.TryGetAcceptedLayoutResult(out UITextLayoutResult current, out Vector2 actual)
                    .ShouldBeTrue();
                current.ShouldBeSameAs(result);
                actual.ShouldBe(size);
                secondNotifications++;
            };

            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            secondNotifications.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void ShapingRunsOnlyAfterOwnerCaptureAndBackendReplacementInvalidatesInput()
    {
        FontGlyphSet font = CreateFont();
        UIRuneMetrics metric = new(Vector2.Zero, new(8, 8), 9, new(0, 0, 8, 8));
        font.InstallRuntimeShapingFace(new UIFontFaceSnapshot([1], 0, 8,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric }));
        XREngine.Scene.SceneNode root = new("Shaping capture", new UIBoundableTransform())
        {
            IsActiveSelf = false
        };
        IUITextShapingBackend? original = UITextShapingBackendRegistry.Current;
        try
        {
            UITextComponent component = root.AddComponent<UITextComponent>()!;
            component.Font = font;
            component.FontSize = 8;
            component.Text = "A";
            int shapeCount = 0;
            int captureThread = Environment.CurrentManagedThreadId;
            int shapeThread = 0;
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ =>
            {
                shapeCount++;
                shapeThread = Environment.CurrentManagedThreadId;
            });
            component.UseShapedText = true;
            UITextLayoutInput captured = component.CaptureLayoutInput().ShouldNotBeNull();
            captured.ShapingRequest.ShouldNotBeNull();
            shapeCount.ShouldBe(0);
            List<UITransform> targets = [];
            UILayoutSnapshot snapshot = UILayoutSnapshot.CaptureRoot(root.Transform as UITransform ??
                throw new InvalidOperationException(), new Vector2(100, 30), targets);
            UILayoutWorkerResult result = Task.Run(() =>
            {
                UILayoutSnapshot resolved = snapshot.ResolveShaping(
                    (IUITextWorkerShapingBackend)UITextShapingBackendRegistry.Current!,
                    null, null, CancellationToken.None);
                return new UILayoutWorkerResult(resolved,
                    UILayoutValueKernel.RunPaged(resolved, null, null));
            }).GetAwaiter().GetResult();
            result.ResolvedInput.Nodes[0].TextInput!.ShapedRun.ShouldNotBeNull();
            result.Output[0].TextResult.ShouldNotBeNull().Glyphs.Span[0].GlyphId.ShouldBe(51u);
            shapeCount.ShouldBe(1);
            shapeThread.ShouldNotBe(captureThread);
            UILayoutSnapshot reused = snapshot.ResolveShaping(
                (IUITextWorkerShapingBackend)UITextShapingBackendRegistry.Current!,
                snapshot, result.ResolvedInput, CancellationToken.None);
            reused.Nodes[0].TextInput!.ShapedRun.ShouldBeSameAs(
                result.ResolvedInput.Nodes[0].TextInput!.ShapedRun);
            reused.Nodes.SharesPageWith(result.ResolvedInput.Nodes, 0).ShouldBeTrue();
            shapeCount.ShouldBe(1);
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ => { });
            component.CaptureLayoutInput().ShouldNotBeSameAs(captured);
            UITextShapingBackendRegistry.Current = null;
            Should.Throw<NotSupportedException>(() => component.CaptureLayoutInput())
                .Message.ShouldContain("UI.WorkerShapingBackendUnavailable");
        }
        finally
        {
            UITextShapingBackendRegistry.Current = original;
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    [Test]
    public void BackendReplacementWithEmptyJournalRecalculatesTextOutput()
    {
        FontGlyphSet font = CreateFont();
        UIRuneMetrics metric = new(Vector2.Zero, new(8, 8), 9, new(0, 0, 8, 8));
        font.InstallRuntimeShapingFace(new UIFontFaceSnapshot([1], 0, 8,
            new Dictionary<uint, UIRuneMetrics> { [51] = metric }));
        XREngine.Scene.SceneNode root = new("Backend generation canvas", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        XREngine.Scene.SceneNode child = new(root) { Name = "Label" };
        child.SetTransform<UIBoundableTransform>();
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 40;
        IUITextShapingBackend? original = UITextShapingBackendRegistry.Current;
        try
        {
            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ => { }, 9);
            UITextComponent text = child.AddComponent<UITextComponent>()!;
            text.Font = font;
            text.FontSize = 8;
            text.Text = "A";
            text.UseShapedText = true;
            List<UITransform> targets = [];
            UILayoutSnapshot accepted = UILayoutSnapshot.Capture(canvas, targets);
            UILayoutOutputPageView acceptedOutput = UILayoutValueKernel.RunPaged(accepted, null, null);
            acceptedOutput[1].TextResult.ShouldNotBeNull().IntrinsicSize.X.ShouldBe(9f);
            UIElementChange[] journal = new UIElementChange[canvas.ChangeJournal.Capacity];
            canvas.ChangeJournal.TryDrain(journal, out _).ShouldBeTrue();

            UITextShapingBackendRegistry.Current = new UITextMutatingShapingBackend(_ => { }, 17);
            List<UITransform> latestTargets = [];
            UILayoutSnapshot latest = UILayoutSnapshot.CaptureIncremental(canvas,
                accepted, targets, latestTargets, Array.Empty<UIElementChange>());
            UILayoutOutputPageView changed = UILayoutValueKernel.RunPaged(latest, accepted, acceptedOutput);

            latest.MeasureVersions[1].ShouldBe(accepted.MeasureVersions[1]);
            latest.ArrangeVersions[1].ShouldBe(accepted.ArrangeVersions[1]);
            latest.IsDirty(1).ShouldBeTrue();
            changed[1].TextResult.ShouldNotBeNull().IntrinsicSize.X.ShouldBe(17f);
        }
        finally
        {
            UITextShapingBackendRegistry.Current = original;
            root.Destroy(true);
            font.Atlas?.Destroy(true);
            font.Destroy(true);
        }
    }

    private static void Destroy(UIText text, FontGlyphSet font)
    {
        text.OnPoolableDestroyed();
        font.Atlas?.Destroy(true);
        font.Destroy(true);
    }

    [Test]
    public void PaintAndPlacementReuseAcceptedLayoutAndGlyphRevisions()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "AA" };
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            UITextLayoutInput input = text.CaptureLayoutInput().ShouldNotBeNull();
            XRDataBuffer transforms = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            XRDataBuffer uvs = text.Mesh.Buffers["GlyphTexCoordsBuffer"];
            ulong transformRevision = transforms.Revision;
            ulong uvRevision = uvs.Revision;
            Vector4 originalTransform = original.Glyphs.Span[0].Transform;
            text.Color = new(1.0f, 0.0f, 0.0f, 1.0f);
            text.OutlineThickness = 0.5f;
            text.LocalTranslation = Vector3.One;
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldBeSameAs(original);
            text.CaptureLayoutInput().ShouldBeSameAs(input);
            transforms.Revision.ShouldBe(transformRevision);
            uvs.Revision.ShouldBe(uvRevision);
            font.DistanceRange = 7;
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldBeSameAs(original);
            text.Mesh.Material!.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(7);
            var glyphs = font.Glyphs.ShouldNotBeNull();
            glyphs["A"].Size = new Vector2(4, 8);
            font.Glyphs = glyphs;
            text.LayoutResult.ShouldBeSameAs(original);
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldNotBeSameAs(original);
            original.Glyphs.Span[0].Transform.ShouldBe(originalTransform);
            transforms.Revision.ShouldBeGreaterThan(transformRevision);
        }
        finally { Destroy(text, font); }
    }

    [Test]
    public void AnimationUsesAcceptedOverridesAndOnlyCommitsChangedUsedSpans()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "AA", AnimatableTransforms = true };
        try
        {
            text.PushGlyphDataIfNeeded();
            XRDataBuffer transforms = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            XRDataBuffer uvs = text.Mesh.Buffers["GlyphTexCoordsBuffer"];
            XRDataBuffer rotations = text.Mesh.Buffers["GlyphRotationsBuffer"];
            Vector4 first = transforms.Get<Vector4>(0) ?? throw new InvalidOperationException("Missing glyph transform.");
            Vector4 second = transforms.Get<Vector4>(16) ?? throw new InvalidOperationException("Missing glyph transform.");
            ulong uvRevision = uvs.Revision;
            text.GlyphRelativeTransforms[1] = (new Vector2(2, 3), Vector2.One, 0.25f);
            text.MarkGlyphTransformsChanged();
            text.GlyphRelativeTransforms[1] = (new Vector2(99), Vector2.One, 1.0f);
            text.PushGlyphDataIfNeeded();
            transforms.Get<Vector4>(0).ShouldBe(first);
            transforms.Get<Vector4>(16).ShouldBe(second + new Vector4(2, 3, 0, 0));
            rotations.Get<float>(4).ShouldBe(0.25f);
            uvs.Revision.ShouldBe(uvRevision);
            var transformRange = transforms.GetDirtyRangesSnapshot().ShouldHaveSingleItem();
            transformRange.OffsetBytes.ShouldBe(16u);
            transformRange.LengthBytes.ShouldBe(16u);
            var rotationRange = rotations.GetDirtyRangesSnapshot().ShouldHaveSingleItem();
            rotationRange.OffsetBytes.ShouldBe(4u);
            rotationRange.LengthBytes.ShouldBe(4u);
            ulong revision = transforms.Revision;
            for (int index = 0; index < 10; index++)
                text.PushGlyphDataIfNeeded();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
                text.PushGlyphDataIfNeeded();
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
            transforms.Revision.ShouldBe(revision);
            text.AnimatableTransforms = false;
            text.PushGlyphDataIfNeeded();
            text.Mesh!.Buffers.ContainsKey("GlyphRotationsBuffer").ShouldBeFalse();
        }
        finally { Destroy(text, font); }
    }

    [Test]
    public void FontNotificationsWaitForRenderOwnerAndCannotRestoreAnOlderTextCapture()
    {
        FontGlyphSet font = CreateFont();
        XRTexture2D originalAtlas = font.Atlas!;
        XRTexture2D replacementAtlas = new(16, 16, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true);
        UIText text = new() { Font = font, FontSize = 8, Text = "A" };
        using ManualResetEventSlim ownerReady = new();
        using ManualResetEventSlim consume = new();
        Task<UITextLayoutResult>? owner = null;
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            XRMaterial material = text.Mesh!.Material!;
            owner = Task.Run(() =>
            {
                ownerReady.Set();
                consume.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                text.Text = "AA";
                text.PushGlyphDataIfNeeded();
                return text.LayoutResult.ShouldNotBeNull();
            });
            ownerReady.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            font.Atlas = replacementAtlas;
            font.DistanceRange = 6;
            // The notifying thread cannot install a new atlas beside the accepted old UVs.
            text.LayoutResult.ShouldBeSameAs(original);
            material.Textures[0].ShouldBeSameAs(originalAtlas);
            consume.Set();
            UITextLayoutResult accepted = owner.GetAwaiter().GetResult();
            accepted.Glyphs.Length.ShouldBe(2);
            text.CaptureLayoutInput().ShouldNotBeNull().Text.ShouldBe("AA");
            material.Textures[0].ShouldBeSameAs(replacementAtlas);
            accepted.Glyphs.Span[0].Uvs.Z.ShouldBe(0.5f);
            font.DistanceRange = 7;
            // An equal Text assignment cannot hide or undo a pending font refresh.
            text.Text = "AA";
            Task.Run(text.PushGlyphDataIfNeeded).GetAwaiter().GetResult();
            text.LayoutResult.ShouldBeSameAs(accepted);
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(7);
        }
        finally
        {
            consume.Set();
            owner?.GetAwaiter().GetResult();
            Destroy(text, font);
            originalAtlas.Destroy(true);
        }
    }

    [Test]
    public void ComponentAndStandaloneRetainOneImmutableLayoutAcrossPoolAndDrawLifetimes()
    {
        FontGlyphSet font = CreateFont();
        font.Atlas!.Mipmaps[0].DataBytes = new byte[8 * 8 * 4];
        UIText standalone = new() { Font = font, FontSize = 8, Text = "AA" };
        XREngine.Scene.SceneNode root = new("Shared text layout", new UIBoundableTransform()) { IsActiveSelf = false };
        UIDrawPayload? held = null;
        try
        {
            standalone.PushGlyphDataIfNeeded();
            UITextLayoutResult shared = standalone.LayoutResult.ShouldNotBeNull();
            UITextMaterialRefreshProbe component = root.AddComponent<UITextMaterialRefreshProbe>()!;
            component.Font = font;
            component.FontSize = 8;
            component.Text = "AA";
            component.ApplyLayoutResult(shared, component.BoundableTransform.ActualSize).ShouldBeTrue();
            component.TryGetCurrentLayoutResult(out UITextLayoutResult accepted).ShouldBeTrue();
            accepted.ShouldBeSameAs(shared);
            using UIDrawPayloadBuilder builder = component.CreateBuilder();
            component.Capture(builder, out held, out string? reason).ShouldBeTrue(reason);
            held!.Retain();
            standalone.OnPoolableReleased();
            component.TryGetCurrentLayoutResult(out UITextLayoutResult afterRelease).ShouldBeTrue();
            afterRelease.ShouldBeSameAs(shared);
            component.Capture(builder, out UIDrawPayload? reused, out reason).ShouldBeTrue(reason);
            reused.ShouldBeSameAs(held);
            foreach (var binding in held.Geometry.RendererBuffers)
            {
                binding.Value.ElementCount.ShouldBe(2u);
                binding.Value.ClientSideSource!.Length.ShouldBe(32u);
            }
            root.Destroy(true);
            builder.Dispose();
            held.IsRetired.ShouldBeFalse();
            held.Geometry.FixedBuffers.All(buffer => !buffer.IsDestroyed).ShouldBeTrue();
        }
        finally
        {
            held?.Release();
            root.Destroy(true);
            Destroy(standalone, font);
        }
        held!.IsRetired.ShouldBeTrue();
        held.Geometry.FixedBuffers.All(buffer => buffer.IsDestroyed).ShouldBeTrue();
    }

    [Test]
    public void PoolReleaseDetachesFontChangesAndResetCapturesCurrentValues()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "A" };
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            XRMaterial material = text.Mesh!.Material!;
            float originalDistance = material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value;
            text.OnPoolableReleased();
            font.DistanceRange = originalDistance + 3;
            text.LayoutResult.ShouldBeNull();
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(originalDistance);
            text.OnPoolableReset();
            text.LayoutResult.ShouldNotBeNull();
            text.LayoutResult.ShouldNotBeSameAs(original);
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(originalDistance + 3);
            text.PushGlyphDataIfNeeded();
            XRDataBuffer buffer = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            buffer.ClientSideSource = new XREngine.Data.DataSource(4u);
            text._dataChanged = true;
            Should.Throw<InvalidOperationException>(text.PushGlyphDataIfNeeded).Message.ShouldContain("GlyphStoragePending");
            Should.Throw<InvalidOperationException>(text.PushGlyphDataIfNeeded);
        }
        finally { Destroy(text, font); }
    }
}
