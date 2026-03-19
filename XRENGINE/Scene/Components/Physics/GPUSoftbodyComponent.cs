using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;

namespace XREngine.Components;

/// <summary>
/// Phase 1 softbody owner component. It stores the simulation proxy, collider bindings, and render-binding metadata,
/// submits batched GPU data each frame, and triggers the centralized softbody dispatcher from a pre-render hook.
/// </summary>
public sealed class GPUSoftbodyComponent : XRComponent, IRenderable
{
    private readonly RenderInfo3D _dispatchRenderInfo;
    private readonly RenderCommandMethod3D _dispatchRenderCommand;

    private int _solverIterations = 4;
    private int _substeps = 1;
    private float _damping = 0.05f;
    private float _colliderMargin = 0.01f;
    private float _simulationStepSeconds;

    public GPUSoftbodyComponent()
    {
        RenderedObjects = [_dispatchRenderInfo = RenderInfo3D.New(this, _dispatchRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, DispatchQueuedSoftbodyWork))];
    }

    [Category("Softbody")]
    [Description("Optional mesh renderer that future cluster skinning passes will target.")]
    public XRMeshRenderer? TargetMeshRenderer { get; set; }

    [Category("Softbody")]
    [Description("Gravity used by the softbody proxy simulation.")]
    public Vector3 Gravity { get; set; } = new(0.0f, -9.8f, 0.0f);

    [Category("Softbody")]
    [Description("Additional external force applied to all simulated particles.")]
    public Vector3 ExternalForce { get; set; } = Vector3.Zero;

    [Category("Softbody")]
    [Description("First-pass particle damping applied during integration.")]
    public float Damping
    {
        get => _damping;
        set => _damping = Math.Clamp(value, 0.0f, 1.0f);
    }

    [Category("Softbody")]
    [Description("Distance-solver iterations requested per frame.")]
    public int SolverIterations
    {
        get => _solverIterations;
        set => _solverIterations = Math.Max(1, value);
    }

    [Category("Softbody")]
    [Description("Substeps requested per frame.")]
    public int Substeps
    {
        get => _substeps;
        set => _substeps = Math.Max(1, value);
    }

    [Category("Softbody")]
    [Description("Extra separation margin used by capsule collider resolution.")]
    public float ColliderMargin
    {
        get => _colliderMargin;
        set => _colliderMargin = Math.Max(0.0f, value);
    }

    [Category("Softbody")]
    [Description("Optional fixed simulation step in seconds. Zero uses the current engine delta for the frame.")]
    public float SimulationStepSeconds
    {
        get => _simulationStepSeconds;
        set => _simulationStepSeconds = Math.Max(0.0f, value);
    }

    [Category("Softbody Debug")]
    [Description("When true, draws debug visualization for particles, constraint links, and collider capsules.")]
    public bool DebugDrawEnabled { get; set; }

    [Category("Softbody")]
    [Description("Simulation proxy particle data submitted to the GPU softbody dispatcher.")]
    public List<GPUSoftbodyParticleData> Particles { get; } = [];

    [Category("Softbody")]
    [Description("Distance constraints between softbody particles.")]
    public List<GPUSoftbodyDistanceConstraintData> DistanceConstraints { get; } = [];

    [Category("Softbody")]
    [Description("Cluster metadata for future soft-cluster skinning and shape matching.")]
    public List<GPUSoftbodyClusterData> Clusters { get; } = [];

    [Category("Softbody")]
    [Description("Cluster membership data linking particles to clusters.")]
    public List<GPUSoftbodyClusterMemberData> ClusterMembers { get; } = [];

    [Category("Softbody")]
    [Description("Capsule collider bindings for the softbody simulation.")]
    public List<GPUSoftbodyColliderData> Colliders { get; } = [];

    [Category("Softbody")]
    [Description("Render-binding metadata reserved for later cluster skinning passes.")]
    public List<GPUSoftbodyRenderBindingData> RenderBindings { get; } = [];

    [Category("Softbody Diagnostics")]
    [DisplayName("Submitted Particle Count")]
    public int SubmittedParticleCount { get; private set; }

    [Category("Softbody Diagnostics")]
    [DisplayName("Submitted Constraint Count")]
    public int SubmittedConstraintCount { get; private set; }

    [Category("Softbody Diagnostics")]
    [DisplayName("Submitted Cluster Count")]
    public int SubmittedClusterCount { get; private set; }

    [Category("Softbody Diagnostics")]
    [DisplayName("Submitted Collider Count")]
    public int SubmittedColliderCount { get; private set; }

    [Category("Softbody Diagnostics")]
    [DisplayName("Submitted Render Binding Count")]
    public int SubmittedRenderBindingCount { get; private set; }

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Dispatch CPU ms")]
    public double LastDispatchCpuMilliseconds => GPUSoftbodyDispatcher.Instance.LastDispatchCpuMilliseconds;

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Dispatch Substeps")]
    public int LastDispatchSubsteps => GPUSoftbodyDispatcher.Instance.LastDispatchedSubsteps;

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Solver Iterations")]
    public int LastDispatchSolverIterations => GPUSoftbodyDispatcher.Instance.LastDispatchedSolverIterations;

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Invalid Particle Count")]
    public int LastInvalidParticleCount => GPUSoftbodyDispatcher.Instance.LastInvalidParticleCount;

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Invalid Constraint Count")]
    public int LastInvalidConstraintCount => GPUSoftbodyDispatcher.Instance.LastInvalidConstraintCount;

    [Category("Softbody Diagnostics")]
    [DisplayName("Last Invalid Collider Count")]
    public int LastInvalidColliderCount => GPUSoftbodyDispatcher.Instance.LastInvalidColliderCount;

    public RenderInfo[] RenderedObjects { get; }

    protected override void OnComponentActivated()
    {
        GPUSoftbodyDispatcher.Instance.Register(this);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, SubmitCurrentFrameData);
        SubmitCurrentFrameData();
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        UnregisterTick(ETickGroup.Late, ETickOrder.Animation, SubmitCurrentFrameData);
        GPUSoftbodyDispatcher.Instance.Unregister(this);
    }

    public void SubmitCurrentFrameData()
    {
        SubmittedParticleCount = Particles.Count;
        SubmittedConstraintCount = DistanceConstraints.Count;
        SubmittedClusterCount = Clusters.Count;
        SubmittedColliderCount = Colliders.Count;
        SubmittedRenderBindingCount = RenderBindings.Count;

        GPUSoftbodyDispatcher.Instance.SubmitData(
            this,
            Particles,
            DistanceConstraints,
            Clusters,
            ClusterMembers,
            Colliders,
            RenderBindings,
            CreateDispatchData());
    }

    private void DispatchQueuedSoftbodyWork()
    {
        GPUSoftbodyDispatcher.Instance.ProcessDispatches();
        if (DebugDrawEnabled)
            RenderDebugVisualization();
    }

    #region Debug Visualization

    private void RenderDebugVisualization()
    {
        if (!IsActive || Engine.Rendering.State.IsShadowPass)
            return;

        // Draw particles as wireframe spheres
        for (int i = 0; i < Particles.Count; i++)
        {
            GPUSoftbodyParticleData p = Particles[i];
            ColorF4 color = p.InverseMass <= 0.0f ? ColorF4.Red : ColorF4.Yellow;
            if (p.Radius > 0.0f)
                Engine.Rendering.Debug.RenderSphere(p.CurrentPosition, p.Radius, false, color);
            else
                Engine.Rendering.Debug.RenderPoint(p.CurrentPosition, color);
        }

        // Draw distance constraint links as lines
        for (int i = 0; i < DistanceConstraints.Count; i++)
        {
            GPUSoftbodyDistanceConstraintData c = DistanceConstraints[i];
            if (c.ParticleA < 0 || c.ParticleA >= Particles.Count ||
                c.ParticleB < 0 || c.ParticleB >= Particles.Count)
                continue;

            Engine.Rendering.Debug.RenderLine(
                Particles[c.ParticleA].CurrentPosition,
                Particles[c.ParticleB].CurrentPosition,
                ColorF4.Orange);
        }

        // Draw capsule colliders
        for (int i = 0; i < Colliders.Count; i++)
        {
            GPUSoftbodyColliderData col = Colliders[i];
            if (col.Type != (int)GPUSoftbodyColliderType.Capsule)
                continue;

            Vector3 start = new(col.SegmentStartRadius.X, col.SegmentStartRadius.Y, col.SegmentStartRadius.Z);
            float radius = col.SegmentStartRadius.W;
            Vector3 end = new(col.SegmentEndFriction.X, col.SegmentEndFriction.Y, col.SegmentEndFriction.Z);
            Engine.Rendering.Debug.RenderCapsule(start, end, radius, false, ColorF4.Cyan);
        }
    }

    #endregion

    private GPUSoftbodyDispatchData CreateDispatchData()
        => new()
        {
            ParticleConstraintRanges = new IVector4(0, Particles.Count, 0, DistanceConstraints.Count),
            ClusterRanges = new IVector4(0, Clusters.Count, 0, ClusterMembers.Count),
            ColliderBindingRanges = new IVector4(0, Colliders.Count, 0, RenderBindings.Count),
            SimulationScalars = new Vector4(Engine.Delta, Damping, ColliderMargin, SimulationStepSeconds),
            GravitySubsteps = new Vector4(Gravity, Substeps),
            ForceIterations = new Vector4(ExternalForce, SolverIterations),
        };
}