namespace XREngine;

/// <summary>
/// Builds the persistent frame-output DAG from ordinary runtime output requests.
/// Execution/deadline scheduling is intentionally owned by the later output scheduler.
/// </summary>
public sealed class RenderOutputGraphPlanner
{
    private const ulong PublicationNodeKey = 0x0100_0000_0000_0001UL;
    private const ulong XrSceneNodeDomain = 0x0200_0000_0000_0000UL;
    private const ulong OutputNodeDomain = 0x0300_0000_0000_0000UL;
    private const ulong TerminalNodeDomain = 0x0400_0000_0000_0000UL;
    private const ulong ProbeNodeDomain = 0x0500_0000_0000_0000UL;

    private readonly RenderOutputDag _graph;
    private ulong _frameId = ulong.MaxValue;

    public RenderOutputGraphPlanner(int nodeCapacity = 256, int edgeCapacity = 512)
        => _graph = new RenderOutputDag(nodeCapacity, edgeCapacity);

    public RenderOutputDag Graph => _graph;

    /// <summary>Reserves an output before lowering the frame's complete request set.</summary>
    public bool Reserve(in RenderOutputRequest request)
        => EnsureFrame(request.FrameId) && _graph.ReserveOutputKey(request.OutputId);

    public int Plan(
        in RenderOutputRequest request,
        bool isDue,
        bool independentDesktopScene,
        EFrameOutputKind xrSourceKind)
        => Plan(
            request,
            isDue,
            independentDesktopScene,
            xrSourceKind,
            ERenderOutputPolicyReason.None,
            xrImagesAcquired: false,
            out _);

    public int Plan(
        in RenderOutputRequest request,
        bool isDue,
        bool independentDesktopScene,
        EFrameOutputKind xrSourceKind,
        ERenderOutputPolicyReason deferralReason,
        bool xrImagesAcquired,
        out RenderOutputSchedulingDecision decision)
    {
        if (!EnsureFrame(request.FrameId))
        {
            decision = new(
                Execute: false,
                ERenderOutputWorkDisposition.Skipped,
                ERenderOutputPolicyReason.DependencyUnavailable,
                ContentAgeFrames: 0u,
                XrCriticalPathReserved: false,
                ForcedRefresh: false);
            return -1;
        }
        ulong terminalKey = GetTerminalNodeKey(request);
        if (_graph.TryGetNodeIndex(terminalKey, out int plannedNode))
        {
            ApplyScheduleAndAdmission(
                plannedNode,
                request,
                isDue,
                deferralReason,
                xrImagesAcquired,
                out decision);
            return plannedNode;
        }

        int publicationNode = AddPublicationNode();
        int terminalNode = request.OutputKind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                AddXrSceneNode(request, publicationNode),
            EFrameOutputKind.DesktopMirror => AddDesktopOutput(
                request, xrSourceKind, independentDesktopScene, publicationNode),
            EFrameOutputKind.DesktopScene or EFrameOutputKind.EditorScenePanel =>
                AddSceneOutput(request, publicationNode),
            EFrameOutputKind.VrPickupMirror or EFrameOutputKind.InWorldMirror =>
                AddViewDependentOutput(request, publicationNode),
            EFrameOutputKind.LightProbeCapture or EFrameOutputKind.ReflectionProbeCapture or
                EFrameOutputKind.ImageBasedLighting => AddProbeOutput(request, publicationNode),
            EFrameOutputKind.SceneCapture or EFrameOutputKind.Thumbnail or EFrameOutputKind.Diagnostic =>
                AddCaptureOutput(request, publicationNode),
            _ => AddNonSceneOutput(request, publicationNode),
        };

