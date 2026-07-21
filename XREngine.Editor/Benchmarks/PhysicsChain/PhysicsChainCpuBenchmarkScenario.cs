using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Production, headless CPU benchmark bridge over the same backend used by a
/// physics-chain world. Setup owns all arrays; steady frames only mutate them.
/// Renderer-backed mesh buckets remain a separate live-editor responsibility.
/// </summary>
public sealed class PhysicsChainCpuBenchmarkScenario : IPhysicsChainBenchmarkScenario
{
    private PhysicsChainBenchmarkCase _case;
    private PhysicsChainBenchmarkDeterministicScenario _deterministic;
    private PhysicsChainCpuBackend? _backend;
    private PhysicsChainTemplate? _template;
    private PhysicsChainCpuSharedColliderSet? _sharedColliders;
    private PhysicsChainArenaHandle[] _handles = [];
    private PhysicsChainArenaHandle[] _stepHandles = [];
    private PhysicsChainCpuParticleInput[] _particleInputs = [];
    private PhysicsChainCpuOutput[] _readbackScratch = [];
    private Vector3[] _restPositions = [];
    private Vector3[] _previousRoots = [];
    private bool[] _wasStepped = [];
    private long _simulationFrame;
    private long _frameCount;
    private long _inputTicks;
    private long _solveTicks;
    private long _compatibilityTicks;
    private long _managedAllocatedBytes;
    private int _activeChains;
    private int _rateLimitedChains;
    private int _sleepingChains;
    private int _culledChains;
    private int _wokenChains;

    public void Setup(
        in PhysicsChainBenchmarkCase matrixCase,
        PhysicsChainBenchmarkMeasurementKind measurementKind,
        in PhysicsChainBenchmarkDeterministicScenario deterministicScenario)
    {
        if (_backend is not null)
            throw new InvalidOperationException("The CPU benchmark scenario is already set up.");
        if (matrixCase.ExecutionMode is not (PhysicsChainBenchmarkExecutionMode.CpuStrict
            or PhysicsChainBenchmarkExecutionMode.CpuQualityTiered))
            throw new NotSupportedException("The CPU benchmark scenario accepts CPU execution modes only.");
        if (matrixCase.RenderingMode is PhysicsChainBenchmarkRenderingMode.IdenticalInstancedMeshes
            or PhysicsChainBenchmarkRenderingMode.DiverseSkinnedRenderers)
            throw new NotSupportedException("Mesh-rendering buckets require the live editor renderer benchmark bridge.");
        if (matrixCase.ChainCount <= 0 || matrixCase.DynamicSegmentCount <= 0 || matrixCase.FixedSimulationRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(matrixCase));

        _case = matrixCase;
        _ = measurementKind;
        _deterministic = deterministicScenario;
        _template = CreateTemplate(matrixCase, deterministicScenario, out _restPositions);
        _backend = new PhysicsChainCpuBackend(matrixCase.ChainCount);
        _handles = new PhysicsChainArenaHandle[matrixCase.ChainCount];
        _stepHandles = new PhysicsChainArenaHandle[matrixCase.ChainCount];
        _particleInputs = new PhysicsChainCpuParticleInput[matrixCase.DynamicSegmentCount];
        _readbackScratch = new PhysicsChainCpuOutput[matrixCase.DynamicSegmentCount];
        _previousRoots = new Vector3[matrixCase.ChainCount];
        _wasStepped = new bool[matrixCase.ChainCount];

        PhysicsChainColliderShape[] colliderShapes = CreateColliderShapes(deterministicScenario);
        PhysicsChainCpuCollider[] uniqueColliders = CreateWorldColliders(deterministicScenario);
        if (matrixCase.ColliderOwnership == PhysicsChainBenchmarkColliderOwnership.Shared)
        {
            _sharedColliders = new PhysicsChainCpuSharedColliderSet(
                PhysicsChainColliderSetFactory.Create(1L, colliderShapes, 1UL));
            ApplySharedColliderPoses(_sharedColliders, deterministicScenario);
            if (!_sharedColliders.RuntimeSet.TrySynchronizeDirtyPoses())
                throw new InvalidOperationException("The shared benchmark collider set failed to synchronize.");
        }

        PhysicsChainCpuConsumerFlags consumers = matrixCase.RenderingMode == PhysicsChainBenchmarkRenderingMode.None
            ? PhysicsChainCpuConsumerFlags.None
            : PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds;
        var treeInputs = new[] { new PhysicsChainCpuTreeInput(Vector3.Zero) };
        for (int chainIndex = 0; chainIndex < matrixCase.ChainCount; ++chainIndex)
        {
            PhysicsChainBenchmarkDynamicInput dynamicInput = deterministicScenario.Sample(chainIndex, 0L);
            FillParticleInputs(dynamicInput.RootPosition, dynamicInput.RootRotation);
            PhysicsChainCpuInput input = CreateCpuInput(dynamicInput, Vector3.Zero);
            _handles[chainIndex] = _sharedColliders is null
                ? _backend.Register(_template, input, treeInputs, _particleInputs, colliders: uniqueColliders, consumerFlags: consumers)
                : _backend.RegisterShared(_template, input, treeInputs, _particleInputs, _sharedColliders, consumerFlags: consumers);
            _previousRoots[chainIndex] = dynamicInput.RootPosition;
        }
    }

