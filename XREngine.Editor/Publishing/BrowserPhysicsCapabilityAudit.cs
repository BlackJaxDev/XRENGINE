using System.Numerics;
using XREngine.Components;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Scene.Physics;

namespace XREngine.Editor.Publishing;

/// <summary>Rejects authored physics requirements that the browser Jolt adapter cannot preserve.</summary>
internal static class BrowserPhysicsCapabilityAudit
{
    internal static void Inspect(XRComponent component, string path)
    {
        // Movement also inherits the authored rigid-body settings below.
        if (component is CharacterMovement3DComponent movement)
        {
            Require(movement.InvisibleWallHeight == 0, nameof(movement.InvisibleWallHeight));
            Require(!movement.ConstrainedClimbing, nameof(movement.ConstrainedClimbing));
            Require(movement.ScaleCoeff == 0.8f, nameof(movement.ScaleCoeff));
            Require(movement.VolumeGrowth == 1.5f, nameof(movement.VolumeGrowth));
        }
        switch (component)
        {
            case DynamicRigidBodyComponent body:
                Require(body.SimulationEnabled, nameof(body.SimulationEnabled));
                Require(!body.SendSleepNotifies, nameof(body.SendSleepNotifies));
                Require(body.DominanceGroup == 0, nameof(body.DominanceGroup));
                Require(body.PhysxOwnerClient == 0, nameof(body.PhysxOwnerClient));
                Require(body.CollisionGroup < 16, nameof(body.CollisionGroup));
                Require(HasEquivalentBrowserGroupMask(body.GroupsMask), nameof(body.GroupsMask));
                Require((body.BodyFlags & ~(PhysicsRigidBodyFlags.Kinematic | PhysicsRigidBodyFlags.EnableCcd)) == 0,
                    nameof(body.BodyFlags));
                Require(body.MassSpaceInertiaTensor == Vector3.One, nameof(body.MassSpaceInertiaTensor));
                Require(body.CenterOfMassLocalPose.Translation == Vector3.Zero &&
                    (body.CenterOfMassLocalPose.Rotation == Quaternion.Identity ||
                     body.CenterOfMassLocalPose.Rotation == -Quaternion.Identity), nameof(body.CenterOfMassLocalPose));
                Require(body.MinCcdAdvanceCoefficient == 0.15f, nameof(body.MinCcdAdvanceCoefficient));
                Require(body.MaxDepenetrationVelocity == 10.0f, nameof(body.MaxDepenetrationVelocity));
                Require(body.MaxContactImpulse == float.MaxValue, nameof(body.MaxContactImpulse));
                Require(body.ContactSlopCoefficient == 0.0f, nameof(body.ContactSlopCoefficient));
                Require(body.StabilizationThreshold == 0.0f, nameof(body.StabilizationThreshold));
                Require(body.SleepThreshold == 0.005f, nameof(body.SleepThreshold));
                Require(body.ContactReportThreshold == 0.0f, nameof(body.ContactReportThreshold));
                Require(body.WakeCounter == 0.1f, nameof(body.WakeCounter));
                InspectShapes(body.Geometry, body.ColliderShapes, body.MaterialDefinition, body.Material, Require, ReportMapping);
                break;
            case StaticRigidBodyComponent staticBody:
                Require(staticBody.SimulationEnabled, nameof(staticBody.SimulationEnabled));
                Require(!staticBody.SendSleepNotifies, nameof(staticBody.SendSleepNotifies));
                Require(staticBody.DominanceGroup == 0, nameof(staticBody.DominanceGroup));
                Require(staticBody.PhysxOwnerClient == 0, nameof(staticBody.PhysxOwnerClient));
                Require(staticBody.CollisionGroup < 16, nameof(staticBody.CollisionGroup));
                Require(HasEquivalentBrowserGroupMask(staticBody.GroupsMask), nameof(staticBody.GroupsMask));
                Require(!staticBody.AutoGenerateConvexCollidersFromSiblingModel,
                    nameof(staticBody.AutoGenerateConvexCollidersFromSiblingModel) + ": bake collision geometry before publishing");
                InspectShapes(staticBody.Geometry, staticBody.ColliderShapes, staticBody.MaterialDefinition, staticBody.Material, Require, ReportMapping);
                break;
            case CharacterControllerComponent controller:
                Require(controller.MaterialDefinition is null, nameof(controller.MaterialDefinition));
                Require(HasBrowserCollisionLayer(controller.CollisionLayerMask.Value), nameof(controller.CollisionLayerMask));
                break;
        }

        void Require(bool supported, string feature)
        {
            if (!supported)
                throw new NotSupportedException($"BrowserCook.PhysicsFeatureUnsupported: '{path}' component '{component.GetType().FullName}', feature '{feature}' is not preserved by the browser Jolt adapter.");
        }

        void ReportMapping(string feature)
            => Debug.LogWarning($"BrowserCook.PhysicsMaterialMapping: '{path}' component '{component.GetType().FullName}', '{feature}' uses Jolt's existing single dynamic-friction coefficient. The separate authored static coefficient is retained but has no separate Jolt solver parameter.");
    }

