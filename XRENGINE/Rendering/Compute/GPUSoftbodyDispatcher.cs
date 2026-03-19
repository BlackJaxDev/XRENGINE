using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Centralized dispatcher that batches active GPU softbody components into shared compute buffers.
/// Phase 1 focuses on lifecycle, buffer orchestration, and shader dispatch plumbing rather than final simulation quality.
/// </summary>
public sealed class GPUSoftbodyDispatcher
{
    private const uint LocalSizeX = 128u;
    private const int MaxSubstepsPerFrame = 8;
    private const int MaxSolverIterationsPerSubstep = 16;
    private const string IntegrateShaderPath = "Compute/Softbody/Integrate.comp";
    private const string CollideShaderPath = "Compute/Softbody/CollideCapsules.comp";
    private const string SolveShaderPath = "Compute/Softbody/SolveDistance.comp";
    private const string FinalizeShaderPath = "Compute/Softbody/Finalize.comp";

    private static GPUSoftbodyDispatcher? _instance;
    public static GPUSoftbodyDispatcher Instance => _instance ??= new GPUSoftbodyDispatcher();

    private readonly ConcurrentDictionary<int, GPUSoftbodyRequest> _registeredComponents = new();
    private readonly List<GPUSoftbodyRequest> _activeRequests = [];

    private readonly List<GPUSoftbodyParticleData> _allParticles = [];
    private readonly List<GPUSoftbodyDistanceConstraintData> _allConstraints = [];
    private readonly List<GPUSoftbodyClusterData> _allClusters = [];
    private readonly List<GPUSoftbodyClusterMemberData> _allClusterMembers = [];
    private readonly List<GPUSoftbodyColliderData> _allColliders = [];
    private readonly List<GPUSoftbodyRenderBindingData> _allRenderBindings = [];
    private readonly List<GPUSoftbodyDispatchData> _allDispatches = [];

    private XRShader? _integrateShader;
    private XRShader? _collideShader;
    private XRShader? _solveShader;
    private XRShader? _finalizeShader;
    private XRRenderProgram? _integrateProgram;
    private XRRenderProgram? _collideProgram;
    private XRRenderProgram? _solveProgram;
    private XRRenderProgram? _finalizeProgram;

    private XRDataBuffer? _particlesBuffer;
    private XRDataBuffer? _solveScratchParticlesBuffer;
    private XRDataBuffer? _constraintsBuffer;
    private XRDataBuffer? _clustersBuffer;
    private XRDataBuffer? _clusterMembersBuffer;
    private XRDataBuffer? _collidersBuffer;
    private XRDataBuffer? _renderBindingsBuffer;
    private XRDataBuffer? _dispatchParamsBuffer;

    private bool _enabled = true;
    private bool _initialized;
    private ulong _lastProcessedFrameId = ulong.MaxValue;

    public int RegisteredComponentCount => _registeredComponents.Count;
    public int TotalParticleCount { get; private set; }
    public int TotalConstraintCount { get; private set; }
    public int TotalClusterCount { get; private set; }
    public int TotalColliderCount { get; private set; }
    public int LastInvalidParticleCount { get; private set; }
    public int LastInvalidConstraintCount { get; private set; }
    public int LastInvalidColliderCount { get; private set; }
    public int LastDispatchedInstanceCount { get; private set; }
    public int LastDispatchedSubsteps { get; private set; }
    public int LastDispatchedSolverIterations { get; private set; }
    public double LastDispatchCpuMilliseconds { get; private set; }

    private GPUSoftbodyDispatcher()
    {
    }

    public void Register(GPUSoftbodyComponent component)
        => _registeredComponents.TryAdd(component.GetHashCode(), new GPUSoftbodyRequest(component));

    public void Unregister(GPUSoftbodyComponent component)
        => _registeredComponents.TryRemove(component.GetHashCode(), out _);

    public bool IsRegistered(GPUSoftbodyComponent component)
        => _registeredComponents.ContainsKey(component.GetHashCode());