    public PhysicsChainBenchmarkSettleSnapshot RunFrame()
    {
        PhysicsChainCpuBackend backend = _backend
            ?? throw new InvalidOperationException("The CPU benchmark scenario has not been set up.");
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long inputStart = Stopwatch.GetTimestamp();
        int stepCount = 0;
        int active = 0;
        int rateLimited = 0;
        int sleeping = 0;
        int culled = 0;
        int woken = 0;
        long nextFrame = _simulationFrame + 1L;
        bool qualityTiered = _case.ExecutionMode == PhysicsChainBenchmarkExecutionMode.CpuQualityTiered;

        for (int chainIndex = 0; chainIndex < _handles.Length; ++chainIndex)
        {
            PhysicsChainBenchmarkDynamicInput dynamicInput = _deterministic.Sample(chainIndex, nextFrame);
            if (!dynamicInput.IsVisible)
                ++culled;
            if (!dynamicInput.IsActive)
            {
                ++sleeping;
                _wasStepped[chainIndex] = false;
                continue;
            }

            ++active;
            bool shouldStep = !qualityTiered || dynamicInput.IsVisible || ((nextFrame + chainIndex) & 3L) == 0L;
            if (!shouldStep)
            {
                ++rateLimited;
                _wasStepped[chainIndex] = false;
                continue;
            }

            if (!_wasStepped[chainIndex])
                ++woken;
            _wasStepped[chainIndex] = true;
            FillParticleInputs(dynamicInput.RootPosition, dynamicInput.RootRotation);
            Vector3 objectMove = dynamicInput.RootPosition - _previousRoots[chainIndex];
            _previousRoots[chainIndex] = dynamicInput.RootPosition;
            if (!backend.TryUpdateParticleInputs(_handles[chainIndex], _particleInputs)
                || !backend.TryUpdateInput(_handles[chainIndex], CreateCpuInput(dynamicInput, objectMove)))
                throw new InvalidOperationException("A current CPU benchmark handle rejected deterministic dynamic inputs.");
            _stepHandles[stepCount++] = _handles[chainIndex];
        }
        _inputTicks += Stopwatch.GetTimestamp() - inputStart;

        long solveStart = Stopwatch.GetTimestamp();
        if (!backend.TryStepBatch(_stepHandles.AsSpan(0, stepCount)))
            throw new InvalidOperationException("The CPU benchmark backend rejected a deterministic batch.");
        _solveTicks += Stopwatch.GetTimestamp() - solveStart;

        long compatibilityStart = Stopwatch.GetTimestamp();
        ConsumeRequestedCpuOutputs(backend, stepCount);
        _compatibilityTicks += Stopwatch.GetTimestamp() - compatibilityStart;
        _managedAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _simulationFrame = nextFrame;
        ++_frameCount;
        _activeChains = active;
        _rateLimitedChains = rateLimited;
        _sleepingChains = sleeping;
        _culledChains = culled;
        _wokenChains = woken;

        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        long signature = HashCode.Combine(
            snapshot.InstanceCapacity,
            snapshot.LiveInstanceCount,
            snapshot.LiveParticleCount,
            snapshot.LiveColliderCount,
            snapshot.SharedColliderReferenceCount);
        return new PhysicsChainBenchmarkSettleSnapshot(
            snapshot.LiveInstanceCount,
            signature,
            PendingPipelineCompilationCount: 0,
            PendingUploadCount: 0,
            RendererCount: 0);
    }

