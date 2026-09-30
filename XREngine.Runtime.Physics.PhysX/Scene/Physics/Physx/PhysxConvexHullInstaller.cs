using System.Numerics;
using MagicPhysX;
using XREngine.Components.Physics;
using XREngine.Data.Tools;

namespace XREngine.Scene.Physics.Physx;

internal static class PhysxConvexHullInstaller
{
    public static void PrepareAndAttach(
        StaticRigidBodyComponent component,
        IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        IReadOnlyList<PhysxConvexMesh> meshes = PhysxConvexHullCooker.CookHulls(
            hulls,
            out _,
            out _,
            requestGpuData: true);
        RuntimeThreadServices.Current.EnqueuePhysicsThread(() => AttachMeshes(component, meshes));
    }

    private static void AttachMeshes(
        StaticRigidBodyComponent component,
        IReadOnlyList<PhysxConvexMesh> meshes)
    {
        if (!component.IsActive || component.RigidBody is not PhysxStaticRigidBody body || body.ShapeCount > 0)
            return;

        PhysxMaterial material = ResolveMaterial(component);
        for (int i = 0; i < meshes.Count; i++)
        {
            unsafe
            {
                PhysxConvexMeshGeometryExtension geometry = new(
                    meshes[i].ConvexMeshPtr,
                    Vector3.One,
                    Quaternion.Identity,
                    tightBounds: false);
                PhysxShape shape = new(
                    geometry,
                    material,
                    PxShapeFlags.SimulationShape | PxShapeFlags.SceneQueryShape | PxShapeFlags.Visualization,
                    isExclusive: true)
                {
                    LocalPose = (component.ShapeOffsetTranslation, component.ShapeOffsetRotation),
                };
                body.AttachShape(shape);
            }
        }
    }

    private static PhysxMaterial ResolveMaterial(StaticRigidBodyComponent component)
    {
        if (component.Material is PhysxMaterial material)
            return material;
        PhysicsMaterialDefinition? definition = component.MaterialDefinition;
        PhysxMaterial created = definition is null
            ? new PhysxMaterial(0.5f, 0.5f, 0.1f)
            : new PhysxMaterial(definition.StaticFriction, definition.DynamicFriction, definition.Restitution)
            {
                Damping = definition.Damping,
            };
        component.Material = created;
        return created;
    }
}
