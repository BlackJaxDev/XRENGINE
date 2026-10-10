using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Tools;
using XREngine.Scene;

namespace XREngine.Components.Scene.Mesh;

public sealed partial class HLODGroupComponent
{
    private CancellationTokenSource? _imposterRebuildCancellation;
    private ulong _imposterRebuildGeneration;
    private bool _imposterCapturePending;
    private string? _imposterRebuildFailure;
    private RenderPipeline? _runtimeImposterCapturePipeline;
    private EMeshSubmissionStrategy? _runtimeImposterCaptureSubmissionStrategy;

    /// <summary>Programmatic capture override only; authored HLOD serialization and defaults are unchanged.</summary>
    [Browsable(false), YamlDotNet.Serialization.YamlIgnore, MemoryPack.MemoryPackIgnore]
    public RenderPipeline? RuntimeImposterCapturePipeline
    {
        get => _runtimeImposterCapturePipeline;
        set => SetField(ref _runtimeImposterCapturePipeline, value);
    }

    /// <summary>Programmatic submission override only; null retains the host's existing policy.</summary>
    [Browsable(false), YamlDotNet.Serialization.YamlIgnore, MemoryPack.MemoryPackIgnore]
    public EMeshSubmissionStrategy? RuntimeImposterCaptureSubmissionStrategy
    {
        get => _runtimeImposterCaptureSubmissionStrategy;
        set => SetField(ref _runtimeImposterCaptureSubmissionStrategy, value);
    }

    /// <summary>True while an unpublished replacement is being captured.</summary>
    [Browsable(false)]
    public bool ImposterCapturePending => _imposterCapturePending;

