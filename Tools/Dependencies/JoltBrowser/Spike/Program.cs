using System.Numerics;
using JoltPhysicsSharp;
using Ray = JoltPhysicsSharp.Ray;

if (!Foundation.Init(doublePrecision: false))
    throw new InvalidOperationException("Jolt initialization failed in the browser spike.");

try
{
    using SingleThreadedJobSystem jobs = SingleThreadedJobSystem.Create();
    using ObjectLayerPairFilterMask pairFilter = new();
    using BroadPhaseLayerInterfaceMask broadPhase = new(2);
    broadPhase.ConfigureLayer(new BroadPhaseLayer(0), 1u, 0u);
    broadPhase.ConfigureLayer(new BroadPhaseLayer(1), 0xFFFFFFFEu, 0u);
    using ObjectVsBroadPhaseLayerFilterMask objectVsBroadPhase = new(broadPhase);

    PhysicsSystemSettings settings = new()
    {
        MaxBodies = 64,
        NumBodyMutexes = 0,
        MaxBodyPairs = 64,
        MaxContactConstraints = 64,
        ObjectLayerPairFilter = pairFilter,
        BroadPhaseLayerInterface = broadPhase,
        ObjectVsBroadPhaseLayerFilter = objectVsBroadPhase,
    };
    using PhysicsSystem world = new(settings);
    world.Gravity = new Vector3(0, -9.81f, 0);

    ObjectLayer layer = ObjectLayerPairFilterMask.GetObjectLayer(1u, uint.MaxValue);
    using BoxShape floorShape = new(new Vector3(10, 1, 10));
    using BoxShape boxShape = new(new Vector3(0.5f));
    using BodyCreationSettings floorSettings = new(floorShape, new Vector3(0, -1, 0), Quaternion.Identity, MotionType.Static, layer);
    using BodyCreationSettings boxSettings = new(boxShape, new Vector3(0, 5, 0), Quaternion.Identity, MotionType.Dynamic, layer);
    BodyID floor = BodyID.Invalid;
    BodyID box = BodyID.Invalid;
    try
    {
        floor = world.BodyInterface.CreateAndAddBody(floorSettings, Activation.DontActivate);
        if (floor.IsInvalid)
            throw new InvalidOperationException("The browser Jolt world did not create the floor.");

        box = world.BodyInterface.CreateAndAddBody(boxSettings, Activation.Activate);
        if (box.IsInvalid)
            throw new InvalidOperationException("The browser Jolt world did not create the falling box.");

        for (int frame = 0; frame < 120; frame++)
        {
            PhysicsUpdateError updateError = world.Update(1f / 60f, 1, jobs);
            if (updateError != PhysicsUpdateError.None)
                throw new InvalidOperationException($"Browser Jolt fixed step {frame} failed: {updateError}.");
        }

        Vector3 finalPosition = world.BodyInterface.GetPosition(box);
        if (!float.IsFinite(finalPosition.Y) || finalPosition.Y >= 5f)
            throw new InvalidOperationException("The browser Jolt box did not fall after 120 fixed steps.");

        List<RayCastResult> hits = [];
        world.NarrowPhaseQuery.CastRay(
            new Ray(new Vector3(0, 10, 0), new Vector3(0, -20, 0)),
            new RayCastSettings(),
            CollisionCollectorType.AllHit,
            hits);
        if (hits.Count == 0)
            throw new InvalidOperationException("The browser Jolt downward ray missed the created world.");

        Console.WriteLine($"Jolt browser spike: 120 steps, box Y={finalPosition.Y}, ray hits={hits.Count}.");
    }
    finally
    {
        if (!box.IsInvalid)
            world.BodyInterface.RemoveAndDestroyBody(box);
        if (!floor.IsInvalid)
            world.BodyInterface.RemoveAndDestroyBody(floor);
    }
}
finally
{
    Foundation.Shutdown();
}