    public PhysicsChainBenchmarkScenarioMetrics CaptureMetrics()
    {
        PhysicsChainCpuBackend backend = _backend
            ?? throw new InvalidOperationException("The CPU benchmark scenario has not been set up.");
        PhysicsChainCpuBackendSnapshot snapshot = backend.GetSnapshot();
        double tickMilliseconds = 1_000.0 / Stopwatch.Frequency;
        double frameDivisor = Math.Max(_frameCount, 1L);
        int particleCount = checked(_case.ChainCount * _case.DynamicSegmentCount);
        bool outputsRequested = _case.RenderingMode != PhysicsChainBenchmarkRenderingMode.None;
        long staticBytes = CalculateTemplateBytes(_template!);
        long stateBytes = (long)particleCount
            * (Unsafe.SizeOf<PhysicsChainCpuState>() + Unsafe.SizeOf<PhysicsChainCpuOutput>() + Unsafe.SizeOf<PhysicsChainCpuParticleInput>());
        long colliderBytes = (long)_deterministic.ColliderCount
            * (_case.ColliderOwnership == PhysicsChainBenchmarkColliderOwnership.Unique ? _case.ChainCount : 1)
            * Unsafe.SizeOf<PhysicsChainCpuCollider>();
        long paletteBytes = outputsRequested ? (long)particleCount * Unsafe.SizeOf<Matrix4x4>() * 2L : 0L;
        long boundsBytes = outputsRequested ? (long)_case.ChainCount * Unsafe.SizeOf<PhysicsChainCpuBounds>() : 0L;
        long readbackBytes = _case.ReadbackMode == PhysicsChainBenchmarkReadbackMode.Disabled
            ? 0L
            : (long)_readbackScratch.Length * Unsafe.SizeOf<PhysicsChainCpuOutput>();
        long liveBytes = staticBytes + stateBytes + colliderBytes + paletteBytes + boundsBytes + readbackBytes;
        int activeParticles = checked(_activeChains * _case.DynamicSegmentCount);

        return new PhysicsChainBenchmarkScenarioMetrics
        {
            CpuStages = new PhysicsChainBenchmarkCpuStageMetrics
            {
                InputGatherMilliseconds = _inputTicks * tickMilliseconds / frameDivisor,
                SolveMilliseconds = _solveTicks * tickMilliseconds / frameDivisor,
                CompatibilitySynchronizationMilliseconds = _compatibilityTicks * tickMilliseconds / frameDivisor,
                ManagedAllocatedBytes = _managedAllocatedBytes / Math.Max(_frameCount, 1L),
            },
            Population = new PhysicsChainBenchmarkPopulationMetrics
            {
                ActiveChains = _activeChains,
                RateLimitedChains = _rateLimitedChains,
                SleepingChains = _sleepingChains,
                CulledChains = _culledChains,
                WokenChains = _wokenChains,
                ActiveParticles = activeParticles,
            },
            Arenas = new PhysicsChainBenchmarkArenaMetrics
            {
                InstanceCapacity = snapshot.InstanceCapacity,
                InstanceLiveCount = snapshot.LiveInstanceCount,
                ParticleCapacity = particleCount,
                ParticleLiveCount = particleCount,
                TemplateCapacity = 1,
                TemplateLiveCount = 1,
                ColliderCapacity = snapshot.LiveColliderCount + (_sharedColliders?.ColliderCount ?? 0),
                ColliderLiveCount = snapshot.LiveColliderCount + (_sharedColliders?.ColliderCount ?? 0),
                OutputCapacity = particleCount,
                OutputLiveCount = particleCount,
                GrowthCount = snapshot.InstanceGrowthCount,
                CapacityBytes = liveBytes,
                LiveBytes = liveBytes,
                HighWaterBytes = liveBytes,
                ResourceBreakdownAvailability = PhysicsChainBenchmarkMetricAvailability.Measured,
                Static = Resource(staticBytes),
                State = Resource(stateBytes),
                Collider = Resource(colliderBytes),
                Palette = Resource(paletteBytes),
                Bounds = Resource(boundsBytes),
                Readback = Resource(readbackBytes),
            },
        };
    }

    public void Teardown()
    {
        PhysicsChainCpuBackend? backend = _backend;
        if (backend is not null)
        {
            for (int i = 0; i < _handles.Length; ++i)
                if (_handles[i].IsValid && !backend.Remove(_handles[i]))
                    throw new InvalidOperationException("A CPU benchmark handle became stale during teardown.");
        }

        _backend = null;
        _template = null;
        _sharedColliders = null;
        _handles = [];
        _stepHandles = [];
        _particleInputs = [];
        _readbackScratch = [];
        _restPositions = [];
        _previousRoots = [];
        _wasStepped = [];
        _simulationFrame = 0L;
        _frameCount = 0L;
        _inputTicks = 0L;
        _solveTicks = 0L;
        _compatibilityTicks = 0L;
        _managedAllocatedBytes = 0L;
    }

