using System.Numerics;
using JoltPhysicsSharp;
using Ray = JoltPhysicsSharp.Ray;

Console.WriteLine("Jolt browser spike: initializing the native foundation.");
if (!Foundation.Init(doublePrecision: false))
    throw new InvalidOperationException("Jolt initialization failed in the browser spike.");

try
{
    for (int iteration = 0; iteration < 8; iteration++)
    {
        Console.WriteLine($"Jolt browser spike: beginning lifecycle {iteration + 1}/8.");
        RunWorldLifecycle();
    }
}
finally
{
    Foundation.Shutdown();
}

Console.WriteLine("Jolt browser spike: 8 repeated lifecycles and teardown complete.");

static void RunWorldLifecycle()
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
    Console.WriteLine("Jolt browser spike: native world and managed listeners created.");
    bool rejectedReusedFilters = false;
    try
    {
        using PhysicsSystem duplicate = new(settings);
    }
    catch (InvalidOperationException)
    {
        rejectedReusedFilters = true;
    }
    if (!rejectedReusedFilters)
        throw new InvalidOperationException("A second physics system accepted already-transferred native filters.");
    world.Gravity = new Vector3(0, -9.81f, 0);
    int contactAddedCount = 0;
    int contactPersistedCount = 0;
    world.OnContactAdded += CountContactAdded;
    world.OnContactPersisted += CountContactPersisted;

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
        if (contactAddedCount == 0 || contactPersistedCount == 0)
            throw new InvalidOperationException("The browser Jolt world did not invoke its managed contact callbacks.");

        Console.WriteLine($"Jolt browser spike: 120 steps passed, contacts added={contactAddedCount}, persisted={contactPersistedCount}; beginning all-hit raycast.");
        List<RayCastResult> hits = [];
        world.NarrowPhaseQuery.CastRay(
            new Ray(new Vector3(0, 10, 0), new Vector3(0, -20, 0)),
            new RayCastSettings(),
            CollisionCollectorType.AllHit,
            hits);
        if (hits.Count == 0)
            throw new InvalidOperationException("The browser Jolt downward ray missed the created world.");

        Console.WriteLine($"Jolt browser spike: 120 steps, box Y={finalPosition.Y}, ray hits={hits.Count}, contacts added={contactAddedCount}, persisted={contactPersistedCount}.");
    }
    finally
    {
        if (!box.IsInvalid)
            world.BodyInterface.RemoveAndDestroyBody(box);
        if (!floor.IsInvalid)
            world.BodyInterface.RemoveAndDestroyBody(floor);
    }

    world.Dispose();
    if (!world.IsDisposed || world.Handle != 0
        || !pairFilter.IsDisposed || pairFilter.Handle != 0
        || !broadPhase.IsDisposed || broadPhase.Handle != 0
        || !objectVsBroadPhase.IsDisposed || objectVsBroadPhase.Handle != 0)
        throw new InvalidOperationException("The world did not release its managed filter wrappers during disposal.");

    void CountContactAdded(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings contactSettings)
        => contactAddedCount++;

    void CountContactPersisted(PhysicsSystem system, in Body body1, in Body body2, in ContactManifold manifold, ref ContactSettings contactSettings)
        => contactPersistedCount++;
}