    public void SubmitData(
        GPUSoftbodyComponent component,
        IReadOnlyList<GPUSoftbodyParticleData> particles,
        IReadOnlyList<GPUSoftbodyDistanceConstraintData> constraints,
        IReadOnlyList<GPUSoftbodyClusterData> clusters,
        IReadOnlyList<GPUSoftbodyClusterMemberData> clusterMembers,
        IReadOnlyList<GPUSoftbodyColliderData> colliders,
        IReadOnlyList<GPUSoftbodyRenderBindingData> renderBindings,
        GPUSoftbodyDispatchData dispatchData)
    {
        if (!_registeredComponents.TryGetValue(component.GetHashCode(), out var request))
            return;

        lock (request.SyncRoot)
        {
            request.Particles.Clear();
            request.Particles.AddRange(particles);
            request.Constraints.Clear();
            request.Constraints.AddRange(constraints);
            request.Clusters.Clear();
            request.Clusters.AddRange(clusters);
            request.ClusterMembers.Clear();
            request.ClusterMembers.AddRange(clusterMembers);
            request.Colliders.Clear();
            request.Colliders.AddRange(colliders);
            request.RenderBindings.Clear();
            request.RenderBindings.AddRange(renderBindings);
            request.DispatchData = dispatchData;
            request.NeedsUpdate = true;
        }
    }

    public void ProcessDispatches()
    {
        if (!_enabled || AbstractRenderer.Current is null)
            return;

        ulong frameId = Engine.Rendering.State.RenderFrameId;
        if (_lastProcessedFrameId == frameId)
            return;

        _activeRequests.Clear();
        foreach (var pair in _registeredComponents)
        {
            var request = pair.Value;
            lock (request.SyncRoot)
            {
                if (request.NeedsUpdate)
                    _activeRequests.Add(request);
            }
        }

        if (_activeRequests.Count == 0)
            return;

        EnsureInitialized();
        BuildCombinedBuffers();

        (int maxSubsteps, int maxSolverIterations) = DetermineLoopCounts();
        LastDispatchedInstanceCount = _activeRequests.Count;
        LastDispatchedSubsteps = maxSubsteps;
        LastDispatchedSolverIterations = maxSolverIterations;

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int currentSubstep = 0; currentSubstep < maxSubsteps; currentSubstep++)
        {
            DispatchIntegratePass(currentSubstep);
            if (TotalColliderCount > 0)
                DispatchCollidePass(currentSubstep);
            if (TotalConstraintCount > 0)
            {
                for (int currentSolverIteration = 0; currentSolverIteration < maxSolverIterations; currentSolverIteration++)
                {
                    DispatchSolveDistancePass(currentSubstep, currentSolverIteration);
                    SwapParticleBuffers();
                }
            }
        }

        if (TotalClusterCount > 0)
            DispatchFinalizePass();
        stopwatch.Stop();
        LastDispatchCpuMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        foreach (var request in _activeRequests)
        {
            lock (request.SyncRoot)
                request.NeedsUpdate = false;
        }

