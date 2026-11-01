using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Editor;
using XREngine.Editor.Cutscenes;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutscenePreviewHostTests
{
    private static readonly Vector3 OriginalPosition = new(7, 8, 9);

    private sealed class FakeResolver(params SceneNode[] nodes) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id)
        {
            foreach (SceneNode node in nodes)
                if (node.ID == id)
                    return node;
            return null;
        }

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static TransformTrackClip MoveClip(double seconds, float endX)
        => new()
        {
            Start = T(0),
            Duration = T(seconds),
            Position = new CutsceneVector3Curve { Times = [T(0), T(seconds)], Values = [Vector3.Zero, new Vector3(endX, 0, 0)] },
            Rotation = new CutsceneQuaternionCurve { Times = [T(0)], Values = [Quaternion.Identity] },
            Scale = new CutsceneVector3Curve { Times = [T(0)], Values = [Vector3.One] },
        };

    private static (SceneNode Node, CutsceneAsset Asset, CutscenePreviewHost Host) MakeRig(ECutsceneFinishBehavior whenFinished = ECutsceneFinishBehavior.RestoreState)
    {
        SceneNode node = new("Hero", new Transform());
        ((Transform)node.Transform).Translation = OriginalPosition;

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        TransformTrack track = new() { Name = "Move", BindingSlot = 0, WhenFinished = whenFinished };
        track.Clips.Add(MoveClip(10, 10));
        asset.Tracks.Add(track);

        return (node, asset, new CutscenePreviewHost(new FakeResolver(node)));
    }

    private static Vector3 Position(SceneNode node) => ((Transform)node.Transform).Translation;

    [Test]
    public void End_RestoresAllValues_AndLeavesTheSceneNotDirty()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        XRScene scene = new("Scene", node);
        scene.ClearDirty();

        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Scrub);
        host.Tick(0.0);
        Position(node).X.ShouldBe(5f, 1e-3f);

        host.End();

        Position(node).ShouldBe(OriginalPosition);
        scene.IsDirty.ShouldBeFalse();
        asset.IsDirty.ShouldBeFalse();
        host.IsActive.ShouldBeFalse();
    }

    [Test]
    public void End_RestoresTracksThatHaveKeepState()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig(ECutsceneFinishBehavior.KeepState);

        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Jump);
        host.Tick(0.0);
        host.End();

        Position(node).ShouldBe(OriginalPosition);
    }

    [Test]
    public void End_RestoresActivation()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        ActivationTrack activation = new() { Name = "Show", BindingSlot = 0 };
        activation.Clips.Add(new ActivationClip { Start = T(8), Duration = T(1) });
        asset.Tracks.Add(activation);
        node.IsActiveSelf = false;

        host.Begin(asset);
        host.Seek(T(8.5), ESeekIntent.Jump);
        host.Tick(0.0);
        node.IsActiveSelf.ShouldBeTrue();

        host.End();

        node.IsActiveSelf.ShouldBeFalse();
    }

    [Test]
    public void NotifyAssetEdited_RecompilesOnTheNextTick_AndKeepsThePosition()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Jump);
        host.Tick(0.0);
        int compiles = host.CompileCount;

        TransformTrackClip clip = (TransformTrackClip)asset.Tracks[0].Clips[0];
        clip.Position = new CutsceneVector3Curve { Times = [T(0), T(10)], Values = [Vector3.Zero, new Vector3(20, 0, 0)] };
        host.NotifyAssetEdited();

        // The edit waits for a frame boundary, so the scene and the compiled data do not change yet.
        host.CompileCount.ShouldBe(compiles);
        Position(node).X.ShouldBe(5f, 1e-3f);

        host.Tick(0.0);

        host.CompileCount.ShouldBe(compiles + 1);
        host.Position.ShouldBe(T(5));
        Position(node).X.ShouldBe(10f, 1e-3f);

        host.End();
        Position(node).ShouldBe(OriginalPosition);
    }

    [Test]
    public void NotifyAssetEdited_ManyCallsInOneFrame_RecompileOnce()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        int compiles = host.CompileCount;

        host.NotifyAssetEdited();
        host.NotifyAssetEdited();
        host.Tick(0.0);
        host.Tick(0.0);

        host.CompileCount.ShouldBe(compiles + 1);
        host.End();
    }

    [Test]
    public void NotifyAssetEdited_AfterTheAssetBecomesShorter_ClampsThePosition()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Seek(T(8), ESeekIntent.Jump);
        host.Tick(0.0);

        asset.Duration = T(4);
        host.NotifyAssetEdited();
        host.Tick(0.0);

        host.Position.ShouldBe(T(4));
        host.End();
    }

    [Test]
    public void NotifyAssetEdited_KeepsThePlayingStateAndTheRate()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Tick(0.0);
        host.SetRate(2.0);
        host.Play();
        host.Tick(1.0);
        host.Position.ToSeconds().ShouldBe(2.0, 1e-6);

        host.NotifyAssetEdited();
        host.Tick(0.0);
        host.State.ShouldBe(ECutsceneTransportState.Playing);
        host.Rate.ShouldBe(2.0);
        host.Position.ToSeconds().ShouldBe(2.0, 1e-6);

        host.Tick(1.0);
        host.Position.ToSeconds().ShouldBe(4.0, 1e-6);
        host.End();
    }

    [Test]
    public void NotifyAssetEdited_AfterATrackIsRemoved_RestoresItsTarget()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Jump);
        host.Tick(0.0);

        asset.Tracks.Clear();
        host.NotifyAssetEdited();
        host.Tick(0.0);

        Position(node).ShouldBe(OriginalPosition);
        host.End();
        Position(node).ShouldBe(OriginalPosition);
    }

    [Test]
    public void NotifyAssetEdited_KeepsAPendingSeek()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Seek(T(3), ESeekIntent.Jump);
        host.NotifyAssetEdited();
        host.Tick(0.0);

        host.Position.ShouldBe(T(3));
        Position(node).X.ShouldBe(3f, 1e-3f);
        host.End();
    }

    [Test]
    public void Preview_DoesNotFireEventsByDefault()
    {
        (_, _, CutscenePreviewHost host) = MakeRig();
        host.FireEventsInPreview.ShouldBeFalse();
    }

    [Test]
    public void LoopRange_WrapsPlaybackInsideTheRange()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.SetLoopRange(T(1), T(2));
        host.Seek(T(1), ESeekIntent.Jump);
        host.Tick(0.0);
        host.Play();

        host.Tick(1.5);
        host.Tick(0.0);

        host.Position.ShouldBe(T(1));
        host.State.ShouldBe(ECutsceneTransportState.Playing);
        host.End();
    }

    [Test]
    public void LoopRange_ThatEndsAtTheDuration_KeepsPlaying()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.SetLoopRange(T(9), T(10));
        host.Seek(T(9), ESeekIntent.Jump);
        host.Tick(0.0);
        host.Play();

        host.Tick(2.0);
        host.Tick(0.0);

        host.Position.ShouldBe(T(9));
        host.State.ShouldBe(ECutsceneTransportState.Playing);
        host.End();
    }

    [Test]
    public void Begin_WithAMissingBinding_ReportsADiagnostic_AndDoesNotThrow()
    {
        (_, CutsceneAsset asset, _) = MakeRig();
        CutscenePreviewHost host = new(new FakeResolver());

        host.Begin(asset);
        host.Tick(0.0);

        List<CutsceneCompileDiagnostic> diagnostics = [];
        host.CopyDiagnostics(diagnostics);
        diagnostics.Count.ShouldBeGreaterThan(0);
        host.End();
    }

    [Test]
    public void CallsWithoutAnActivePreview_DoNothing()
    {
        (_, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();

        host.Tick(1.0);
        host.Seek(T(1), ESeekIntent.Jump);
        host.NotifyAssetEdited();
        host.End();

        host.IsActive.ShouldBeFalse();
        host.Begin(asset);
        host.IsActive.ShouldBeTrue();
        host.End();
    }

    [Test]
    public void Begin_WhileActive_RestoresThePreviousPreviewFirst()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Jump);
        host.Tick(0.0);

        host.Begin(asset);

        Position(node).ShouldBe(OriginalPosition);
        host.End();
    }

    [Test]
    public void End_GivesTheViewportCameraBack()
    {
        SceneNode cameraNode = new("Shot", new Transform());
        CameraComponent shot = cameraNode.AddComponent<CameraComponent>()!;
        SceneNode editorNode = new("EditorCamera", new Transform());
        CameraComponent editorCamera = editorNode.AddComponent<CameraComponent>()!;
        XRViewport viewport = new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false,
            CameraComponent = editorCamera,
        };

        CutsceneAsset asset = new() { Duration = T(4) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = cameraNode.ID });
        CameraCutTrack track = new() { Name = "Cuts", BindingSlot = 0 };
        track.Clips.Add(new CameraCutClip { Start = T(0), Duration = T(4), CameraBindingSlot = 0 });
        asset.Tracks.Add(track);

        CutscenePreviewHost host = new(new FakeResolver(cameraNode), viewport);
        host.Begin(asset);
        host.Seek(T(1), ESeekIntent.Jump);
        host.Tick(0.0);
        viewport.CameraComponent.ShouldBe(shot);

        host.NotifyAssetEdited();
        host.Tick(0.0);
        viewport.CameraComponent.ShouldBe(shot);

        host.End();
        viewport.CameraComponent.ShouldBe(editorCamera);
        viewport.HasCameraClaim.ShouldBeFalse();
    }

    [Test]
    public void DirectorOpen_WithoutResolver_UsesTheRegisteredFactory()
    {
        (SceneNode node, CutsceneAsset asset, _) = MakeRig();
        SceneNode owner = new("Director", new Transform());
        CutsceneDirectorComponent director = owner.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;

        Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? previous = CutsceneDirectorComponent.DefaultBindingResolverFactory;
        CutsceneDirectorComponent? seen = null;
        try
        {
            CutsceneDirectorComponent.DefaultBindingResolverFactory = d =>
            {
                seen = d;
                return new FakeResolver(node);
            };

            director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();

            seen.ShouldBe(director);
            director.IsOpen.ShouldBeTrue();
            director.Close();
        }
        finally
        {
            CutsceneDirectorComponent.DefaultBindingResolverFactory = previous;
        }
    }

    [Test]
    public void DirectorOpen_WithoutResolverOrFactory_ReportsADiagnostic()
    {
        (_, CutsceneAsset asset, _) = MakeRig();
        SceneNode owner = new("Director", new Transform());
        CutsceneDirectorComponent director = owner.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;

        Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? previous = CutsceneDirectorComponent.DefaultBindingResolverFactory;
        try
        {
            CutsceneDirectorComponent.DefaultBindingResolverFactory = null;

            director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();

            director.IsOpen.ShouldBeFalse();
            director.Diagnostics.Count.ShouldBe(1);
            director.Diagnostics[0].Message.ShouldContain("resolver");
        }
        finally
        {
            CutsceneDirectorComponent.DefaultBindingResolverFactory = previous;
        }
    }

    [Test]
    public void Editing_AddTransformTrack_SeedsTheCurrentTransform_SoTheNodeDoesNotJump()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode node = new("Hero", new Transform());
        node.Transform.SetParent(root.Transform, false, EParentAssignmentMode.Immediate);
        ((Transform)node.Transform).Translation = OriginalPosition;
        CutsceneAsset asset = new() { Duration = T(10) };

        TransformTrack track = CutsceneAssetEditing.AddTransformTrack(asset, node);

        asset.Bindings.Count.ShouldBe(1);
        asset.Bindings[0].ObjectId.ShouldBe(node.ID);
        asset.Bindings[0].NodePath.ShouldBe("Root/Hero");

        CutscenePreviewHost host = new(new FakeResolver(node));
        host.Begin(asset);
        host.Seek(T(5), ESeekIntent.Jump);
        host.Tick(0.0);
        Position(node).ShouldBe(OriginalPosition);
        host.End();

        CutsceneAssetEditing.AddTransformTrack(asset, node);
        asset.Bindings.Count.ShouldBe(1);
        asset.Tracks.Count.ShouldBe(2);
        track.BindingSlot.ShouldBe(0);
    }

    [Test]
    public void Editing_AddCameraTrack_BindsTheCameraSlot()
    {
        SceneNode node = new("Shot", new Transform());
        CutsceneAsset asset = new() { Duration = T(10) };

        CameraCutTrack track = CutsceneAssetEditing.AddCameraCutTrack(asset, node);

        track.Clips.Count.ShouldBe(1);
        CameraCutClip clip = track.Clips[0].ShouldBeOfType<CameraCutClip>();
        clip.CameraBindingSlot.ShouldBe(0);
        clip.Duration.ShouldBe(T(10));
    }

    [Test]
    public void Editing_SetAndRemoveKey_KeepTheKeysSorted()
    {
        TransformTrackClip clip = MoveClip(10, 10);

        CutsceneAssetEditing.SetTransformKey(clip, T(5), new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One);

        clip.Position.Times.ShouldBe([T(0), T(5), T(10)]);
        clip.Position.Sample(T(5)).ShouldBe(new Vector3(1, 2, 3));
        clip.Rotation.Times.ShouldBe([T(0), T(5)]);
        clip.Scale.Times.ShouldBe([T(0), T(5)]);

        // A key at the same time replaces the old key.
        CutsceneAssetEditing.SetTransformKey(clip, T(5), new Vector3(4, 5, 6), Quaternion.Identity, Vector3.One);
        clip.Position.Times.Length.ShouldBe(3);
        clip.Position.Sample(T(5)).ShouldBe(new Vector3(4, 5, 6));

        CutsceneAssetEditing.RemoveTransformKey(clip, T(5), T(0.01)).ShouldBeTrue();
        clip.Position.Times.ShouldBe([T(0), T(10)]);
        clip.Rotation.Times.ShouldBe([T(0)]);
        CutsceneAssetEditing.RemoveTransformKey(clip, T(5), T(0.01)).ShouldBeFalse();
    }

    [Test]
    public void SaveGuard_RejectsAtTheSaveCore_AndAllowsOtherAssets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cutscene-save-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            AssetManager assets = new()
            {
                GameAssetsPath = directory,
                SaveGuard = asset => asset is XRScene ? "Rejected." : null,
            };

            XRScene scene = new("GuardedScene");
            Should.Throw<InvalidOperationException>(() => assets.SaveToImmediate(scene, directory)).Message.ShouldBe("Rejected.");
            Directory.GetFiles(directory).ShouldBeEmpty();

            CutsceneAsset cutscene = new() { Name = "AllowedCutscene", Duration = T(1) };
            cutscene.MarkDirty();
            assets.SaveToImmediate(cutscene, directory);
            File.Exists(cutscene.FilePath).ShouldBeTrue();
            cutscene.IsDirty.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (RuntimeWorld Runtime, XRWorld Target, CutsceneAsset Asset) OpenSessionPreview()
    {
        XRWorld target = new("PreviewWorld", new XRScene("PreviewScene", new SceneNode("Root")));
        RuntimeWorld runtime = new(new JitterScene(), target);
        CutsceneAsset asset = new() { Duration = T(4) };
        CutscenePreviewSession.OpenPreview(asset, runtime);
        return (runtime, target, asset);
    }

    private static List<CutsceneCompileDiagnostic> SessionDiagnostics()
    {
        List<CutsceneCompileDiagnostic> diagnostics = [];
        CutscenePreviewSession.CopyDiagnostics(diagnostics);
        return diagnostics;
    }

    [Test]
    public void Session_RefusesSceneAndWorldSaves_OnlyWhileAPreviewIsOpen()
    {
        (RuntimeWorld runtime, XRWorld target, CutsceneAsset asset) = OpenSessionPreview();
        Func<XRAsset, string?>? previousGuard = Engine.Assets.SaveGuard;
        try
        {
            Engine.Assets.SaveGuard = CutscenePreviewSession.GetSaveRejection;

            CutscenePreviewSession.GetSaveRejection(new XRScene("S")).ShouldNotBeNull().ShouldContain("End the preview");
            CutscenePreviewSession.GetSaveRejection(target).ShouldNotBeNull();
            CutscenePreviewSession.GetSaveRejection(asset).ShouldBeNull();

            McpToolResponse response = EditorMcpActions.SaveWorldAsync(new McpToolContext(runtime)).GetAwaiter().GetResult();
            response.IsError.ShouldBeTrue();
            response.Message.ShouldContain("End the preview");

            CutscenePreviewSession.Close();
            CutscenePreviewSession.GetSaveRejection(target).ShouldBeNull();
        }
        finally
        {
            Engine.Assets.SaveGuard = previousGuard;
            CutscenePreviewSession.Close();
            runtime.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveAll_DuringAPreview_SkipsTheScene_AndSavesTheRest(bool useAsync)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cutscene-save-all-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        (RuntimeWorld runtime, _, _) = OpenSessionPreview();
        try
        {
            AssetManager assets = new() { GameAssetsPath = directory, SaveGuard = CutscenePreviewSession.GetSaveRejection };
            XRScene scene = new("DirtyScene") { FilePath = Path.Combine(directory, "DirtyScene.asset") };
            CutsceneAsset other = new() { Name = "DirtyCutscene", Duration = T(1), FilePath = Path.Combine(directory, "DirtyCutscene.asset") };
            scene.MarkDirty();
            other.MarkDirty();
            assets.DirtyAssets[scene.ID] = scene;
            assets.DirtyAssets[other.ID] = other;

            if (useAsync)
                assets.SaveAllAsync().Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            else
                assets.SaveAll();

            File.Exists(scene.FilePath).ShouldBeFalse();
            File.Exists(other.FilePath).ShouldBeTrue();
            scene.IsDirty.ShouldBeTrue();
            other.IsDirty.ShouldBeFalse();
            assets.DirtyAssets.ContainsKey(scene.ID).ShouldBeTrue();
            assets.DirtyAssets.ContainsKey(other.ID).ShouldBeFalse();

            // A direct save of the rejected asset still fails.
            Should.Throw<InvalidOperationException>(() => assets.SaveImmediate(scene));
        }
        finally
        {
            CutscenePreviewSession.Close();
            runtime.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Session_WorldChange_ClosesThePreview_AndRecordsADiagnostic()
    {
        (RuntimeWorld runtime, _, _) = OpenSessionPreview();
        try
        {
            CutscenePreviewSession.AdvanceFrame(0.0);
            CutscenePreviewSession.IsOpen.ShouldBeTrue();

            runtime.RetargetWorld(new XRWorld("OtherWorld"));
            CutscenePreviewSession.AdvanceFrame(0.0);

            CutscenePreviewSession.IsOpen.ShouldBeFalse();
            SessionDiagnostics().ShouldContain(d => d.Message.Contains("world changed"));

            // A new open starts with no old failure.
            CutscenePreviewSession.OpenPreview(new CutsceneAsset { Duration = T(1) }, runtime);
            SessionDiagnostics().ShouldNotContain(d => d.Message.Contains("world changed"));
        }
        finally
        {
            CutscenePreviewSession.Close();
            runtime.Dispose();
        }
    }

    [Test]
    public void End_RemovesGizmoMovesOfBoundNodesFromUndo_AndKeepsOtherEntries()
    {
        (SceneNode node, CutsceneAsset asset, CutscenePreviewHost host) = MakeRig();
        Transform bound = (Transform)node.Transform;
        SceneNode otherNode = new("Other", new Transform());
        Transform other = (Transform)otherNode.Transform;
        Undo.ClearHistory();
        Undo.Track(bound);
        Undo.Track(other);
        try
        {
            host.Begin(asset);
            host.Seek(T(5), ESeekIntent.Jump);
            host.Tick(0.0);

            using (Undo.BeginUserInteraction())
            using (Undo.BeginChange("Gizmo move"))
            {
                bound.Translation = new Vector3(50, 0, 0);
                other.Translation = new Vector3(60, 0, 0);
            }

            host.End();
            Position(node).ShouldBe(OriginalPosition);

            Undo.TryUndo().ShouldBeTrue();
            Position(node).ShouldBe(OriginalPosition);
            other.Translation.ShouldBe(Vector3.Zero);
            Undo.CanUndo.ShouldBeFalse();
        }
        finally
        {
            Undo.Untrack(bound);
            Undo.Untrack(other);
            Undo.ClearHistory();
        }
    }

    [Test]
    public void SuppressionScope_DefaultDisposal_DoesNotChangeTheCount()
    {
        SceneNode node = new("Node", new Transform());
        Transform transform = (Transform)node.Transform;
        Undo.ClearHistory();
        Undo.Track(transform);
        try
        {
            default(Undo.RecordingSuppressionScope).Dispose();

            using (Undo.SuppressRecording())
            using (Undo.BeginUserInteraction())
                transform.Translation = Vector3.UnitX;
            Undo.CanUndo.ShouldBeFalse();

            using (Undo.BeginUserInteraction())
                transform.Translation = Vector3.UnitY;
            Undo.CanUndo.ShouldBeTrue();
        }
        finally
        {
            Undo.Untrack(transform);
            Undo.ClearHistory();
        }
    }

    [Test]
    public void Editing_TrimAndMove_KeepTheClipContentInPlace()
    {
        TransformTrackClip clip = MoveClip(10, 10);
        clip.Speed = 2.0;

        CutsceneAssetEditing.TrimClipStart(clip, T(2), T(0.1));
        clip.Start.ShouldBe(T(2));
        clip.Duration.ShouldBe(T(8));
        clip.ClipOffset.ShouldBe(T(4));

        CutsceneAssetEditing.TrimClipEnd(clip, T(5), T(0.1));
        clip.Duration.ShouldBe(T(3));

        CutsceneAssetEditing.MoveClip(clip, T(-1));
        clip.Start.ShouldBe(CutsceneTime.Zero);
        clip.Duration.ShouldBe(T(3));

        // A trim cannot make the clip shorter than the minimum.
        CutsceneAssetEditing.TrimClipEnd(clip, CutsceneTime.Zero, T(0.5));
        clip.Duration.ShouldBe(T(0.5));
        CutsceneAssetEditing.ToClipLocalTime(clip, T(0.25)).ShouldBe(T(4.5));
    }
}