    /// <summary>The last capture failure; a failure leaves the previously completed state visible.</summary>
    [Browsable(false)]
    public string? ImposterRebuildFailure => _imposterRebuildFailure;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName is nameof(World) or nameof(IsActive) or nameof(UseOctahedralImposters) or nameof(GenerateImposterOnRebuild) or
            nameof(ImposterSheetSize) or nameof(ImposterCapturePadding) or nameof(ImposterCaptureDepth) or nameof(UseFurthestLod) or nameof(IncludeInactiveNodes) or
            nameof(RuntimeImposterCapturePipeline) or nameof(RuntimeImposterCaptureSubmissionStrategy))
            CancelImposterRebuild();
    }

    private void CancelImposterRebuild()
    {
        SetField(ref _imposterRebuildGeneration, checked(_imposterRebuildGeneration + 1), publishNotifications: false);
        CancellationTokenSource? previous = _imposterRebuildCancellation;
        SetField(ref _imposterRebuildCancellation, null, publishNotifications: false);
        previous?.Cancel();
        // The running operation owns disposal after its capture resources unwind.
        SetField(ref _imposterCapturePending, false, nameof(ImposterCapturePending));
    }

    private void BeginImposterRebuild()
    {
        CancelImposterRebuild();
        IRuntimeRenderWorld world = World.GetRenderWorld()
            ?? throw new InvalidOperationException("HLOD.CaptureWorldMissing: rebuild requires a render world.");
        List<RenderableMesh> sources = CollectSources();
        ProxyBuild? candidate = BuildProxyCandidateFromSources(sources);
        if (candidate?.Bounds is not { IsValid: true } bounds)
        {
            candidate?.Dispose();
            SetField(ref _imposterRebuildFailure, "HLOD.CaptureSourceMissing: no eligible proxy geometry was produced.", nameof(ImposterRebuildFailure));
            return;
        }
        Matrix4x4 matrix = Transform.RenderMatrix;
        var sourceMatrices = new Matrix4x4[sources.Count];
        var sourceRenderers = new XRMeshRenderer?[sources.Count];
        List<Func<bool>> sourceRevisions = [];
        for (int index = 0; index < sources.Count; index++)
        {
            RenderableMesh source = sources[index];
            var lods = source.LODs;
            RenderableMesh.RenderableLOD[] lodSnapshot = [.. lods];
            int lodVersion = source.LodRegistrationVersion;
            sourceRevisions.Add(() => ReferenceEquals(source.LODs, lods) && source.LodRegistrationVersion == lodVersion &&
                MatchesSourceLods(lods, lodSnapshot));
            sourceMatrices[index] = sources[index].Component.Transform.RenderMatrix;
            sourceRenderers[index] = UseFurthestLod ? sources[index].LODs.Last?.Value?.Renderer : sources[index].CurrentLODRenderer;
            if (sourceRenderers[index] is not { } sourceRenderer) continue;
            ObserveSourceRenderer(sourceRenderer, sourceRevisions);
        }
        CancellationTokenSource cancellation = new();
        SetField(ref _imposterRebuildCancellation, cancellation, publishNotifications: false);
        ulong generation = _imposterRebuildGeneration;
        SetField(ref _imposterRebuildFailure, null, nameof(ImposterRebuildFailure));
        SetField(ref _imposterCapturePending, true, nameof(ImposterCapturePending));
        bool IsCurrent()
        {
            if (IsDestroyed || cancellation.IsCancellationRequested || _imposterRebuildGeneration != generation ||
                !ReferenceEquals(World.GetRenderWorld(), world) || Transform.RenderMatrix != matrix)
                return false;
            for (int index = 0; index < sources.Count; index++)
            {
                RenderableMesh source = sources[index];
                XRMeshRenderer? renderer = UseFurthestLod ? source.LODs.Last?.Value?.Renderer : source.CurrentLODRenderer;
                if (source.Component.IsDestroyed || !ReferenceEquals(source.Component.World.GetRenderWorld(), world) ||
                    source.Component.Transform.RenderMatrix != sourceMatrices[index] || !ReferenceEquals(renderer, sourceRenderers[index]))
                    return false;
            }
            foreach (Func<bool> revision in sourceRevisions)
                if (!revision()) return false;
            return true;
        }
        _ = CompleteImposterRebuildAsync(candidate, sources, world, matrix, bounds, generation,
            IsCurrent, cancellation);
    }

    private static void ObserveSourceRenderer(XRMeshRenderer renderer, List<Func<bool>> checks)
        => SceneCaptureSourceGeometry.ObserveRenderer(renderer, checks);

    private static bool MatchesSourceLods(LinkedList<RenderableMesh.RenderableLOD> current, RenderableMesh.RenderableLOD[] expected)
    {
        if (current.Count != expected.Length) return false;
        var node = current.First;
        for (int index = 0; index < expected.Length; index++, node = node!.Next)
            if (node is null || !ReferenceEquals(node.Value, expected[index])) return false;
        return node is null;
    }

    private async Task CompleteImposterRebuildAsync(ProxyBuild candidate, List<RenderableMesh> sources,
        IRuntimeRenderWorld world, Matrix4x4 matrix, AABB bounds, ulong generation,
        Func<bool> isCurrent, CancellationTokenSource cancellation)
    {
        OctahedralImposterGenerator.Result? result = null;
        GeneratedImposterResources? resultOwner = null;
        SceneNode? replacementNode = null;
        bool installed = false;
        try
        {
            var settings = new OctahedralImposterGenerator.Settings(ImposterSheetSize, ImposterCapturePadding, CaptureDepth: false,
                Pipeline: RuntimeImposterCapturePipeline, SubmissionStrategy: RuntimeImposterCaptureSubmissionStrategy);
            result = await OctahedralImposterGenerator.GenerateProxyAsync(candidate.Renderer, world, matrix,
                bounds, settings, isCurrent, cancellation.Token);
            if (result is not null) resultOwner = new GeneratedImposterResources(result);
            if (result is null || !isCurrent())
                throw new OperationCanceledException("HLOD.ObsoleteCapture: the replacement no longer matches its source.");
            List<RenderableMesh> currentSources = CollectSources();
            if (currentSources.Count != sources.Count)
                throw new OperationCanceledException("HLOD.SourceSelectionChanged: the source set changed before replacement.");
            for (int index = 0; index < sources.Count; index++)
                if (!ReferenceEquals(currentSources[index], sources[index]))
                    throw new OperationCanceledException("HLOD.SourceSelectionChanged: the source set changed before replacement.");

            // The candidate billboard is inactive until every replacement resource and callback exists.
            replacementNode = SceneNode.NewChild(name: ImposterNodeName);
            replacementNode.IsActiveSelf = false;
            if (replacementNode.IsActiveSelf) throw new InvalidOperationException("HLOD.CandidateActivationRejected: the replacement must remain inactive until adoption.");
            OctahedralBillboardComponent billboard = replacementNode.AddComponent<OctahedralBillboardComponent>()
                ?? throw new InvalidOperationException("HLOD.BillboardCreationFailed");
            billboard.Name = "HLOD Octahedral Imposter";
            billboard.ApplyCaptureResult(result, matchBounds: true);
            foreach (RenderInfo info in billboard.RenderedObjects)
                info.PreCollectCommandsCallback = ImposterPreCollect;
            if (!isCurrent()) throw new OperationCanceledException("HLOD.ObsoleteCapture: source changed before replacement.");

            InstallImposterReplacement(candidate, resultOwner!, result.Views, sources, replacementNode, billboard, isCurrent);
            installed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_imposterRebuildGeneration == generation && !IsDestroyed)
                SetField(ref _imposterRebuildFailure, error.Message, nameof(ImposterRebuildFailure));
            Debug.LogException(error, "HLOD impostor rebuild failed; retaining the last completed state.");
        }
        finally
        {
            try
            {
                if (!installed) ReleaseRebuildResources(replacementNode, candidate, resultOwner);
                else resultOwner?.DisposePreviews();
            }
            finally
            {
                cancellation.Dispose();
                if (_imposterRebuildGeneration == generation)
                {
                    SetField(ref _imposterRebuildCancellation, null, publishNotifications: false);
                    SetField(ref _imposterCapturePending, false, nameof(ImposterCapturePending));
                }
            }
        }
    }

    private void InstallImposterReplacement(ProxyBuild candidate, GeneratedImposterResources impostorOwner,
        XRTexture2DArray views, List<RenderableMesh> sources, SceneNode replacementNode,
        OctahedralBillboardComponent billboard, Func<bool> isCurrent)
    {
        if (!isCurrent()) throw new OperationCanceledException("HLOD.ObsoleteCapture: source changed before adoption.");
        if (!impostorOwner.Matches(billboard.ImpostorAsset, billboard.ImposterViews))
            throw new OperationCanceledException("HLOD.ReplacementChanged: the prepared billboard no longer references its captured result.");
        XRMeshRenderer? oldProxy = _proxyRenderer;
        ProxyBuild? oldProxyOwner = _ownedProxy;
        GeneratedImposterResources? oldImposterOwner = _ownedImposter;
        SceneNode? oldNode = _imposterNode;
        OctahedralBillboardComponent? oldBillboard = _imposterBillboard;
        XRTexture2DArray? oldViews = _imposterViews;
        AABB? oldBounds = _renderInfo.LocalCullingVolume;
        int oldPass = _renderCommand.RenderPass;
        bool oldBuilt = _built;
        SourceHook[] oldHooks = [.. _sourceHooks];
        try
        {
            UnhookSources();
            InstallProxy(candidate);
            SetField(ref _imposterNode, replacementNode, publishNotifications: false);
            SetField(ref _imposterBillboard, billboard, publishNotifications: false);
            SetField(ref _imposterViews, views, publishNotifications: false);
            SetField(ref _ownedImposter, impostorOwner, publishNotifications: false);
            HookSources(sources);
            if (!isCurrent()) throw new OperationCanceledException("HLOD.ObsoleteCapture: source changed during replacement.");
            SetField(ref _built, true, publishNotifications: false);
            replacementNode.IsActiveSelf = true;
            if (!replacementNode.IsActiveSelf) throw new InvalidOperationException("HLOD.ReplacementActivationRejected: the completed replacement could not be activated.");
            if (!impostorOwner.Matches(billboard.ImpostorAsset, billboard.ImposterViews))
                throw new OperationCanceledException("HLOD.ReplacementChanged: activation replaced the prepared billboard result.");
            if (!isCurrent()) throw new OperationCanceledException("HLOD.ObsoleteCapture: source changed during activation.");
        }
        catch (Exception adoptionError)
        {
            List<Exception>? rollbackFailures = null;
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) { (rollbackFailures ??= []).Add(error); }
            }
            SetField(ref _proxyRenderer, oldProxy, publishNotifications: false);
            SetField(ref _ownedProxy, oldProxyOwner, publishNotifications: false);
            SetField(ref _imposterNode, oldNode, publishNotifications: false);
            SetField(ref _imposterBillboard, oldBillboard, publishNotifications: false);
            SetField(ref _imposterViews, oldViews, publishNotifications: false);
            SetField(ref _ownedImposter, oldImposterOwner, publishNotifications: false);
            SetField(ref _built, oldBuilt, publishNotifications: false);
            Restore(() => replacementNode.IsActiveSelf = false);
            Restore(UnhookSources);
            Restore(() => _renderCommand.Mesh = oldProxy);
            Restore(() => _renderCommand.RenderPass = oldPass);
            Restore(() => _renderInfo.LocalCullingVolume = oldBounds);
            foreach (SourceHook hook in oldHooks)
            {
                _sourceHooks.Add(hook);
                Restore(() =>
                {
                    if (ReferenceEquals(hook.RenderInfo.PreCollectCommandsCallback, hook.OriginalCallback))
                        hook.RenderInfo.PreCollectCommandsCallback = hook.InstalledCallback;
                });
            }
            if (rollbackFailures is not null)
                throw new AggregateException("HLOD replacement callback failed during rollback.", [adoptionError, .. rollbackFailures]);
            throw;
        }
        ReleaseRebuildResources(oldNode, oldProxyOwner, oldImposterOwner);
    }

    private static void ReleaseRebuildResources(SceneNode? node, ProxyBuild? proxy, GeneratedImposterResources? impostor)
    {
        List<Exception>? failures = null;
        try { node?.Destroy(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { proxy?.Dispose(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { impostor?.Dispose(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) Debug.LogException(new AggregateException(failures), "HLOD generated resource cleanup failed.");
    }
}