        if (terminalNode >= 0)
        {
            ApplyScheduleAndAdmission(
                terminalNode,
                request,
                isDue,
                deferralReason,
                xrImagesAcquired,
                out decision);
        }
        else
        {
            decision = new(
                Execute: false,
                ERenderOutputWorkDisposition.Skipped,
                ERenderOutputPolicyReason.DependencyUnavailable,
                ContentAgeFrames: 0u,
                XrCriticalPathReserved: false,
                ForcedRefresh: false);
        }
        return terminalNode;
    }

    private void ApplyScheduleAndAdmission(
        int terminalNode,
        in RenderOutputRequest request,
        bool isDue,
        ERenderOutputPolicyReason deferralReason,
        bool xrImagesAcquired,
        out RenderOutputSchedulingDecision decision)
    {
        bool reserveXrPath = xrImagesAcquired &&
            request.OutputClass == ERenderOutputClass.XrCritical;
        _graph.ApplyScheduleToPrerequisites(
            terminalNode,
            request.Schedule.Priority,
            request.Schedule.DeadlineMs,
            reserveXrPath);

        if (isDue)
        {
            decision = RenderOutputSchedulingDecision.Fresh(reserveXrPath);
            return;
        }

        RenderOutputDagNodeStatus status = _graph.GetStatus(terminalNode);
        uint maximumDeferrals = ResolveMaximumDeferrals(request);
        bool forceRefresh = !status.HasCompletedResult ||
            maximumDeferrals != uint.MaxValue &&
            status.ConsecutiveDeferrals >= maximumDeferrals;
        if (forceRefresh)
        {
            decision = new(
                Execute: true,
                ERenderOutputWorkDisposition.FreshRender,
                ERenderOutputPolicyReason.MaximumDeferralReached,
                status.ContentAgeFrames,
                reserveXrPath,
                ForcedRefresh: true);
            return;
        }

        if ((request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0 &&
            _graph.TryReuse(terminalNode))
        {
            status = _graph.GetStatus(terminalNode);
            decision = new(
                Execute: false,
                ERenderOutputWorkDisposition.ReusedStale,
                ERenderOutputPolicyReason.HeldLastImage,
                status.ContentAgeFrames,
                reserveXrPath,
                ForcedRefresh: false);
            return;
        }

        ERenderOutputPolicyReason reason = deferralReason == ERenderOutputPolicyReason.None
            ? ERenderOutputPolicyReason.Cadence
            : deferralReason;
        if ((request.FallbackPolicy &
             (ERenderOutputFallbackPolicy.AllowCadenceReduction |
              ERenderOutputFallbackPolicy.AllowBudgetDeferral)) != 0)
        {
            _graph.SetDeferred(terminalNode, reason);
            status = _graph.GetStatus(terminalNode);
            decision = new(
                Execute: false,
                ERenderOutputWorkDisposition.Deferred,
                reason,
                status.ContentAgeFrames,
                reserveXrPath,
                ForcedRefresh: false);
            return;
        }

        _graph.SetSkipped(terminalNode, reason);
        decision = new(
            Execute: false,
            ERenderOutputWorkDisposition.Skipped,
            reason,
            status.ContentAgeFrames,
            reserveXrPath,
            ForcedRefresh: false);
    }

    private static uint ResolveMaximumDeferrals(in RenderOutputRequest request)
    {
        uint configured = request.Schedule.MaxContentAgeFrames;
        if (configured != uint.MaxValue)
            return configured;

        return request.OutputClass switch
        {
            ERenderOutputClass.VisibleMirror => 2u,
            ERenderOutputClass.BackgroundCapture => 8u,
            ERenderOutputClass.Diagnostic => 4u,
            _ => 1u,
        };
    }

    public void Complete(in RenderOutputRequest request)
    {
        if (!EnsureFrame(request.FrameId))
            return;
        if (_graph.TryGetNodeIndex(GetTerminalNodeKey(request), out int nodeIndex))
            _graph.SetProgress(nodeIndex, 1.0f);
    }

    public bool TryGetStatus(in RenderOutputRequest request, out RenderOutputDagNodeStatus status)
    {
        if (!EnsureFrame(request.FrameId))
        {
            status = default;
            return false;
        }
        if (_graph.TryGetNodeIndex(GetTerminalNodeKey(request), out int nodeIndex))
        {
            status = _graph.GetStatus(nodeIndex);
            return true;
        }

        status = default;
        return false;
    }

    /// <summary>
    /// Connects two already-planned outputs through an explicit dataflow edge.
    /// Callers must have matched the dependent consumer set to exactly one
    /// producer set before invoking this method.
    /// </summary>
    public bool TryAddOutputDependency(
        in RenderOutputRequest prerequisite,
        in RenderOutputRequest dependent,
        out string? failureReason)
    {
        failureReason = null;
        if (prerequisite.ProducerDependencySetId == 0UL ||
            dependent.ConsumerDependencySetId == 0UL ||
            prerequisite.ProducerDependencySetId != dependent.ConsumerDependencySetId)
        {
            failureReason = "output producer/consumer dependency IDs do not match";
            return false;
        }
        if (!_graph.TryGetNodeIndex(GetTerminalNodeKey(prerequisite), out int prerequisiteNode) ||
            !_graph.TryGetNodeIndex(GetTerminalNodeKey(dependent), out int dependentNode))
        {
            failureReason = "output dependency references an unplanned terminal node";
            return false;
        }
        if (!_graph.AddDependency(prerequisiteNode, dependentNode))
        {
            failureReason = "output dependency is cyclic or exceeds DAG edge capacity";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adds an engine-owned prerequisite edge, such as shadow production before
    /// a scene output, without requiring a producer/consumer data-set contract
    /// from the caller-facing request.
    /// </summary>
    public bool TryAddRequiredDependency(
        in RenderOutputRequest prerequisite,
        in RenderOutputRequest dependent)
    {
        if (!_graph.TryGetNodeIndex(GetTerminalNodeKey(prerequisite), out int prerequisiteNode) ||
            !_graph.TryGetNodeIndex(GetTerminalNodeKey(dependent), out int dependentNode))
        {
            return false;
        }

        return _graph.AddDependency(prerequisiteNode, dependentNode);
    }

    public bool RefreshSchedule(
        in RenderOutputRequest request,
        bool xrImagesAcquired)
    {
        if (!_graph.TryGetNodeIndex(GetTerminalNodeKey(request), out int terminalNode))
            return false;

        _graph.ApplyScheduleToPrerequisites(
            terminalNode,
            request.Schedule.Priority,
            request.Schedule.DeadlineMs,
            xrImagesAcquired && request.OutputClass == ERenderOutputClass.XrCritical);
        return true;
    }

    private bool EnsureFrame(ulong frameId)
    {
        if (_frameId == frameId)
            return true;
        if (_frameId != ulong.MaxValue && frameId < _frameId)
            return false;
        _frameId = frameId;
        _graph.BeginFrame(unchecked((uint)frameId));
        return true;
    }

    private int AddPublicationNode()
    {
        int publication = _graph.AddNode(new(
            PublicationNodeKey,
            PublicationNodeKey,
            ERenderOutputDagNodeKind.Publish,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            PublicationNodeKey,
            0u,
            Cacheable: false,
            Resumable: false,
            "Shared scene/material/light publication"));
        int uploads = _graph.AddNode(new(
            PublicationNodeKey + 1UL,
            PublicationNodeKey,
            ERenderOutputDagNodeKind.Upload,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            PublicationNodeKey,
            0u,
            Cacheable: false,
            Resumable: false,
            "Frame texture/resource uploads"));
        if (publication >= 0 && uploads >= 0)
            _graph.AddDependency(publication, uploads);
        return uploads;
    }

    private int AddDesktopOutput(
        in RenderOutputRequest request,
        EFrameOutputKind xrSourceKind,
        bool independentDesktopScene,
        int publicationNode)
    {
        int eyeNode = independentDesktopScene
            ? -1
            : AddXrSceneNode(
                RenderOutputRequest.CreateDefault(
                    EVrOutputViewKind.LeftEye,
                    xrSourceKind,
                    request.FrameId) with
                {
                    OutputId = request.OutputId,
                    ViewFamilyId = GetXrFamilyKey(xrSourceKind),
                    Target = request.Target,
                    ProducerDependencySetId = request.ProducerDependencySetId,
                    ConsumerDependencySetId = request.ConsumerDependencySetId,
                },
                publicationNode);
        return AuxiliaryOutputGraphBuilder.AddDesktopMirror(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            request.OutputId,
            eyeNode,
            independentDesktopScene,
            publicationNode,
            request.Schedule.MaxContentAgeFrames,
            cacheLastResult: (request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0);
    }

    private int AddSceneOutput(in RenderOutputRequest request, int publicationNode)
        => AuxiliaryOutputGraphBuilder.AddDesktopMirror(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            request.OutputId,
            renderedEyeNode: -1,
            independentCamera: true,
            publicationNode,
            request.Schedule.MaxContentAgeFrames,
            cacheLastResult: (request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0);

    private int AddViewDependentOutput(in RenderOutputRequest request, int publicationNode)
    {
        AuxiliaryOutputPolicy policy = CreateAuxiliaryPolicy(request, cacheLastResult: true);
        int node = AuxiliaryOutputGraphBuilder.AddViewDependentOutput(
            _graph, policy, request.OutputId, GetOutputNodeKey(request), publicationNode);
        return AuxiliaryOutputGraphBuilder.AddPostProcess(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            node,
            policy.EnablePostProcess,
            request.Schedule.MaxContentAgeFrames,
            policy.CacheLastResult);
    }

    private int AddCaptureOutput(in RenderOutputRequest request, int publicationNode)
    {
        bool cache = (request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0;
        int node = AuxiliaryOutputGraphBuilder.AddCapture(
            _graph,
            GetOutputNodeKey(request),
            request.OutputId,
            request.OutputId,
            request.Schedule.MaxContentAgeFrames,
            cache,
            publicationNode);
        bool postProcess = HasPostProcess(request.OutputKind);
        if (!postProcess)
            return node;
        return AuxiliaryOutputGraphBuilder.AddPostProcess(
            _graph,
            GetTerminalNodeKey(request),
            request.OutputId,
            node,
            enabled: true,
            request.Schedule.MaxContentAgeFrames,
            cacheLastResult: cache);
    }

    private int AddProbeOutput(in RenderOutputRequest request, int publicationNode)
        => AuxiliaryOutputGraphBuilder.AddProbePipeline(
            _graph,
            request.OutputId,
            GetProbeNodeBase(request),
            faceCount: 6,
            prefilterMipCount: 6,
            publicationNode);

    private int AddNonSceneOutput(in RenderOutputRequest request, int publicationNode)
    {
        ERenderOutputDagNodeKind nodeKind = request.OutputKind switch
        {
            EFrameOutputKind.Shadow => ERenderOutputDagNodeKind.Shadow,
            EFrameOutputKind.Present => ERenderOutputDagNodeKind.Present,
            _ => ERenderOutputDagNodeKind.Publish,
        };
        int node = _graph.AddNode(new(
            GetTerminalNodeKey(request),
            request.OutputId,
            nodeKind,
            ERenderOutputDataClass.ViewIndependent,
            0UL,
            request.OutputId,
            request.Schedule.MaxContentAgeFrames,
            Cacheable: false,
            Resumable: false,
            GetDebugName(request.OutputKind)));
        if (node >= 0)
            _graph.AddDependency(publicationNode, node);
        return node;
    }

    private int AddXrSceneNode(
        in RenderOutputRequest request,
        int publicationNode)
    {
        ulong key = GetXrSceneNodeKey(request);
        int node = _graph.AddNode(new(
            key,
            request.OutputId,
            ERenderOutputDagNodeKind.SceneView,
            ERenderOutputDataClass.ViewDependent,
            request.ViewFamilyId,
            request.Target.CompatibilityKey,
            0u,
            Cacheable: false,
            Resumable: false,
            request.OutputKind == EFrameOutputKind.OpenXREyeSubmit ? "OpenXR eye family" : "OpenVR eye family"));
        if (node >= 0)
            _graph.AddDependency(publicationNode, node);
        return node;
    }

    private static AuxiliaryOutputPolicy CreateAuxiliaryPolicy(
        in RenderOutputRequest request,
        bool cacheLastResult)
        => new(
            request.OutputId,
            ScreenCoverage: 1.0f,
            request.Schedule.DesiredRateHz,
            request.Schedule.MaxContentAgeFrames,
            ResolutionScale: 1.0f,
            RecursionLimit: 1,
            RequiresIndependentCamera: true,
            EnablePostProcess: true,
            cacheLastResult);

    private static string GetDebugName(EFrameOutputKind kind)
        => kind switch
        {
            EFrameOutputKind.ImGuiOverlay => "ImGui overlay",
            EFrameOutputKind.DynamicTextOverlay => "Dynamic text overlay",
            EFrameOutputKind.Present => "Present",
            EFrameOutputKind.UiPreview => "UI preview",
            _ => "Frame output",
        };

    private static bool HasPostProcess(EFrameOutputKind kind)
        => kind is EFrameOutputKind.SceneCapture or EFrameOutputKind.Thumbnail;

    private static ulong GetTerminalNodeKey(in RenderOutputRequest request)
        => request.OutputKind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                GetXrSceneNodeKey(request),
            EFrameOutputKind.LightProbeCapture or EFrameOutputKind.ReflectionProbeCapture or
                EFrameOutputKind.ImageBasedLighting => GetProbeNodeBase(request) + 0x800UL,
            EFrameOutputKind.Diagnostic => GetOutputNodeKey(request),
            _ => TerminalNodeDomain ^ GetVersionedOutputIdentity(request),
        };

    private static ulong GetProbeNodeBase(in RenderOutputRequest request)
        => ProbeNodeDomain ^ GetVersionedOutputIdentity(request);

    private static ulong GetOutputNodeKey(in RenderOutputRequest request)
        => OutputNodeDomain ^ GetVersionedOutputIdentity(request);

    private static ulong GetXrSceneNodeKey(in RenderOutputRequest request)
        => XrSceneNodeDomain ^ GetVersionedOutputIdentity(request);

    private static ulong GetXrFamilyKey(EFrameOutputKind kind)
        => XrSceneNodeDomain ^ (0x100UL + (ulong)(uint)kind);

    private static ulong GetVersionedOutputIdentity(in RenderOutputRequest request)
    {
        ulong hash = request.OutputId;
        Add(ref hash, request.Target.CompatibilityKey);
        Add(ref hash, request.ProducerDependencySetId);
        Add(ref hash, request.ConsumerDependencySetId);
        Add(ref hash, (ulong)(uint)request.OutputKind);
        return hash == 0UL ? 1UL : hash;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