    private void ConsumeRequestedCpuOutputs(PhysicsChainCpuBackend backend, int stepCount)
    {
        switch (_case.ReadbackMode)
        {
            case PhysicsChainBenchmarkReadbackMode.Disabled:
                return;
            case PhysicsChainBenchmarkReadbackMode.SparseSockets:
                for (int i = 0; i < stepCount; i += 100)
                    if (!backend.TryGetOutput(_stepHandles[i], _case.DynamicSegmentCount - 1, out _))
                        throw new InvalidOperationException("Sparse socket output was unavailable.");
                return;
            case PhysicsChainBenchmarkReadbackMode.SparseWholeChains:
                for (int i = 0; i < stepCount; i += 100)
                    if (!backend.TryCopyOutputs(_stepHandles[i], _readbackScratch))
                        throw new InvalidOperationException("Sparse whole-chain output was unavailable.");
                return;
            case PhysicsChainBenchmarkReadbackMode.DiagnosticFullSync:
                for (int i = 0; i < stepCount; ++i)
                    if (!backend.TryCopyOutputs(_stepHandles[i], _readbackScratch))
                        throw new InvalidOperationException("Diagnostic whole-chain output was unavailable.");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(_case.ReadbackMode));
        }
    }

    private PhysicsChainCpuInput CreateCpuInput(in PhysicsChainBenchmarkDynamicInput input, Vector3 objectMove)
        => new(
            DeltaTime: 1.0f / _case.FixedSimulationRateHz,
            Speed: 1.0f,
            ObjectScale: 1.0f,
            Weight: 1.0f,
            Gravity: new Vector3(0.0f, -9.81f, 0.0f),
            ExternalForce: input.ExternalForce,
            ObjectMove: objectMove,
            ResetState: 0u,
            CollisionEnabled: _deterministic.ColliderCount == 0 ? 0u : 1u);

    private void FillParticleInputs(Vector3 rootPosition, Quaternion rootRotation)
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(rootRotation);
        for (int particleIndex = 0; particleIndex < _particleInputs.Length; ++particleIndex)
        {
            Matrix4x4 matrix = rotation;
            matrix.Translation = rootPosition + Vector3.Transform(_restPositions[particleIndex], rootRotation);
            _particleInputs[particleIndex] = new PhysicsChainCpuParticleInput(matrix);
        }
    }

    private static PhysicsChainTemplate CreateTemplate(
        in PhysicsChainBenchmarkCase matrixCase,
        in PhysicsChainBenchmarkDeterministicScenario deterministic,
        out Vector3[] restPositions)
    {
        int count = matrixCase.DynamicSegmentCount;
        var particles = new PhysicsChainTemplateParticle[count];
        var childCounts = new int[count];
        var depths = new int[count];
        restPositions = new Vector3[count];
        int maximumDepth = 0;
        float maximumReach = 0.0f;
        for (int particleIndex = 0; particleIndex < count; ++particleIndex)
        {
            int parent = deterministic.GetParentIndex(particleIndex);
            if (parent >= 0)
                ++childCounts[parent];
            int depth = parent < 0 ? 0 : depths[parent] + 1;
            depths[particleIndex] = depth;
            maximumDepth = Math.Max(maximumDepth, depth);
            float length = parent < 0 ? 0.0f : deterministic.GetRestLength(particleIndex);
            Vector3 restOffset = parent < 0 ? Vector3.Zero : new Vector3(0.0f, -length, 0.0f);
            restPositions[particleIndex] = parent < 0 ? Vector3.Zero : restPositions[parent] + restOffset;
            maximumReach = MathF.Max(maximumReach, restPositions[particleIndex].Length());
            particles[particleIndex] = new PhysicsChainTemplateParticle(
                parent,
                depth,
                particleIndex,
                0,
                length,
                length > 0.0f ? 1.0f / length : 0.0f,
                length,
                Damping: 0.08f,
                Elasticity: 0.12f,
                Stiffness: 0.15f,
                Inert: 0.5f,
                Friction: 0.2f,
                Radius: 0.025f,
                restOffset,
                Quaternion.Identity);
        }
        for (int particleIndex = 0; particleIndex < count; ++particleIndex)
            particles[particleIndex] = particles[particleIndex] with { ChildCount = childCounts[particleIndex] };

        var ordered = new int[count];
        var ranges = new PhysicsChainDepthRange[maximumDepth + 1];
        int orderedIndex = 0;
        for (int depth = 0; depth <= maximumDepth; ++depth)
        {
            int start = orderedIndex;
            for (int particleIndex = 0; particleIndex < count; ++particleIndex)
                if (depths[particleIndex] == depth)
                    ordered[orderedIndex++] = particleIndex;
            ranges[depth] = new PhysicsChainDepthRange(0, depth, start, orderedIndex - start);
        }

        return PhysicsChainTemplateFactory.Create(
            [new PhysicsChainTemplateTree(0, count, maximumDepth, maximumReach)],
            particles,
            ordered,
            ranges);
    }

    private static PhysicsChainColliderShape[] CreateColliderShapes(
        in PhysicsChainBenchmarkDeterministicScenario deterministic)
    {
        var shapes = new PhysicsChainColliderShape[deterministic.ColliderCount];
        for (int i = 0; i < shapes.Length; ++i)
        {
            PhysicsChainBenchmarkColliderInput input = deterministic.GetCollider(i);
            shapes[i] = input.Kind switch
            {
                PhysicsChainBenchmarkColliderKind.Sphere => PhysicsChainColliderShape.Sphere(Vector3.Zero, input.Dimensions.X),
                PhysicsChainBenchmarkColliderKind.Capsule => PhysicsChainColliderShape.Capsule(Vector3.Zero, Vector3.UnitY * input.Dimensions.Y, input.Dimensions.X),
                PhysicsChainBenchmarkColliderKind.Box => PhysicsChainColliderShape.Box(Vector3.Zero, input.Dimensions),
                PhysicsChainBenchmarkColliderKind.Plane => PhysicsChainColliderShape.Plane(Vector3.Zero, Vector3.UnitY),
                _ => throw new ArgumentOutOfRangeException(nameof(input.Kind)),
            };
        }
        return shapes;
    }

    private static void ApplySharedColliderPoses(
        PhysicsChainCpuSharedColliderSet shared,
        in PhysicsChainBenchmarkDeterministicScenario deterministic)
    {
        for (int i = 0; i < shared.ColliderCount; ++i)
        {
            PhysicsChainBenchmarkColliderInput input = deterministic.GetCollider(i);
            Matrix4x4 pose = Matrix4x4.CreateFromQuaternion(input.Rotation);
            pose.Translation = input.Position;
            if (!shared.TrySetPose(i, pose))
                throw new InvalidOperationException("A deterministic shared collider pose was rejected.");
        }
    }

    private static PhysicsChainCpuCollider[] CreateWorldColliders(
        in PhysicsChainBenchmarkDeterministicScenario deterministic)
    {
        var colliders = new PhysicsChainCpuCollider[deterministic.ColliderCount];
        for (int i = 0; i < colliders.Length; ++i)
        {
            PhysicsChainBenchmarkColliderInput input = deterministic.GetCollider(i);
            Vector3 axisX = Vector3.Transform(Vector3.UnitX, input.Rotation);
            Vector3 axisY = Vector3.Transform(Vector3.UnitY, input.Rotation);
            Vector3 axisZ = Vector3.Transform(Vector3.UnitZ, input.Rotation);
            colliders[i] = input.Kind switch
            {
                PhysicsChainBenchmarkColliderKind.Sphere => PhysicsChainCpuCollider.Sphere(input.Position, input.Dimensions.X),
                PhysicsChainBenchmarkColliderKind.Capsule => PhysicsChainCpuCollider.Capsule(
                    input.Position - axisY * input.Dimensions.Y,
                    input.Position + axisY * input.Dimensions.Y,
                    input.Dimensions.X),
                PhysicsChainBenchmarkColliderKind.Box => PhysicsChainCpuCollider.Box(input.Position, axisX, axisY, axisZ, input.Dimensions),
                PhysicsChainBenchmarkColliderKind.Plane => PhysicsChainCpuCollider.Plane(axisY, -Vector3.Dot(axisY, input.Position), inside: false),
                _ => throw new ArgumentOutOfRangeException(nameof(input.Kind)),
            };
        }
        return colliders;
    }

    private static long CalculateTemplateBytes(PhysicsChainTemplate template)
        => (long)template.Trees.Length * Unsafe.SizeOf<PhysicsChainTemplateTree>()
            + (long)template.Particles.Length * Unsafe.SizeOf<PhysicsChainTemplateParticle>()
            + (long)template.DepthOrderedParticleIndices.Length * sizeof(int)
            + (long)template.DepthRanges.Length * Unsafe.SizeOf<PhysicsChainDepthRange>();

    private static PhysicsChainBenchmarkArenaResourceMetrics Resource(long bytes)
        => new()
        {
            CapacityBytes = bytes,
            LiveBytes = bytes,
            HighWaterBytes = bytes,
        };
}
