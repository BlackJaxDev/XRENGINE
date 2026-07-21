using System.Numerics;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Stateless deterministic source for every authored and dynamic benchmark
/// input. Scenario bridges use this source instead of process-global random
/// state so matched runs and matrix shards produce identical inputs.
/// </summary>
public readonly struct PhysicsChainBenchmarkDeterministicScenario
{
    private const float Tau = MathF.PI * 2.0f;
    private readonly PhysicsChainBenchmarkCase _case;
    private readonly uint _seed;

    public PhysicsChainBenchmarkDeterministicScenario(
        in PhysicsChainBenchmarkCase matrixCase,
        int deterministicSeed)
    {
        _case = matrixCase;
        _seed = unchecked((uint)deterministicSeed);
    }

    /// <summary>Returns the stable parent index for a dynamic segment.</summary>
    public int GetParentIndex(int segmentIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentIndex, _case.DynamicSegmentCount);
        if (segmentIndex == 0)
            return -1;

        if (_case.Topology == PhysicsChainBenchmarkTopology.Linear)
            return segmentIndex - 1;

        // A repeatable binary tree exposes sibling and depth dependencies while
        // keeping every parent before its children in the authored stream.
        return (segmentIndex - 1) >> 1;
    }

    /// <summary>Returns a stable non-uniform rest length for one segment.</summary>
    public float GetRestLength(int segmentIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentIndex, _case.DynamicSegmentCount);
        return 0.04f + UnitFloat(Hash((uint)segmentIndex, 0x4C454E47u)) * 0.12f;
    }

    public int ColliderCount => _case.ColliderScenario switch
    {
        PhysicsChainBenchmarkColliderScenario.None => 0,
        PhysicsChainBenchmarkColliderScenario.TwoSimple => 2,
        PhysicsChainBenchmarkColliderScenario.FiveMixed => 5,
        PhysicsChainBenchmarkColliderScenario.LargeBroadphase => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(_case.ColliderScenario)),
    };

    /// <summary>Returns a stable mixed-shape collider layout.</summary>
    public PhysicsChainBenchmarkColliderInput GetCollider(int colliderIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(colliderIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(colliderIndex, ColliderCount);

        PhysicsChainBenchmarkColliderKind kind = _case.ColliderScenario switch
        {
            PhysicsChainBenchmarkColliderScenario.TwoSimple => PhysicsChainBenchmarkColliderKind.Sphere,
            PhysicsChainBenchmarkColliderScenario.FiveMixed => (PhysicsChainBenchmarkColliderKind)(colliderIndex & 3),
            PhysicsChainBenchmarkColliderScenario.LargeBroadphase => (PhysicsChainBenchmarkColliderKind)(colliderIndex % 3),
            _ => PhysicsChainBenchmarkColliderKind.Sphere,
        };
        uint index = (uint)colliderIndex;
        float x = SignedUnitFloat(Hash(index, 0x434F4C58u)) * 18.0f;
        float y = UnitFloat(Hash(index, 0x434F4C59u)) * 4.0f - 1.0f;
        float z = SignedUnitFloat(Hash(index, 0x434F4C5Au)) * 18.0f;
        float radius = 0.2f + UnitFloat(Hash(index, 0x52414449u)) * 0.8f;
        Vector3 dimensions = kind switch
        {
            PhysicsChainBenchmarkColliderKind.Capsule => new Vector3(radius, 0.5f + radius, radius),
            PhysicsChainBenchmarkColliderKind.Box => new Vector3(radius, radius * 0.75f, radius * 1.25f),
            PhysicsChainBenchmarkColliderKind.Plane => Vector3.One,
            _ => new Vector3(radius),
        };
        float yaw = UnitFloat(Hash(index, 0x524F544Eu)) * Tau;
        return new PhysicsChainBenchmarkColliderInput(
            kind,
            new Vector3(x, y, z),
            dimensions,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
    }

    /// <summary>
    /// Samples fixed-step root motion, force, collider ownership, activity, and
    /// visibility without allocating or mutating random state.
    /// </summary>
    public PhysicsChainBenchmarkDynamicInput Sample(int chainIndex, long simulationFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chainIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chainIndex, _case.ChainCount);
        ArgumentOutOfRangeException.ThrowIfNegative(simulationFrame);

        uint chain = (uint)chainIndex;
        long wrappedFrame = simulationFrame % 3_600L;
        float time = (float)wrappedFrame / _case.FixedSimulationRateHz;
        float phase = UnitFloat(Hash(chain, 0x50484153u)) * Tau;
        float row = MathF.Floor(MathF.Sqrt(_case.ChainCount));
        float width = MathF.Max(row, 1.0f);
        float baseX = chainIndex % (int)width;
        float baseZ = chainIndex / (int)width;
        Vector3 root = new(
            baseX * 0.75f + MathF.Sin(time * 0.7f + phase) * 0.08f,
            1.25f + MathF.Sin(time * 1.1f + phase) * 0.12f,
            baseZ * 0.75f + MathF.Cos(time * 0.9f + phase) * 0.08f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitY,
            MathF.Sin(time * 0.45f + phase) * 0.35f);
        Vector3 force = new Vector3(
            MathF.Sin(time * 1.7f + phase),
            MathF.Cos(time * 1.3f + phase) * 0.25f,
            MathF.Cos(time * 1.9f + phase)) * 0.9f;

        int colliderSetIndex = _case.ColliderOwnership == PhysicsChainBenchmarkColliderOwnership.Shared
            ? 0
            : chainIndex;
        bool active = IsActive(chainIndex);
        bool visible = IsVisible(chainIndex, simulationFrame);
        return new PhysicsChainBenchmarkDynamicInput(root, rotation, force, colliderSetIndex, active, visible);
    }

    private bool IsActive(int chainIndex)
    {
        uint bucket = Hash((uint)chainIndex, 0x41435456u) % 100u;
        return _case.ActivityProfile switch
        {
            PhysicsChainBenchmarkActivityProfile.Active100 => true,
            PhysicsChainBenchmarkActivityProfile.Active50 => bucket < 50u,
            PhysicsChainBenchmarkActivityProfile.Active10 => bucket < 10u,
            PhysicsChainBenchmarkActivityProfile.SleepingOffscreenHeavy => bucket < 10u,
            _ => throw new ArgumentOutOfRangeException(nameof(_case.ActivityProfile)),
        };
    }

    private bool IsVisible(int chainIndex, long simulationFrame)
    {
        if (_case.ActivityProfile != PhysicsChainBenchmarkActivityProfile.SleepingOffscreenHeavy)
            return true;

        uint epoch = (uint)(simulationFrame / Math.Max(_case.FixedSimulationRateHz, 1));
        return Hash((uint)chainIndex ^ epoch, 0x56495349u) % 100u < 20u;
    }

    private uint Hash(uint value, uint stream)
    {
        uint hash = value ^ _seed ^ stream;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return hash;
    }

    private static float UnitFloat(uint value)
        => (value >> 8) * (1.0f / 16_777_216.0f);

    private static float SignedUnitFloat(uint value)
        => UnitFloat(value) * 2.0f - 1.0f;
}