        _lastProcessedFrameId = frameId;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
            Reset();
    }

    public void Reset()
    {
        _activeRequests.Clear();
        _lastProcessedFrameId = ulong.MaxValue;
        foreach (var pair in _registeredComponents)
        {
            lock (pair.Value.SyncRoot)
                pair.Value.NeedsUpdate = false;
        }
    }

    public void Dispose()
    {
        Reset();

        _particlesBuffer?.Dispose();
        _solveScratchParticlesBuffer?.Dispose();
        _constraintsBuffer?.Dispose();
        _clustersBuffer?.Dispose();
        _clusterMembersBuffer?.Dispose();
        _collidersBuffer?.Dispose();
        _renderBindingsBuffer?.Dispose();
        _dispatchParamsBuffer?.Dispose();

        _integrateProgram?.Destroy();
        _collideProgram?.Destroy();
        _solveProgram?.Destroy();
        _finalizeProgram?.Destroy();

        _registeredComponents.Clear();
        _initialized = false;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _integrateShader = ShaderHelper.LoadEngineShader(IntegrateShaderPath, EShaderType.Compute);
        _collideShader = ShaderHelper.LoadEngineShader(CollideShaderPath, EShaderType.Compute);
        _solveShader = ShaderHelper.LoadEngineShader(SolveShaderPath, EShaderType.Compute);
        _finalizeShader = ShaderHelper.LoadEngineShader(FinalizeShaderPath, EShaderType.Compute);

        _integrateProgram = new XRRenderProgram(true, false, _integrateShader);
        _collideProgram = new XRRenderProgram(true, false, _collideShader);
        _solveProgram = new XRRenderProgram(true, false, _solveShader);
        _finalizeProgram = new XRRenderProgram(true, false, _finalizeShader);

        _initialized = true;
    }

    private void BuildCombinedBuffers()
    {
        int invalidParticles = 0;
        int invalidConstraints = 0;
        int invalidColliders = 0;

        _allParticles.Clear();
        _allConstraints.Clear();
        _allClusters.Clear();
        _allClusterMembers.Clear();
        _allColliders.Clear();
        _allRenderBindings.Clear();
        _allDispatches.Clear();

        int particleOffset = 0;
        int constraintOffset = 0;
        int clusterOffset = 0;
        int clusterMemberOffset = 0;
        int colliderOffset = 0;
        int renderBindingOffset = 0;

        for (int instanceIndex = 0; instanceIndex < _activeRequests.Count; instanceIndex++)
        {
            GPUSoftbodyRequest request = _activeRequests[instanceIndex];
            lock (request.SyncRoot)
            {
                foreach (var particle in request.Particles)
                {
                    GPUSoftbodyParticleData adjusted = SanitizeParticle(particle, instanceIndex, ref invalidParticles);
                    _allParticles.Add(adjusted);
                }

                foreach (var constraint in request.Constraints)
                {
                    if (TrySanitizeConstraint(constraint, request.Particles.Count, particleOffset, instanceIndex, out GPUSoftbodyDistanceConstraintData adjusted, ref invalidConstraints))
                        _allConstraints.Add(adjusted);
                }

                foreach (var cluster in request.Clusters)
                {
                    var adjusted = cluster;
                    adjusted.MemberStart += clusterMemberOffset;
                    adjusted.InstanceIndex = instanceIndex;
                    _allClusters.Add(adjusted);
                }

                foreach (var member in request.ClusterMembers)
                {
                    var adjusted = member;
                    adjusted.ClusterIndex += clusterOffset;
                    adjusted.ParticleIndex += particleOffset;
                    _allClusterMembers.Add(adjusted);
                }

                foreach (var collider in request.Colliders)
                {
                    if (TrySanitizeCollider(collider, instanceIndex, out GPUSoftbodyColliderData adjusted, ref invalidColliders))
                        _allColliders.Add(adjusted);
                }

                foreach (var renderBinding in request.RenderBindings)
                {
                    var adjusted = renderBinding;
                    adjusted.ClusterIndex += clusterOffset;
                    _allRenderBindings.Add(adjusted);
                }

                _allDispatches.Add(new GPUSoftbodyDispatchData
                {
                    ParticleConstraintRanges = new XREngine.Data.Vectors.IVector4(particleOffset, request.Particles.Count, constraintOffset, request.Constraints.Count),
                    ClusterRanges = new XREngine.Data.Vectors.IVector4(clusterOffset, request.Clusters.Count, clusterMemberOffset, request.ClusterMembers.Count),
                    ColliderBindingRanges = new XREngine.Data.Vectors.IVector4(colliderOffset, request.Colliders.Count, renderBindingOffset, request.RenderBindings.Count),
                    SimulationScalars = request.DispatchData.SimulationScalars,
                    GravitySubsteps = request.DispatchData.GravitySubsteps,
                    ForceIterations = request.DispatchData.ForceIterations,
                });

                particleOffset += request.Particles.Count;
                constraintOffset += request.Constraints.Count;
                clusterOffset += request.Clusters.Count;
                clusterMemberOffset += request.ClusterMembers.Count;
                colliderOffset += request.Colliders.Count;
                renderBindingOffset += request.RenderBindings.Count;
            }
        }

        LastInvalidParticleCount = invalidParticles;
        LastInvalidConstraintCount = invalidConstraints;
        LastInvalidColliderCount = invalidColliders;

        TotalParticleCount = _allParticles.Count;
        TotalConstraintCount = _allConstraints.Count;
        TotalClusterCount = _allClusters.Count;
        TotalColliderCount = _allColliders.Count;

        EnsureBufferCapacity<GPUSoftbodyParticleData>(ref _particlesBuffer, "SoftbodyParticles", (uint)Math.Max(_allParticles.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyParticleData>(ref _solveScratchParticlesBuffer, "SoftbodyParticlesScratch", (uint)Math.Max(_allParticles.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyDistanceConstraintData>(ref _constraintsBuffer, "SoftbodyConstraints", (uint)Math.Max(_allConstraints.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyClusterData>(ref _clustersBuffer, "SoftbodyClusters", (uint)Math.Max(_allClusters.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyClusterMemberData>(ref _clusterMembersBuffer, "SoftbodyClusterMembers", (uint)Math.Max(_allClusterMembers.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyColliderData>(ref _collidersBuffer, "SoftbodyColliders", (uint)Math.Max(_allColliders.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyRenderBindingData>(ref _renderBindingsBuffer, "SoftbodyRenderBindings", (uint)Math.Max(_allRenderBindings.Count, 1));
        EnsureBufferCapacity<GPUSoftbodyDispatchData>(ref _dispatchParamsBuffer, "SoftbodyDispatchParams", (uint)Math.Max(_allDispatches.Count, 1));

        UploadBufferData(_particlesBuffer, _allParticles);
        UploadBufferData(_constraintsBuffer, _allConstraints);
        UploadBufferData(_clustersBuffer, _allClusters);
        UploadBufferData(_clusterMembersBuffer, _allClusterMembers);
        UploadBufferData(_collidersBuffer, _allColliders);
        UploadBufferData(_renderBindingsBuffer, _allRenderBindings);
        UploadBufferData(_dispatchParamsBuffer, _allDispatches);
    }

    private (int maxSubsteps, int maxSolverIterations) DetermineLoopCounts()
    {
        int maxSubsteps = 1;
        int maxSolverIterations = 1;
        foreach (GPUSoftbodyDispatchData dispatch in _allDispatches)
        {
            int requestedSubsteps = Math.Clamp((int)MathF.Round(dispatch.GravitySubsteps.W), 1, MaxSubstepsPerFrame);
            int requestedSolverIterations = Math.Clamp((int)MathF.Round(dispatch.ForceIterations.W), 1, MaxSolverIterationsPerSubstep);
            if (requestedSubsteps > maxSubsteps)
                maxSubsteps = requestedSubsteps;
            if (requestedSolverIterations > maxSolverIterations)
                maxSolverIterations = requestedSolverIterations;
        }

        return (maxSubsteps, maxSolverIterations);
    }

    private void DispatchIntegratePass(int currentSubstep)
    {
        if (_integrateProgram is null || _particlesBuffer is null || _dispatchParamsBuffer is null || TotalParticleCount == 0)
            return;

        BindSharedBuffers(_integrateProgram);
        _integrateProgram.Uniform("particleCount", TotalParticleCount);
        _integrateProgram.Uniform("instanceCount", _allDispatches.Count);
        _integrateProgram.Uniform("currentSubstep", currentSubstep);
        _integrateProgram.DispatchCompute(ComputeGroups((uint)TotalParticleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchCollidePass(int currentSubstep)
    {
        if (_collideProgram is null || _particlesBuffer is null || _collidersBuffer is null || _dispatchParamsBuffer is null || TotalParticleCount == 0)
            return;

        BindSharedBuffers(_collideProgram);
        _collideProgram.Uniform("particleCount", TotalParticleCount);
        _collideProgram.Uniform("colliderCount", TotalColliderCount);
        _collideProgram.Uniform("currentSubstep", currentSubstep);
        _collideProgram.DispatchCompute(ComputeGroups((uint)TotalParticleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchSolveDistancePass(int currentSubstep, int currentSolverIteration)
    {
        if (_solveProgram is null || _particlesBuffer is null || _solveScratchParticlesBuffer is null || _constraintsBuffer is null || _dispatchParamsBuffer is null || TotalParticleCount == 0)
            return;

        _solveProgram.BindBuffer(_particlesBuffer, 0u);
        _solveProgram.BindBuffer(_constraintsBuffer!, 1u);
        _solveProgram.BindBuffer(_dispatchParamsBuffer!, 6u);
        _solveProgram.BindBuffer(_solveScratchParticlesBuffer, 7u);
        _solveProgram.Uniform("particleCount", TotalParticleCount);
        _solveProgram.Uniform("constraintCount", TotalConstraintCount);
        _solveProgram.Uniform("currentSubstep", currentSubstep);
        _solveProgram.Uniform("currentSolverIteration", currentSolverIteration);
        _solveProgram.DispatchCompute(ComputeGroups((uint)TotalParticleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void DispatchFinalizePass()
    {
        if (_finalizeProgram is null || _clustersBuffer is null || _clusterMembersBuffer is null || _dispatchParamsBuffer is null || TotalClusterCount == 0)
            return;

        BindSharedBuffers(_finalizeProgram);
        _finalizeProgram.Uniform("clusterCount", TotalClusterCount);
        _finalizeProgram.DispatchCompute(ComputeGroups((uint)TotalClusterCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private void BindSharedBuffers(XRRenderProgram program)
    {
        program.BindBuffer(_particlesBuffer!, 0u);
        program.BindBuffer(_constraintsBuffer!, 1u);
        program.BindBuffer(_clustersBuffer!, 2u);
        program.BindBuffer(_clusterMembersBuffer!, 3u);
        program.BindBuffer(_collidersBuffer!, 4u);
        program.BindBuffer(_renderBindingsBuffer!, 5u);
        program.BindBuffer(_dispatchParamsBuffer!, 6u);
    }

    private void SwapParticleBuffers()
        => (_particlesBuffer, _solveScratchParticlesBuffer) = (_solveScratchParticlesBuffer, _particlesBuffer);

    private static void EnsureBufferCapacity<T>(ref XRDataBuffer? buffer, string name, uint elementCount) where T : struct
    {
        uint componentCount = (uint)(Marshal.SizeOf<T>() / sizeof(float));
        if (buffer is null || buffer.ElementCount < elementCount)
        {
            buffer?.Dispose();
            buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Float, componentCount, false, false)
            {
                DisposeOnPush = false,
                Usage = EBufferUsage.DynamicDraw,
            };
        }
    }

    private static void UploadBufferData<T>(XRDataBuffer? buffer, List<T> data) where T : struct
    {
        if (buffer is null)
            return;

        if (data.Count == 0)
            buffer.SetDataRaw(new T[] { default });
        else
            buffer.SetDataRaw(data);

        buffer.PushData();
    }

    private static uint ComputeGroups(uint count)
        => Math.Max(1u, (count + LocalSizeX - 1u) / LocalSizeX);

    private static GPUSoftbodyParticleData SanitizeParticle(GPUSoftbodyParticleData particle, int instanceIndex, ref int invalidCount)
    {
        GPUSoftbodyParticleData adjusted = particle;
        if (!IsFinite(adjusted.RestPosition))
        {
            adjusted.RestPosition = Vector3.Zero;
            invalidCount++;
        }
        if (!IsFinite(adjusted.CurrentPosition))
        {
            adjusted.CurrentPosition = adjusted.RestPosition;
            invalidCount++;
        }
        if (!IsFinite(adjusted.PreviousPosition))
        {
            adjusted.PreviousPosition = adjusted.CurrentPosition;
            invalidCount++;
        }
        if (!float.IsFinite(adjusted.InverseMass) || adjusted.InverseMass < 0.0f)
        {
            adjusted.InverseMass = float.IsFinite(adjusted.InverseMass) ? Math.Max(0.0f, adjusted.InverseMass) : 0.0f;
            invalidCount++;
        }
        if (!float.IsFinite(adjusted.Radius) || adjusted.Radius < 0.0f)
        {
            adjusted.Radius = float.IsFinite(adjusted.Radius) ? Math.Max(0.0f, adjusted.Radius) : 0.0f;
            invalidCount++;
        }

        adjusted.InstanceIndex = instanceIndex;
        adjusted._pad0 = 0;
        return adjusted;
    }

    private static bool TrySanitizeConstraint(
        GPUSoftbodyDistanceConstraintData constraint,
        int localParticleCount,
        int particleOffset,
        int instanceIndex,
        out GPUSoftbodyDistanceConstraintData adjusted,
        ref int invalidCount)
    {
        adjusted = constraint;
        if (constraint.ParticleA < 0 || constraint.ParticleA >= localParticleCount ||
            constraint.ParticleB < 0 || constraint.ParticleB >= localParticleCount ||
            !float.IsFinite(constraint.RestLength) || constraint.RestLength < 0.0f)
        {
            invalidCount++;
            return false;
        }

        adjusted.ParticleA += particleOffset;
        adjusted.ParticleB += particleOffset;
        adjusted.RestLength = Math.Max(0.0f, constraint.RestLength);
        adjusted.Compliance = float.IsFinite(constraint.Compliance) ? Math.Max(0.0f, constraint.Compliance) : 0.0f;
        adjusted.Stiffness = float.IsFinite(constraint.Stiffness) ? Math.Clamp(constraint.Stiffness, 0.0f, 1.0f) : 1.0f;
        adjusted.InstanceIndex = instanceIndex;
        adjusted._pad0 = 0.0f;
        adjusted._pad1 = 0.0f;
        return true;
    }

    private static bool TrySanitizeCollider(
        GPUSoftbodyColliderData collider,
        int instanceIndex,
        out GPUSoftbodyColliderData adjusted,
        ref int invalidCount)
    {
        adjusted = collider;
        if (collider.Type != (int)GPUSoftbodyColliderType.Capsule ||
            !IsFinite(collider.SegmentStartRadius) ||
            !IsFinite(collider.SegmentEndFriction) ||
            !IsFinite(collider.VelocityAndDrag) ||
            !float.IsFinite(collider.Margin) ||
            collider.SegmentStartRadius.W < 0.0f)
        {
            invalidCount++;
            return false;
        }

        adjusted.SegmentStartRadius.W = Math.Max(0.0f, collider.SegmentStartRadius.W);
        adjusted.SegmentEndFriction.W = Math.Clamp(collider.SegmentEndFriction.W, 0.0f, 1.0f);
        adjusted.VelocityAndDrag.W = Math.Clamp(collider.VelocityAndDrag.W, 0.0f, 1.0f);
        adjusted.Margin = Math.Max(0.0f, collider.Margin);
        adjusted.CollisionMask = float.IsFinite(collider.CollisionMask) ? collider.CollisionMask : 0.0f;
        adjusted.InstanceIndex = instanceIndex;
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

public sealed class GPUSoftbodyRequest(GPUSoftbodyComponent component)
{
    public object SyncRoot { get; } = new();
    public GPUSoftbodyComponent Component { get; } = component;
    public List<GPUSoftbodyParticleData> Particles { get; } = [];
    public List<GPUSoftbodyDistanceConstraintData> Constraints { get; } = [];
    public List<GPUSoftbodyClusterData> Clusters { get; } = [];
    public List<GPUSoftbodyClusterMemberData> ClusterMembers { get; } = [];
    public List<GPUSoftbodyColliderData> Colliders { get; } = [];
    public List<GPUSoftbodyRenderBindingData> RenderBindings { get; } = [];
    public GPUSoftbodyDispatchData DispatchData;
    public bool NeedsUpdate;
}