    private static bool HasBrowserCollisionLayer(int mask)
        => mask == 0 || (mask & ushort.MaxValue) != 0;

    private static bool HasEquivalentBrowserGroupMask(PhysicsGroupsMask mask)
    {
        // The pinned 32-bit Jolt object layer packs sixteen group bits and sixteen
        // mask bits. PhysX packs four ushort words into its 64-bit filter mask.
        // Compare their effective masks over every admitted group, including each
        // backend's empty-mask sentinel, rather than rejecting neutral high words.
        ulong nativeMask = (ushort)mask.Word0 | ((ulong)(ushort)mask.Word1 << 16)
            | ((ulong)(ushort)mask.Word2 << 32) | ((ulong)(ushort)mask.Word3 << 48);
        ushort nativeLow = nativeMask == 0 ? ushort.MaxValue : (ushort)nativeMask;
        ushort browserLow = mask.Word0 == 0 ? ushort.MaxValue : (ushort)mask.Word0;
        return nativeLow == browserLow;
    }

    private static void InspectShapes(IPhysicsGeometry? fallback, IReadOnlyList<PhysicsColliderShape> shapes,
        PhysicsMaterialDefinition? material, AbstractPhysicsMaterial? runtimeMaterial,
        Action<bool, string> require, Action<string> reportMapping)
    {
        // Native material objects belong to a desktop backend; the neutral definition
        // is the portable input. Do not publish a pointer-bearing backend object.
        require(runtimeMaterial is null, "Material: use a backend-neutral MaterialDefinition");
        PhysicsMaterialDefinition? effectiveMaterial = material;
        if (effectiveMaterial is null)
            foreach (PhysicsColliderShape shape in shapes)
                if (shape.Enabled && shape.Material is not null)
                {
                    effectiveMaterial = shape.Material;
                    break;
                }
        if (effectiveMaterial is not null && effectiveMaterial.StaticFriction != effectiveMaterial.DynamicFriction)
            reportMapping("MaterialDefinition.StaticFriction");

        bool hasGeometry = false;
        for (int index = 0; index < shapes.Count; index++)
        {
            PhysicsColliderShape shape = shapes[index];
            if (!shape.Enabled || shape.Geometry is null)
                continue;
            hasGeometry = true;
            InspectGeometry(shape.Geometry, $"ColliderShapes[{index}].Geometry", require);
            if (effectiveMaterial is not null)
            {
                PhysicsMaterialDefinition? shapeMaterial = shape.Material ?? material;
                if (shape.Material is { } authored && !ReferenceEquals(authored, effectiveMaterial)
                    && authored.StaticFriction != authored.DynamicFriction)
                    reportMapping($"ColliderShapes[{index}].Material.StaticFriction");
                // A null per-shape definition without a body definition selects
                // PhysX's default material; Jolt instead uses one body-wide material.
                require(MatchesBodyMaterial(shapeMaterial, effectiveMaterial),
                    $"ColliderShapes[{index}].Material: distinct per-shape materials");
            }
        }
        if (!hasGeometry && fallback is not null)
        {
            InspectGeometry(fallback, "Geometry", require);
            require(effectiveMaterial is null || MatchesBodyMaterial(material, effectiveMaterial),
                "ColliderShapes.Material: a geometry-free entry changes the fallback body's material");
        }
    }

    private static bool MatchesBodyMaterial(PhysicsMaterialDefinition? authored, PhysicsMaterialDefinition effective)
        => (authored?.DynamicFriction ?? 0.5f) == effective.DynamicFriction &&
            (authored?.Restitution ?? 0.1f) == effective.Restitution &&
            (authored?.Damping ?? 0.0f) == effective.Damping;

    private static void InspectGeometry(IPhysicsGeometry geometry, string feature, Action<bool, string> require)
        => require(geometry is IPhysicsGeometry.Sphere or IPhysicsGeometry.Box or IPhysicsGeometry.Capsule
            or IPhysicsGeometry.Plane or PhysicsConvexHullGeometry or PhysicsTriangleMeshGeometry
            or PhysicsHeightFieldGeometry, feature + ": no backend-neutral Jolt shape adapter");
}
